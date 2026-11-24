using System.Text.Json.Nodes;

namespace Reminders.Core;

public sealed partial class ReminderService
{
    private enum TodayScheduleChange { Add, Restore, Clear }

    private JsonNode ChangeTodaySchedule(JsonObject request, TodayScheduleChange action)
    {
        var requestedId = request.Required("id");
        var expectedTag = request.Optional("edit_base_tag", 2048);
        var (reminder, changed) = cache.Transaction(() =>
        {
            var id = cache.ResolveId(requestedId);
            var current = cache.Reminder(id) ?? throw CoreException.Bad("This task is no longer available.").WithPresentation("ErrorReminderUnavailable");
            if (current.Flag("completed") || current.Flag("deleted") ||
                (expectedTag is not null && expectedTag != current.Text("change_tag")))
                return (current, false);

            var zone = TimeUtil.Zone();
            var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
            var allDay = current.Flag("all_day");
            var previous = TimeUtil.LocalDue(current.Text("due_date"), allDay, zone);
            var scheduledToday = previous?.Date == today;
            var canRestore = current.Flag("can_undo_today");
            var schedule = action switch
            {
                TodayScheduleChange.Add when !scheduledToday => TodaySchedule(previous, allDay, today, zone),
                TodayScheduleChange.Restore when canRestore => cache.BeforeTodayMove(id),
                TodayScheduleChange.Clear when scheduledToday && !canRestore && current["recurrence"] is null =>
                    new JsonObject { ["due_date"] = null, ["all_day"] = false },
                _ => null
            };
            if (schedule is null) return (current, false);
            schedule["id"] = id;
            schedule["edit_base_tag"] = expectedTag;
            var saved = LocalChangeOne(schedule, "update").AsObject();
            if (action == TodayScheduleChange.Add) cache.RememberTodayMove(id, current, saved);
            // A changed schedule invalidates its history in the same SQLite transaction.
            return (cache.Reminder(id)!, true);
        });
        if (changed) QueueSync();
        return reminder;
    }

    private static JsonObject TodaySchedule(DateTime? previous, bool allDay, DateTime today, TimeZoneInfo zone)
    {
        if (previous is null || allDay)
            return new JsonObject { ["due_date"] = TimeUtil.CalendarDate(today), ["all_day"] = true };
        // Use the same wall time on today's date, resolving a daylight-saving
        // gap or overlap with the usual reminder time policy.
        var wallTime = previous.Value.TimeOfDay;
        var time = new TimeSpan(wallTime.Hours, wallTime.Minutes, wallTime.Seconds);
        return new JsonObject { ["due_date"] = TimeUtil.Iso(TimeUtil.Anchor(today.Add(time), zone)), ["all_day"] = false };
    }
}
