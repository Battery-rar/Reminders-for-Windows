using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class ICloudClient
{
    internal async Task<JsonNode> CreateList(ListCreation list, CancellationToken token)
    {
        var cloud = Cloud;
        for (var attempt = 0; ; attempt++)
        {
            var (records, _) = await cloud.Changes(null, ["Account"], token);
            var accounts = records.Where(record => record.Text("recordType") == "Account" && !record.Flag("deleted") &&
                record.Text("reason") != "deleted" && record.Text("recordName") is { Length: > 8 } name && name.StartsWith("Account/", StringComparison.Ordinal)).ToArray();
            if (accounts.Length != 1 || accounts[0] is not { } account || account.Text("recordChangeTag") is not { Length: > 0 } tag ||
                CloudKit.Field(account, "ListIDsMergeableOrderingAsset") is not null ||
                CloudKit.Field(account, "ListIDsMergeableOrdering") is not JsonValue archive || !archive.TryGetValue<string>(out var ordering) ||
                string.IsNullOrEmpty(ordering) || ordering.Length > ((100 * 1024 + 2) / 3) * 4)
                throw ListCreation.OrderingUnavailable();

            var updated = AppleDocument.AppendList(ordering, list.Id["List/".Length..], out var alreadyPresent);
            if (alreadyPresent)
            {
                // A dropped CloudKit response can leave the whole atomic write saved.
                var existing = (await cloud.LookupMany([list.Id], token, allowMissing: true)).SingleOrDefault();
                return existing is not null && list.Matches(existing) ? existing : throw ListCreation.Conflict();
            }

            // The service caps an inline CloudKit field. Apple stores larger
            // documents in a separate asset; this path preserves those accounts.
            if (Convert.FromBase64String(updated).Length > 100 * 1024) throw ListCreation.OrderingUnavailable();
            if (CloudKit.Field(account, "ResolutionTokenMap") is not null &&
                J.Parse(CloudKit.Text(account, "ResolutionTokenMap"))?.Property("map") is not JsonObject)
                throw ListCreation.OrderingUnavailable();
            var accountFields = new JsonObject
            {
                ["ListIDsMergeableOrdering"] = J.Node(new { value = updated }),
                ["ResolutionTokenMap"] = CloudKit.ResolutionTokens(["listIDsMergeableOrdering"], account),
            };
            try
            {
                var operations = new JsonArray(CloudKit.Operation("create", list.Id, null, list.Fields(), "List"),
                    CloudKit.Operation("update", account.Required("recordName"), tag, accountFields, "Account"));
                var saved = (await cloud.Modify(operations, token)).SingleOrDefault(record => record.Text("recordName") == list.Id);
                if (saved is not null && list.Matches(saved)) return saved;
                var readBack = (await cloud.LookupMany([list.Id], token, allowMissing: true)).SingleOrDefault();
                return readBack is not null && list.Matches(readBack) ? readBack : throw ListCreation.Conflict();
            }
            catch (CoreException error) when (error.Code == "CONFLICT" && attempt == 0)
            {
                // Refetch the account document and change tag before one retry.
            }
            catch (CoreException error) when (error.Code == "CONFLICT")
            {
                throw ListCreation.Conflict();
            }
        }
    }
}
