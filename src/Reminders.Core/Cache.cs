using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class Cache : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private static readonly string[] Flags = ["completed", "flagged", "all_day", "deleted", "is_group", "resolved", "can_undo_today"];
    internal static readonly string[] EditFields = ["title", "description", "due_date", "priority", "completed", "flagged", "list_id", "parent_id", "deleted", "all_day", "recurrence", "recurrence_ids", "time_zone"];

    static Cache() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());

    public Cache(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        Execute("PRAGMA trusted_schema=OFF; PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        // Migrate before creating indexes, including caches from pre-v2 releases.
        if (Query("SELECT name FROM sqlite_master WHERE type='table' AND name='reminders'").Count > 0 && !Query("PRAGMA table_info(reminders)").Any(r => r.Text("name") == "completed_date")) Execute("ALTER TABLE reminders ADD COLUMN completed_date TEXT");
        using var stream = typeof(Cache).Assembly.GetManifestResourceStream("Reminders.Core.Schema.sql")!;
        using var reader = new StreamReader(stream);
        Execute(reader.ReadToEnd());
        var columns = Query("PRAGMA table_info(reminders)").Select(r => r.Text("name")).ToHashSet();
        foreach (var column in new[] { "recurrence", "recurrence_ids", "time_zone", "parent_id" })
            if (!columns.Contains(column)) Execute($"ALTER TABLE reminders ADD COLUMN {column} TEXT");
        if (!columns.Contains("parent_id"))
            Execute("UPDATE reminders SET parent_id=(SELECT json_extract(json,'$.fields.ParentReminder.value.recordName') FROM related_records WHERE related_records.id=reminders.id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_reminders_parent ON reminders(parent_id)");
        if (Meta("schema_version") != "3" && Settings.Number("sync_minutes") == 10)
            SetSettings(new() { ["sync_minutes"] = 1 });
        SetMeta("schema_version", "3");
        if (Meta("calendar_dates") != "1") MigrateCalendarDates();
        if (Meta("cloud_due_utc") != "1") MigrateCloudDueDates();
        RetireTagWrites();
    }

    private void MigrateCloudDueDates() => Transaction(() =>
    {
        var rows = Query("""
            SELECT r.id, r.due_date, r.all_day, c.json
            FROM reminders r JOIN related_records c ON c.id=r.id
            WHERE r.change_tag IS json_extract(c.json, '$.recordChangeTag')
            AND NOT EXISTS (
                SELECT 1 FROM outbox o WHERE o.reminder_id=r.id
                AND CASE WHEN json_valid(o.payload) THEN
                    json_type(o.payload, '$.due_date') IS NOT NULL OR json_type(o.payload, '$.all_day') IS NOT NULL
                END
            )
            AND (
                NOT EXISTS (SELECT 1 FROM conflicts f WHERE f.reminder_id=r.id AND f.resolved=0)
                OR r.dirty=0
            )
            """);
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (J.Parse(row.Text("json")) is not JsonObject record ||
                (CloudKit.Int(record, "AllDay") != 0) != row.Flag("all_day") ||
                CloudKit.Field(record, "DueDate") is not JsonValue field || !field.TryGetValue<long>(out var timestamp) ||
                timestamp < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() ||
                timestamp > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) continue;
            var date = TimeUtil.FromCloudDue(timestamp, row.Flag("all_day"), CloudKit.Text(record, "TimeZone"));
            if (date != row.Text("due_date")) Execute("UPDATE reminders SET due_date=@p0 WHERE id=@p1", date, row.Required("id"));
        }
        // A fresh snapshot also repairs rows whose retained wire record is missing
        // or belongs to a different revision, without rewriting cloud dates.
        if (Query("SELECT 1 FROM reminders LIMIT 1").Count > 0) SetMeta("cloudkit_read_protocol", null);
        SetMeta("cloud_due_utc", "1");
        return true;
    });

    private void MigrateCalendarDates() => Transaction(() =>
    {
        var localDueEdits = new HashSet<string>();
        var queuedDayModes = new Dictionary<string, bool>();
        foreach (var queued in Query("""
            SELECT o.seq, o.reminder_id, o.payload, r.all_day,
                   json_extract(c.json, '$.fields.AllDay.value') AS cloud_all_day
            FROM outbox o JOIN reminders r ON r.id=o.reminder_id LEFT JOIN related_records c ON c.id=r.id
            ORDER BY o.seq
            """).OfType<JsonObject>())
        {
            if (J.Parse(queued.Text("payload")) is not JsonObject patch) continue;
            var id = queued.Required("reminder_id");
            var allDay = patch.ContainsKey("all_day") ? patch.Flag("all_day") :
                queuedDayModes.GetValueOrDefault(id, queued.Number("cloud_all_day", queued.Flag("all_day") ? 1 : 0) != 0);
            queuedDayModes[id] = allDay;
            if (!patch.ContainsKey("due_date")) continue;
            localDueEdits.Add(id);
            if (allDay && patch.Text("due_date") is { Length: > 10 } due && TryLegacyCalendarDate(due, TimeUtil.Zone()) is { } date)
            {
                patch["due_date"] = date;
                Execute("UPDATE outbox SET payload=@p0 WHERE seq=@p1", patch.ToJsonString(), queued.Number("seq"));
            }
        }
        var rows = Query("""
            SELECT r.id, r.due_date, r.time_zone,
                   json_extract(c.json, '$.fields.DueDate.value') AS cloud_due
            FROM reminders r LEFT JOIN related_records c ON c.id=r.id
            WHERE r.all_day=1 AND length(r.due_date)>10
            """);
        foreach (var row in rows.OfType<JsonObject>())
        {
            var id = row.Required("id");
            var due = row.Required("due_date");
            var locallyEdited = localDueEdits.Contains(id);
            string? date;
            if (!locallyEdited && row["cloud_due"] is JsonValue value && value.TryGetValue<long>(out var cloudDue))
                date = TimeUtil.FromCloudDue(cloudDue, true, row.Text("time_zone"));
            else
                date = TryLegacyCalendarDate(due, TimeUtil.Zone(locallyEdited ? null : row.Text("time_zone")));
            if (date is not null) Execute("UPDATE reminders SET due_date=@p0 WHERE id=@p1", date, id);
        }
        SetMeta("calendar_dates", "1");
        return true;
    });

    private static string? TryLegacyCalendarDate(string due, TimeZoneInfo zone) =>
        DateTimeOffset.TryParse(due, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var oldDue)
            ? TimeUtil.CalendarDate(TimeZoneInfo.ConvertTime(oldDue, zone).Date) : null;

    private static (string Tomorrow, string Boundary) TodayCutoff()
    {
        var zone = TimeUtil.Zone();
        var tomorrow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date.AddDays(1);
        return (TimeUtil.CalendarDate(tomorrow), TimeUtil.Iso(TimeUtil.Anchor(tomorrow, zone)));
    }
    private SqliteCommand Command(string sql, object?[] args)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + i, args[i] ?? DBNull.Value);
        return command;
    }
    public void Execute(string sql, params object?[] args) { lock (gate) { using var cmd = Command(sql, args); cmd.ExecuteNonQuery(); } }
    public JsonArray Query(string sql, params object?[] args)
    {
        lock (gate)
        {
            using var cmd = Command(sql, args); using var reader = cmd.ExecuteReader(); var rows = new JsonArray();
            while (reader.Read())
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i); var value = reader.GetValue(i);
                    row[name] = value is DBNull ? null : Flags.Contains(name) ? JsonValue.Create(Convert.ToInt64(value) != 0) : J.Node(value);
                }
                rows.Add(row);
            }
            return rows;
        }
    }
    // Mutations and their queue entry commit together; a crash cannot save an
    // offline edit without saving the corresponding upload.
    public T Transaction<T>(Func<T> action)
    {
        lock (gate)
        {
            Execute("BEGIN IMMEDIATE");
            try { var result = action(); Execute("COMMIT"); return result; }
            catch { Execute("ROLLBACK"); throw; }
        }
    }
    public string? Meta(string key) => Query("SELECT value FROM meta WHERE key=@p0", key).FirstOrDefault().Text("value");
    public void SetMeta(string key, string? value)
    {
        if (value is null) Execute("DELETE FROM meta WHERE key=@p0", key);
        else Execute("INSERT INTO meta(key,value) VALUES(@p0,@p1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    }
    public JsonArray Lists
    {
        get
        {
            var order = Settings.Array("list_order").Select(v => v?.ToString()).ToList();
            var rows = Query("""
                SELECT l.*, (SELECT COUNT(*) FROM reminders r WHERE r.list_id=l.id AND r.completed=0 AND r.deleted=0) AS open_count,
                       json_extract(c.json, '$.fields.BadgeEmblem') AS badge_field
                FROM lists l LEFT JOIN related_records c ON c.id=l.id ORDER BY l.position
                """);
            foreach (var list in rows.OfType<JsonObject>())
            {
                var badge = CloudKit.TextField(J.Parse(list.Text("badge_field")));
                list.Remove("badge_field");
                if (badge is not null) list["badge_emblem"] = badge;
            }
            return new JsonArray(rows.OrderBy(l => { var index = order.IndexOf(l.Text("id")); return index < 0 ? int.MaxValue : index; }).Select(l => l?.DeepClone()).ToArray());
        }
    }
    public void ReplaceLists(JsonArray lists) => Transaction(() =>
    {
        Execute("DELETE FROM lists");
        for (var i = 0; i < lists.Count; i++) { var l = lists[i]!; Execute("INSERT INTO lists(id,title,color_hex,count,is_group,position) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)", l.Text("id"), l.Text("title"), l.Text("color_hex"), l.Number("count"), l.Flag("is_group"), i); }
        return true;
    });
    public JsonObject? Reminder(string id)
    {
        lock (gate) { var row = Query($"SELECT r.*, {TodayUndoColumn} FROM reminders r WHERE r.id=@p0", id).FirstOrDefault() as JsonObject; if (row is not null) AttachReminderMetadata(row); return row; }
    }
    private void AttachReminderMetadata(JsonObject row)
    {
        if (row.Flag("can_undo_today"))
        {
            var zone = TimeUtil.Zone();
            row["can_undo_today"] = TimeUtil.LocalDue(row.Text("due_date"), row.Flag("all_day"), zone)?.Date
                == TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        }
        row["tags"] = new JsonArray(Query("SELECT name FROM tags WHERE reminder_id=@p0 ORDER BY name", row.Text("id")).Select(t => JsonValue.Create(t.Text("name"))).ToArray());
        row["hashtag_ids"] = new JsonArray(Query("SELECT id FROM tags WHERE reminder_id=@p0 ORDER BY name", row.Text("id")).Select(t => JsonValue.Create(t.Text("id"))).ToArray());
        row["recurrence_ids"] = J.Parse(row.Text("recurrence_ids")) ?? new JsonArray();
        row["recurrence"] = J.Parse(row.Text("recurrence"));
        if (row["recurrence"] is null && row.Array("recurrence_ids").FirstOrDefault()?.ToString() is { } ruleId)
        {
            var rule = J.Parse(Query("SELECT json FROM related_records WHERE id=@p0", ruleId.StartsWith("RecurrenceRule/") ? ruleId : "RecurrenceRule/" + ruleId).FirstOrDefault().Text("json"));
            row["recurrence"] = rule is null ? J.Node(new { supported = false }) : Recurrence.Describe(rule);
        }
        if (row.Array("recurrence_ids").Count > 1 && row["recurrence"] is JsonObject repeat) repeat["supported"] = false;
    }
    public JsonArray Reminders(JsonNode p)
    {
        lock (gate)
        {
            var scope = p.Text("scope"); var args = new List<object?>();
            string Param(object? v) { args.Add(v); return "@p" + (args.Count - 1); }
            var sql = $"SELECT DISTINCT r.*, {TodayUndoColumn} FROM reminders r ";
            if (p.Text("tag") is { } tag) sql += "JOIN tags t ON t.reminder_id=r.id AND t.name=" + Param(tag) + " ";
            sql += scope == "deleted" ? "WHERE r.deleted=1 " : "WHERE r.deleted=0 ";
            if (scope == "today")
            {
                var (tomorrow, boundary) = TodayCutoff();
                if (!p.Flag("include_completed")) sql += "AND r.completed=0 ";
                sql += "AND r.due_date < CASE WHEN r.all_day=1 THEN " + Param(tomorrow) + " ELSE " + Param(boundary) + " END ";
            }
            else if (scope == "upcoming") sql += p.Flag("include_completed") ? "AND r.due_date IS NOT NULL " : "AND r.completed=0 AND r.due_date IS NOT NULL ";
            else sql += scope switch { "completed" => "AND r.completed=1 ", "deleted" => "", _ => p.Flag("include_completed") ? "" : "AND r.completed=0 " };
            if (p.Text("list_id") is { } list) sql += "AND r.list_id=" + Param(list) + " ";
            if (p.Optional("search", 4096) is { Length: > 0 } search) sql += "AND (r.title LIKE " + Param("%" + search + "%") + " OR r.description LIKE " + Param("%" + search + "%") + ") ";
            const string dueOrder = "CASE WHEN r.all_day=1 THEN julianday(r.due_date,'utc') ELSE julianday(r.due_date) END";
            var order = p.Text("sort") switch
            {
                "title" => $"r.title COLLATE NOCASE,{dueOrder}", "title_desc" => "r.title COLLATE NOCASE DESC",
                "due" => $"r.due_date IS NULL,{dueOrder},r.all_day DESC,r.title COLLATE NOCASE", "due_desc" => $"r.due_date IS NULL,{dueOrder} DESC",
                "priority" => $"CASE r.priority WHEN 1 THEN 0 WHEN 5 THEN 1 WHEN 9 THEN 2 ELSE 3 END,r.due_date IS NULL,{dueOrder}",
                "created" => "r.created DESC", "created_asc" => "r.created", _ => scope == "completed" ? "r.completed_date DESC NULLS LAST,r.modified DESC"
                    : $"r.completed,r.due_date IS NULL,{dueOrder},r.all_day DESC,r.title COLLATE NOCASE"
            };
            var rows = Query(sql + "ORDER BY " + order + " LIMIT " + Param(Math.Clamp(p.Number("limit", 1000), 1, scope == "completed" ? 50 : 10000)), args.ToArray());
            foreach (var row in rows.OfType<JsonObject>()) AttachReminderMetadata(row);
            // Today shows only its own matches. The visible parent IDs are
            // enough to build a tree when both a parent and child qualify.
            if (p.Flag("include_parents") && scope != "today")
            {
                var filterText = p.Optional("search", 4096); var filterTag = p.Text("tag");
                if (!string.IsNullOrEmpty(filterText) || filterTag is not null)
                {
                    // Search the family context that was visible before filtering.
                    var scopeQuery = p.DeepClone().AsObject(); scopeQuery.Remove("search"); scopeQuery.Remove("tag");
                    var included = rows.OfType<JsonObject>().Select(row => row.Required("id")).ToHashSet();
                    foreach (var candidate in Reminders(scopeQuery).OfType<JsonObject>())
                    {
                        if (!string.IsNullOrEmpty(filterText) && !(candidate.Text("title") ?? "").Contains(filterText, StringComparison.OrdinalIgnoreCase)
                            && !(candidate.Text("description") ?? "").Contains(filterText, StringComparison.OrdinalIgnoreCase)) continue;
                        if (filterTag is not null && !candidate.Array("tags").Any(tagName => tagName?.ToString() == filterTag)) continue;
                        if (!included.Add(candidate.Required("id"))) continue;
                        var match = candidate.DeepClone(); match["context_only"] = false; rows.Add(match);
                    }
                }
                IncludeParents(rows, p);
            }
            return rows;
        }
    }
    private static object? Sql(JsonNode? v) => v is null ? null : v is JsonValue x && x.TryGetValue<bool>(out var b) ? b ? 1 : 0 : v is JsonValue y && y.TryGetValue<long>(out var n) ? n : v is JsonValue z && z.TryGetValue<string>(out var s) ? s : v.ToJsonString();
    public void Edit(string id, JsonObject fields, bool dirty = true)
    {
        var selected = fields.Where(f => EditFields.Contains(f.Key)).ToArray(); if (selected.Length == 0) return;
        var args = selected.Select(f => Sql(f.Value)).ToList(); args.Add(TimeUtil.Now); args.Add(dirty ? 1 : 0); args.Add(id);
        Execute("UPDATE reminders SET " + string.Join(",", selected.Select((f, i) => f.Key + "=@p" + i)) + $",modified=@p{selected.Length},dirty=@p{selected.Length + 1} WHERE id=@p{selected.Length + 2}", args.ToArray());
    }
    public void Upsert(JsonNode r, bool local = false)
    {
        lock (gate)
        {
            var id = r.Required("id"); var old = Reminder(id);
            if (!local && old.Number("dirty") != 0) return;
            string[] columns = ["id", "list_id", "parent_id", "title", "description", "due_date", "priority", "completed", "completed_date", "flagged", "all_day", "deleted", "created", "modified", "change_tag", "notified", "dirty", "recurrence", "recurrence_ids", "time_zone"];
            var copy = (JsonObject)r.DeepClone(); copy["dirty"] = local ? 1 : 0; copy["notified"] = old.Text("due_date") == r.Text("due_date") ? old.Number("notified") : 0;
            foreach (var key in new[] { "title", "description", "list_id" }) copy[key] ??= "";
            foreach (var key in new[] { "priority", "completed", "flagged", "all_day", "deleted" }) copy[key] ??= 0;
            Execute("INSERT INTO reminders(" + string.Join(",", columns) + ") VALUES(" + string.Join(",", columns.Select((_, i) => "@p" + i)) + ") ON CONFLICT(id) DO UPDATE SET " + string.Join(",", columns.Where(c => c != "id" && c != "created").Select(c => c + "=excluded." + c)), columns.Select(c => Sql(copy[c])).ToArray());
        }
    }
    public JsonArray Tags => Query("SELECT name,COUNT(*) AS n FROM tags t JOIN reminders r ON r.id=t.reminder_id WHERE r.deleted=0 AND r.completed=0 GROUP BY name ORDER BY name");
    public void ApplyRecords(JsonArray records, bool full = false) => Transaction(() =>
    {
        ApplyRecordsInTransaction(records, full);
        return true;
    });
    internal void ApplyRecordsInTransaction(JsonArray records, bool full = false)
    {
        if (full)
        {
            Execute("DELETE FROM lists");
            Execute("DELETE FROM related_records");
            Execute("DELETE FROM tags WHERE reminder_id IN (SELECT id FROM reminders WHERE dirty=0)");
            Execute("UPDATE reminders SET deleted=1 WHERE dirty=0");
        }
        foreach (var record in records.OfType<JsonObject>())
        {
            if (record.Text("recordName") is not { Length: > 0 } id) continue;
            var type = record.Text("recordType") ?? id.Split('/')[0];
            var deleted = record.Flag("deleted") || record.Text("reason") == "deleted" || CloudKit.Int(record, "Deleted") != 0;
            if (deleted)
            {
                Execute("DELETE FROM related_records WHERE id=@p0", id);
                if (type == "List") Execute("DELETE FROM lists WHERE id=@p0", id);
                else if (type == "Hashtag") Execute("DELETE FROM tags WHERE id=@p0", id);
                else if (type == "Reminder" && Reminder(id).Number("dirty") == 0) Edit(id, new() { ["deleted"] = true }, false);
                continue;
            }
            var combined = J.Parse(Query("SELECT json FROM related_records WHERE id=@p0", id).FirstOrDefault().Text("json")) as JsonObject ?? new();
            foreach (var pair in record)
            {
                if (pair.Key == "fields" && combined["fields"] is JsonObject oldFields && pair.Value is JsonObject changed)
                { foreach (var field in changed) oldFields[field.Key] = field.Value?.DeepClone(); }
                else combined[pair.Key] = pair.Value?.DeepClone();
            }
            Execute("INSERT INTO related_records(id,record_type,json) VALUES(@p0,@p1,@p2) ON CONFLICT(id) DO UPDATE SET json=excluded.json", id, type, combined.ToJsonString());
            if (type == "Reminder" && ICloudClient.ToReminder(combined) is { } reminder) Upsert(reminder);
            else if (type == "Hashtag" && CloudKit.Text(combined, "Name") is { } name && CloudKit.Field(combined, "Reminder").Text("recordName") is { } parent)
                Execute("INSERT OR REPLACE INTO tags(id,name,reminder_id) VALUES(@p0,@p1,@p2)", id, name, parent);
            else if (type == "List")
            {
                var rawColor = CloudKit.Text(combined, "Color");
                Execute("INSERT INTO lists(id,title,color_hex,count,is_group,position) VALUES(@p0,@p1,@p2,@p3,@p4,(SELECT COALESCE(MAX(position) + 1, 0) FROM lists)) ON CONFLICT(id) DO UPDATE SET title=excluded.title,color_hex=excluded.color_hex,count=excluded.count,is_group=excluded.is_group",
                    id, CloudKit.Text(combined, "Name") ?? "Untitled", rawColor?.StartsWith('#') == true ? rawColor : J.Parse(rawColor).Text("daHexString"), CloudKit.Int(combined, "Count"), CloudKit.Int(combined, "IsGroup") != 0);
            }
        }
    }
    public void ReplaceTags(string id, JsonArray tags) => Transaction(() => { ReplaceTagsInTransaction(id, tags); return true; });
    internal void ReplaceTagsInTransaction(string id, JsonArray tags)
    {
        Execute("DELETE FROM tags WHERE reminder_id=@p0", id);
        foreach (var tag in tags) Execute("INSERT OR REPLACE INTO tags(id,name,reminder_id) VALUES(@p0,@p1,@p2)", tag.Text("id"), tag.Text("name"), id);
    }
    // Older builds may leave failed tag uploads ahead of ordinary task edits.
    // Retain their requests locally, but never send them or guess a cloud repair.
    internal void RetireTagWrites() => Transaction(() =>
    {
        var pending = Query("SELECT * FROM outbox WHERE op IN ('tag','tags','repair_tags') ORDER BY seq");
        var conflicts = Query("SELECT * FROM conflicts WHERE resolved=0").Where(row => J.Parse(row.Text("local_json")).Flag("tags_edit")).ToArray();
        if (pending.Count == 0 && conflicts.Length == 0) return true;
        var archive = J.Parse(Meta("disabled_tag_writes")) as JsonArray ?? new();
        var affected = new HashSet<string>();
        foreach (var row in pending)
        {
            archive.Add(new JsonObject { ["kind"] = "upload", ["data"] = row!.DeepClone() });
            affected.Add(row.Required("reminder_id"));
            Execute("DELETE FROM outbox WHERE seq=@p0", row.Number("seq"));
        }
        foreach (var row in conflicts)
        {
            archive.Add(new JsonObject { ["kind"] = "conflict", ["data"] = row!.DeepClone() });
            affected.Add(row.Required("reminder_id"));
            Execute("UPDATE conflicts SET resolved=1 WHERE id=@p0", row.Number("id"));
        }
        SetMeta("disabled_tag_writes", archive.ToJsonString());
        var cloudTags = Query("SELECT json FROM related_records WHERE record_type='Hashtag'")
            .Select(row => J.Parse(row.Text("json"))).OfType<JsonObject>().ToArray();
        foreach (var id in affected)
        {
            // Replace speculative local additions/removals with the last cloud
            // snapshot. The next download remains authoritative for display.
            Execute("DELETE FROM tags WHERE reminder_id=@p0", id);
            foreach (var tag in cloudTags.Where(tag => !tag.Flag("deleted") && tag.Text("reason") != "deleted"
                && CloudKit.Int(tag, "Deleted") == 0 && CloudKit.Field(tag, "Reminder").Text("recordName") == id))
                if (CloudKit.Text(tag, "Name") is { } name)
                    Execute("INSERT OR REPLACE INTO tags(id,name,reminder_id) VALUES(@p0,@p1,@p2)", tag.Required("recordName"), name, id);
            Execute("UPDATE reminders SET dirty=EXISTS(SELECT 1 FROM outbox WHERE reminder_id=@p0) WHERE id=@p0", id);
        }
        return true;
    });
    public JsonNode Counts
    {
        get { lock (gate) { var (tomorrow, boundary) = TodayCutoff(); long Count(string where, params object?[] args) => Query("SELECT COUNT(*) AS n FROM reminders WHERE " + where, args)[0].Number("n"); return J.Node(new { today = Count("deleted=0 AND completed=0 AND due_date < CASE WHEN all_day=1 THEN @p0 ELSE @p1 END", tomorrow, boundary), upcoming = Count("deleted=0 AND completed=0 AND due_date IS NOT NULL"), all = Count("deleted=0 AND completed=0"), completed = Count("deleted=0 AND completed=1"), deleted = Count("deleted=1") }); } }
    }
    public JsonObject Settings
    {
        get
        {
            var defaults = (JsonObject)J.Node(new { theme = "system", sync_minutes = 1, notifications_enabled = true, stale_after_minutes = 60, max_individual_toasts = 3, default_list_id = (string?)null, search_scope = "list", onboarded = false, print_group_by = "due", print_include_notes = true, print_include_completed = false, remember_password = true, sort_by = new { }, list_order = Array.Empty<string>() });
            if (J.Parse(Meta("settings")) is JsonObject stored) foreach (var pair in stored) if (defaults.ContainsKey(pair.Key)) defaults[pair.Key] = pair.Value?.DeepClone();
            return defaults;
        }
    }
    public JsonNode SetSettings(JsonObject patch)
    {
        if (patch.ContainsKey("list_order") && (patch["list_order"] is not JsonArray order || order.Count > 10000 || order.Any(v => v is not JsonValue text || !text.TryGetValue<string>(out var id) || id.Length is 0 or > 2048)))
            throw CoreException.Bad("List order must contain list identifiers.").WithPresentation("ErrorInvalidListOrder");
        lock (gate) { var s = Settings; foreach (var p in patch) if (s.ContainsKey(p.Key)) s[p.Key] = p.Value?.DeepClone(); SetMeta("settings", s.ToJsonString()); return s; }
    }
    public void Enqueue(string id, string op, JsonNode payload, string? tag) => Execute("INSERT INTO outbox(reminder_id,op,payload,base_tag,created_at) VALUES(@p0,@p1,@p2,@p3,@p4)", id, op, payload.ToJsonString(), tag, TimeUtil.Now);
    public JsonArray Pending(int limit = 100) => Query("SELECT * FROM outbox ORDER BY seq LIMIT @p0", limit);
    // Keep edits to a single reminder in order, while allowing another reminder
    // past an earlier rejected upload. Successful creates may remap queued IDs.
    public JsonNode? NextUpload(long afterSeq) => Query("SELECT * FROM outbox AS next WHERE seq>@p0 AND NOT EXISTS (SELECT 1 FROM outbox AS earlier WHERE earlier.reminder_id=next.reminder_id AND earlier.seq<next.seq) AND NOT EXISTS (SELECT 1 FROM outbox AS parent WHERE next.op='create' AND parent.seq<next.seq AND parent.reminder_id=json_extract(next.payload,'$.parent_id')) ORDER BY seq LIMIT 1", afterSeq).FirstOrDefault();
    public void Acknowledge(long seq, string id, string? tag) => Transaction(() => { Execute("DELETE FROM outbox WHERE seq=@p0", seq); Execute("UPDATE outbox SET base_tag=@p0 WHERE reminder_id=@p1 AND seq>@p2", tag, id, seq); Execute("UPDATE reminders SET change_tag=@p0,dirty=EXISTS(SELECT 1 FROM outbox WHERE reminder_id=@p1) WHERE id=@p1", tag, id); return true; });
    public string ResolveId(string id) => Meta("id_alias:" + id) ?? id;
    public void ReplaceId(string oldId, string newId) => Transaction(() =>
    {
        Execute("UPDATE reminders SET id=@p0 WHERE id=@p1", newId, oldId);
        Execute("UPDATE reminders SET parent_id=@p0 WHERE parent_id=@p1", newId, oldId);
        Execute("UPDATE outbox SET payload=json_set(payload,'$.parent_id',@p0) WHERE json_extract(payload,'$.parent_id')=@p1", newId, oldId);
        foreach (var table in new[] { "tags", "outbox" }) Execute($"UPDATE {table} SET reminder_id=@p0 WHERE reminder_id=@p1", newId, oldId);
        SetMeta("id_alias:" + oldId, newId); return true;
    });
    public JsonArray Conflicts
    {
        get { var rows = Query("SELECT * FROM conflicts WHERE resolved=0 ORDER BY detected_at DESC"); foreach (var row in rows.OfType<JsonObject>()) { row["local"] = J.Parse(row.Text("local_json")); row["remote"] = J.Parse(row.Text("remote_json")); row.Remove("local_json"); row.Remove("remote_json"); } return rows; }
    }
    private void PurgeData()
    {
        foreach (var table in new[] { "lists", "reminders", "tags", "outbox", "conflicts", "related_records" }) Execute($"DELETE FROM {table}");
        foreach (var key in new[] { "sync_cursor", "last_sync", "last_full_sync", "cloudkit_read_protocol" }) SetMeta(key, null);
        Execute("DELETE FROM meta WHERE key GLOB 'id_alias:*'");
        SetMeta("disabled_tag_writes", null);
    }
    public void EnsureCanSwitchAccount(bool switching)
    {
        if (switching && Query("SELECT EXISTS(SELECT 1 FROM outbox) OR EXISTS(SELECT 1 FROM conflicts WHERE resolved=0) AS unsynced")[0].Flag("unsynced"))
            throw CoreException.Bad("Sync queued changes or sign out and clear the cache before switching accounts.").WithPresentation("ErrorSwitchAccountNeedsSyncOrCacheClear");
    }

    public void SaveAccount(string account, bool chinaMainland, bool replaceCache, Action saveCredentials) => Transaction(() =>
    {
        EnsureCanSwitchAccount(replaceCache);
        if (replaceCache) PurgeData();
        SetMeta("apple_id", account);
        SetMeta("china_mainland", chinaMainland ? "1" : null);
        saveCredentials();
        return true;
    });
    public JsonNode Clear(bool discardPending) => Transaction(() =>
    {
        var pending = Query("SELECT COUNT(*) AS count FROM outbox")[0].Number("count");
        var conflicts = Query("SELECT COUNT(*) AS count FROM conflicts WHERE resolved=0")[0].Number("count");
        if ((pending > 0 || conflicts > 0) && !discardPending)
            throw new CoreException("PENDING_CHANGES", "Confirm discarding unsynced changes and conflicts before clearing the cache.").WithPresentation("ErrorCacheClearNeedsConfirmation");
        PurgeData();
        return J.Node(new { cleared = true, discarded_pending = pending, discarded_conflicts = conflicts });
    });
    public void Dispose() { lock (gate) connection.Dispose(); }
}
