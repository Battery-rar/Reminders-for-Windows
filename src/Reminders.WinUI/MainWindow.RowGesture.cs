using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Reminders.Windows.Services;
using Windows.Foundation;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private const double MouseDragSlop = 6;
    /// <summary>Horizontal pans belong to the row, vertical panning stays with the list.</summary>
    private const ManipulationModes RowSwipeMode = ManipulationModes.TranslateX | ManipulationModes.System;
    /// <summary>While a long press is held the row also needs vertical movement for the drag.</summary>
    private const ManipulationModes RowHoldMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;

    private readonly ConditionalWeakTable<Grid, RowGesture> _rowGestures = new();
    private Grid? _activeGestureRow;
    private Grid? _revealedGestureRow;
    private FrameworkElement? _reminderDragSource;
    private RowPress? _mouseRowPress;
    private PointerRoutedEventArgs? _lastGestureEvent;
    private DispatcherQueueTimer? _rowHoldTimer;
    private ScrollViewer? _reminderScrollViewer;

    private sealed class RowGesture
    {
        internal readonly ReminderTouchGesture Gesture = new();
        internal uint PointerId;
        internal Pointer? Pointer;
        internal bool Captured;
        internal bool ManipulationActive;
        internal bool PointerHandlersAttached;
        internal double LastY;
        internal double VelocityY;
        internal long LastMovedAt;
        internal PointerPoint? LastPoint;
        internal bool HoldMoveLogged;
        internal ScrollMode? PausedScrollMode;
        internal ContentPresenter? Body;
        internal TranslateTransform? Shift;
        internal Grid? Actions;
        internal Button? TodayAction;
        internal Button? CompleteAction;
        internal TextBlock? TodayLabel;
        internal TextBlock? CompleteLabel;
        internal FontIcon? TodayGlyph;
        internal Storyboard? Animation;
        internal long MenuShownAt;
    }

    private readonly record struct RowPress(Grid Row, ReminderItem Reminder, uint PointerId, Point Origin);

    /// <summary>
    /// Every touch contact on a reminder row starts here. The native swipe control is gone because
    /// it hands the contact to the compositor on press, which made a held finger reveal its buttons
    /// and left no way to decide between swiping, holding and dragging.
    /// </summary>
    private void ReminderRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid row || !Claim(e)) return;
        if (row.DataContext is not ReminderItem reminder) return;
        if (_reminderDrag is not null || _dialogOpen || _windowClosed || _accountBusy) return;
        var point = e.GetCurrentPoint(row);
        if (!point.IsInContact || point.Properties.IsRightButtonPressed) return;
        if (IsRowButton(e.OriginalSource as DependencyObject, row)) return;

        if (point.PointerDeviceType == PointerDeviceType.Mouse)
        {
            _mouseRowPress = point.Properties.IsLeftButtonPressed
                ? new RowPress(row, reminder, point.PointerId, point.Position) : null;
            return;
        }
        if (point.PointerDeviceType != PointerDeviceType.Touch || !point.Properties.IsPrimary) return;

        if (_activeGestureRow is { } active)
        {
            if (ReferenceEquals(active, row)) return;
            ResetRowGesture(active);
        }
        if (_revealedGestureRow is { } revealed && !ReferenceEquals(revealed, row)) ResetRowGesture(revealed);

        PrepareRowActions(row);
        var state = State(row);
        state.PointerId = point.PointerId;
        state.Pointer = e.Pointer;
        state.ManipulationActive = false;
        state.LastY = point.Position.Y;
        state.VelocityY = 0;
        state.LastMovedAt = Environment.TickCount64;
        state.LastPoint = point;
        state.HoldMoveLogged = false;
        var today = state.TodayAction?.Visibility == Visibility.Visible ? ReminderTouchGesture.ActionWidth : 0;
        var complete = state.CompleteAction?.Visibility == Visibility.Visible ? ReminderTouchGesture.ActionWidth : 0;
        state.Gesture.Press(point.Position.X, point.Position.Y, Environment.TickCount64, today, complete);
        _activeGestureRow = row;
        StartRowHoldTimer();
        AppLog.Info($"[touch] press row={reminder.Id} x={point.Position.X:F0} y={point.Position.Y:F0} today={today:F0} complete={complete:F0}");
    }

    private void ReminderRow_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid row || !Claim(e)) return;
        if (_mouseRowPress is { } press && ReferenceEquals(press.Row, row) && press.PointerId == e.Pointer.PointerId)
        {
            var mouse = e.GetCurrentPoint(row);
            if (!mouse.Properties.IsLeftButtonPressed)
            {
                _mouseRowPress = null;
                return;
            }
            if (Math.Abs(mouse.Position.X - press.Origin.X) < MouseDragSlop &&
                Math.Abs(mouse.Position.Y - press.Origin.Y) < MouseDragSlop) return;
            _mouseRowPress = null;
            e.Handled = true;
            BeginRowDrag(row, press.Reminder, mouse);
            return;
        }
        if (_activeGestureRow is not { } active || !ReferenceEquals(active, row)) return;
        MoveRowGesture(row, e);
    }

    private void MoveRowGesture(Grid row, PointerRoutedEventArgs e)
    {
        var state = State(row);
        if (state.PointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(row);
        if (!point.IsInContact) return;
        state.LastPoint = point;
        var before = state.Gesture.Phase;
        var drag = state.Gesture.Move(point.Position.X, point.Position.Y, Environment.TickCount64);
        if (state.Gesture.Phase != before)
            AppLog.Info($"[touch] {before}->{state.Gesture.Phase} row={ReminderName(row)} x={point.Position.X:F0} y={point.Position.Y:F0} moves={state.Gesture.Moves}");
        if (state.Gesture.Phase == ReminderGesturePhase.Swipe)
        {
            // Only when the framework did not recognise the pan: capture early enough to keep the
            // list from taking the contact, and drive the same offset from raw moves.
            if (!state.Captured && !state.ManipulationActive) state.Captured = row.CapturePointer(e.Pointer);
            ApplyRowOffset(row, animate: false);
        }
        else if (state.Gesture.Phase == ReminderGesturePhase.Scroll)
        {
            ScrollRowWithGesture(state, point);
        }
        if (drag && row.DataContext is ReminderItem reminder) BeginRowDrag(row, reminder, point);
    }

    private void ReminderRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid row || !Claim(e)) return;
        if (_mouseRowPress?.Row == row) _mouseRowPress = null;
        ReleaseRowGesture(row, e);
    }

    private void ReleaseRowGesture(Grid row, PointerRoutedEventArgs e)
    {
        if (_activeGestureRow is not { } active || !ReferenceEquals(active, row)) return;
        var state = State(row);
        if (state.PointerId != e.Pointer.PointerId) return;
        _rowHoldTimer?.Stop();
        var point = e.GetCurrentPoint(row);
        var before = state.Gesture.Phase;
        var menu = state.Gesture.Release(Environment.TickCount64);
        AppLog.Info($"[touch] release {before} row={ReminderName(row)} moves={state.Gesture.Moves} offset={state.Gesture.Offset:F0} menu={menu}");
        if (before == ReminderGesturePhase.Scroll) FlingRowScroll(state);
        ShowHeldRow(row, state, held: false);
        ApplyRowOffset(row, animate: true);
        ClearActiveGesture(row);
        if (!menu) return;
        e.Handled = true;
        state.MenuShownAt = Environment.TickCount64;
        ShowReminderRowMenu(row, point.Position);
    }

    /// <summary>
    /// The row's own recognizer handles horizontal pans; the System flag keeps vertical panning
    /// with the list. Receiving this at all is the proof that the contact was taken from Direct
    /// Manipulation, which raw pointer moves never manage.
    /// </summary>
    private void ReminderRow_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        if (sender is not Grid row || e.PointerDeviceType != PointerDeviceType.Touch) return;
        if (_activeGestureRow is not { } active || !ReferenceEquals(active, row)) return;
        var state = State(row);
        state.ManipulationActive = true;
        if (!state.Gesture.BeginSwipe()) return;
        _rowHoldTimer?.Stop();
        AppLog.Info($"[touch] manipulation started row={ReminderName(row)} x={e.Position.X:F0} y={e.Position.Y:F0}");
        ApplyRowOffset(row, animate: false);
    }

    private void ReminderRow_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (sender is not Grid row || e.PointerDeviceType != PointerDeviceType.Touch) return;
        var state = State(row);
        if (state.Gesture.Phase == ReminderGesturePhase.Hold)
        {
            var x = e.Cumulative.Translation.X;
            var y = e.Cumulative.Translation.Y;
            if (!state.HoldMoveLogged)
            {
                state.HoldMoveLogged = true;
                AppLog.Info($"[touch] hold moved x={x:F0} y={y:F0}");
            }
            if (Math.Max(Math.Abs(x), Math.Abs(y)) < ReminderTouchGesture.DragSlop) return;
            if (row.DataContext is not ReminderItem reminder) return;
            if (state.LastPoint is { IsInContact: true } pointer) BeginRowDrag(row, reminder, pointer);
            else AppLog.Info("[touch] hold moved without a live pointer point");
            return;
        }
        if (state.Gesture.Phase != ReminderGesturePhase.Swipe) return;
        state.Gesture.SwipeTo(e.Cumulative.Translation.X);
        ApplyRowOffset(row, animate: false);
    }

    private void ReminderRow_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (sender is not Grid row || e.PointerDeviceType != PointerDeviceType.Touch) return;
        var state = State(row);
        state.ManipulationActive = false;
        if (state.Gesture.Phase != ReminderGesturePhase.Swipe) return;
        var revealed = state.Gesture.EndSwipe();
        AppLog.Info($"[touch] manipulation completed row={ReminderName(row)} x={e.Cumulative.Translation.X:F0} revealed={revealed}");
        ApplyRowOffset(row, animate: true);
        ClearActiveGesture(row);
    }

    private void StartRowHoldTimer()
    {
        _rowHoldTimer ??= CreateRowHoldTimer();
        _rowHoldTimer.Stop();
        _rowHoldTimer.Start();
    }

    private DispatcherQueueTimer CreateRowHoldTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(40);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => TickRowHold();
        return timer;
    }

    private void TickRowHold()
    {
        if (_activeGestureRow is not { } row)
        {
            _rowHoldTimer?.Stop();
            return;
        }
        var state = State(row);
        if (state.Gesture.Phase != ReminderGesturePhase.Pending)
        {
            _rowHoldTimer?.Stop();
            return;
        }
        if (!state.Gesture.TryBeginHold(Environment.TickCount64)) return;
        _rowHoldTimer?.Stop();
        // The list owns vertical panning through the System flag, so the row claims both axes and
        // pauses the list's own scrolling for the rest of this contact. Both changes are undone
        // when the contact ends; without them a held finger still scrolls instead of dragging.
        row.ManipulationMode = RowHoldMode;
        row.CancelDirectManipulations();
        if (FindReminderScrollViewer() is { } scroll)
        {
            state.PausedScrollMode = scroll.VerticalScrollMode;
            scroll.VerticalScrollMode = ScrollMode.Disabled;
        }
        state.Captured = state.Pointer is { } pointer && row.CapturePointer(pointer);
        ShowHeldRow(row, state, held: true);
        AppLog.Info($"[touch] hold row={ReminderName(row)} captured={state.Captured} mode=both scroll={state.PausedScrollMode is not null}");
    }

    /// <summary>Puts the row and the list back the way they were before a long press.</summary>
    private void RestoreRowManipulation(Grid row)
    {
        row.ManipulationMode = RowSwipeMode;
        if (!_rowGestures.TryGetValue(row, out var state) || state.PausedScrollMode is not { } mode) return;
        state.PausedScrollMode = null;
        if (FindReminderScrollViewer() is { } scroll) scroll.VerticalScrollMode = mode;
    }

    private static void ShowHeldRow(Grid row, RowGesture state, bool held)
    {
        if (state.Body is { } body) body.Opacity = held ? 0.6 : 1;
    }

    /// <summary>
    /// Fallback for a list that does not scroll by itself: the state machine only reaches the Scroll
    /// phase when raw moves keep arriving, which means Direct Manipulation never took the contact.
    /// </summary>
    private void ScrollRowWithGesture(RowGesture state, PointerPoint point)
    {
        var now = Environment.TickCount64;
        var elapsed = Math.Max(1, now - state.LastMovedAt);
        var delta = state.LastY - point.Position.Y;
        state.VelocityY = delta / elapsed;
        state.LastY = point.Position.Y;
        state.LastMovedAt = now;
        if (Math.Abs(delta) < 0.5 || FindReminderScrollViewer() is not { } scroll) return;
        scroll.ChangeView(null, scroll.VerticalOffset + delta, null, disableAnimation: true);
    }

    private void FlingRowScroll(RowGesture state)
    {
        if (Math.Abs(state.VelocityY) < 0.05 || FindReminderScrollViewer() is not { } scroll) return;
        scroll.ChangeView(null, scroll.VerticalOffset + state.VelocityY * 200, null, disableAnimation: false);
    }

    private ScrollViewer? FindReminderScrollViewer()
    {
        if (_reminderScrollViewer is not null) return _reminderScrollViewer;
        var pending = new Stack<DependencyObject>();
        pending.Push(ReminderList);
        while (pending.TryPop(out var node))
        {
            if (node is ScrollViewer scroll)
            {
                _reminderScrollViewer = scroll;
                return scroll;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                pending.Push(VisualTreeHelper.GetChild(node, index));
        }
        return null;
    }

    private void ReminderRow_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (Claim(e)) CancelRowGesture(sender, e);
    }

    private void ReminderRow_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (Claim(e)) CancelRowGesture(sender, e);
    }

    private void ReminderList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activeGestureRow is not { } row || !Claim(e)) return;
        MoveRowGesture(row, e);
    }

    private void ReminderList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_activeGestureRow is not { } row || !Claim(e)) return;
        ReleaseRowGesture(row, e);
    }

    private void ReminderList_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_activeGestureRow is not { } row || !Claim(e)) return;
        CancelRowGesture(row, e);
    }

    private void CancelRowGesture(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid row) return;
        if (_activeGestureRow is not { } active || !ReferenceEquals(active, row)) return;
        var state = State(row);
        if (state.PointerId != e.Pointer.PointerId) return;
        _rowHoldTimer?.Stop();
        var before = state.Gesture.Phase;
        state.Gesture.Cancel();
        AppLog.Info($"[touch] {e.Pointer.PointerDeviceType} {before} ended row={ReminderName(row)} moves={state.Gesture.Moves} offset={state.Gesture.Offset:F0}");
        ShowHeldRow(row, state, held: false);
        ApplyRowOffset(row, animate: true);
        ClearActiveGesture(row);
    }

    private void ReminderRow_Holding(object sender, HoldingRoutedEventArgs args)
    {
        if (args.PointerDeviceType != PointerDeviceType.Touch) return;
        AppLog.Info($"[touch] holding {args.HoldingState} row={ReminderName(sender as FrameworkElement)}");
    }

    private void ReminderRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid row) return;
        var state = State(row);
        if (state.PointerHandlersAttached) return;
        state.PointerHandlersAttached = true;
        // A manipulation-mode element can see its pointer events marked handled before the CLR
        // handlers run, so the same handlers are also registered for handled events.
        row.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ReminderRow_PointerPressed), true);
        row.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ReminderRow_PointerMoved), true);
        row.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ReminderRow_PointerReleased), true);
        row.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(ReminderRow_PointerCanceled), true);
        row.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ReminderRow_PointerCaptureLost), true);
    }

    private void ReminderRow_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not Grid row) return;
        ResetRowGesture(row);
        PrepareRowActions(row);
    }

    private void ReminderRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid row) ResetRowGesture(row);
    }

    private void ReminderRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid viewport)
            viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private async void BeginRowDrag(Grid row, ReminderItem reminder, PointerPoint point)
    {
        ResetRowGesture(row);
        if (!point.IsInContact || _reminderDrag is not null || _windowClosed || _accountBusy || _dialogOpen
            || _savingDetail || _sidebarMoveBusy || !CanChangeReminderRow(reminder)) return;
        _reminderDragSource = row;
        AppLog.Info($"[touch] drag start row={reminder.Id}");
        try { await row.StartDragAsync(point); }
        catch (Exception error) { AppLog.Error("Could not start dragging a reminder", error); }
        finally
        {
            _reminderDragSource = null;
            if (_reminderDrag is not null) EndReminderDrag();
        }
    }

    private void ReminderRow_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _reminderDragSource) ||
            sender is not FrameworkElement { DataContext: ReminderItem reminder } ||
            _reminderDrag is not null || _windowClosed || _accountBusy || _dialogOpen || _savingDetail ||
            _sidebarMoveBusy || !CanChangeReminderRow(reminder))
        {
            e.Cancel = true;
            return;
        }
        BeginReminderDrag(reminder, e.Data);
    }

    private RowGesture State(Grid row)
    {
        var state = _rowGestures.GetValue(row, _ => new RowGesture());
        if (state.Body is null)
        {
            state.Body = row.FindName("ReminderRowBody") as ContentPresenter;
            state.Shift = state.Body?.RenderTransform as TranslateTransform;
            state.Actions = row.FindName("ReminderRowActions") as Grid;
            state.TodayAction = row.FindName("ReminderRowTodayAction") as Button;
            state.CompleteAction = row.FindName("ReminderRowCompleteAction") as Button;
            state.TodayLabel = row.FindName("ReminderRowTodayLabel") as TextBlock;
            state.CompleteLabel = row.FindName("ReminderRowCompleteLabel") as TextBlock;
            state.TodayGlyph = row.FindName("ReminderRowTodayGlyph") as FontIcon;
        }
        return state;
    }

    private void PrepareRowActions(Grid row)
    {
        var state = State(row);
        var reminder = row.DataContext as ReminderItem;
        var (key, glyph) = reminder?.TodayAction switch
        {
            ReminderTodayAction.Add => ("RowAddToToday", "\uE787"),
            ReminderTodayAction.Undo => ("RowUndoToday", "\uE7A7"),
            ReminderTodayAction.Clear => ("RowMoveOutOfToday", "\uE787"),
            _ => (null, null)
        };
        if (state.TodayAction is { } today && state.TodayLabel is { } todayLabel)
        {
            today.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
            if (key is not null)
            {
                todayLabel.Text = L10n.Get(key);
                if (state.TodayGlyph is { } icon) icon.Glyph = glyph!;
                AutomationProperties.SetName(today, todayLabel.Text);
                ToolTipService.SetToolTip(today, todayLabel.Text);
            }
        }
        if (state.CompleteAction is { } complete && state.CompleteLabel is { } completeLabel)
        {
            var hasComplete = reminder?.CanMarkComplete == true;
            complete.Visibility = hasComplete ? Visibility.Visible : Visibility.Collapsed;
            if (hasComplete)
            {
                completeLabel.Text = L10n.Get("RowMarkComplete");
                AutomationProperties.SetName(complete, completeLabel.Text);
                ToolTipService.SetToolTip(complete, completeLabel.Text);
            }
        }
    }

    private void ApplyRowOffset(Grid row, bool animate)
    {
        var state = State(row);
        if (state.Body is null || state.Shift is null || state.Actions is null) return;
        var offset = state.Gesture.Offset;
        var shown = Math.Abs(offset) > 0.5;
        state.Actions.IsHitTestVisible = shown;
        // The action strip is only visible in the gap the body has opened: the clip keeps the
        // accent colour from showing through the transparent row body during a partial swipe.
        var width = state.Actions.ActualWidth;
        var height = state.Actions.ActualHeight;
        state.Actions.Clip = new RectangleGeometry
        {
            Rect = !shown
                ? new Rect(0, 0, 0, 0)
                : offset < 0
                    ? new Rect(Math.Max(0, width + offset), 0, Math.Min(width, -offset), height)
                    : new Rect(0, 0, Math.Min(width, offset), height)
        };
        if (animate) AnimateRowShift(state, offset);
        else
        {
            state.Animation?.Stop();
            state.Animation = null;
            state.Shift.X = offset;
        }
        if (shown) _revealedGestureRow = row;
        else if (ReferenceEquals(_revealedGestureRow, row)) _revealedGestureRow = null;
    }

    private static void AnimateRowShift(RowGesture state, double offset)
    {
        if (state.Shift is null) return;
        state.Animation?.Stop();
        var animation = new DoubleAnimation
        {
            To = offset,
            Duration = TimeSpan.FromMilliseconds(140),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, state.Shift);
        Storyboard.SetTargetProperty(animation, "X");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        state.Animation = storyboard;
        storyboard.Begin();
    }

    private void ClearActiveGesture(Grid row)
    {
        if (ReferenceEquals(_activeGestureRow, row)) _activeGestureRow = null;
        RestoreRowManipulation(row);
        if (!_rowGestures.TryGetValue(row, out var state)) return;
        state.PointerId = 0;
        if (!state.Captured) return;
        state.Captured = false;
        row.ReleasePointerCaptures();
    }

    private void ResetRowGesture(Grid row)
    {
        _rowHoldTimer?.Stop();
        if (ReferenceEquals(_revealedGestureRow, row)) _revealedGestureRow = null;
        if (ReferenceEquals(_activeGestureRow, row)) _activeGestureRow = null;
        if (_mouseRowPress?.Row == row) _mouseRowPress = null;
        if (!_rowGestures.TryGetValue(row, out var state)) return;
        state.Gesture.Reset();
        RestoreRowManipulation(row);
        state.Animation?.Stop();
        state.Animation = null;
        state.PointerId = 0;
        state.Pointer = null;
        state.ManipulationActive = false;
        state.VelocityY = 0;
        if (state.Body is not null) state.Body.Opacity = 1;
        if (state.Shift is not null) state.Shift.X = 0;
        if (state.Actions is not null)
        {
            state.Actions.IsHitTestVisible = false;
            state.Actions.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 0, 0) };
        }
        if (!state.Captured) return;
        state.Captured = false;
        row.ReleasePointerCaptures();
    }

    private void ResetReminderGestures()
    {
        _rowHoldTimer?.Stop();
        _activeGestureRow = null;
        _revealedGestureRow = null;
        _mouseRowPress = null;
        if (ReminderList.ItemsPanelRoot is not { } panel) return;
        foreach (var container in panel.Children.OfType<ListViewItem>())
            if (container.ContentTemplateRoot is Grid row) ResetRowGesture(row);
    }

    private void ShowReminderRowMenu(Grid row, Point position)
    {
        if (row.DataContext is not ReminderItem reminder) return;
        if (BuildReminderMenu(reminder) is not { } actions) return;
        ResetRowGesture(row);
        if (position.X != 0 || position.Y != 0)
            actions.ShowAt(row, new FlyoutShowOptions { Position = position });
        else actions.ShowAt(row);
    }

    private static bool IsRowButton(DependencyObject? source, Grid row)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, row); node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return true;
        return false;
    }

    private static string ReminderName(FrameworkElement? element) =>
        element?.DataContext is ReminderItem reminder ? reminder.Id : "-";

    /// <summary>
    /// The same routed event can reach the row and the list fallback; only the first one acts on it.
    /// </summary>
    private bool Claim(PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(_lastGestureEvent, e)) return false;
        _lastGestureEvent = e;
        return true;
    }
}
