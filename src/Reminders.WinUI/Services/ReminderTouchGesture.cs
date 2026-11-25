namespace Reminders.Windows.Services;

/// <summary>The single gesture one touch contact on a reminder row has settled on.</summary>
internal enum ReminderGesturePhase
{
    Idle,
    Pending,
    Swipe,
    Scroll,
    Hold,
    Drag
}

/// <summary>
/// Arbitration for one touch contact on a reminder row: a direct horizontal swipe reveals the row
/// actions, a stationary press becomes a long press, and only a long press that keeps moving starts
/// a drag. The caller feeds positions and timestamps; every decision stays here so it can be tested
/// without WinUI or a real touch screen.
/// </summary>
internal sealed class ReminderTouchGesture
{
    /// <summary>Horizontal travel that confirms a swipe.</summary>
    internal const double SwipeSlop = 12;
    /// <summary>Drift a contact may keep and still count as a stationary long press.</summary>
    internal const double HoldSlop = 10;
    /// <summary>Travel after a confirmed long press that starts a drag instead of a swipe.</summary>
    internal const double DragSlop = 14;
    internal const long HoldDelayMilliseconds = 450;
    /// <summary>Width of the action strip a swipe reveals.</summary>
    internal const double ActionWidth = 144;

    private double _originX;
    private double _originY;
    private double _drift;
    private double _from;
    private double _left;
    private double _right;
    private double _offset;
    private long _pressedAt;
    private ReminderGesturePhase _phase;

    internal ReminderGesturePhase Phase => _phase;

    /// <summary>Horizontal distance the row body is shifted; negative reveals the right-hand action.</summary>
    internal double Offset => _offset;

    internal bool TodayRevealed => _offset > 0.5;

    internal bool CompleteRevealed => _offset < -0.5;

    /// <summary>Number of move events this contact delivered; reported when the contact ends.</summary>
    internal int Moves { get; private set; }

    internal void Press(double x, double y, long now, double todayActionWidth, double completeActionWidth)
    {
        if (_phase != ReminderGesturePhase.Idle) return;
        _originX = x;
        _originY = y;
        _pressedAt = now;
        _from = _offset;
        _left = Math.Max(0, todayActionWidth);
        _right = Math.Max(0, completeActionWidth);
        _drift = 0;
        Moves = 0;
        _phase = ReminderGesturePhase.Pending;
    }

    /// <summary>Returns true when the contact has turned into a drag.</summary>
    internal bool Move(double x, double y, long now)
    {
        if (_phase is ReminderGesturePhase.Idle or ReminderGesturePhase.Scroll or ReminderGesturePhase.Drag) return false;
        Moves++;
        var dx = x - _originX;
        var dy = y - _originY;
        var drift = Math.Max(Math.Abs(dx), Math.Abs(dy));

        if (_phase == ReminderGesturePhase.Pending)
        {
            // The long press is judged on the drift before this frame, so a finger that stayed still
            // and only then moves starts a drag instead of being read as a late swipe.
            if (now - _pressedAt >= HoldDelayMilliseconds && _drift <= HoldSlop)
            {
                _phase = ReminderGesturePhase.Hold;
            }
            else if (Math.Abs(dx) >= SwipeSlop && Math.Abs(dx) > Math.Abs(dy) &&
                     (dx < 0 ? _right > 0 : _left > 0))
            {
                _phase = ReminderGesturePhase.Swipe;
            }
            else if (Math.Abs(dy) >= SwipeSlop)
            {
                _phase = ReminderGesturePhase.Scroll;
                _drift = Math.Max(_drift, drift);
                return false;
            }
            else
            {
                _drift = Math.Max(_drift, drift);
                return false;
            }
        }

        _drift = Math.Max(_drift, drift);
        if (_phase == ReminderGesturePhase.Swipe)
        {
            _offset = ClampSwipe(_from + dx);
            return false;
        }
        if (_phase == ReminderGesturePhase.Hold && drift >= DragSlop)
        {
            _phase = ReminderGesturePhase.Drag;
            return true;
        }
        return false;
    }

    /// <summary>Ends the contact. Returns true when releasing a long press should open the menu.</summary>
    internal bool Release(long now)
    {
        if (_phase == ReminderGesturePhase.Idle) return false;
        if (_phase == ReminderGesturePhase.Pending && now - _pressedAt >= HoldDelayMilliseconds && _drift <= HoldSlop)
            _phase = ReminderGesturePhase.Hold;

        var menu = _phase == ReminderGesturePhase.Hold;
        if (_phase is ReminderGesturePhase.Pending or ReminderGesturePhase.Swipe) Settle();
        _phase = ReminderGesturePhase.Idle;
        return menu;
    }

    /// <summary>
    /// Moves a stationary contact into the long press once the delay has passed. The caller runs this
    /// on a timer so the row can show that the hold was accepted before the finger moves again.
    /// </summary>
    internal bool TryBeginHold(long now)
    {
        if (_phase != ReminderGesturePhase.Pending) return false;
        if (now - _pressedAt < HoldDelayMilliseconds || _drift > HoldSlop) return false;
        _phase = ReminderGesturePhase.Hold;
        return true;
    }

    /// <summary>
    /// Accepts the horizontal pan the framework recognised. Direct Manipulation claims every touch pan
    /// before raw pointer moves arrive, so the swipe normally starts here instead of in <see cref="Move"/>.
    /// </summary>
    internal bool BeginSwipe()
    {
        if (_phase != ReminderGesturePhase.Pending) return false;
        _phase = ReminderGesturePhase.Swipe;
        return true;
    }

    internal void SwipeTo(double translation)
    {
        if (_phase != ReminderGesturePhase.Swipe) return;
        _offset = ClampSwipe(_from + translation);
    }

    /// <summary>Ends a framework swipe and reports whether an action stays revealed.</summary>
    internal bool EndSwipe()
    {
        if (_phase != ReminderGesturePhase.Swipe) return false;
        Settle();
        _phase = ReminderGesturePhase.Idle;
        return _offset != 0;
    }

    /// <summary>Abandons the contact and restores the reveal it started from.</summary>
    internal void Cancel()
    {
        _phase = ReminderGesturePhase.Idle;
        _offset = _from;
    }

    internal void Reset()
    {
        _phase = ReminderGesturePhase.Idle;
        _offset = 0;
        _from = 0;
        _drift = 0;
        Moves = 0;
    }

    private void Settle()
    {
        if (_offset > 0) _offset = _offset >= _left / 2 ? _left : 0;
        else if (_offset < 0) _offset = -_offset >= _right / 2 ? -_right : 0;
    }

    private double ClampSwipe(double value) => _from > 0.5
        ? Math.Clamp(value, 0, _left)
        : _from < -0.5
            ? Math.Clamp(value, -_right, 0)
            : Math.Clamp(value, -_right, _left);
}
