using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;
using Reminders.Windows.Services;

internal static class SidebarChecks
{
    public static void Run(Action<bool, string> check)
    {
        var family = "👩‍👩‍👧‍👦";
        check(SidebarPresentation.ResolveIcon("{\"Emoji\":\"" + family + "\"}") == new SidebarIcon(family, true), "iCloud emoji retains the entire grapheme");
        check(SidebarPresentation.ResolveIcon("cart.fill").Glyph == "\uE7BF", "SF Symbol names map to readable Windows icons");
        check(SidebarPresentation.ResolveIcon("shopping4").Glyph == "\uE7BF", "iCloud badge names map by category");
        check(SidebarPresentation.ResolveIcon("\uE734").Glyph == "\uE734", "previous custom Windows icons retain their appearance");
        check(SidebarPresentation.ResolveIcon("unknown.symbol") == SidebarPresentation.DefaultIcon &&
            SidebarPresentation.ResolveIcon("{\"Emoji\":\"a💼\"}") == SidebarPresentation.DefaultIcon, "unknown names and non-emoji text use the list fallback");
        check(SidebarPresentation.ResolveIcon("1️⃣").IsEmoji && SidebarPresentation.ResolveIcon("🇨🇳").IsEmoji, "keycap and flag emoji render as single symbols");
        check(SidebarPresentation.ResolveIcon("↔️").IsEmoji && SidebarPresentation.ResolveIcon("⬆️").IsEmoji, "BMP direction emoji render");
        check(SidebarPresentation.ResolveIcon("""{"Emoji":"\uD800"}""").IsFallback, "a malformed escaped emoji uses the fallback");
        check(!SidebarPresentation.ResolveIcon("list.bullet").IsFallback, "a requested native list icon is distinct from an unresolved symbol");

        var input = new[] { (Id: "b", Title: "Beta"), (Id: "a", Title: "Alpha"), (Id: "c", Title: "Gamma") };
        string Order(string sort, params string[] manual) => string.Join("", SidebarPresentation.Order(input, sort, manual, x => x.Id, x => x.Title).Select(x => x.Id));
        check(Order("manual", "c", "c", "removed", "a") == "cab", "manual order skips duplicate and removed entries and appends new lists");
        check(Order("name_asc", "c", "b") == "abc" && Order("name_desc", "a") == "cba", "name sorting is independent of saved manual order");
        string[] manual = ["a", "b", "c", "d"];
        check(SidebarPresentation.Reorder(manual, "a", "c", after: true)?.SequenceEqual(["b", "c", "a", "d"]) == true,
            "a sidebar drop after an item only moves the dragged list");
        check(SidebarPresentation.Reorder(manual, "d", "b", after: false)?.SequenceEqual(["a", "d", "b", "c"]) == true,
            "a sidebar drop before an item preserves the other list positions");
        check(SidebarPresentation.Reorder(manual, "a", "b", after: false) is null &&
            SidebarPresentation.Reorder(manual, "b", "b", after: true) is null &&
            SidebarPresentation.Reorder(manual, "a", "removed", after: true) is null,
            "no-op and stale sidebar drops never save a new order");
        var saved = new UiPreferenceData { ListSort = "name_desc", ListOrder = ["c", "b"], Lists = new() { ["a"] = new() { Glyph = family } } };
        var restored = JsonSerializer.Deserialize<UiPreferenceData>(JsonSerializer.Serialize(saved))!;
        check(restored.ListSort == "name_desc" && restored.ListOrder.SequenceEqual(saved.ListOrder) && restored.Lists["a"].Glyph == family,
            "sidebar order, sort mode and Unicode icon survive persistence");

        var directory = Path.Combine(Path.GetTempPath(), "reminders-sidebar-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cache = new Cache(Path.Combine(directory, "cache.db"));
            var emoji = "{\"Emoji\":\"🛒\"}";
            var field = J.Node(new { type = "ENCRYPTED_BYTES", value = Convert.ToBase64String(Encoding.UTF8.GetBytes(emoji)) });
            cache.ApplyRecords(new JsonArray(J.Node(new { recordName = "List/s", recordType = "List", fields = new { Name = CloudKit.String("Shopping"), BadgeEmblem = field } })));
            cache.ApplyRecords(new JsonArray(J.Node(new { recordName = "List/s", recordType = "List", fields = new { Name = CloudKit.String("Groceries") } })));
            var row = cache.Lists.Single();
            check(row.Text("badge_emblem") == emoji && row.Text("title") == "Groceries" && row.Property("badge_field") is null,
                "list icon survives partial CloudKit updates and cache supplies only the decoded icon");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
