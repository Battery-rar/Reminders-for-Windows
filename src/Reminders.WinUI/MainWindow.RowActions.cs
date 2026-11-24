using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private readonly HashSet<string> _reschedulingRows = [];

    private bool CanChangeReminderRow(ReminderItem reminder) => !_windowClosed && !_accountBusy && !_dialogOpen
        && !_savingDetail && !_detailTransition && reminder.CanEditRow && !_reschedulingRows.Contains(reminder.Id)
        && !_completing.Contains(reminder.Id) && (ReferenceEquals(_selected, reminder) || _reminders.Contains(reminder));

    private void ReminderRow_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not SwipeControl { DataContext: ReminderItem reminder } row || !CanChangeReminderRow(reminder)) return;
        var actions = new MenuFlyout();
        if (reminder.CanMarkComplete)
            AddAction(L10n.Get("RowMarkComplete"), "\uE73E", () => SetReminderCompletionAsync(reminder, true));
        if (reminder.CanAddToToday)
            AddAction(L10n.Get("RowAddToToday"), "\uE787", () => AddReminderToTodayAsync(reminder));
        if (actions.Items.Count > 0) actions.Items.Add(new MenuFlyoutSeparator());
        AddAction(L10n.Get("DeleteReminder"), "\uE74D", () => DeleteReminderAsync(reminder));

        args.Handled = true;
        if (args.TryGetPosition(row, out var position)) actions.ShowAt(row, new FlyoutShowOptions { Position = position });
        else actions.ShowAt(row);

        void AddAction(string text, string glyph, Func<Task> run)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += async (_, _) => await run();
            actions.Items.Add(item);
        }
    }

    private async void SwipeToComplete_Invoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (args.SwipeControl.DataContext is ReminderItem { CanMarkComplete: true } reminder)
            await SetReminderCompletionAsync(reminder, true);
    }

    private async void SwipeToToday_Invoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (args.SwipeControl.DataContext is ReminderItem reminder) await AddReminderToTodayAsync(reminder);
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ReminderItem reminder } checkbox) return;
        await SetReminderCompletionAsync(reminder, checkbox.IsChecked == true);
        if (ReferenceEquals(checkbox.DataContext, reminder)) checkbox.IsChecked = reminder.Completed;
    }

    private async Task SetReminderCompletionAsync(ReminderItem reminder, bool completed)
    {
        if (!CanChangeReminderRow(reminder) || reminder.Completed == completed) return;
        if (_detailDirty && (ReferenceEquals(_selected, reminder) || completed && IsAncestorOfSelection(reminder))
            && !await ConfirmLeaveDetailAsync()) return;
        if (!CanChangeReminderRow(reminder) || reminder.Completed == completed) return;

        var id = reminder.Id;
        _completing.Add(id);
        var saved = false;
        try
        {
            reminder.Completed = completed;
            if (_demo) CompleteDemoFamily(reminder, completed);
            else await _sync.CallAsync("update_reminder", new { id, completed });
            saved = true;
            if (completed) await AnimateCompletionAsync(reminder);
            _completing.Remove(id);
            await RefreshAllAsync();
        }
        catch (Exception error)
        {
            if (!saved) reminder.Completed = !completed;
            ShowInfo(L10n.Get(saved ? "ReminderSavedRefreshFailed" : "CouldntUpdateReminder"), L10n.Error(error), InfoBarSeverity.Error);
        }
        finally { _completing.Remove(id); }

    }

    private async Task AddReminderToTodayAsync(ReminderItem reminder)
    {
        if (!CanChangeReminderRow(reminder) || !reminder.CanAddToToday) return;
        if (_detailDirty && ReferenceEquals(_selected, reminder) && !await ConfirmLeaveDetailAsync()) return;
        if (!CanChangeReminderRow(reminder) || !reminder.CanAddToToday ||
            ReminderSchedule.MoveToToday(reminder.DueDate, reminder.AllDay, DateTimeOffset.Now, TimeZoneInfo.Local) is not { } schedule) return;

        var id = reminder.Id;
        _reschedulingRows.Add(id);
        try
        {
            if (_demo)
            {
                reminder.DueDate = schedule.DueDate;
                reminder.AllDay = schedule.AllDay;
                reminder.RefreshTimeMetadata();
                if (ReferenceEquals(_selected, reminder)) ShowDetail(reminder, animate: false);
            }
            else await _sync.CallAsync("update_reminder", new { id, due_date = schedule.DueDate, all_day = schedule.AllDay, edit_base_tag = reminder.ChangeTag });
            await RefreshAllAsync();
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntUpdateReminder"), L10n.Error(error), InfoBarSeverity.Error); }
        finally { _reschedulingRows.Remove(id); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } reminder) await DeleteReminderAsync(reminder);
    }

    private async Task DeleteReminderAsync(ReminderItem target)
    {
        if (!CanChangeReminderRow(target)) return;
        if (_detailDirty && IsAncestorOfSelection(target) && !await ConfirmLeaveDetailAsync()) return;
        if (!CanChangeReminderRow(target)) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme,
                Title = L10n.Get("DeleteReminder"), Content = L10n.Format("DeleteReminderDetails", target.DisplayTitle),
                PrimaryButtonText = L10n.Get("Delete"), CloseButtonText = L10n.Get("Cancel"), DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (_demo)
            {
                var children = DemoDescendants(target.Id);
                target.Deleted = true;
                foreach (var child in children) child.Deleted = true;
            }
            else await _sync.CallAsync("delete_reminder", new { id = target.Id });
            if (_selected?.Id == target.Id || IsAncestorOfSelection(target)) ShowDetail(null);
            await RefreshAllAsync();
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntDeleteReminder"), L10n.Error(error), InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }

    private bool IsAncestorOfSelection(ReminderItem reminder)
    {
        if (_selected?.ParentId is not { } parentId) return false;
        var seen = new HashSet<string> { _selected.Id };
        for (string? id = parentId; id is not null && seen.Add(id); id = _reminders.Find(item => item.Id == id)?.ParentId)
            if (id == reminder.Id) return true;
        return false;
    }
}

public sealed partial class AvailableSwipeItemsConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) => value is true ? parameter : null;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
