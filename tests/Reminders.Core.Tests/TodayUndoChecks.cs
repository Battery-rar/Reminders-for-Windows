using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Reminders.Core;

internal static class TodayUndoChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-today-undo-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "cache.db");
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeUtil.Zone()).Date;
        var originalInstant = TimeUtil.Anchor(today.AddDays(2).AddHours(13).AddMinutes(21).AddSeconds(17).AddMilliseconds(348), TimeUtil.Zone());
        var original = TimeUtil.Iso(originalInstant);
        var moved = TimeUtil.Iso(TimeUtil.Anchor(today.AddHours(13).AddMinutes(21).AddSeconds(17), TimeUtil.Zone()));
        var allDayDate = TimeUtil.CalendarDate(today.AddDays(3));
        var todayDate = TimeUtil.CalendarDate(today);
        async Task<JsonNode> Call(ReminderService service, string method, object p) =>
            J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(p))).GetRawText())!;
        Task<JsonNode> Read(ReminderService service, string id) => Call(service, "reminder", new { id });
        Task<JsonNode> Move(ReminderService service, string id, string tag = "base") =>
            Call(service, "move_reminder_to_today", new { id, edit_base_tag = tag });
        Task<JsonNode> Undo(ReminderService service, string id, string tag = "base") =>
            Call(service, "undo_reminder_today", new { id, edit_base_tag = tag });
        try
        {
            using (var seed = new Cache(path))
            {
                void Add(string id, string? due_date, bool all_day = false, string? parent_id = null) =>
                    seed.Upsert(J.Node(new { id, due_date, all_day, parent_id, change_tag = "base", title = id, list_id = "list" }));
                Add("parent", original);
                Add("child", allDayDate, true, "parent");
                Add("undated", null);
                Add("native-today", todayDate, true);
                Add("manual-change", allDayDate, true);
                Add("remote-change", allDayDate, true);
                Add("Reminder/wire", original);
            }
            var secrets = new MemorySecrets();
            await using (var service = new ReminderService(root, secrets))
            {
                check((await Move(service, "parent", "stale")).Text("due_date") == original, "stale Today action keeps the current schedule");
                var target = await Move(service, "parent");
                check(target.Text("due_date") == moved && !target.Flag("all_day") && target.Flag("can_undo_today"), "Today move preserves local clock time and enables undo");
                check((await Read(service, "child")).Text("due_date") == allDayDate, "moving a parent only changes its schedule");
                var repeated = await Move(service, "parent");
                check(repeated.Text("due_date") == moved && repeated.Flag("can_undo_today"), "a duplicate Today move keeps the original snapshot");
                var unscheduled = await Move(service, "undated");
                check(unscheduled.Text("due_date") == todayDate && unscheduled.Flag("all_day") && unscheduled.Flag("can_undo_today"), "an undated reminder moves to an all-day date");
                check(!(await Undo(service, "native-today")).Flag("can_undo_today"), "an originally due-today reminder has no undo");
                check((await Read(service, "native-today")).Text("due_date") == todayDate, "undo without history cannot clear an original Today schedule");
                await Call(service, "update_reminder", new { id = "parent", description = "retained note" });
                check((await Read(service, "parent")).Flag("can_undo_today"), "an unrelated edit leaves the saved schedule available");
                await Move(service, "manual-change");
                await Call(service, "update_reminder", new { id = "manual-change", due_date = allDayDate });
                await Call(service, "update_reminder", new { id = "manual-change", due_date = todayDate });
                check(!(await Undo(service, "manual-change")).Flag("can_undo_today") &&
                    (await Read(service, "manual-change")).Text("due_date") == todayDate, "manually changing the schedule permanently invalidates an old snapshot");
                await Move(service, "remote-change");
                await Move(service, "Reminder/wire");
            }
            using (var cache = new Cache(path))
            {
                cache.ReplaceId("undated", "renamed");
                var pending = cache.Pending().Single(row => row.Text("reminder_id") == "remote-change");
                cache.Acknowledge(pending.Number("seq"), "remote-change", "acknowledged");
                cache.Upsert(J.Node(new { id = "remote-change", due_date = todayDate, all_day = true, title = "remote change", list_id = "list", change_tag = "acknowledged" }));
                check(cache.Reminder("remote-change").Flag("can_undo_today"), "acknowledging the same applied schedule keeps undo");
                cache.Upsert(J.Node(new { id = "remote-change", due_date = allDayDate, all_day = true, title = "remote change", list_id = "list", change_tag = "remote" }));
                cache.Upsert(J.Node(new { id = "remote-change", due_date = todayDate, all_day = true, title = "remote change", list_id = "list", change_tag = "remote-again" }));
                check(!cache.Reminder("remote-change").Flag("can_undo_today"), "changing the cloud schedule permanently invalidates an old snapshot");
                var wireEdit = cache.Pending().Single(row => row.Text("reminder_id") == "Reminder/wire");
                var wirePatch = J.Parse(wireEdit.Text("payload"))!.AsObject();
                var posted = ICloudClient.UpdateFields(new JsonObject(), wirePatch, setMissingTimeZone: true);
                var alternateUtc = DateTimeOffset.Parse(moved.Replace("Z", "+00:00", StringComparison.Ordinal));
                var postedRecord = new JsonObject { ["fields"] = posted };
                check(CloudKit.Field(postedRecord, "DueDate")!.GetValue<long>() == alternateUtc.ToUnixTimeMilliseconds() &&
                    wirePatch.Count == 2, "the normal upload serializes the exact move timestamp, with no local history data");
                var ack = new JsonObject
                {
                    ["recordName"] = "Reminder/wire", ["recordType"] = "Reminder", ["recordChangeTag"] = "acknowledged",
                    ["fields"] = new JsonObject
                    {
                        ["DueDate"] = CloudKit.Timestamp(alternateUtc.ToUnixTimeMilliseconds()),
                        ["AllDay"] = CloudKit.Number(0), ["List"] = CloudKit.Reference("list")
                    }
                };
                cache.Acknowledge(wireEdit.Number("seq"), "Reminder/wire", "acknowledged");
                cache.ApplyRecords(new JsonArray(ack));
                check(cache.Reminder("Reminder/wire").Text("due_date") == moved &&
                    cache.Reminder("Reminder/wire").Flag("can_undo_today"), "an epoch-millisecond CloudKit acknowledgement preserves timed undo");
            }
            await using (var resumed = new ReminderService(root, secrets))
            {
                var rows = await Call(resumed, "reminders", new { scope = "today" });
                check(rows.AsArray().Single(row => row.Text("id") == "parent").Flag("can_undo_today"), "Today rows advertise persisted undo after a restart");
                var restored = await Undo(resumed, "parent");
                check(restored.Text("due_date") == original && !restored.Flag("all_day") && !restored.Flag("can_undo_today") &&
                    restored.Text("description") == "retained note", "undo restores the exact UTC due date/time and leaves other fields intact");
                check((await Read(resumed, "child")).Text("due_date") == allDayDate, "undoing a parent leaves its child untouched");
                var cleared = await Undo(resumed, "undated");
                check(cleared.Text("id") == "renamed" && cleared["due_date"] is null && !cleared.Flag("all_day"), "undo follows the ID alias and restores the unset schedule");
                var child = await Move(resumed, "child");
                var childRestored = await Undo(resumed, "child");
                check(child.Flag("all_day") && childRestored.Flag("all_day") && childRestored.Text("due_date") == allDayDate, "undo preserves an all-day calendar date");
                check((await Undo(resumed, "Reminder/wire", "acknowledged")).Text("due_date") == original,
                    "undo restores the precise original time after the acknowledged move");
            }
            using var durable = new Cache(path);
            var parentChanges = durable.Pending().Where(row => row.Text("reminder_id") == "parent")
                .Select(row => J.Parse(row.Text("payload"))).ToArray();
            check(parentChanges.Length == 3 && parentChanges[0].Text("due_date") == moved &&
                parentChanges[2].Text("due_date") == original, "move and undo enqueue ordered exact due-date edits even while offline");
            var clearedSchedule = J.Parse(durable.Pending().Last(row => row.Text("reminder_id") == "renamed").Text("payload"));
            check(clearedSchedule is JsonObject patch && patch.ContainsKey("due_date") && patch["due_date"] is null &&
                !patch.Flag("all_day"), "undo enqueues an explicit unschedule operation for Apple");
            var wireUndo = J.Parse(durable.Pending().Single(row => row.Text("reminder_id") == "Reminder/wire").Text("payload"))!.AsObject();
            var undoFields = ICloudClient.UpdateFields(new JsonObject(), wireUndo);
            check(CloudKit.Field(new JsonObject { ["fields"] = undoFields }, "DueDate")!.GetValue<long>() == originalInstant.ToUnixTimeMilliseconds(),
                "the normal upload sends the original epoch-millisecond time on undo");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
