using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class PhaseTwoSyncChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-phase-two-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RestoreAndSync(Path.Combine(root, "global"), false, check);
            await RestoreAndSync(Path.Combine(root, "mainland"), true, check);
            await PreserveDraftBase(Path.Combine(root, "editor"), check);
            CheckCalendarMigration(Path.Combine(root, "calendar.db"), check);
            CheckUtcScheduleMigration(Path.Combine(root, "utc.db"), check);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task RestoreAndSync(string directory, bool mainland, Action<bool, string> check)
    {
        const string alias = "alias@example.com";
        const string canonical = "canonical@example.com";
        var domain = mainland ? "icloud.com.cn" : "icloud.com";
        var cloud = "p01-ckdatabasews." + domain;
        using (var cache = new Cache(Path.Combine(directory, "cache.db")))
        {
            cache.SetMeta("apple_id", alias);
            if (mainland) cache.SetMeta("china_mainland", "1");
            cache.SetMeta("sync_cursor", "previous-cursor");
            cache.SetMeta("cloudkit_read_protocol", "4");
            cache.ReplaceLists(new JsonArray(J.Node(new { id = "previous-list", title = "Previous list" })));
        }
        var vault = new MemorySecrets();
        var secrets = new SecretStore(vault);
        var saved = J.Node(new { apple_id = canonical, dsid = "123", session_token = "test", china_mainland = mainland }).ToJsonString();
        secrets.Session(alias, saved, mainland);
        var startedRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishedSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validations = 0;
        var downloads = 0;
        var starts = 0;
        var refreshes = 0;
        var differentAccount = false;

        HttpMessageHandler Handler() => new ReplyHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/accountLogin"))
            {
                check(uri.Host == "setup." + domain, "session restoration uses its configured region");
                if (Interlocked.Increment(ref validations) == 1)
                {
                    startedRestore.TrySetResult();
                    await releaseRestore.Task.WaitAsync(token);
                }
                return Reply(new
                {
                    dsInfo = new { appleId = canonical, dsid = differentAccount ? "999" : "123" },
                    hsaTrustedBrowser = true,
                    webservices = new { ckdatabasews = new { url = "https://" + cloud } }
                });
            }
            check(uri.Host == cloud && uri.AbsolutePath.EndsWith("/changes/zone"), "recovered session downloads only in its configured region");
            if (Interlocked.Increment(ref downloads) == 1) return Reply(new { }, HttpStatusCode.Unauthorized);
            var query = J.Parse(await request.Content!.ReadAsStringAsync(token));
            check(query.Array("zones")[0].Text("syncToken") is null, "pending full request is honored when restoration ends");
            return Reply(new { zones = new[] { new { records = Array.Empty<object>(), syncToken = "fresh" } } });
        });

        await using (var service = new ReminderService(directory, vault, Handler))
        {
            service.EventReceived += (name, status) =>
            {
                if (name == "sync_started") Interlocked.Increment(ref starts);
                if (name == "auth_changed" && status.GetProperty("authenticated").GetBoolean()) Interlocked.Increment(ref refreshes);
                if (name == "sync_finished") finishedSync.TrySetResult();
            };
            await service.StartAsync();
            await startedRestore.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!(await Call(service, "sync", new { automatic = true })).Flag("queued"), "automatic triggers do not interrupt startup restoration");
            for (var i = 0; i < 12; i++)
                check((await Call(service, "sync", new { full = i == 8 })).Flag("queued"), "explicit sync request is accepted during restoration");
            releaseRestore.TrySetResult();
            await finishedSync.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(starts == 1 && validations == 2 && downloads == 2 && refreshes == 2,
                "startup requests coalesce into one sync that retries its expired session once");
        }

        differentAccount = true;
        using var resumed = new ICloudClient(alias, secrets, Handler, mainland);
        check(!await resumed.Restore(default) && !await resumed.Restore(default), "a validated session for a different DSID is consistently rejected");
        check(J.Parse(secrets.Session(alias, mainland)).Text("dsid") == "123" && downloads == 2,
            "a mismatched DSID cannot replace the saved session or reach CloudKit");
    }

    private static async Task PreserveDraftBase(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "cache.db");
        using (var cache = new Cache(path))
        {
            cache.SetMeta("signed_out", "1");
            cache.Upsert(J.Node(new { id = "reminder", list_id = "list", title = "On Apple", description = "Newer notes", change_tag = "current" }));
        }
        await using (var service = new ReminderService(directory, new MemorySecrets()))
        {
            var result = await Call(service, "update_reminder", new { id = "reminder", title = "Windows draft", edit_base_tag = "baseline" });
            check(result.Text("title") == "Windows draft" && result.Text("description") == "Newer notes",
                "saving a draft retains unrelated newer cache fields");
        }
        using (var reopened = new Cache(path))
        {
            check(reopened.Pending(1)[0].Text("base_tag") == "baseline", "the draft retains the server revision from when editing began");
            reopened.Execute("DELETE FROM outbox");
            reopened.Execute("UPDATE reminders SET title='Latest Apple title', description='Latest Apple notes', dirty=0 WHERE id='reminder'");
            var draft = J.Node(new { title = "Windows draft", description = "Old notes", conflicting_patch = new { title = "Windows draft" } });
            reopened.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES('reminder',@p0,'{}','now')", draft.ToJsonString());
        }
        await using (var service = new ReminderService(directory, new MemorySecrets()))
        {
            var conflicts = (await Call(service, "conflicts", new { })).AsArray();
            await Call(service, "resolve_conflict", new { id = conflicts[0].Number("id"), keep = "local" });
            var resolved = await Call(service, "reminder", new { id = "reminder" });
            check(resolved.Text("title") == "Windows draft" && resolved.Text("description") == "Latest Apple notes",
                "keeping a conflict's local edit preserves newer server fields outside the conflicting patch");
        }
    }

    private static void CheckCalendarMigration(string path, Action<bool, string> check)
    {
        const string cloudDate = "2026-03-08";
        var raw = TimeUtil.Parse(cloudDate + "T00:00:00Z").ToUnixTimeMilliseconds();
        var oldDate = TimeUtil.Iso(TimeUtil.Anchor(DateTimeOffset.FromUnixTimeMilliseconds(raw).UtcDateTime, TimeUtil.Zone("Asia/Shanghai")));
        using (var cache = new Cache(path))
        {
            cache.ApplyRecords(new JsonArray(J.Node(new
            {
                recordName = "cloud",
                recordType = "Reminder",
                fields = new { DueDate = CloudKit.Timestamp(raw), AllDay = CloudKit.Number(1), TimeZone = CloudKit.String("Asia/Shanghai") }
            })));
            cache.Execute("UPDATE reminders SET due_date=@p0, dirty=1 WHERE id='cloud'", oldDate);
            cache.Enqueue("cloud", "update", J.Node(new { title = "Offline title edit" }), "base");
            var localWall = new DateTime(2026, 3, 12);
            var localBefore = TimeUtil.Iso(TimeUtil.Anchor(localWall, TimeUtil.Zone()));
            cache.Upsert(J.Node(new { id = "local", list_id = "list", due_date = localBefore, all_day = true }), true);
            cache.Enqueue("local", "update", J.Node(new { due_date = localBefore, all_day = true }), "base");
            cache.SetMeta("calendar_dates", null);
        }
        using var migrated = new Cache(path);
        check(migrated.Reminder("cloud").Text("due_date") == cloudDate && migrated.PendingCount == 2,
            "legacy date migration keeps the native day alongside a queued title edit");
        var local = migrated.Pending().Single(row => row.Text("reminder_id") == "local");
        check(migrated.Reminder("local").Text("due_date") == "2026-03-12" &&
            J.Parse(local.Text("payload")).Text("due_date") == "2026-03-12", "local date and its queued patch migrate together");
    }

    private static async Task<JsonNode> Call(ReminderService service, string method, object parameters) =>
        J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;

    private static void CheckUtcScheduleMigration(string path, Action<bool, string> check)
    {
        const string oldShifted = "2026-07-01T08:30:00.000000Z";
        const string pendingDate = "2026-07-03T12:00:00.000000Z";
        var original = TimeUtil.Parse("2026-07-01T16:30:00Z");
        JsonNode NativeRecord(string id) => J.Node(new
        {
            recordName = id, recordType = "Reminder", recordChangeTag = "same-cloud-version",
            fields = new { DueDate = CloudKit.Timestamp(original.ToUnixTimeMilliseconds()), TimeZone = CloudKit.String("Asia/Shanghai") }
        });

        using (var cache = new Cache(path))
        {
            cache.ApplyRecords(new JsonArray(NativeRecord("title-edit"), NativeRecord("date-edit"), NativeRecord("clean-conflict"), NativeRecord("newer-wire")));
            cache.Execute("UPDATE reminders SET due_date=@p0, dirty=1, notified=1 WHERE id='title-edit'", oldShifted);
            cache.Enqueue("title-edit", "update", J.Node(new { title = "An offline title" }), "base");
            cache.Execute("UPDATE reminders SET due_date=@p0, dirty=1 WHERE id='date-edit'", pendingDate);
            cache.Enqueue("date-edit", "update", J.Node(new { due_date = pendingDate }), "base");
            cache.Execute("UPDATE reminders SET due_date=@p0 WHERE id='clean-conflict'", oldShifted);
            cache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES('clean-conflict','{}','{}','now')");
            cache.Execute("UPDATE reminders SET due_date=@p0, dirty=1 WHERE id='newer-wire'", oldShifted);
            var newerWire = NativeRecord("newer-wire");
            newerWire["recordChangeTag"] = "updated-cloud-version";
            newerWire["fields"]!["AllDay"] = CloudKit.Number(1);
            cache.ApplyRecords(new JsonArray(newerWire));
            cache.SetMeta("cloudkit_read_protocol", "4");
            cache.SetMeta("cloud_due_utc", null);
        }
        using var restored = new Cache(path);
        check(restored.Reminder("title-edit").Text("due_date") == TimeUtil.Iso(original) &&
            restored.Reminder("title-edit").Number("notified") == 1 && restored.PendingCount == 2,
            "cache repair corrects double conversion without duplicating already delivered notifications");
        check(restored.Reminder("date-edit").Text("due_date") == pendingDate, "cache repair preserves a queued date change");
        check(restored.Reminder("clean-conflict").Text("due_date") == TimeUtil.Iso(original) && restored.Conflicts.Count == 1,
            "cache repair also corrects a known cloud version without resolving the conflict");
        check(restored.Reminder("newer-wire").Text("due_date") == oldShifted && restored.Meta("cloudkit_read_protocol") is null,
            "cache repair waits for a fresh snapshot before decoding data from a newer wire revision");

        var localToday = TimeUtil.CalendarDate(DateTime.Today);
        restored.Upsert(J.Node(new { id = "done-today", list_id = "", completed = true, due_date = localToday, all_day = true }));
        restored.Upsert(J.Node(new { id = "done-undated", list_id = "", completed = true }));
        foreach (var scope in new[] { "today", "upcoming" })
        {
            check(!restored.Reminders(J.Node(new { scope })).Any(row => row.Text("id") == "done-today"), $"{scope} defaults to hiding completed");
            var shown = restored.Reminders(J.Node(new { scope, include_completed = true }));
            check(shown.Any(row => row.Text("id") == "done-today") && !shown.Any(row => row.Text("id") == "done-undated"),
                $"{scope} shows completed reminders matching its date filter");
        }
    }

    private static HttpResponseMessage Reply(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body)) };

    private sealed class ReplyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
