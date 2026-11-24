using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;
using Reminders.Windows.Services;

internal static class ReminderEditPatchChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-edit-patch-" + Guid.NewGuid().ToString("N"));
        async Task<JsonNode> Call(ReminderService service, string method, object parameters) =>
            J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
        try
        {
            await using var service = new ReminderService(root, new MemorySecrets());
            var row = await Call(service, "create_reminder", new { list_id = "a", title = "Original", description = "Original notes" });
            var id = row.Required("id");
            var baseline = J.Node(new { Title = "Original", Notes = "Original notes", List = "a", Date = (string?)null,
                Time = (string?)null, AllDay = false, Flagged = false, Priority = 0, Repeat = 0, Interval = (string?)null }).AsObject();
            var values = new Dictionary<string, object?> { ["title"] = "My new title", ["description"] = "Original notes", ["list_id"] = "a",
                ["due_date"] = null, ["all_day"] = false, ["flagged"] = false, ["priority"] = 0, ["recurrence"] = null };
            // Another device's newer values arrived while the title editor was open.
            await Call(service, "update_reminder", new { id, description = "Newer notes", flagged = true, priority = 1 });
            var titleDraft = baseline.DeepClone().AsObject(); titleDraft["Title"] = "My new title";
            await Call(service, "update_reminder", ReminderEditPatch.Build(id, baseline.ToJsonString(), titleDraft.ToJsonString(), values));
            var saved = await Call(service, "reminder", new { id });
            check(saved.Text("title") == "My new title" && saved.Text("description") == "Newer notes", "editing a title preserves notes updated while the editor was open");
            check(saved.Flag("flagged") && saved.Number("priority") == 1, "untouched flag and priority values retain concurrent changes");
            await Call(service, "update_reminder", ReminderEditPatch.Build(id, baseline.ToJsonString(), baseline.ToJsonString(), values));
            check((await Call(service, "reminder", new { id })).Text("title") == "My new title", "an unchanged editor cannot write stale values back to the cache");
            var dated = baseline.DeepClone().AsObject(); dated["Date"] = "2026-11-03"; dated["Time"] = "09:00:00";
            values["due_date"] = "2026-11-03T14:00:00Z";
            await Call(service, "update_reminder", new { id, all_day = true });
            await Call(service, "update_reminder", ReminderEditPatch.Build(id, baseline.ToJsonString(), dated.ToJsonString(), values));
            saved = await Call(service, "reminder", new { id });
            check(!saved.Flag("all_day") && DateTimeOffset.Parse(saved.Required("due_date"), System.Globalization.CultureInfo.InvariantCulture) ==
                new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero), "an explicit timed-date edit sends its all-day state with the date");

            var date = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
            var newTime = new TimeSpan(10, 45, 0);
            var timeOnly = dated.DeepClone().AsObject();
            timeOnly["Time"] = "10:45:00";
            values["due_date"] = ReminderSchedule.Format(date, allDay: false, newTime);
            var patch = ReminderEditPatch.Build(id, dated.ToJsonString(), timeOnly.ToJsonString(), values);
            check(patch.ContainsKey("due_date") && patch.Count == 3, "changing the clock on the same date creates only a schedule patch");
            saved = await Call(service, "update_reminder", patch);
            var wall = date.Date.Add(newTime);
            var expectedTimestamp = new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)).ToUnixTimeMilliseconds();
            var record = new JsonObject { ["fields"] = new JsonObject
            {
                ["DueDate"] = CloudKit.Timestamp(new DateTimeOffset(wall.AddHours(-1), TimeZoneInfo.Local.GetUtcOffset(wall)).ToUnixTimeMilliseconds()),
                ["AllDay"] = CloudKit.Number(0), ["TimeZone"] = CloudKit.String("Asia/Shanghai")
            } };
            var fields = ICloudClient.UpdateFields(record, J.Node(new { due_date = saved.Required("due_date"), all_day = saved.Flag("all_day") }).AsObject());
            check(CloudKit.Field(new JsonObject { ["fields"] = fields }, "DueDate")!.GetValue<long>() == expectedTimestamp,
                "a same-day time edit reaches the outgoing CloudKit timestamp as the selected local time");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
