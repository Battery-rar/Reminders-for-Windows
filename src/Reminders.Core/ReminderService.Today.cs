using System.Text.Json.Nodes;

namespace Reminders.Core;

public sealed partial class ReminderService
{
    private JsonNode ChangeTodaySchedule(JsonObject request, bool undo)
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

            var schedule = undo
                ? current.Flag("can_undo_today") ? cache.BeforeTodayMove(id) : null
                : TodaySchedule(current);
            if (schedule is null) return (current, false);
            schedule["id"] = id;
            schedule["edit_base_tag"] = expectedTag;
            var saved = LocalChangeOne(schedule, "update").AsObject();
            if (!undo) cache.RememberTodayMove(id, current, saved);
            // A changed schedule invalidates its history in the same SQLite transaction.
            return (cache.Reminder(id)!, true);
        });
        if (changed) QueueSync();
        return reminder;
    }

    private static JsonObject? TodaySchedule(JsonObject reminder)
    {
        var zone = TimeUtil.Zone();
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        var allDay = reminder.Flag("all_day");
        var previous = TimeUtil.LocalDue(reminder.Text("due_date"), allDay, zone);
        if (previous?.Date == today) return null;
        if (previous is null || allDay)
            return new JsonObject { ["due_date"] = TimeUtil.CalendarDate(today), ["all_day"] = true };
        // Use the same wall time on today's date, resolving a daylight-saving
        // gap or overlap with the usual reminder time policy.
        var wallTime = previous.Value.TimeOfDay;
        var time = new TimeSpan(wallTime.Hours, wallTime.Minutes, wallTime.Seconds);
        return new JsonObject { ["due_date"] = TimeUtil.Iso(TimeUtil.Anchor(today.Add(time), zone)), ["all_day"] = false };
    }
}
