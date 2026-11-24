using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class NativeScheduleWriteChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        const string reminderId = "Reminder/timed";
        const string oldAlarm = "Alarm/old";
        const string oldTrigger = "AlarmTrigger/old";
        const string locationAlarm = "Alarm/location";
        const string locationTrigger = "AlarmTrigger/location";
        const string scheduleId = "23310000-0000-4000-8000-000000000001";
        const string newAlarm = "Alarm/" + scheduleId;
        const string newTrigger = "AlarmTrigger/" + scheduleId;
        JsonObject Record(string id, JsonObject fields) =>
            new() { ["recordName"] = id, ["recordType"] = id.Split('/')[0], ["recordChangeTag"] = "old", ["fields"] = fields };
        var records = new Dictionary<string, JsonObject>
        {
            [reminderId] = Record(reminderId, new()
            {
                ["DueDate"] = CloudKit.Timestamp(TimeUtil.Parse("2026-11-28T01:00:00Z").ToUnixTimeMilliseconds()),
                ["AllDay"] = CloudKit.Number(0), ["TimeZone"] = CloudKit.String("Asia/Shanghai"),
                ["AlarmIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "old", "location" } }),
                ["ResolutionTokenMap"] = CloudKit.String("{\"map\":{\"dueDate\":{\"counter\":4,\"modificationTime\":44.5}}}")
            }),
            [oldAlarm] = Record(oldAlarm, new() { ["Reminder"] = CloudKit.Reference(reminderId), ["TriggerID"] = CloudKit.String("old") }),
            [oldTrigger] = Record(oldTrigger, new() { ["Alarm"] = CloudKit.Reference(oldAlarm), ["Type"] = CloudKit.String("Date") }),
            [locationAlarm] = Record(locationAlarm, new() { ["Reminder"] = CloudKit.Reference(reminderId), ["TriggerID"] = CloudKit.String("location") }),
            [locationTrigger] = Record(locationTrigger, new() { ["Alarm"] = CloudKit.Reference(locationAlarm), ["Type"] = CloudKit.String("Location") })
        };
        var writes = 0;
        var loseReply = true;
        var secrets = new SecretStore(new MemorySecrets());
        secrets.Session("test@example.com", J.Node(new { session_token = "mock" }).ToJsonString());
        static HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
        {
            var body = J.Parse(await request.Content!.ReadAsStringAsync())!;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", appleId = "test@example.com", hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
            if (path.EndsWith("/records/lookup")) return Reply(new JsonObject { ["records"] = new JsonArray(body.Array("records")
                .Select(row => records.TryGetValue(row!.Required("recordName"), out var found) ? found.DeepClone()
                    : J.Node(new { recordName = row.Text("recordName"), serverErrorCode = "NOT_FOUND" })).ToArray()) });
            if (!path.EndsWith("/records/modify")) throw new InvalidOperationException("Unexpected endpoint");
            check(body.Flag("atomic"), "date trigger and reminder are written atomically");
            writes++;
            var results = new JsonArray();
            foreach (var operation in body.Array("operations"))
            {
                var supplied = operation!["record"]!;
                var id = supplied.Required("recordName");
                if (operation.Text("operationType") == "create")
                {
                    check(!records.ContainsKey(id), "schedule retry does not create a duplicate record");
                    records[id] = supplied.DeepClone().AsObject();
                }
                else
                {
                    check(supplied.Text("recordChangeTag") == records[id].Text("recordChangeTag"), "schedule writes retain record change tags");
                    foreach (var field in supplied["fields"]!.AsObject()) records[id]["fields"]![field.Key] = field.Value?.DeepClone();
                }
                records[id]["recordChangeTag"] = writes.ToString();
                results.Add(records[id].DeepClone());
            }
            if (loseReply) { loseReply = false; throw new HttpRequestException("Lost schedule reply"); }
            return Reply(new JsonObject { ["records"] = results });
        }));
        check(await client.Restore(default), "native schedule fixture signs in");
        var patch = J.Node(new { due_date = "2026-11-28T02:45:00Z", all_day = false, schedule_id = scheduleId }).AsObject();
        try { await client.Update(reminderId, patch, "old", default); check(false, "lost date edit reply is retryable"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "native date edit simulated a lost response"); }
        check(records.ContainsKey(newAlarm) && records.ContainsKey(newTrigger), "same-day edit creates the linked Apple date alarm and trigger");
        var saved = await client.Update(reminderId, patch, "old", default);
        check(writes == 1 && TimeUtil.Parse(saved.Required("due_date")) == TimeUtil.Parse("2026-11-28T02:45:00Z"), "retry recognizes a committed date edit");
        check(CloudKit.Field(records[reminderId], "AlarmIDs")!.AsArray().Select(entry => entry!.ToString()).SequenceEqual([scheduleId, "location"]),
            "reminder points to the new date alarm and preserves the location alarm");
        check(CloudKit.Int(records[oldAlarm], "Deleted") == 1 && CloudKit.Int(records[oldTrigger], "Deleted") == 1
            && records[locationAlarm].Text("recordChangeTag") == "old" && records[locationTrigger].Text("recordChangeTag") == "old",
            "only the superseded date alarm and trigger are retired");
        check(records[newAlarm]["parent"].Text("recordName") == reminderId && records[newTrigger]["parent"].Text("recordName") == newAlarm
            && CloudKit.Text(records[newAlarm], "TriggerID") == scheduleId && CloudKit.Field(records[newTrigger], "Alarm").Text("recordName") == newAlarm,
            "the linked records use Apple's reminder and alarm hierarchy");
        var components = J.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(CloudKit.Text(records[newTrigger], "DateComponentsData")!)));
        check(components.Number("hour") == 10 && components.Number("minute") == 45 && components.Number("day") == 28
            && components?["timeZone"].Text("identifier") == "Asia/Shanghai", "the trigger stores the precise wall time in the reminder's zone");
        var cleared = await client.Update(reminderId, J.Node(new { due_date = (string?)null, all_day = true }).AsObject(), saved.Text("change_tag"), default);
        check(cleared.Text("due_date") is null && CloudKit.Int(records[newTrigger], "Deleted") == 1
            && CloudKit.Field(records[reminderId], "AlarmIDs")!.AsArray().Single()!.ToString() == "location", "clearing the date retires its date trigger and keeps the location alarm");
    }
}
