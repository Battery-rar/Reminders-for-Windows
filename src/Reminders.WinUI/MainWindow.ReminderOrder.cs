using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reminders.Windows.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private readonly Dictionary<(NavKind Kind, string Key), List<string>> _demoReminderOrder = [];
    private ListViewItem? _reminderDropTarget;
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
        var ranks = order.Select((id, rank) => (id, rank)).ToDictionary(entry => entry.id, entry => entry.rank);
        return rows.OrderBy(row => ranks.GetValueOrDefault(row.Id, int.MaxValue));
    }

    private ReminderItem? ReminderMoveNeighbor(ReminderItem reminder, int step)
    {
        if (!CanReorderReminders || !_reminders.Contains(reminder)) return null;
        var siblings = ReminderHierarchy.Siblings(_reminders, reminder).ToList();
        var destination = siblings.IndexOf(reminder) + step;
        return destination >= 0 && destination < siblings.Count ? siblings[destination] : null;
    }

    private async Task<bool> MoveReminderRelativeAsync(ReminderItem reminder, ReminderItem anchor, bool after)
    {
        if (!CanReorderReminders || !_reminders.Contains(reminder) || !_reminders.Contains(anchor)) return false;
        var siblings = ReminderHierarchy.Siblings(_reminders, reminder).Select(row => row.Id).ToHashSet();
        var adjusted = SidebarPresentation.Reorder(_reminders.Where(row => siblings.Contains(row.Id)).Select(row => row.Id),
            reminder.Id, anchor.Id, after);
        if (adjusted is null) return false;

        var ids = new List<string>(_reminders.Count);
        var index = 0;
        foreach (var row in _reminders) ids.Add(siblings.Contains(row.Id) ? adjusted[index++] : row.Id);
        var query = CurrentReminderQuery;
        var account = _accountEpoch;
        _reminderOrderBusy = true;
        try
        {
            if (_demo) _demoReminderOrder[(query.Kind, query.Key)] = ids;
            else
            {
                var saved = await _sync.CallAsync("save_reminder_order", new Dictionary<string, object>
                {
                    [ReminderScopeParameter] = query.Key,
                    ["ids"] = ids
                });
                if (!saved.Flag("saved")) return false;
            }
            if (!_windowClosed && account == _accountEpoch && query == CurrentReminderQuery)
                await LoadRemindersAsync();
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

    private static string? ReminderVisibleParent(ReminderItem reminder, List<ReminderItem> rows) =>
        reminder.ParentId is { } id && rows.Find(row => row.Id == id) is { } parent
            && ReminderHierarchy.IsParent(reminder, parent) ? id : null;

    private bool CanDropReminder(ReminderDrag drag, ReminderItem target) =>
        CanReorderReminders
        && drag.AccountEpoch == _accountEpoch && drag.Query == CurrentReminderQuery
        && !ReferenceEquals(drag.Reminder, target) && _reminders.Contains(drag.Reminder) && _reminders.Contains(target)
        && ReminderVisibleParent(drag.Reminder, _reminders) == ReminderVisibleParent(target, _reminders);

    private void ReminderRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(ReminderDragFormat)) return;
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (_reminderDrag is not { } drag || sender is not FrameworkElement { DataContext: ReminderItem target } row ||
            !CanDropReminder(drag, target) || ReminderList.ContainerFromItem(target) is not ListViewItem container)
        {
            ClearReminderDropTarget();
            return;
        }
        if (!ReferenceEquals(_reminderDropTarget, container))
        {
            ClearReminderDropTarget();
            _reminderDropTarget = container;
            if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var brush))
                container.BorderBrush = brush as Brush;
        }
        var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
        container.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsContentVisible = true;
    }

    private void ReminderRow_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReminderItem target } &&
            ReferenceEquals(ReminderList.ContainerFromItem(target), _reminderDropTarget))
            ClearReminderDropTarget();
    }

    private async void ReminderRow_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(ReminderDragFormat)) return;
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (_reminderDrag is not { } drag || sender is not FrameworkElement { DataContext: ReminderItem target } row
            || !CanDropReminder(drag, target)) return;
        var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
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
        _reminderDropTarget?.ClearValue(Control.BorderBrushProperty);
        _reminderDropTarget?.ClearValue(Control.BorderThicknessProperty);
        _reminderDropTarget = null;
    }
}
