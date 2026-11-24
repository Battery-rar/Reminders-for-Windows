using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Reminders.Core;

internal static class ManualReminderOrderChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-manual-order-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "cache.db");
        var secrets = new MemorySecrets();
        const string list = "List/a";
        var anchor = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        JsonNode Row(string id, int created, int? due, long priority = 0, string listId = list) => J.Node(new
        {
            id, list_id = listId, title = id, priority,
            created = TimeUtil.Iso(anchor.AddDays(created)),
            due_date = due is int day ? TimeUtil.Iso(anchor.AddDays(day)) : null
        });
        static async Task<JsonElement> Call(ReminderService service, string method, object parameters) =>
            await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters));
        static async Task<string?[]> Rows(ReminderService service, object filter)
        {
            var rows = await Call(service, "reminders", filter);
            return rows.EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray();
        }
        async Task BadOrder(ReminderService service, object filter, string message)
        {
            try { await Call(service, "save_reminder_order", filter); }
            catch (CoreException error) when (error.Code == "BAD_REQUEST") { check(true, message); return; }
            check(false, message);
        }

        try
        {
            using (var seed = new Cache(path))
            {
                seed.Upsert(Row("a", 0, 6, 1));
                seed.Upsert(Row("b", 1, 2, 5));
                seed.Upsert(Row("local/c", 2, 4, 1));
                seed.Upsert(Row("d", 3, null, 1));
                seed.Upsert(Row("other", 4, 1, 9, "List/b"));
                seed.Edit("b", new JsonObject { ["parent_id"] = "a" }, dirty: false);
                seed.ReplaceTags("b", new JsonArray(J.Node(new { id = "tag/b", name = "work" })));
                seed.ReplaceTags("d", new JsonArray(J.Node(new { id = "tag/d", name = "work" })));
            }

            await using (var service = new ReminderService(root, secrets))
            {
                var filter = new { list_id = list, sort = "manual", include_completed = true };
                check((await Rows(service, filter)).SequenceEqual(new[] { "a", "b", "local/c", "d" }),
                    "Manual order without ranks starts in creation order, independently of due date");
                check((await Rows(service, new { list_id = list, sort = "priority" })).SequenceEqual(new[] { "local/c", "a", "d", "b" }),
                    "priority takes precedence, with due date and then undated within the same priority");
                await Call(service, "save_reminder_order", new { list_id = list, ids = new[] { "local/c", "b", "a", "d" } });
                check((await Rows(service, filter)).SequenceEqual(new[] { "local/c", "b", "a", "d" }), "saved manual order is immediately visible");
                check((await Rows(service, new { scope = "all", sort = "manual" })).SequenceEqual(new[] { "a", "b", "local/c", "d", "other" }),
                    "saving one list does not change the smart views");
                var tagged = new { tag = "work", sort = "manual", include_parents = true };
                check((await Rows(service, tagged)).SequenceEqual(new[] { "a", "b", "d" }), "the tag view starts in creation order including parent context");
                await Call(service, "save_reminder_order", new { tag = "work", ids = new[] { "d", "a", "b" } });
                check((await Rows(service, tagged)).SequenceEqual(new[] { "d", "a", "b" }), "tag view ranks apply to visible parent context");
                check((await Rows(service, filter)).SequenceEqual(new[] { "local/c", "b", "a", "d" }), "saving a tag view leaves the custom list order intact");
                await BadOrder(service, new { list_id = list, ids = new[] { "a", "missing" } }, "manual order rejects IDs outside the cache");
                await BadOrder(service, new { list_id = list, ids = new[] { "a", "other" } }, "a list reorder rejects an item in a different list");
                await BadOrder(service, new { list_id = list, scope = "all", ids = new[] { "b", "a" } }, "manual order rejects ambiguous view selectors");
                await BadOrder(service, new { list_id = list, ids = new[] { "a", "a" } }, "manual order rejects duplicates");
                await BadOrder(service, new { list_id = list, ids = new[] { "a" } }, "manual order requires enough IDs for a move");
                await BadOrder(service, new { list_id = list, ids = Enumerable.Repeat("a", 10001).ToArray() }, "manual order bounds the number of IDs");
                // b remains ranked even when the current presentation omits it.
                await Call(service, "save_reminder_order", new { list_id = list, ids = new[] { "a", "local/c", "d" } });
                check((await Rows(service, filter)).SequenceEqual(new[] { "a", "b", "local/c", "d" }), "reordering a subset preserves the omitted ranked reminder's slot");
            }

            using (var cache = new Cache(path))
            {
                cache.ReplaceId("local/c", "cloud/c");
                cache.Upsert(Row("a", 0, 0, 9));
                cache.Upsert(Row("new", 1, 0, 1));
                check(cache.PendingCount == 0, "manual order neither changes nor enqueues Apple reminder writes");
            }
            await using (var reopened = new ReminderService(root, secrets))
            {
                var filter = new { list_id = list, sort = "manual" };
                check((await Rows(reopened, filter)).SequenceEqual(new[] { "a", "b", "cloud/c", "d", "new" }),
                    "manual order survives restart, an upload ID remap, sync date and priority edits, and new items");
                await Call(reopened, "save_reminder_order", new { list_id = list, ids = new[] { "local/c", "b", "a", "d", "new" } });
                check((await Rows(reopened, filter)).SequenceEqual(new[] { "cloud/c", "b", "a", "d", "new" }),
                    "a reorder finishing after an ID remap resolves the old source ID");
            }

            using (var cache = new Cache(path))
            {
                cache.SaveAccount("another@example.test", false, true, () => { });
                cache.Upsert(Row("a", 0, 6));
                cache.Upsert(Row("cloud/c", 1, 4));
                check(cache.Reminders(J.Node(new { list_id = list, sort = "manual" })).Select(r => r.Text("id"))
                    .SequenceEqual(new[] { "a", "cloud/c" }), "account changes purge reminder ranks even for reused IDs");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
