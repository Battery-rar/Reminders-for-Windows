using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using global::Windows.ApplicationModel.DataTransfer;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private const string ReminderDragFormat = "Battery-rar.Reminders.sidebar-move";
    private sealed record ReminderDrag(ReminderItem Reminder, string SourceList, long AccountEpoch, string Token);
    private ReminderDrag? _reminderDrag;
    private NavigationViewItem? _sidebarDropTarget;
    private bool _sidebarMoveBusy;

    private void ReminderList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_windowClosed || _accountBusy || _dialogOpen || _savingDetail || _sidebarMoveBusy ||
            e.Items.Count != 1 || e.Items[0] is not ReminderItem { Deleted: false } reminder)
        {
            e.Cancel = true;
            return;
        }

        _reminderDrag = new(reminder, reminder.ListId, _accountEpoch, Guid.NewGuid().ToString("N"));
        // Keep titles, account identifiers and reminder IDs inside the app.
        e.Data.SetData(ReminderDragFormat, _reminderDrag.Token);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void ReminderList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _reminderDrag = null;
        ClearSidebarDropTarget();
        if (_hoverExpanded) _paneHoverCloseTimer.Start();
    }

    private void AttachReminderDropTarget(NavigationViewItem item)
    {
        item.AllowDrop = true;
        item.DragOver += SidebarList_DragOver;
        item.DragLeave += SidebarList_DragLeave;
        item.Drop += SidebarList_Drop;
    }

    private ReminderList? GetDropList(object sender, ReminderDrag? drag)
    {
        if (drag is null || drag.AccountEpoch != _accountEpoch || _accountBusy || _windowClosed ||
            sender is not NavigationViewItem { Tag: string tag } item || !Navigation.MenuItems.Contains(item) ||
            !tag.StartsWith("list:", StringComparison.Ordinal) || tag[5..] == drag.SourceList) return null;
        return _lists.FirstOrDefault(list => !list.IsGroup && list.Id == tag[5..]);
    }

    private void SidebarNavigation_DragOver(object sender, DragEventArgs e)
    {
        if (_reminderDrag is null || !e.DataView.Contains(ReminderDragFormat)) return;
        e.AcceptedOperation = DataPackageOperation.None;
        var position = e.GetPosition(Navigation);
        if (!Navigation.IsPaneOpen && position.X <= Navigation.CompactPaneLength + 8) OpenPaneForHover();
        else if (_hoverExpanded)
        {
            if (position.X <= Navigation.OpenPaneLength) _paneHoverCloseTimer.Stop();
            else if (!_paneHoverCloseTimer.IsEnabled) _paneHoverCloseTimer.Start();
        }
    }

    private void SidebarList_DragOver(object sender, DragEventArgs e)
    {
        var list = GetDropList(sender, _reminderDrag);
        if (list is null || !e.DataView.Contains(ReminderDragFormat) || _sidebarMoveBusy)
        {
            if (e.DataView.Contains(ReminderDragFormat)) e.AcceptedOperation = DataPackageOperation.None;
            ClearSidebarDropTarget();
            return;
        }

        _paneHoverCloseTimer.Stop();
        if (sender is NavigationViewItem item && !ReferenceEquals(item, _sidebarDropTarget))
        {
            ClearSidebarDropTarget();
            _sidebarDropTarget = item;
            if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var brush))
                item.Background = brush as Brush;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = false;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = L10n.Format("MoveReminderToList", list.Title);
        e.Handled = true;
        if (!Navigation.IsPaneOpen) OpenPaneForHover();
    }

    private void SidebarList_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _sidebarDropTarget)) ClearSidebarDropTarget();
    }

    private void ClearSidebarDropTarget()
    {
        _sidebarDropTarget?.ClearValue(Control.BackgroundProperty);
        _sidebarDropTarget = null;
    }

    private async void SidebarList_Drop(object sender, DragEventArgs e)
    {
        var drag = _reminderDrag;
        var list = GetDropList(sender, drag);
        if (drag is null || list is null || _sidebarMoveBusy || !e.DataView.Contains(ReminderDragFormat)) return;
        e.Handled = true;
        var deferral = e.GetDeferral();
        var accepted = false;
        try
        {
            if (await e.DataView.GetDataAsync(ReminderDragFormat) is string token &&
                token == drag.Token && ReferenceEquals(drag, _reminderDrag) && GetDropList(sender, drag) is not null)
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                _sidebarMoveBusy = accepted = true;
            }
        }
        catch (Exception error) { AppLog.Error("Could not read the reminder drag", error); }
        finally
        {
            ClearSidebarDropTarget();
            deferral.Complete();
        }
        if (!accepted) return;

        try
        {
            if (_detailDirty && ReferenceEquals(_selected, drag.Reminder) && !await ConfirmLeaveDetailAsync()) return;
            if (drag.AccountEpoch != _accountEpoch || _accountBusy || _windowClosed) return;
            if (_demo)
            {
                if (drag.Reminder.Deleted || drag.Reminder.ListId != drag.SourceList) return;
                MoveDemoFamily(drag.Reminder, list.Id);
            }
            else
            {
                var reminder = await _sync.CallAsync<ReminderItem>("reminder", new { id = drag.Reminder.Id });
                if (drag.AccountEpoch != _accountEpoch || _accountBusy || _windowClosed || reminder.Deleted) return;
                if (reminder.ListId != drag.SourceList)
                {
                    if (reminder.ListId != list.Id)
                        ShowInfo(L10n.Get("CouldntMoveReminder"), L10n.Get("ReminderAlreadyMoved"), InfoBarSeverity.Warning);
                    return;
                }
                await _sync.CallAsync("update_reminder", new
                {
                    id = reminder.Id, list_id = list.Id, expected_list_id = drag.SourceList, edit_base_tag = reminder.ChangeTag
                });
            }
            await RefreshAllAsync();
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntMoveReminder"), L10n.Error(error), InfoBarSeverity.Error); }
        finally { _sidebarMoveBusy = false; }
    }
}
