using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reminders.Windows.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private readonly Dictionary<(NavKind Kind, string Key), List<string>> _demoReminderOrder = [];
    private Grid? _reminderDropRow;
    private bool _reminderDropAfter;
    private bool _reminderOrderBusy;

    private string ReminderSort => _uiPreferences.ReminderSort is "manual" or "priority" or "title"
        ? _uiPreferences.ReminderSort : "due";

    private string ReminderScopeParameter => _view.Kind switch
    {
        NavKind.List => "list_id",
        NavKind.Tag => "tag",
        _ => "scope"
    };

    private bool CanReorderReminders => ReminderSort == "manual" && string.IsNullOrWhiteSpace(SearchBox.Text)
        && _renderedQuery == CurrentReminderQuery && !_windowClosed && !_accountBusy && !_dialogOpen
        && !_savingDetail && !_detailTransition && !_sidebarMoveBusy && !_reminderOrderBusy;

    private void UpdateReminderSortButton()
    {
        var sort = ReminderSort;
        var styleKey = sort == "manual" ? "DefaultButtonStyle" : "AccentButtonStyle";
        if (Application.Current.Resources.TryGetValue(styleKey, out var style) && style is Style buttonStyle
            && ReminderSortButton.Style != buttonStyle)
            ReminderSortButton.Style = buttonStyle;
        if (ReminderSortButton.Flyout is MenuFlyout menu)
            foreach (var option in menu.Items.OfType<RadioMenuFlyoutItem>())
                option.IsChecked = Equals(option.Tag, sort);
    }

    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string sort } || sort == ReminderSort) return;
        _uiPreferences.ReminderSort = sort;
        SaveUiPreferences();
        await LoadRemindersAsync();
    }

    private IEnumerable<ReminderItem> ApplyDemoReminderOrder(IEnumerable<ReminderItem> rows)
    {
        if (!_demoReminderOrder.TryGetValue((_view.Kind, _view.Key), out var order)) return rows;
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < order.Count; index++) ranks.TryAdd(order[index], index);
        return rows.OrderBy(row => ranks.GetValueOrDefault(row.Id, int.MaxValue));
    }

    private ReminderItem? ReminderMoveNeighbor(ReminderItem reminder, int step)
    {
        if (!CanReorderReminders || !_visibleReminders.Contains(reminder)) return null;
        var siblings = ReminderHierarchy.Siblings(_visibleReminders, reminder).ToList();
        var destination = siblings.IndexOf(reminder) + step;
        return destination >= 0 && destination < siblings.Count ? siblings[destination] : null;
    }

    private async Task<bool> MoveReminderRelativeAsync(ReminderItem reminder, ReminderItem anchor, bool after)
    {
        if (!CanReorderReminders || !_reminders.Contains(reminder) || !_reminders.Contains(anchor)) return false;
        if (ReminderOrderPlan.Place(_visibleReminders, reminder, anchor, after) is not { } ids) return false;
        var query = CurrentReminderQuery;
        var account = _accountEpoch;
        _reminderOrderBusy = true;
        try
        {
            if (_demo)
            {
                var key = (query.Kind, query.Key);
                _demoReminderOrder[key] = ReminderOrderPlan.Merge(_demoReminderOrder.GetValueOrDefault(key) ?? [], ids);
            }
            else
            {
                var saved = await _sync.CallAsync("save_reminder_order", new Dictionary<string, object>
                {
                    [ReminderScopeParameter] = query.Key,
                    ["ids"] = ids
                });
                if (!saved.Flag("saved")) return false;
            }
            AppLog.Info($"[order] moved {reminder.Id} after={after} anchor={anchor.Id} rows={ids.Count} demo={_demo} view={ReminderScopeParameter}:{query.Key}");
            if (!_windowClosed && account == _accountEpoch && query == CurrentReminderQuery)
            {
                await LoadRemindersAsync();
                var index = _reminders.FindIndex(row => row.Id == reminder.Id);
                AppLog.Info($"[order] {reminder.Id} is at {index} of {_reminders.Count} after the reload");
            }
            return true;
        }
        catch (Exception error)
        {
            if (!_windowClosed && account == _accountEpoch)
                ShowInfo(L10n.Get("CouldntReorderReminders"), L10n.Error(error), InfoBarSeverity.Error);
            return false;
        }
        finally { _reminderOrderBusy = false; }
    }

    private static string? ReminderVisibleParent(ReminderItem reminder, IReadOnlyList<ReminderItem> rows) =>
        reminder.ParentId is { } id && rows.FirstOrDefault(row => row.Id == id) is { } parent
            && ReminderHierarchy.IsParent(reminder, parent) ? id : null;

    private bool CanDropReminder(ReminderDrag drag, ReminderItem target) =>
        CanReorderReminders
        && drag.AccountEpoch == _accountEpoch && drag.Query == CurrentReminderQuery
        && !ReferenceEquals(drag.Reminder, target) && _visibleReminders.Contains(drag.Reminder) && _visibleReminders.Contains(target)
        && ReminderVisibleParent(drag.Reminder, _visibleReminders) == ReminderVisibleParent(target, _visibleReminders);

    private void ReminderRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(ReminderDragFormat)) return;
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (_reminderDrag is not { } drag || sender is not Grid { DataContext: ReminderItem target } row ||
            !CanDropReminder(drag, target))
        {
            ClearReminderDropTarget();
            return;
        }
        var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
        // The insert line is drawn over the row. Changing the container's border thickness instead
        // resized the row under the pointer, which moved rows that were not being dragged and made
        // the drop position flip while the pointer hovered near the midpoint.
        if (!ReferenceEquals(row, _reminderDropRow) || after != _reminderDropAfter)
        {
            ClearReminderDropTarget();
            _reminderDropRow = row;
            _reminderDropAfter = after;
            if (row.FindName("ReminderDropLine") is Border line)
            {
                line.VerticalAlignment = after ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                line.Visibility = Visibility.Visible;
            }
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsContentVisible = true;
    }

    private void ReminderRow_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _reminderDropRow)) ClearReminderDropTarget();
    }

    private async void ReminderRow_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(ReminderDragFormat)) return;
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (_reminderDrag is not { } drag || sender is not Grid { DataContext: ReminderItem target } row
            || !CanDropReminder(drag, target)) return;
        var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
        AppLog.Info($"[order] drop target={target.Id} after={after} moving={drag.Reminder.Id}");
        var deferral = e.GetDeferral();
        try
        {
            if (await e.DataView.GetDataAsync(ReminderDragFormat) is string token && token == drag.Token
                && ReferenceEquals(drag, _reminderDrag) && CanDropReminder(drag, target)
                && await MoveReminderRelativeAsync(drag.Reminder, target, after))
                e.AcceptedOperation = DataPackageOperation.Move;
        }
        catch (Exception error) { AppLog.Error("Could not finish reordering the reminder list", error); }
        finally
        {
            ClearReminderDropTarget();
            deferral.Complete();
        }
    }

    private void ClearReminderDropTarget()
    {
        if (_reminderDropRow?.FindName("ReminderDropLine") is Border line) line.Visibility = Visibility.Collapsed;
        _reminderDropRow = null;
    }
}
