using Reminders.Windows.Services;

internal static class ReminderTouchGestureChecks
{
    private const double Width = ReminderTouchGesture.ActionWidth;

    internal static void Run(Action<bool, string> check)
    {
        // A finger that stays put is a long press and must never expose the swipe buttons.
        var hold = new ReminderTouchGesture();
        hold.Press(200, 300, 0, Width, Width);
        hold.Move(202, 301, 200);
        hold.Move(204, 303, 300);
        check(hold.Phase == ReminderGesturePhase.Pending, "drift below the swipe slop keeps the contact undecided");
        check(hold.Offset == 0, "an undecided contact does not move the row");
        hold.Move(205, 304, 460);
        check(hold.Phase == ReminderGesturePhase.Hold, "a stationary contact becomes a long press after the hold delay");
        check(hold.Offset == 0, "a long press keeps the row actions hidden");
        check(hold.Release(700), "releasing a long press asks for the menu");
        check(hold.Phase == ReminderGesturePhase.Idle && hold.Offset == 0,
            "the long-press release leaves the row in place");

        var resting = new ReminderTouchGesture();
        resting.Press(0, 0, 0, Width, Width);
        resting.Move(1, 1, 120);
        resting.Move(3, 2, 240);
        resting.Move(4, 3, 900);
        check(resting.Phase == ReminderGesturePhase.Hold && resting.Offset == 0,
            "holding still for a second still never exposes the swipe buttons");
        check(resting.Release(1400), "a still finger opens the menu however long it is held");

        // A direct horizontal move takes the contact at once and follows the finger.
        var swipe = new ReminderTouchGesture();
        swipe.Press(300, 100, 0, Width, Width);
        swipe.Move(270, 102, 100);
        check(swipe.Phase == ReminderGesturePhase.Swipe, "an early horizontal move starts the swipe");
        check(Math.Abs(swipe.Offset + 30) < 0.001, "the row body follows the finger one to one");
        swipe.Move(280, 100, 1000);
        check(swipe.Phase == ReminderGesturePhase.Swipe, "pausing after a swipe stays a swipe");
        check(!swipe.Release(1200), "a swipe release does not open the menu");
        check(Math.Abs(swipe.Offset) < 0.001, "a swipe shorter than half the action width closes again");

        var reveal = new ReminderTouchGesture();
        reveal.Press(300, 100, 0, Width, Width);
        reveal.Move(200, 100, 80);
        reveal.Release(140);
        check(reveal.CompleteRevealed && !reveal.TodayRevealed,
            "a left swipe past halfway reveals only the complete action");
        check(Math.Abs(reveal.Offset + Width) < 0.001, "the revealed row settles at exactly one action width");

        var today = new ReminderTouchGesture();
        today.Press(0, 0, 0, Width, Width);
        today.Move(100, 0, 80);
        today.Release(120);
        check(today.TodayRevealed && Math.Abs(today.Offset - Width) < 0.001,
            "a right swipe past halfway reveals the today action");

        var oneSided = new ReminderTouchGesture();
        oneSided.Press(0, 0, 0, 0, Width);
        oneSided.Move(60, 0, 80);
        check(oneSided.Phase != ReminderGesturePhase.Swipe && oneSided.Offset == 0,
            "a row without a today action cannot be swiped to that side");
        oneSided.Release(120);

        var scroll = new ReminderTouchGesture();
        scroll.Press(0, 0, 0, Width, Width);
        scroll.Move(3, -40, 80);
        check(scroll.Phase == ReminderGesturePhase.Scroll, "an early vertical move is left to the list");
        check(!scroll.Release(200) && scroll.Offset == 0, "scrolling neither reveals actions nor opens the menu");

        // After a confirmed long press every direction drags and nothing is revealed.
        foreach (var (dx, dy) in new[] { (-40, 0), (40, 0), (0, -40), (0, 40), (34, 34) })
        {
            var drag = new ReminderTouchGesture();
            drag.Press(100, 100, 0, Width, Width);
            drag.Move(101, 101, 460);
            check(drag.Phase == ReminderGesturePhase.Hold, "a still finger holds before any drag");
            check(drag.Move(100 + dx, 100 + dy, 520), "moving after a long press starts a drag");
            check(drag.Phase == ReminderGesturePhase.Drag, "the contact changes into a drag");
            check(drag.Offset == 0, "a drag never reveals the row actions");
            check(!drag.Release(600), "ending a drag does not open the menu");
            check(drag.Offset == 0, "a drag leaves the row in place");
        }

        var lateMove = new ReminderTouchGesture();
        lateMove.Press(0, 0, 0, Width, Width);
        lateMove.Move(1, 1, 460);
        check(!lateMove.Move(8, 4, 500) && lateMove.Phase == ReminderGesturePhase.Hold,
            "a small move after the hold is not yet a drag");
        check(lateMove.Release(520), "that contact still opens the menu");

        var single = new ReminderTouchGesture();
        single.Press(100, 100, 0, Width, Width);
        single.Press(0, 0, 0, Width, Width);
        single.Move(120, 100, 100);
        check(single.Phase == ReminderGesturePhase.Swipe && Math.Abs(single.Offset - 20) < 0.001,
            "a second press does not restart an active contact");
        single.Release(140);

        var reopened = new ReminderTouchGesture();
        reopened.Press(0, 0, 0, Width, Width);
        reopened.Move(-100, 0, 100);
        reopened.Release(120);
        check(Math.Abs(reopened.Offset + Width) < 0.001, "the complete action is revealed");
        reopened.Press(0, 0, 0, Width, Width);
        reopened.Move(40, 0, 100);
        check(Math.Abs(reopened.Offset + 104) < 0.001, "a new swipe continues from the revealed position");
        reopened.Cancel();
        check(Math.Abs(reopened.Offset + Width) < 0.001, "cancelling restores the reveal the contact started from");

        var bounded = new ReminderTouchGesture();
        bounded.Press(0, 0, 0, Width, Width);
        bounded.Move(-60, 0, 100);
        check(Math.Abs(bounded.Offset + 60) < 0.001, "a partial swipe follows the finger");
        bounded.Move(-400, 0, 140);
        check(Math.Abs(bounded.Offset + Width) < 0.001, "a swipe cannot pass the action width");
        bounded.Release(180);
        bounded.Press(0, 0, 0, Width, Width);
        bounded.Move(3, 0, 80);
        bounded.Release(120);
        check(Math.Abs(bounded.Offset + Width) < 0.001, "a small wobble on a revealed row keeps it in place");

        check(!new ReminderTouchGesture().Release(0), "a release without a press does nothing");

        // The framework's manipulation router drives the swipe; raw moves are the fallback.
        var framework = new ReminderTouchGesture();
        framework.Press(300, 100, 0, Width, Width);
        check(framework.Move(298, 101, 40) == false && framework.Phase == ReminderGesturePhase.Pending,
            "a few pixels of drift stay undecided until the framework reports a pan");
        check(framework.BeginSwipe(), "the role of the recognizer starts the swipe before the hold delay");
        framework.SwipeTo(-40);
        check(Math.Abs(framework.Offset + 40) < 0.001, "the framework translation moves the row one to one");
        framework.SwipeTo(-90);
        check(Math.Abs(framework.Offset + 90) < 0.001, "the row keeps following the framework translation");
        framework.SwipeTo(-400);
        check(Math.Abs(framework.Offset + Width) < 0.001, "a framework swipe stops at the action width");
        check(framework.EndSwipe() && framework.Phase == ReminderGesturePhase.Idle,
            "ending a long framework swipe keeps the action revealed");

        var frameworkHold = new ReminderTouchGesture();
        frameworkHold.Press(0, 0, 0, Width, Width);
        check(frameworkHold.TryBeginHold(449) == false, "the hold needs the full delay");
        check(frameworkHold.TryBeginHold(450), "a still contact holds at the delay");
        check(frameworkHold.BeginSwipe() == false, "a confirmed hold no longer accepts a horizontal pan");
        check(frameworkHold.Move(0, -30, 470), "that hold can still start a drag downward");

        var driftedHold = new ReminderTouchGesture();
        driftedHold.Press(0, 0, 0, Width, Width);
        driftedHold.Move(8, 9, 100);
        check(driftedHold.TryBeginHold(460), "drift inside the hold tolerance still holds");
        var tooFar = new ReminderTouchGesture();
        tooFar.Press(0, 0, 0, Width, Width);
        tooFar.Move(11, 0, 200);
        check(tooFar.TryBeginHold(460) == false, "drift beyond the hold tolerance never becomes a hold");

        var holdThenSwipe = new ReminderTouchGesture();
        holdThenSwipe.Press(0, 0, 0, Width, Width);
        holdThenSwipe.Move(1, 1, 460);
        check(holdThenSwipe.Phase == ReminderGesturePhase.Hold, "a still contact is held before it moves");
        check(holdThenSwipe.BeginSwipe() == false && holdThenSwipe.Offset == 0,
            "a hold that moves horizontally drags instead of revealing actions");
        check(ReminderTouchGesture.SwipeSlop < ReminderTouchGesture.DragSlop,
            "a swipe is decided before a drag travel is reached");
        check(ReminderTouchGesture.HoldDelayMilliseconds == 450, "the long press matches the tested hold delay");
    }
}
