using System.Text;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class ICloudClient
{
    private const string AlarmPrefix = "Alarm/";
    private const string TriggerPrefix = "AlarmTrigger/";

    private static string? ScheduleIdentity(JsonObject patch) => patch.ContainsKey("due_date")
        && Guid.TryParse(patch.Text("schedule_id"), out var identity) ? identity.ToString().ToUpperInvariant() : null;

    private static JsonNode? DueDateToken(JsonNode record) =>
        J.Parse(CloudKit.Text(record, "ResolutionTokenMap"))?["map"]?["dueDate"];

    private async Task AddScheduleOperations(string reminderId, JsonNode original, JsonObject patch, JsonObject fields,
        JsonArray operations, CancellationToken token)
    {
        if (!patch.ContainsKey("due_date")) return;

        var identity = ScheduleIdentity(patch);
        if (identity is not null)
        {
            var tokens = J.Parse(fields["ResolutionTokenMap"].Text("value"))!;
            tokens["map"]!["dueDate"]!["replicaID"] = identity;
            fields["ResolutionTokenMap"] = CloudKit.String(tokens.ToJsonString());
        }

        var refs = CloudKit.Field(original, "AlarmIDs");
        if (refs is not null && refs is not JsonArray)
            throw CoreException.Bad("iCloud returned an unsupported alarm list for this reminder.").WithPresentation("ErrorReminderOperationRefused");
        var previous = (refs as JsonArray ?? new JsonArray()).Select(value => value?.ToString() ?? "").ToArray();
        var previousNames = previous.Select(id => RecordName(id, AlarmPrefix)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var retired = new HashSet<string>(StringComparer.Ordinal);

        if (previousNames.Length > 0)
        {
            var alarms = (await Cloud.LookupMany(previousNames, token, allowMissing: true)).OfType<JsonObject>()
                .Where(alarm => previousNames.Contains(alarm.Text("recordName"), StringComparer.Ordinal)
                    && alarm.Text("recordType") == "Alarm" && CloudKit.Int(alarm, "Deleted") == 0
                    && CloudKit.Field(alarm, "Reminder").Text("recordName") == reminderId)
                .ToArray();
            var triggerNames = alarms.Select(alarm => RecordName(CloudKit.Text(alarm, "TriggerID"), TriggerPrefix))
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (triggerNames.Length > 0)
            {
                var triggers = (await Cloud.LookupMany(triggerNames, token, allowMissing: true)).OfType<JsonObject>()
                    .Where(trigger => trigger.Text("recordType") == "AlarmTrigger" && CloudKit.Int(trigger, "Deleted") == 0
                        && CloudKit.Text(trigger, "Type") == "Date" && trigger.Text("recordChangeTag") is not null)
                    .ToLookup(trigger => trigger.Text("recordName"), StringComparer.Ordinal);
                foreach (var alarm in alarms)
                {
                    var alarmName = alarm.Required("recordName");
                    var trigger = triggers[RecordName(CloudKit.Text(alarm, "TriggerID"), TriggerPrefix)]
                        .FirstOrDefault(candidate => CloudKit.Field(candidate, "Alarm").Text("recordName") == alarmName);
                    if (trigger is null || alarm.Text("recordChangeTag") is not { } alarmTag) continue;
                    retired.Add(alarmName);
                    operations.Add(CloudKit.Operation("update", alarmName, alarmTag,
                        new() { ["Deleted"] = CloudKit.Number(1) }, "Alarm"));
                    operations.Add(CloudKit.Operation("update", trigger.Required("recordName"), trigger.Text("recordChangeTag"),
                        new() { ["Deleted"] = CloudKit.Number(1) }, "AlarmTrigger"));
                }
            }
        }

        var alarmIds = previous.Where(id => !retired.Contains(RecordName(id, AlarmPrefix) ?? "")).ToList();
        var allDay = patch.ContainsKey("all_day") ? patch.Flag("all_day") : CloudKit.Int(original, "AllDay") != 0;
        var instantField = fields["DueDate"]?["value"];
        if (!allDay && instantField is JsonValue value && value.TryGetValue<long>(out var milliseconds))
        {
            var alarmId = identity ?? Guid.NewGuid().ToString().ToUpperInvariant();
            // Move a data bit so the linked record has its own UUID and retries
            // of the same queued edit still address the same two records.
            var triggerBytes = Guid.Parse(alarmId).ToByteArray();
            triggerBytes[^1] ^= 0x80;
            var triggerId = new Guid(triggerBytes).ToString().ToUpperInvariant();
            var alarmName = AlarmPrefix + alarmId;
            var triggerName = TriggerPrefix + triggerId;
            var referenceZone = ScheduleReferenceZone(original);
            var zone = TimeUtil.Zone(fields["TimeZone"].Text("value") ?? CloudKit.Text(original, "TimeZone"));
            var zoneId = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
            var wall = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), zone);
            var components = J.Node(new
            {
                year = wall.Year, month = wall.Month, day = wall.Day, hour = wall.Hour,
                minute = wall.Minute, second = wall.Second, timeZone = new { identifier = zoneId }
            });
            var oldToken = DueDateToken(original);
            var modification = oldToken?["modificationTime"] is JsonValue time && time.TryGetValue<double>(out var seconds) ? seconds : 0;
            // CloudKit can infer these field types from the same values sent by Apple's web client.
            var nonce = oldToken.Number("counter") * 100_000_000_000d + modification;
            operations.Add(CloudKit.Operation("create", alarmName, null, new()
            {
                ["AlarmUID"] = CloudKit.String(alarmId), ["Reminder"] = ScheduleReference(reminderId, referenceZone),
                ["TriggerID"] = CloudKit.String(triggerId),
                ["Deleted"] = CloudKit.Number(0), ["Imported"] = CloudKit.Number(0),
                ["DueDateResolutionTokenAsNonce"] = new JsonObject { ["value"] = nonce }
            }, "Alarm", reminderId));
            // CloudKit rejects a nested record parent here; the validated Alarm reference is the link.
            operations.Add(CloudKit.Operation("create", triggerName, null, new()
            {
                ["Alarm"] = ScheduleReference(alarmName, referenceZone), ["Type"] = new JsonObject { ["value"] = "Date" },
                ["DateComponentsData"] = new JsonObject { ["value"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(components.ToJsonString())) },
                ["Deleted"] = CloudKit.Number(0)
            }, "AlarmTrigger"));
            alarmIds.Insert(0, alarmId);
        }
        if (alarmIds.Count != previous.Length || retired.Count > 0)
            fields["AlarmIDs"] = J.Node(new { type = "STRING_LIST", value = alarmIds });
    }

    private static JsonNode ScheduleReferenceZone(JsonNode reminder)
    {
        var fallback = CloudKit.Zone;
        var zoneName = fallback.Required("zoneName");
        var owner = new[] { CloudKit.Field(reminder, "List")?["zoneID"], reminder["zoneID"] }
            .Where(zone => zone.Text("zoneName") == zoneName)
            .Select(zone => zone.Text("ownerRecordName"))
            .FirstOrDefault(name => !string.IsNullOrEmpty(name) && name != "_defaultOwner") ?? fallback.Required("ownerRecordName");
        return J.Node(new { zoneName, ownerRecordName = owner });
    }

    private static JsonNode ScheduleReference(string recordName, JsonNode zone) =>
        J.Node(new { value = new { recordName, zoneID = zone, action = "VALIDATE" } });

    private static string? RecordName(string? id, string prefix) => string.IsNullOrEmpty(id) ? null
        : id.StartsWith(prefix, StringComparison.Ordinal) ? id : id.Contains('/') ? null : prefix + id;
}
