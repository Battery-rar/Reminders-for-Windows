using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reminders.Core;

/// <summary>In-process iCloud engine. Cache calls remain available during network operations.</summary>
public sealed partial class ReminderService : IAsyncDisposable
{
    private readonly Cache cache;
    private readonly FileStream cacheLease;
    private readonly ICloudClient? cloudClient;
    private readonly SyncEngine? sync;
    private ICloudClient client => cloudClient ?? throw new CoreException("AUTH_REQUIRED", "This account only stores reminders on this PC.").WithPresentation("ErrorLocalAccountNoICloud");
    public bool IsLocalDemo => cloudClient is null;
    private readonly SemaphoreSlim clientGate = new(1, 1);
    private readonly SemaphoreSlim signInGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource backgroundLifetime = new();
    private readonly ConcurrentDictionary<long, Task> background = new();
    private readonly ConcurrentDictionary<long, Task> queuedWork = new();
    private readonly object workGate = new();
    private long nextTask;
    private JsonObject authSnapshot;
    private bool restoringAtStartup;
    private bool syncWorkerScheduled;
    private bool syncPending;
    private bool fullSyncPending;
    private bool disposed;
    public event Action<string, JsonElement>? EventReceived;
    public event Action<string>? Diagnostic;
    public ReminderService(string dataDirectory) : this(dataDirectory, new WindowsSecrets()) { }
    internal ReminderService(string dataDirectory, ISecrets secrets, Func<HttpMessageHandler>? handlerFactory = null)
    {
        (cache, cacheLease) = OpenCache(dataDirectory);
        try
        {
            cloudClient = new ICloudClient((cache.Meta("apple_id") ?? "").Trim().ToLowerInvariant(), new SecretStore(secrets), handlerFactory, cache.Meta("china_mainland") == "1");
            client.Diagnostic += message => Diagnostic?.Invoke(message);
            authSnapshot = client.Status; sync = new(cache, client, clientGate, Emit);
        }
        catch { DisposeStorage(); throw; }
    }
    private ReminderService((Cache Cache, FileStream Lease) local)
    {
        // This constructor never opens the iCloud cache, credential vault or HTTP client.
        (cache, cacheLease) = local;
        try { DemoAccount.Seed(cache); }
        catch { DisposeStorage(); throw; }
        authSnapshot = (JsonObject)J.Node(new { authenticated = true, apple_id = "demo", needs_2fa = false, restoring = false, local_only = true });
    }
    public static ReminderService OpenDemo(string dataDirectory, string? password)
    {
        DemoAccount.ValidatePassword(password);
        return new ReminderService(OpenCache(Path.Combine(dataDirectory, "demo")));
    }
    private static (Cache Cache, FileStream Lease) OpenCache(string directory)
    {
        Directory.CreateDirectory(directory);
        FileStream lease;
        try { lease = new FileStream(Path.Combine(directory, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
        {
            throw new CoreException("CACHE_IN_USE", "Reminders is already using this cache in another instance.")
                .WithPresentation("ErrorCacheInUse");
        }
        // Leave the lock file at its original path so every instance opens the same file.
        try { return (new Cache(Path.Combine(directory, "cache.db")), lease); }
        catch { lease.Dispose(); throw; }
    }

    private void DisposeStorage()
    {
        try { cloudClient?.Dispose(); }
        finally
        {
            try { cache.Dispose(); }
            finally { cacheLease.Dispose(); }
        }
    }
    private bool QueueSync(bool full = false, bool automatic = false)
    {
        lock (workGate)
        {
            if (sync is null || disposed || backgroundLifetime.IsCancellationRequested || cache.Meta("signed_out") == "1") return false;
            if (automatic && (restoringAtStartup || syncWorkerScheduled)) return false;
            syncPending = true;
            fullSyncPending |= full;
            ScheduleSyncWorker();
            return true;
        }
    }

    // Called under workGate. Incoming triggers share one worker, including
    // changes saved while startup is still restoring the account.
    private void ScheduleSyncWorker()
    {
        if (restoringAtStartup || syncWorkerScheduled || !syncPending || disposed || backgroundLifetime.IsCancellationRequested) return;
        syncWorkerScheduled = true;
        Queue(async token =>
        {
            try
            {
                while (true)
                {
                    bool full;
                    lock (workGate)
                    {
                        if (!syncPending || token.IsCancellationRequested) break;
                        full = fullSyncPending;
                        syncPending = fullSyncPending = false;
                    }
                    await sync!.Sync(full, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Emit("sync_cancelled", new JsonObject());
                throw;
            }
            finally
            {
                lock (workGate)
                {
                    syncWorkerScheduled = false;
                    ScheduleSyncWorker();
                }
            }
        });
    }
    private void Emit(string name, JsonNode data)
    {
        if (name == "sync_progress" && data.Text("message") is { } message) Diagnostic?.Invoke("sync: " + message);
        if (name == "auth_changed" && data is JsonObject status) Volatile.Write(ref authSnapshot, (JsonObject)status.DeepClone());
        // Subscribers cannot abort a sync by throwing from a UI event handler.
        try { EventReceived?.Invoke(name, JsonSerializer.SerializeToElement(data)); } catch (Exception ex) { Diagnostic?.Invoke($"Event '{name}' failed: {ex.GetType().Name}"); }
    }
    private void Queue(Func<CancellationToken, Task> action)
    {
        lock (workGate)
        {
            if (disposed || backgroundLifetime.IsCancellationRequested) return;
            var backgroundToken = backgroundLifetime.Token;
            var id = Interlocked.Increment(ref nextTask);
            var task = Task.Run(async () =>
            {
                using var workLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, backgroundToken);
                try { await action(workLifetime.Token); }
                catch (OperationCanceledException) when (workLifetime.IsCancellationRequested) { }
                catch (CoreException ex) { Emit("sync_error", ex.Body); Diagnostic?.Invoke($"sync: {ex.Code}: {ex.Message}"); }
                catch (Exception ex) { Emit("sync_error", new CoreException("ERROR", "The sync operation failed", ex.GetType().Name).WithPresentation("ErrorSyncOperationFailed").Body); Diagnostic?.Invoke($"sync failed: {ex.GetType().Name}"); }
            });
            background[id] = task;
            queuedWork[id] = task;
            _ = task.ContinueWith(_ => { background.TryRemove(id, out var ignored); queuedWork.TryRemove(id, out ignored); }, TaskScheduler.Default);
        }
    }
    private async Task CancelBackgroundWorkAsync()
    {
        Task[] work;
        Task cancellation;
        lock (workGate)
        {
            cancellation = backgroundLifetime.CancelAsync();
            syncPending = fullSyncPending = false;
            work = queuedWork.Values.ToArray();
        }
        await cancellation;
        await Task.WhenAll(work);
    }
    private void ResumeBackgroundWork()
    {
        lock (workGate)
        {
            if (!backgroundLifetime.IsCancellationRequested) return;
            backgroundLifetime.Dispose(); backgroundLifetime = new();
        }
    }
    public Task StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
        if (IsLocalDemo) { Emit("ready", J.Node(new { apple_id = "demo", local_only = true })); return Task.CompletedTask; }
        lock (workGate)
        {
            if (!restoringAtStartup && !backgroundLifetime.IsCancellationRequested && client.Account.Length > 0 && cache.Meta("signed_out") != "1")
            {
                restoringAtStartup = true;
                client.Restoring = true; Volatile.Write(ref authSnapshot, client.Status);
                Queue(async ct =>
                {
                    try
                    {
                        bool restored = false;
                        foreach (var delay in new[] { 0, 5, 15, 45, 120, 300 })
                        {
                            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                            restored = await WithClient(() => client.Restore(ct), ct);
                            if (restored || !client.RestoreRetryable) break;
                            client.Restoring = true; Volatile.Write(ref authSnapshot, client.Status);
                        }
                        ct.ThrowIfCancellationRequested();
                        client.Restoring = false; Emit("auth_changed", client.Status);
                        if (restored) QueueSync(cache.Lists.Count == 0);
                    }
                    finally
                    {
                        client.Restoring = false;
                        lock (workGate)
                        {
                            restoringAtStartup = false;
                            ScheduleSyncWorker();
                        }
                    }
                });
            }
        }
        Emit("ready", J.Node(new { apple_id = client.Account })); return Task.CompletedTask;
    }
    private async Task<T> WithClient<T>(Func<Task<T>> action, CancellationToken token)
    {
        if (IsLocalDemo) throw new CoreException("AUTH_REQUIRED", "This account only stores reminders on this PC.").WithPresentation("ErrorLocalAccountNoICloud");
        await clientGate.WaitAsync(token);
        try { return await action(); }
        finally { try { Volatile.Write(ref authSnapshot, client.Status); } finally { clientGate.Release(); } }
    }
    private async Task<T> WithSignIn<T>(Func<Task<T>> action, CancellationToken token)
    {
        await signInGate.WaitAsync(token);
        try
        {
            // Drain sync through its final cache writes before a session can change regions.
            await CancelBackgroundWorkAsync();
            return await WithClient(() => { client.Restoring = false; return action(); }, token);
        }
        finally
        {
            ResumeBackgroundWork();
            signInGate.Release();
        }
    }
    public async Task<JsonElement> CallAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        Task<JsonElement> call; long id;
        lock (workGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            id = Interlocked.Increment(ref nextTask);
            call = CallCore(method, parameters, cancellationToken); background[id] = call;
        }
        try { return await call; } finally { background.TryRemove(id, out _); }
    }
    private async Task<JsonElement> CallCore(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var p = parameters.ValueKind == JsonValueKind.Object ? JsonNode.Parse(parameters.GetRawText())!.AsObject() : new JsonObject();
        var result = await Dispatch(method, p, linked.Token);
        return JsonSerializer.SerializeToElement(result);
    }
    private async Task<JsonNode?> Dispatch(string method, JsonObject p, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (method)
        {
            case "ping": return J.Node(new { pong = true });
            case "auth_status":
                var status = (JsonObject)Volatile.Read(ref authSnapshot).DeepClone(); status["signed_out"] = cache.Meta("signed_out") == "1"; status["has_cache"] = !status.Flag("signed_out") && cache.Lists.Count > 0; status["last_sync"] = cache.Meta("last_sync"); return status;
            case "login":
                if (IsLocalDemo) throw CoreException.Bad("Leave the local account before signing in again.").WithPresentation("ErrorLocalAccountStillActive");
                var account = (p.Optional("apple_id", 320) ?? cache.Meta("apple_id") ?? "").Trim().ToLowerInvariant(); if (account.Length == 0) throw new CoreException("AUTH_REQUIRED", "No Apple ID provided").WithPresentation("ErrorNoAppleIDProvided");
                if (DemoAccount.IsDemoUsername(account)) throw new CoreException("AUTH_REQUIRED", "Use the local sign-in flow for this account.").WithPresentation("ErrorLocalSignInRequired");
                var password = p.Optional("password", 4096);
                var login = await WithSignIn(async () =>
                {
                    var chinaMainland = p.Property("china_mainland") is null
                        ? account == client.Account && client.ChinaMainland : p.Flag("china_mainland");
                    // Mixing another account's cached reminders with a new session
                    // would upload edits to the wrong CloudKit container.
                    var switching = client.IsSwitchingAccount(account, chinaMainland);
                    cache.EnsureCanSwitchAccount(switching);
                    return await client.Connect(account, chinaMainland, password, cache.Settings.Flag("remember_password"), p.Flag("accept_terms"), token,
                        saveCredentials => cache.SaveAccount(account, chinaMainland, switching, saveCredentials));
                }, token);
                cache.SetMeta("signed_out", null);
                QueueSync(cache.Lists.Count == 0); return login;
            case "request_2fa": return await WithClient(() => client.RequestCode(p.Text("method") == "sms", token), token);
            case "browser_login":
                var browserAccount = p.Optional("apple_id", 320)?.Trim().ToLowerInvariant();
                var browserState = p["state"] as JsonObject ?? throw CoreException.Bad("Missing Apple session.").WithPresentation("ErrorMissingAppleSession");
                var renewSession = p.Flag("renew_session");
                var browserLogin = await WithSignIn(async () =>
                {
                    if (renewSession && cache.Meta("signed_out") == "1")
                        throw new CoreException("AUTH_REQUIRED", "No Apple ID provided").WithPresentation("ErrorNoAppleIDProvided");
                    var chinaMainland = browserState.Flag("china_mainland");
                    var authenticated = await client.ImportBrowserSession(browserAccount, browserState, token, account =>
                        cache.EnsureCanSwitchAccount(client.IsSwitchingAccount(account, chinaMainland)),
                        (account, saveCredentials) => cache.SaveAccount(account, chinaMainland, client.IsSwitchingAccount(account, chinaMainland), saveCredentials), renewSession);
                    cache.SetMeta("signed_out", null); return authenticated;
                }, token);
                QueueSync(cache.Lists.Count == 0); return browserLogin;
            case "submit_2fa":
                var code = p.Required("code"); var verified = await WithSignIn(() => client.SubmitCode(code, token), token); cache.SetMeta("signed_out", null); QueueSync(cache.Lists.Count == 0); return verified;
            case "sign_out":
                if (IsLocalDemo) { authSnapshot["authenticated"] = false; return J.Node(new { signed_out = true }); }
                // Restoration and full-history sync can hold the account gate
                // across slow HTTP calls. Cancel them before waiting to sign out.
                await CancelBackgroundWorkAsync();
                await WithClient(() => { client.SignOut(); cache.SetMeta("signed_out", "1"); return Task.FromResult(true); }, token);
                if (p.Flag("purge")) cache.SetMeta("sync_cursor", null); return J.Node(new { signed_out = true });
            case "unsynced_reminders": return cache.UnsyncedReminders();
            case "discard_unsynced":
                if (IsLocalDemo) throw CoreException.Bad("Local-account reminders have no iCloud copy to restore.").WithPresentation("ErrorLocalAccountCannotRestore");
                if (!p.Flag("confirmed")) throw CoreException.Bad("Confirm which unsynced changes to discard.").WithPresentation("ErrorDiscardNeedsConfirmation");
                if (p["targets"] is not JsonArray targets) throw CoreException.Bad("Select the reminders to discard.").WithPresentation("ErrorDiscardNoSelection");
                await CancelBackgroundWorkAsync();
                try
                {
                    var selected = cache.ValidateDiscard(targets);
                    var ids = selected.Select(row => row.Text("cloud_id")).OfType<string>().ToArray();
                    var snapshots = ids.Length == 0 ? new Dictionary<string, JsonObject?>()
                        : await WithClient(() => client.RecoverySnapshots(ids, token), token);
                    token.ThrowIfCancellationRequested();
                    return cache.DiscardUnsynced(targets, snapshots, token);
                }
                finally { ResumeBackgroundWork(); }
            case "clear_cache":
                if (IsLocalDemo) throw CoreException.Bad("Local-account reminders are stored only on this PC and are not a downloadable cache.").WithPresentation("ErrorLocalAccountCannotClearCache");
                await CancelBackgroundWorkAsync();
                try
                {
                    return await WithClient(() =>
                    {
                        return Task.FromResult(cache.Clear(p.Flag("discard_pending")));
                    }, token);
                }
                finally
                {
                    ResumeBackgroundWork();
                    if (!lifetime.IsCancellationRequested) await StartAsync(lifetime.Token);
                }
            case "lists": return cache.Lists;
            case "create_list":
                var listToCreate = ListCreation.FromRequest(p);
                if (IsLocalDemo)
                {
                    return cache.Transaction(() =>
                    {
                        var existing = cache.Lists.SingleOrDefault(list => list.Text("id") == listToCreate.Id);
                        if (existing is not null)
                            return existing.Text("title") == listToCreate.Title && existing.Text("color_hex") == listToCreate.Color &&
                                existing.Text("badge_emblem") == listToCreate.Badge ? existing : throw ListCreation.Conflict();
                        var listRecord = new JsonObject { ["recordName"] = listToCreate.Id, ["recordType"] = "List", ["fields"] = listToCreate.Fields() };
                        cache.ApplyRecordsInTransaction(new JsonArray(listRecord));
                        return cache.Lists.Single(list => list.Text("id") == listToCreate.Id);
                    });
                }
                return await WithClient(async () =>
                {
                    var created = await client.CreateList(listToCreate, token);
                    cache.ApplyRecords(new JsonArray(created.DeepClone()));
                    return cache.Lists.Single(list => list.Text("id") == listToCreate.Id);
                }, token);
            case "reminders": return cache.Reminders(p);
            case "reminder": return cache.Reminder(cache.ResolveId(p.Required("id")));
            case "tags": return cache.Tags;
            case "create_tag": throw new CoreException("UNSUPPORTED", "Tag editing is unavailable. Manage tags in Apple Reminders.").WithPresentation("ErrorTagEditingUnavailable");
            case "smart_counts": return cache.Counts;
            case "settings": return cache.Settings;
            case "set_settings": return cache.SetSettings(p);
            case "create_reminder":
                RejectTagEditing(p);
                var r = new JsonObject { ["id"] = "local/" + Guid.NewGuid(), ["list_id"] = p.Required("list_id"), ["title"] = p.Optional("title", 4096) ?? "", ["description"] = p.Optional("description", 100000) ?? "", ["due_date"] = TimeUtil.Normalize(p["due_date"], p.Flag("all_day")), ["priority"] = p.Number("priority"), ["completed"] = false, ["flagged"] = p.Flag("flagged"), ["all_day"] = p.Flag("all_day"), ["created"] = TimeUtil.Now, ["modified"] = TimeUtil.Now };
                r["recurrence"] = Recurrence.Validate(p["recurrence"], r.Text("due_date"));
                r["cloud_id"] = "Reminder/" + Guid.NewGuid().ToString().ToUpperInvariant();
                r["recurrence_id"] = Guid.NewGuid().ToString().ToUpperInvariant();
                cache.Transaction(() =>
                {
                    if (p.ContainsKey("parent_id") && p["parent_id"] is not null)
                    {
                        var parentId = cache.ResolveId(p.Required("parent_id"));
                        var parent = cache.Reminder(parentId);
                        if (parent is null || parent.Flag("deleted") || parent.Flag("completed")) throw CoreException.Bad("Choose an unfinished parent task.").WithPresentation("ErrorSubtaskParentMustBeUnfinished");
                        if (parent.Text("list_id") != r.Text("list_id")) throw CoreException.Bad("A subtask must use its parent's list.").WithPresentation("ErrorSubtaskListMismatch");
                        r["parent_id"] = parentId;
                    }
                    cache.Upsert(r, !IsLocalDemo); if (!IsLocalDemo) cache.Enqueue(r.Required("id"), "create", r, null); return true;
                }); QueueSync(); return cache.Reminder(r.Required("id"));
            case "update_reminder": return LocalChange(p, "update");
            case "move_reminder_to_today": return ChangeTodaySchedule(p, undo: false);
            case "undo_reminder_today": return ChangeTodaySchedule(p, undo: true);
            case "delete_reminder": return LocalChange(p, "delete");
            case "restore_reminder": return LocalChange(p, "restore");
            case "sync": return J.Node(new { queued = QueueSync(p.Flag("full"), p.Flag("automatic")), local_only = IsLocalDemo });
            case "sync_status": return J.Node(new { running = sync?.Running ?? false, local_only = IsLocalDemo, last_sync = cache.Meta("last_sync"), has_cursor = cache.Meta("sync_cursor") is not null, pending_pushes = cache.PendingCount, conflicts = cache.Conflicts.Count, sync_minutes = cache.Settings.Number("sync_minutes", 1) });
            case "conflicts": return cache.Conflicts;
            case "resolve_conflict":
                var conflictId = p.Number("id", -1); if (conflictId < 0) throw CoreException.Bad("missing conflict id").WithPresentation("ErrorMissingConflictId");
                var conflict = cache.Conflicts.FirstOrDefault(c => c.Number("id") == conflictId);
                if (p.Text("keep") == "local" && conflict?["local"] is JsonObject local)
                {
                    var patch = new JsonObject { ["id"] = conflict.Required("reminder_id") };
                    var savedPatch = local["conflicting_patch"] as JsonObject;
                    var changes = savedPatch ?? local;
                    foreach (var f in Cache.EditFields)
                        if (changes.ContainsKey(f) && (savedPatch is not null || f != "recurrence" || local.Flag("recurrence_edit"))) patch[f] = changes[f]?.DeepClone();
                    var operation = savedPatch?.ContainsKey("deleted") == true ? savedPatch.Flag("deleted") ? "delete" : "restore" : "update";
                    LocalChange(patch, operation);
                }
                cache.Execute("UPDATE conflicts SET resolved=1 WHERE id=@p0", conflictId); return J.Node(new { resolved = conflictId });
            case "due_notifications": return Notifications(p);
            case "shutdown": return J.Node(new { bye = true });
            default: throw new CoreException("NO_METHOD", $"unknown method '{method}'").WithPresentation("ErrorUnknownMethod", method);
        }
    }
    private static void RejectTagEditing(JsonObject p)
    {
        if (p.ContainsKey("tags")) throw new CoreException("UNSUPPORTED", "Tag editing is unavailable. Manage tags in Apple Reminders.").WithPresentation("ErrorTagEditingUnavailable");
    }
    private JsonNode LocalChangeOne(JsonObject p, string operation)
    {
        RejectTagEditing(p);
        var id = cache.ResolveId(p.Required("id")); JsonObject? result = null;
        {
            var current = cache.Reminder(id) ?? throw CoreException.Bad($"No such reminder: {id}").WithPresentation("ErrorNoSuchReminder", id); var fields = new JsonObject();
            if (operation is "delete" or "restore") fields["deleted"] = operation == "delete";
            if (operation == "restore" && current.Text("parent_id") is { } restoreParentId)
            {
                var parent = cache.Reminder(restoreParentId);
                if (parent is null || parent.Flag("deleted") || parent.Text("list_id") != current.Text("list_id")) fields["parent_id"] = null;
            }
            if (operation is not ("delete" or "restore"))
            {
                if (p.ContainsKey("list_id"))
                {
                    var listId = p.Required("list_id");
                    if (listId != current.Text("list_id"))
                    {
                        if (!cache.Lists.Any(list => list.Text("id") == listId && !list.Flag("is_group")))
                            throw CoreException.Bad("Choose an existing reminder list.").WithPresentation("ErrorReminderListUnavailable");
                        fields["list_id"] = listId;
                        if (current.Text("parent_id") is { } parentId && cache.Reminder(parentId).Text("list_id") != listId)
                            fields["parent_id"] = null;
                    }
                }
                if (p.ContainsKey("parent_id"))
                {
                    var requestedParent = p.Optional("parent_id", 2048);
                    if (requestedParent != current.Text("parent_id") && !(requestedParent is null && fields.ContainsKey("parent_id")))
                        throw CoreException.Bad("Create a subtask from its parent task. Changing a task's parent is not supported.").WithPresentation("ErrorSubtaskReparentingUnsupported");
                }
                foreach (var (key, max) in new[] { ("title", 4096), ("description", 100000) }) if (p.Optional(key, max) is { } value) fields[key] = value;
                foreach (var key in new[] { "priority", "completed", "flagged", "all_day" }) if (p.ContainsKey(key)) fields[key] = p[key]?.DeepClone();
                var allDay = fields.ContainsKey("all_day") ? fields.Flag("all_day") : current.Flag("all_day");
                if (p.ContainsKey("due_date")) fields["due_date"] = TimeUtil.Normalize(p["due_date"], allDay);
                else if (allDay != current.Flag("all_day") && current.Text("due_date") is { } raw)
                {
                    var zone = TimeUtil.Zone();
                    fields["due_date"] = allDay ? TimeUtil.CalendarDate(TimeUtil.AllDayDate(raw, zone)) : TimeUtil.Iso(TimeUtil.Parse(raw));
                }
                var due = fields.ContainsKey("due_date") ? fields.Text("due_date") : current.Text("due_date");
                if (p.ContainsKey("recurrence"))
                {
                    if (fields.Flag("completed")) throw CoreException.Bad("Save the repeat change before completing this reminder.").WithPresentation("ErrorSaveRepeatChangeBeforeCompletion");
                    if (current["recurrence"] is JsonObject existing && existing.Property("supported") is not null && !existing.Flag("supported"))
                        throw CoreException.Bad("Edit this custom repeat on an Apple device to preserve its schedule.").WithPresentation("EditCustomRepeatOnAppleDevice");
                    fields["recurrence"] = Recurrence.Validate(p["recurrence"], due);
                    fields["recurrence_id"] = Guid.NewGuid().ToString().ToUpperInvariant();
                    if (fields["recurrence"] is null) fields["recurrence_ids"] = new JsonArray();
                }
                var effectiveRepeat = fields.ContainsKey("recurrence") ? fields["recurrence"] : current["recurrence"];
                if (effectiveRepeat is not null)
                {
                    if (fields.ContainsKey("due_date") && fields["due_date"] is null) throw CoreException.Bad("A repeating reminder needs a due date.").WithPresentation("ErrorRepeatNeedsDueDate");
                    if (fields.Flag("completed") && effectiveRepeat.Property("supported")?.ToString() == "false")
                        throw CoreException.Bad("Complete this custom repeat on an Apple device to preserve its schedule.").WithPresentation("ErrorCustomRepeatOnAppleDevice");
                }
                if (fields.Flag("completed"))
                {
                    if (current.Flag("completed")) fields.Remove("completed");
                    else fields["completion_id"] = "Reminder/" + Guid.NewGuid().ToString().ToUpperInvariant();
                }
            }
            if (IsLocalDemo && fields.Flag("completed") && current["recurrence"] is JsonObject localRepeat && current.Text("due_date") is { } localDue)
            {
                var next = Recurrence.Next(new JsonObject { ["fields"] = Recurrence.Fields(id, localRepeat) }, localDue, current.Text("time_zone"), DateTimeOffset.UtcNow);
                if (next is not null)
                {
                    var occurrence = (JsonObject)current.DeepClone(); occurrence["id"] = fields.Required("completion_id");
                    occurrence["completed"] = true; occurrence["completed_date"] = TimeUtil.Now; occurrence["recurrence"] = null; occurrence["recurrence_ids"] = new JsonArray();
                    cache.Upsert(occurrence); fields["completed"] = false; fields["due_date"] = next;
                }
            }
            cache.Edit(id, fields, dirty: !IsLocalDemo);
            if (IsLocalDemo && fields.ContainsKey("completed")) cache.Execute("UPDATE reminders SET completed_date=@p0 WHERE id=@p1", fields.Flag("completed") ? TimeUtil.Now : null, id);
            if (!IsLocalDemo && fields.Count > 0)
            {
                var baseTag = p.Optional("edit_base_tag", 2048) ?? current.Text("change_tag");
                cache.Enqueue(id, operation == "delete" ? "delete" : "update", operation == "delete" ? new JsonObject() : fields, baseTag);
            }
            result = cache.Reminder(id);
        }
        return operation == "delete" ? J.Node(new { deleted = id }) : result!;
    }
    private JsonNode Notifications(JsonObject p)
    {
        var settings = cache.Settings; var toasts = new JsonArray(); var ids = new JsonArray();
        if (cache.Meta("signed_out") == "1" || !settings.Flag("notifications_enabled")) return new JsonObject { ["toasts"] = toasts, ["notified_ids"] = ids };
        var now = p.Text("now") is { } raw ? TimeUtil.Parse(raw) : DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-Math.Clamp(p.Number("stale_after_minutes", settings.Number("stale_after_minutes", 60)), 1, 1440));
        var localNow = TimeZoneInfo.ConvertTime(now, TimeUtil.Zone());
        var due = cache.Query("SELECT * FROM reminders WHERE deleted=0 AND completed=0 AND notified=0 AND due_date <= CASE WHEN all_day=1 THEN @p0 ELSE @p1 END ORDER BY due_date",
            TimeUtil.CalendarDate(localNow.Date), TimeUtil.Iso(now));
        var missed = new List<JsonNode>(); var fresh = new List<JsonNode>();
        foreach (var r in due.OfType<JsonObject>()) { var alert = TimeUtil.NotifyAt(r.Required("due_date"), r.Flag("all_day")); if (alert > now) continue; (alert < cutoff ? missed : fresh).Add(r); }
        static JsonNode Summary(List<JsonNode> reminders, bool missed)
        {
            var names = reminders.Take(3).Select(r => r.Text("title")).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            var count = reminders.Count;
            var title = (missed, count) switch
            {
                (true, 1) => "1 reminder was due while you were away",
                (true, _) => $"{count} reminders were due while you were away",
                _ => $"{count} reminders are due"
            };
            return J.Node(new
            {
                title,
                body = string.Join(", ", names) + (missed && count > 3 ? $", and {count - 3} more" : ""),
                reminder_id = (string?)null, kind = "summary", summary_kind = missed ? "missed" : "due", count, names
            });
        }
        if (missed.Count > 0) toasts.Add(Summary(missed, true));
        if (fresh.Count > Math.Clamp(p.Number("max_individual", settings.Number("max_individual_toasts", 3)), 1, 10))
            toasts.Add(Summary(fresh, false));
        else foreach (var reminder in fresh)
        {
            var rawTitle = reminder.Text("title");
            var dueAt = TimeZoneInfo.ConvertTime(TimeUtil.Parse(reminder.Required("due_date")), TimeUtil.Zone());
            var allDay = reminder.Flag("all_day");
            toasts.Add(J.Node(new
            {
                title = string.IsNullOrEmpty(rawTitle) ? "Reminder" : rawTitle,
                body = allDay ? dueAt.ToString("ddd dd MMM") : $"{dueAt:ddd dd MMM}, {dueAt:t}",
                reminder_id = reminder.Text("id"), kind = "reminder", raw_title = rawTitle, due_at = dueAt, all_day = allDay
            }));
        }
        foreach (var r in missed.Concat(fresh)) { ids.Add(r.Text("id")); cache.Execute("UPDATE reminders SET notified=1 WHERE id=@p0", r.Text("id")); }
        return new JsonObject { ["toasts"] = toasts, ["notified_ids"] = ids };
    }
    public async ValueTask DisposeAsync()
    {
        Task[] work;
        lock (workGate) { if (disposed) return; disposed = true; work = background.Values.ToArray(); }
        await lifetime.CancelAsync();
        // Failed direct calls have already reached their callers. Their failure
        // must not prevent disposal of the cache or HTTP client.
        try { await Task.WhenAll(work); } catch (Exception) { }
        await clientGate.WaitAsync();
        try
        {
            try { if (cloudClient?.Connected == true) cloudClient.Persist(); }
            finally { DisposeStorage(); }
        }
        finally { clientGate.Release(); }
        lifetime.Dispose(); backgroundLifetime.Dispose(); clientGate.Dispose(); signInGate.Dispose();
    }
}
