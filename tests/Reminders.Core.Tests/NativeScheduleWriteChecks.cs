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
        const string noAlarmReminderId = "Reminder/previously-unscheduled";
        const string newScheduleId = "23310000-0000-4000-8000-000000000002";
        const string zoneOwner = "_native-fixture-owner";
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
            [locationTrigger] = Record(locationTrigger, new() { ["Alarm"] = CloudKit.Reference(locationAlarm), ["Type"] = CloudKit.String("Location") }),
            [noAlarmReminderId] = Record(noAlarmReminderId, new()
            {
                ["List"] = J.Node(new { type = "REFERENCE", value = new { recordName = "List/personal", action = "VALIDATE",
                    zoneID = new { zoneName = "Reminders", ownerRecordName = zoneOwner, zoneType = "REGULAR_CUSTOM_ZONE" } } }),
                ["AllDay"] = CloudKit.Number(0), ["TimeZone"] = CloudKit.String("Asia/Shanghai")
            })
        };
        var writes = 0;
        var loseReply = true;
        string? attemptedTrigger = null;
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
            var requestOperations = body.Array("operations");
            foreach (var create in requestOperations.Where(operation => operation.Text("operationType") == "create"))
            {
                var record = create!["record"]!;
                if (record.Text("recordType") == "Alarm")
                    check(record["fields"]?["CreationDate"] is null && record["fields"]?["LastModifiedDate"] is null,
                        "Alarm creation sends only fields supported by CloudKit");
                if (record.Text("recordType") == "AlarmTrigger")
                    check(record["parent"] is null && record["fields"]?["Imported"] is null && CloudKit.Field(record, "Alarm") is JsonObject,
                        "AlarmTrigger uses a validated Alarm reference without a nested record parent");
            }
            if (requestOperations.Any(operation => operation?["record"].Text("recordName") == noAlarmReminderId))
            {
                var alarm = requestOperations.Single(operation => operation?["record"].Text("recordType") == "Alarm")!["record"]!;
                var trigger = requestOperations.Single(operation => operation?["record"].Text("recordType") == "AlarmTrigger")!["record"]!;
                var alarmReference = CloudKit.Field(alarm, "Reminder");
                var triggerReference = CloudKit.Field(trigger, "Alarm");
                check(new[] { alarmReference, triggerReference }.All(reference => reference.Text("action") == "VALIDATE" &&
                    reference?["zoneID"] is JsonObject { Count: 2 } zone && zone.Text("zoneName") == "Reminders" && zone.Text("ownerRecordName") == zoneOwner),
                    "native date-alarm writes explicitly qualify both validated references with the reminder's zone");
                check(alarmReference.Text("recordName") == noAlarmReminderId && triggerReference.Text("recordName") == "Alarm/" + newScheduleId &&
                    trigger.Text("recordName") == "AlarmTrigger/" + CloudKit.Text(alarm, "TriggerID"),
                    "date-alarm relationships and TriggerID address the intended records");
                if (attemptedTrigger is null)
                {
                    attemptedTrigger = trigger.Required("recordName");
                    throw new HttpRequestException("Schedule request did not reach iCloud");
                }
                check(trigger.Text("recordName") == attemptedTrigger, "pre-commit retry uses the identical date trigger identity");
            }
            var results = new JsonArray();
            foreach (var operation in requestOperations)
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
        var triggerUid = CloudKit.Text(records[newAlarm], "TriggerID");
        var newTrigger = "AlarmTrigger/" + triggerUid;
        check(Guid.TryParse(triggerUid, out _) && triggerUid != scheduleId, "native date alarm and trigger use separate UUIDs");
        check(records.ContainsKey(newAlarm) && records.ContainsKey(newTrigger), "same-day edit creates the linked Apple date alarm and trigger");
        var saved = await client.Update(reminderId, patch, "old", default);
        check(writes == 1 && TimeUtil.Parse(saved.Required("due_date")) == TimeUtil.Parse("2026-11-28T02:45:00Z"), "retry recognizes a committed date edit");
        check(CloudKit.Field(records[reminderId], "AlarmIDs")!.AsArray().Select(entry => entry!.ToString()).SequenceEqual([scheduleId, "location"]),
            "reminder points to the new date alarm and preserves the location alarm");
        check(CloudKit.Int(records[oldAlarm], "Deleted") == 1 && CloudKit.Int(records[oldTrigger], "Deleted") == 1
            && records[locationAlarm].Text("recordChangeTag") == "old" && records[locationTrigger].Text("recordChangeTag") == "old",
            "only the superseded date alarm and trigger are retired");
        check(records[newAlarm]["parent"].Text("recordName") == reminderId && records[newTrigger]["parent"] is null
            && CloudKit.Field(records[newTrigger], "Alarm").Text("recordName") == newAlarm,
            "the records link the trigger to the alarm and the alarm to its reminder");
        var components = J.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(CloudKit.Text(records[newTrigger], "DateComponentsData")!)));
        check(components.Number("hour") == 10 && components.Number("minute") == 45 && components.Number("day") == 28
            && components?["timeZone"].Text("identifier") == "Asia/Shanghai", "the trigger stores the precise wall time in the reminder's zone");
        var cleared = await client.Update(reminderId, J.Node(new { due_date = (string?)null, all_day = true }).AsObject(), saved.Text("change_tag"), default);
        check(cleared.Text("due_date") is null && CloudKit.Int(records[newTrigger], "Deleted") == 1
            && CloudKit.Field(records[reminderId], "AlarmIDs")!.AsArray().Single()!.ToString() == "location", "clearing the date retires its date trigger and keeps the location alarm");
        var timed = J.Node(new { due_date = "2026-11-28T03:25:00Z", all_day = false, schedule_id = newScheduleId }).AsObject();
        try { await client.Update(noAlarmReminderId, timed, "old", default); check(false, "an undelivered date write is retryable"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "native date edit simulated a failed request before commit"); }
        var firstSchedule = await client.Update(noAlarmReminderId, timed, "old", default);
        check(TimeUtil.Parse(firstSchedule.Required("due_date")) == TimeUtil.Parse("2026-11-28T03:25:00Z"),
            "timed edit succeeds on a reminder without prior alarms");
    }
}
