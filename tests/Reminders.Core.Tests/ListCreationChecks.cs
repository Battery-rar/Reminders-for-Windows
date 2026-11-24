using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class ListCreationChecks
{
    // Synthetic archive from Apple's web CRArray after appending on two replicas
    // and deleting the middle list. Contains two live IDs and a tombstone.
    private const string NativeOrder = "ChAKBggAEAEYAAoGCAEQARgAGtABes0BCqABCnISBu+/vO+/vBoQCgQIABAAEAAaBAgAEAAoARoQCgQIARAAEAEaBAgBEAAoAhoSCgQIARABEAEaBAgCEAggASgDGhAKBAgCEAEQARoECAIQACgEGhYKCAgAEP////8PEAAaCAgAEP////8PKgIIASoCCAESFAgAEhA/LCohz1nPL6gTq/5nKCqKEhQIARIQXaolYHP8ARkMiBYb65OrihIoChIKAjACEgIwARoICgYIABABGAAKEgoCMAQSAjADGggKBggBEAEYABowai4IARoqCAASJiIkNDAwMDAwMDAtMDAwMC00MDAwLTgwMDAtMDAwMDAwMDAwMDA0GgxqCggCGgYIARICEAIaMGouCAEaKggAEiYiJDYwMDAwMDAwLTAwMDAtNDAwMC04MDAwLTAwMDAwMDAwMDAwNhoMagoIAhoGCAESAhADIgRzZWxmIglVVUlESW5kZXgqF2NvbS5hcHBsZS5DUkRULk5TTnVtYmVyKhdjb20uYXBwbGUuQ1JEVC5OU1N0cmluZyoVY29tLmFwcGxlLkNSRFQuTlNVVUlEKhdjb20uYXBwbGUubm90ZXMuQ1JUYWJsZSoXY29tLmFwcGxlLm5vdGVzLklDVGFibGUqF2NvbS5hcHBsZS5ub3Rlcy5DUlRhYmxlKhdjb20uYXBwbGUubm90ZXMuSUNUYWJsZSoXY29tLmFwcGxlLm5vdGVzLkNSVGFibGUqF2NvbS5hcHBsZS5ub3Rlcy5JQ1RhYmxlKhdjb20uYXBwbGUubm90ZXMuQ1JUYWJsZSoXY29tLmFwcGxlLm5vdGVzLklDVGFibGUqF2NvbS5hcHBsZS5ub3Rlcy5DUlRhYmxlKhdjb20uYXBwbGUubm90ZXMuSUNUYWJsZSoXY29tLmFwcGxlLm5vdGVzLkNSVGFibGUqF2NvbS5hcHBsZS5ub3Rlcy5JQ1RhYmxlMhAQAAAAAAAAQAAIAAAAAAAAMhAgAAAAAAAAQAAIAAAAAAAAMhA/LCohz1nPL6gTq/5nKCqKMhBdqiVgc/wBGQyIFhvrk6uKOjgKGgoQEAAAAAAAAEAACAAAAAAAABICCAISAggBChoKECAAAAAAAABAAAgAAAAAAAASAggCEgIICQ==";
    private const string FirstList = "40000000-0000-4000-8000-000000000004";
    private const string DeletedList = "50000000-0000-4000-8000-000000000005";
    private const string LastList = "60000000-0000-4000-8000-000000000006";
    private const string RequestId = "A0000000-0000-4000-8000-000000000007";
    private const string NativeEmptyOrder = "CgAaNHoyCi4KLBIAGhAKBAgAEAAQABoECAAQACgBGhYKCAgAEP////8PEAAaCAgAEP////8PEgAqF2NvbS5hcHBsZS5DUkRULk5TTnVtYmVyKhdjb20uYXBwbGUuQ1JEVC5OU1N0cmluZyoVY29tLmFwcGxlLkNSRFQuTlNVVUlEOgA=";
    // The empty archive above with only the unused type names removed.
    private const string EmptyOrderWithoutTypes = "CgAaNHoyCi4KLBIAGhAKBAgAEAAQABoECAAQACgBGhYKCAgAEP////8PEAAaCAgAEP////8PEgA6AA==";

    public static async Task RunAsync(Action<bool, string> check)
    {
        NewListEmojiChecks(check);
        var updated = AppleDocument.AppendList(NativeOrder, RequestId, out var alreadyPresent);
        check(!alreadyPresent && updated != NativeOrder, "append adds a missing list to Apple’s account order");
        foreach (var id in new[] { FirstList, LastList, RequestId })
            check(AppleDocument.AppendList(updated, id, out var present) == updated && present, "ordering preserves live entry " + id);
        AppleDocument.AppendList(updated, DeletedList, out var deleted);
        check(!deleted, "a historical tombstone does not count as a live list");
        foreach (var empty in new[] { NativeEmptyOrder, EmptyOrderWithoutTypes })
        {
            var first = AppleDocument.AppendList(empty, RequestId, out var existed);
            check(!existed && AppleDocument.AppendList(first, RequestId, out var committed) == first && committed, "an empty account can add and recover its first list");
        }

        await CloudChecks(check);
    }

    private static void NewListEmojiChecks(Action<bool, string> check)
    {
        const string family = "👨‍👩‍👧‍👦";
        var request = new JsonObject { ["title"] = "Family", ["badge_emblem"] = "{ \"Emoji\": \"" + family + "\" }" };
        ListCreation list;
        try { list = ListCreation.FromRequest(request); }
        catch (CoreException) { check(false, "new lists accept a single Apple Emoji badge"); return; }
        var fields = list.Fields();
        var badge = CloudKit.Text(new JsonObject { ["fields"] = fields }, "BadgeEmblem");
        check(J.Parse(badge).Text("Emoji") == family && Reminders.Windows.Services.SidebarPresentation.ResolveIcon(badge).IsEmoji,
            "new lists send the complete emoji in the Apple badge and render it on Windows");
        fields["BadgeEmblem"] = CloudKit.String("{ \"Emoji\": \"" + family + "\" }");
        check(list.Matches(new JsonObject { ["recordName"] = list.Id, ["recordType"] = "List", ["fields"] = fields.DeepClone() }),
            "an equivalent emoji after CloudKit readback matches the created list");
        foreach (var invalid in new[] { "{\"Emoji\":\"hello\"}", "{\"Emoji\":\"😃😃\"}", "{\"Emoji\":\"😃\",\"other\":true}", "{\"Emoji\":\"\\uD800\"}", "{\"Emoji\":123}", "{broken" })
        {
            request["badge_emblem"] = invalid;
            try { ListCreation.FromRequest(request); check(false, "invalid Apple emoji badge is rejected"); }
            catch (CoreException error) { check(error.Code == "BAD_REQUEST", "invalid Apple emoji badge is rejected"); }
        }
    }

    private static async Task CloudChecks(Action<bool, string> check)
    {
        const string accountName = "Account/_test-user";
        const string email = "list-test@example.com";
        var request = J.Node(new { title = "  购物 🛒  ", color_hex = "#007aff", badge_emblem = "shopping2", request_id = RequestId }).AsObject();
        var list = ListCreation.FromRequest(request);
        var account = J.Node(new
        {
            recordName = accountName, recordType = "Account", recordChangeTag = "revision-1",
            fields = new
            {
                ListIDsMergeableOrdering = new { type = "BYTES", value = NativeOrder },
                SharedListIDsToOwnerNames = new { type = "STRING", value = "{\"shared-list\":\"shared-owner\"}" },
                ResolutionTokenMap = new { type = "STRING", value = "{\"map\":{\"listIDsMergeableOrdering\":{\"counter\":3},\"didFinishMigration\":{\"counter\":9}}}" },
            },
        });
        JsonNode? savedList = null;
        var writes = 0;
        var transport = new SecretStore(new MemorySecrets());
        transport.Session(email, J.Node(new { session_token = "mock" }).ToJsonString());
        static HttpResponseMessage Reply(JsonNode value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString()) };
        using var client = new ICloudClient(email, transport, () => new ScriptHandler(async request =>
        {
            var body = J.Parse(await request.Content!.ReadAsStringAsync())!;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", appleId = email, hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
            if (path.EndsWith("/changes/zone"))
            {
                var zone = body.Array("zones").Single();
                check(zone.Array("desiredRecordTypes").Single()!.ToString() == "Account" && zone?["zoneID"].Text("zoneName") == "Reminders",
                    "list creation reads account metadata from the Reminders zone");
                return Reply(new JsonObject { ["zones"] = new JsonArray(new JsonObject { ["records"] = new JsonArray(account.DeepClone()) }) });
            }
            if (path.EndsWith("/records/lookup"))
            {
                check(body.Array("records").Single().Text("recordName") == list.Id, "retry looks up only the intended list");
                return Reply(new JsonObject { ["records"] = new JsonArray(savedList!.DeepClone()) });
            }
            if (!path.EndsWith("/records/modify")) throw new InvalidOperationException("Unexpected CloudKit endpoint.");

            var operations = body.Array("operations");
            check(body.Flag("atomic") && operations.Count == 2, "new list and account ordering update atomically");
            var create = operations[0]!["record"]!;
            var accountUpdate = operations[1]!["record"]!;
            check(operations[0].Text("operationType") == "create" && create.Text("recordType") == "List" && create.Text("recordName") == list.Id,
                "new personal list uses the stable native record ID");
            check(create["fields"]?["Name"].Flag("isEncrypted") == true && CloudKit.Text(create, "Name") == "购物 🛒" &&
                CloudKit.Text(create, "ReminderIDs") == "[]" && CloudKit.Int(create, "IsLinkedToAccount") == 1 &&
                CloudKit.Text(create, "BadgeEmblem") == "shopping2" && J.Parse(CloudKit.Text(create, "Color")).Text("ckSymbolicColorName") == "blue",
                "new list writes native fields, an encrypted name, and empty reminder ordering");
            check(operations[1].Text("operationType") == "update" && accountUpdate.Text("recordName") == accountName &&
                accountUpdate.Text("recordChangeTag") == account.Text("recordChangeTag") && accountUpdate["fields"]!.AsObject().Count == 2,
                "account update uses current revision and patches only ordering plus its token");
            var map = J.Parse(CloudKit.Text(accountUpdate, "ResolutionTokenMap"))?["map"];
            check(CloudKit.Field(accountUpdate, "ListIDsMergeableOrdering") is JsonValue archive && archive.TryGetValue<string>(out var updatedOrder) &&
                AppleDocument.AppendList(updatedOrder, RequestId, out var present) == updatedOrder && present,
                "the account update includes the created list in its inline binary archive");
            check(map?["listIDsMergeableOrdering"].Number("counter") == 4 && map?["didFinishMigration"].Number("counter") == 9,
                "ordering update advances only its own resolution token");
            if (writes++ == 0)
            {
                account["recordChangeTag"] = "revision-2";
                return Reply(J.Node(new { records = new[] { new { recordName = accountName, serverErrorCode = "CONFLICT" } } }));
            }
            foreach (var field in accountUpdate["fields"]!.AsObject()) account["fields"]![field.Key] = field.Value?.DeepClone();
            savedList = create.DeepClone();
            savedList["fields"]!["Name"] = J.Node(new { type = "ENCRYPTED_BYTES", value = Convert.ToBase64String(Encoding.UTF8.GetBytes(list.Title)) });
            throw new HttpRequestException("Simulated loss of the successful response.");
        }));

        check(await client.Restore(default), "create-list CloudKit fixture authenticates");
        try { await client.CreateList(list, default); check(false, "a lost response remains retryable"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "a lost response remains retryable"); }
        check(writes == 2, "concurrent account updates use a single fresh-merge retry");
        var acknowledged = await client.CreateList(list, default);
        check(writes == 2 && list.Matches(acknowledged), "stable request retry recovers the committed list without a second upload");
        try { await client.CreateList(list with { Title = "Different list" }, default); check(false, "request ID collision is rejected"); }
        catch (CoreException error) { check(error.Code == "CONFLICT" && writes == 2, "request ID collision is rejected without writes"); }
        account["fields"]!["ListIDsMergeableOrderingAsset"] = J.Node(new { type = "ASSETID", value = new { downloadURL = "https://example.invalid/list-order" } });
        try { await client.CreateList(list, default); check(false, "asset-backed accounts preserve their list ordering"); }
        catch (CoreException error) { check(error.Code == "UNSUPPORTED" && writes == 2, "asset-backed accounts are detected before cloud writes"); }
        account["fields"]!.AsObject().Remove("ListIDsMergeableOrderingAsset");
        account["fields"]!["ListIDsMergeableOrdering"] = J.Node(new { type = "BYTES", value = "" });
        try { await client.CreateList(list, default); check(false, "missing account order cannot be replaced by a new archive"); }
        catch (CoreException error) { check(error.Code == "UNSUPPORTED" && writes == 2, "missing account order is detected before cloud writes"); }
    }
}
