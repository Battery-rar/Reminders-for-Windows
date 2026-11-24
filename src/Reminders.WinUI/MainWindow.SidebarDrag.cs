using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Foundation;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private const string ReminderDragFormat = "Battery-rar.Reminders.sidebar-move";
    private const string SidebarReorderFormat = "Battery-rar.Reminders.sidebar-reorder";
    private static readonly TimeSpan SidebarHoldDelay = TimeSpan.FromMilliseconds(450);
    private const double SidebarDragSlop = 6;
    private sealed record ReminderDrag(ReminderItem Reminder, string SourceList, long AccountEpoch, string Token);
    private sealed record SidebarPress(NavigationViewItem Item, uint PointerId, Point Origin, long Started, long AccountEpoch);
    private sealed record SidebarReorder(NavigationViewItem Item, string ListId, long AccountEpoch, string Token);
    private ReminderDrag? _reminderDrag;
    private SidebarPress? _sidebarPress;
    private SidebarReorder? _sidebarReorder;
    private NavigationViewItem? _sidebarDropTarget;
    private NavigationViewItem? _sidebarInsertTarget;
    private bool _sidebarTrackingPointer;
    private bool _sidebarMoveBusy;
    private bool _sidebarOrderBusy;

    private bool CanReorderSidebar => !_windowClosed && !_accountBusy && !_dialogOpen && !_savingDetail
        && !_sidebarMoveBusy && !_sidebarOrderBusy && SidebarSort == SidebarPresentation.Manual;

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
        item.DragStarting += SidebarList_DragStarting;
        item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SidebarList_PointerPressed), true);
        if (_sidebarTrackingPointer) return;
        _sidebarTrackingPointer = true;
        Navigation.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(SidebarList_PointerMoved), true);
        Navigation.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SidebarList_PointerEnded), true);
        Navigation.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(SidebarList_PointerEnded), true);
        Navigation.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SidebarList_PointerEnded), true);
    }

    private void SidebarList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _sidebarPress = null;
        if (!CanReorderSidebar || _sidebarReorder is not null || _reminderDrag is not null || sender is not NavigationViewItem item)
            return;
        var point = e.GetCurrentPoint(Navigation);
        if (point.Properties.IsRightButtonPressed || !point.IsInContact ||
            (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)) return;
        _sidebarPress = new(item, point.PointerId, point.Position, Stopwatch.GetTimestamp(), _accountEpoch);
    }

    private void SidebarList_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_sidebarPress?.PointerId == e.Pointer.PointerId) _sidebarPress = null;
    }

    private async void SidebarList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_sidebarPress is not { } press || press.PointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(Navigation);
        if (!point.IsInContact || !CanReorderSidebar || press.AccountEpoch != _accountEpoch)
        {
            _sidebarPress = null;
            return;
        }
        if (Math.Max(Math.Abs(point.Position.X - press.Origin.X), Math.Abs(point.Position.Y - press.Origin.Y)) < SidebarDragSlop)
            return;
        _sidebarPress = null;
        if (Stopwatch.GetElapsedTime(press.Started) < SidebarHoldDelay || SidebarListId(press.Item) is not { } id) return;

        var reorder = new SidebarReorder(press.Item, id, press.AccountEpoch, Guid.NewGuid().ToString("N"));
        _sidebarReorder = reorder;
        _paneHoverCloseTimer.Stop();
        e.Handled = true;
        try { await press.Item.StartDragAsync(e.GetCurrentPoint(press.Item)); }
        catch (Exception error) { AppLog.Error("Could not start reordering the sidebar", error); }
        finally
        {
            if (ReferenceEquals(_sidebarReorder, reorder)) _sidebarReorder = null;
            ClearSidebarInsertTarget();
            if (_hoverExpanded) _paneHoverCloseTimer.Start();
        }
    }

    private void SidebarList_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (_sidebarReorder is not { } reorder || !ReferenceEquals(sender, reorder.Item))
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetData(SidebarReorderFormat, reorder.Token);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private string? SidebarListId(object sender) =>
        sender is NavigationViewItem { Tag: string tag } item && Navigation.MenuItems.Contains(item)
            && tag.StartsWith("list:", StringComparison.Ordinal) && _lists.Any(list => !list.IsGroup && list.Id == tag[5..])
        ? tag[5..] : null;

    private List<string>? SidebarDropOrder(object sender, DragEventArgs e, SidebarReorder? reorder)
    {
        if (reorder is null || reorder.AccountEpoch != _accountEpoch || !CanReorderSidebar ||
            sender is not NavigationViewItem item || SidebarListId(item) is not { } target) return null;
        var after = e.GetPosition(item).Y >= item.ActualHeight / 2;
        return SidebarPresentation.Reorder(OrderedLists().Select(list => list.Id), reorder.ListId, target, after);
    }

    private void SidebarReorderDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (SidebarDropOrder(sender, e, _sidebarReorder) is null || sender is not NavigationViewItem item)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearSidebarInsertTarget();
            return;
        }
        if (!ReferenceEquals(item, _sidebarInsertTarget))
        {
            ClearSidebarInsertTarget();
            _sidebarInsertTarget = item;
            if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var brush))
                item.BorderBrush = brush as Brush;
        }
        var after = e.GetPosition(item).Y >= item.ActualHeight / 2;
        item.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        _paneHoverCloseTimer.Stop();
        if (!Navigation.IsPaneOpen) OpenPaneForHover();
    }

    private void ClearSidebarInsertTarget()
    {
        _sidebarInsertTarget?.ClearValue(Control.BorderBrushProperty);
        _sidebarInsertTarget?.ClearValue(Control.BorderThicknessProperty);
        _sidebarInsertTarget = null;
    }

    private async Task DropSidebarListAsync(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        var reorder = _sidebarReorder;
        if (reorder is null) return;
        var deferral = e.GetDeferral();
        try
        {
            if (await e.DataView.GetDataAsync(SidebarReorderFormat) is string token && token == reorder.Token
                && ReferenceEquals(reorder, _sidebarReorder) && SidebarDropOrder(sender, e, reorder) is { } order
                && await SaveListOrderAsync(reorder.ListId, order))
                e.AcceptedOperation = DataPackageOperation.Move;
        }
        catch (Exception error) { AppLog.Error("Could not finish reordering the sidebar", error); }
        finally
        {
            ClearSidebarInsertTarget();
            deferral.Complete();
        }
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
        if ((_reminderDrag is null || !e.DataView.Contains(ReminderDragFormat)) &&
            (_sidebarReorder is null || !e.DataView.Contains(SidebarReorderFormat))) return;
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
        if (e.DataView.Contains(SidebarReorderFormat)) { SidebarReorderDragOver(sender, e); return; }
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
        if (ReferenceEquals(sender, _sidebarInsertTarget)) ClearSidebarInsertTarget();
    }

    private void ClearSidebarDropTarget()
    {
        _sidebarDropTarget?.ClearValue(Control.BackgroundProperty);
        _sidebarDropTarget = null;
        ClearSidebarInsertTarget();
    }

    private async void SidebarList_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(SidebarReorderFormat)) { await DropSidebarListAsync(sender, e); return; }
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
