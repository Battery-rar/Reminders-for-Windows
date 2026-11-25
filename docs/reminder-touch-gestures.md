# Reminder touch gestures

A reminder row owns exactly one touch contact. The row decides whether that
contact becomes a swipe, a long press, a drag or a scroll, and every other
feature consumes the decision instead of competing for the same finger.

## Why the native swipe control is gone

`SwipeControl` calls `VisualInteractionSource.TryRedirectForManipulation` on
every touch press. The contact belongs to the compositor from that moment, so a
held finger still produced compositor motion (the reported "hold still reveals
the buttons" bug), `Close()` during the interaction did not stop it, and no
code could decide between swiping, holding and dragging. The public API offers
no way to arm the native swipe only after a horizontal move is confirmed.

Raw pointer moves cannot replace it either, and the device log from 1.1.19
shows why: `press` is followed by `Touch Pending ended ... moves=0`. The row
gets the contact down, then Direct Manipulation claims the pan before the first
`PointerMoved` arrives, so nothing horizontal can be measured at all. The row
therefore claims horizontal pans with `ManipulationMode="TranslateX,System"`:
the element's own recognizer reports X translations, and the `System` flag
leaves vertical panning to the list. Swipe offsets come from
`ManipulationDelta.Cumulative.Translation.X`.

The state machine in `src/Reminders.WinUI/Services/ReminderTouchGesture.cs`
still holds every decision and has no WinUI dependency, so the rules are
covered by `tests/Reminders.Core.Tests/ReminderTouchGestureChecks.cs`.

## States

| Contact | Result |
| --- | --- |
| Framework reports a horizontal pan | Swipe: the row follows the finger; left reveals Complete, right reveals the Today action |
| Raw moves go vertical first | Scroll: the row leaves the contact to the list |
| Stays within 10 px for 450 ms | Long press: the row dims, does not move, and releasing opens the reminder menu |
| Long press, then moves 14 px in any direction | Drag from the whole row; the action buttons stay hidden |
| Releases a swipe past half the action width | The action stays revealed; a shorter swipe closes |

A pause inside a swipe is still a swipe, and a confirmed long press never turns
into one. A side without an action cannot be revealed, and a second finger
cannot restart an active contact. Cancellation, capture loss, recycling and
list refreshes reset the state and restore the reveal the contact started from.

At the 450 ms mark the row dims its body and takes the contact for itself: it switches
`ManipulationMode` to `TranslateX,TranslateY`, cancels the pending direct
manipulation and disables the list's vertical scrolling for the rest of that
contact. All three are undone when the contact ends. The dim is the feedback
that the hold was accepted; taking the axis is what lets a vertical move start
the drag instead of scrolling the list.

Revealed actions call the same commands as the context menu
(`SetReminderCompletionAsync`, `ChangeReminderTodayAsync`), so Today undo,
clearing the schedule and parent-only edits keep their existing behaviour.

## Dragging

The list no longer uses `CanDragItems`. The row calls
`UIElement.StartDragAsync` with the pointer point of the confirmed long-press
move, and for the mouse after 6 px of movement. `DragStarting` publishes one
internal token (`Battery-rar.Reminders.reminder-drag`) that both drop targets
accept:

- another reminder row: manual sort only, same visible parent, reuses
  `MoveReminderRelativeAsync` and the local order store;
- a sidebar list: reuses the existing move-to-list path.

Both targets validate the token, account epoch, current query and view before
touching data, and an invalid drop or a cancelled drag changes nothing.

## What is kept

Tapping still selects the row and opens the detail pane, vertical panning still
scrolls, and the checkbox, subtask chevron, mouse drag, keyboard access and the
context menu work as before. The order grabber and its resw entries were
removed; the whole row is the drag surface.

## Diagnosing a device gesture

The app writes one `[touch]` line per state change to
`%LOCALAPPDATA%\Apple Reminders on Windows\logs\app.log`. Run the demo build
(`Reminders.exe --demo`) and tail the file while testing:

```powershell
Get-Content "$env:LOCALAPPDATA\Apple Reminders on Windows\logs\app.log" -Tail 40 -Wait
```

```
[touch] press row=... x=... y=... today=144 complete=144
[touch] manipulation started row=... / manipulation completed row=... revealed=True
[touch] release Swipe row=... moves=21 offset=-144 menu=False
[touch] holding Started row=...
[touch] hold row=... captured=True
[touch] Pending->Hold row=... moves=1
[touch] release Hold row=... moves=2 offset=0 menu=True
[touch] Hold->Drag ... / drag start row=... / dragging row=... list=...
```

What the lines prove:

- **no `press` line**: the row never received the contact, so the list is
  consuming it before the row sees it;
- **`press` then `PointerCanceled`/`CaptureLost` with `moves=0`**: the
  compositor took the contact before the first move arrived, which is the
  1.1.19 failure;
- **`manipulation started` then an offset**: the framework handed the
  horizontal pan to the row, so the swipe path is live;
- **`manipulation started` missing but a swipe still follows the finger**: the
  raw-move fallback captured the contact before Direct Manipulation took it;
- **`hold ... captured=True mode=both scroll=True`**: the row took both axes and
  paused list scrolling for the rest of the contact;
- **`hold moved x=... y=...`**: a vertical or diagonal move after the hold
  reached the row, which starts the drag at 14 px;
- **`release Hold ... menu=True` with `offset=0`**: a stationary hold neither
  moved the row nor revealed an action;
- **`drag start` followed by `dragging`**: the OS accepted the drag and the
  token was published.

The parent `ListView` also forwards pointer moves, releases and cancellations
to the active row when a captured ancestor swallowed them, so a contact that
never reaches the row still completes instead of hanging.
