using System.Text;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class Cache
{
    private static string ManualOrderKey(JsonNode request) =>
        request.Text("list_id") is { } list ? "list:" + list :
        request.Text("tag") is { } tag ? "tag:" + tag :
        "smart:" + (request.Text("scope") ?? "all");

    internal JsonNode SaveReminderOrder(JsonObject request)
    {
        var list = request.Optional("list_id", 2048);
        var tag = request.Optional("tag", 2048);
        var scope = request.Optional("scope", 16);
        if (new[] { list, tag, scope }.Count(value => value is not null) != 1 ||
            list is "" || tag is "" || scope is not (null or "today" or "upcoming" or "all" or "completed" or "deleted") ||
            request["ids"] is not JsonArray { Count: >= 2 and <= 10000 } ids ||
            ids.Any(id => id is not JsonValue value || !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 2048))
            throw InvalidReminderOrder();

        return Transaction(() =>
        {
            var requested = Query("""
                SELECT r.id FROM json_each(@p0) AS incoming
                LEFT JOIN meta AS alias ON alias.key='id_alias:' || incoming.value
                JOIN reminders AS r ON r.id=COALESCE(alias.value,incoming.value)
                WHERE @p1 IS NULL OR r.list_id=@p1
                ORDER BY CAST(incoming.key AS INTEGER)
                """, ids.ToJsonString(), list).OfType<JsonObject>().Select(row => row.Required("id")).ToArray();
            var selected = requested.ToHashSet(StringComparer.Ordinal);
            if (requested.Length != ids.Count || selected.Count != ids.Count) throw InvalidReminderOrder();

            var key = ManualOrderKey(request);
            var previous = ReadManualOrder(key).OfType<JsonObject>().Select(row => row.Required("reminder_id")).ToArray();
            var ranked = previous.ToHashSet(StringComparer.Ordinal);
            var ordered = previous.Concat(requested.Where(id => !ranked.Contains(id))).ToArray();
            // Swap the visible positions while retaining any ranked reminders omitted from the view.
            var next = 0;
            for (var i = 0; i < ordered.Length; i++)
                if (selected.Contains(ordered[i])) ordered[i] = requested[next++];

            Execute("""
                INSERT INTO reminder_manual_order(view_key,reminder_id,position)
                SELECT @p0,value,CAST(key AS INTEGER) FROM json_each(@p1) WHERE 1
                ON CONFLICT(view_key,reminder_id) DO UPDATE SET position=excluded.position
                WHERE position<>excluded.position
                """, key, J.Node(ordered).ToJsonString());
            return J.Node(new { saved = true });
        });
    }

    private JsonArray ReadManualOrder(string key) =>
        Query("SELECT reminder_id,position FROM reminder_manual_order WHERE view_key=@p0 ORDER BY position", key);

    private JsonArray OrderManualContext(JsonArray rows, string key)
    {
        var positions = ReadManualOrder(key).OfType<JsonObject>().ToDictionary(row => row.Required("reminder_id"), row => row.Number("position"));
        return new JsonArray(rows.OfType<JsonObject>().OrderBy(row => positions.GetValueOrDefault(row.Required("id"), long.MaxValue))
            .ThenBy(row => row.Text("created") is null)
            .ThenBy(row => row.Text("created"), StringComparer.Ordinal)
            .ThenBy(row => row.Required("id"), StringComparer.Ordinal)
            .Select(row => row.DeepClone()).ToArray());
    }

    private static CoreException InvalidReminderOrder() =>
        CoreException.Bad("Cannot reorder these reminders. Refresh the list and try again.").WithPresentation("ErrorInvalidReminderOrder");
}
