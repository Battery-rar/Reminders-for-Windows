using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed record ListCreation(string Id, string Title, string Color, string Badge)
{
    private static readonly Dictionary<string, (string Cloud, string Native)> Palette = new(StringComparer.Ordinal)
    {
        ["#FF3B30"] = ("red", "red"), ["#FF9500"] = ("orange", "orange"), ["#FFCC00"] = ("yellow", "yellow"),
        ["#34C759"] = ("green", "green"), ["#51AAF2"] = ("lightBlue", "custom"), ["#007AFF"] = ("blue", "blue"),
        ["#5856D6"] = ("indigo", "custom"), ["#EA426A"] = ("pink", "custom"), ["#C077DB"] = ("purple", "purple"),
        ["#A2845E"] = ("brown", "custom"), ["#5B6770"] = ("gray", "custom"), ["#D9A69F"] = ("rose", "custom"),
    };
    private static readonly HashSet<string> Badges = new(("default bookmarks1 bookmarks2 celebration1 celebration2 " +
        "education1 education2 education3 education4 education5 finance1 finance2 finance3 fitness sport6 food wine " +
        "health1 health2 lifestyle1 lifestyle2 location1 location2 location3 media1 media2 media3 media4 media5 nature1 nature2 " +
        "people1 people2 people3 pet1 pet2 pet3 shopping1 shopping2 shopping3 shopping4 sport1 sport2 sport3 sport4 sport5 " +
        "transport1 transport2 transport3 transport4 vacation weather2 weather1 weather3 weather4 weather5 " +
        "work1 work2 work3 work4 work5 concept1 concept2 concept3 symbol7 symbol4 symbol3 symbol2 symbol5 symbol6 symbol1").Split(' '), StringComparer.Ordinal);

    private static CoreException Invalid() => CoreException.Bad("Enter a list name of up to 255 UTF-8 bytes and select a valid color and icon.").WithPresentation("ErrorInvalidNewList");
    internal static CoreException OrderingUnavailable() => new CoreException("UNSUPPORTED", "iCloud's list ordering could not be updated safely. Try creating the list in Apple Reminders.").WithPresentation("ErrorListOrderingUnavailable");
    internal static CoreException Conflict() => new CoreException("CONFLICT", "iCloud could not confirm this list creation. Refresh your lists and try again.").WithPresentation("ErrorListCreateConflict");

    internal static ListCreation FromRequest(JsonObject request)
    {
        var title = request.Text("title")?.Trim();
        var color = request["color_hex"] is null ? "#007AFF" : request.Text("color_hex")?.ToUpperInvariant();
        var badge = request["badge_emblem"] is null ? "default" : request.Text("badge_emblem");
        if (string.IsNullOrEmpty(title) || Encoding.UTF8.GetByteCount(title) > 255 || title.Any(char.IsControl) ||
            color is not { Length: 7 } || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit) || badge is null || !Badges.Contains(badge))
            throw Invalid();
        var id = Guid.NewGuid();
        if (request["request_id"] is not null && (!Guid.TryParse(request.Text("request_id"), out id) || id == Guid.Empty)) throw Invalid();
        return new ListCreation("List/" + id.ToString("D").ToUpperInvariant(), title, color, badge);
    }

    internal JsonObject Fields()
    {
        var (cloud, native) = Palette.GetValueOrDefault(Color, ("custom", "custom"));
        var rgb = int.Parse(Color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["Name"] = J.Node(new { type = "STRING", isEncrypted = true, value = Title }),
            ["Color"] = CloudKit.String(J.Node(new
            {
                daSymbolicColorName = native, ckSymbolicColorName = cloud, daHexString = Color,
                red = rgb >> 16, green = rgb >> 8 & 255, blue = rgb & 255, alpha = 1, colorRGBSpace = 2,
            }).ToJsonString()),
            ["BadgeEmblem"] = CloudKit.String(Badge),
            ["IsLinkedToAccount"] = CloudKit.Number(1),
            ["ReminderIDs"] = CloudKit.String("[]"),
            ["ResolutionTokenMap"] = CloudKit.ResolutionTokens(["reminderIDsMergeableOrdering", "name", "parentAccount", "badgeEmblem", "color"]),
        };
    }

    internal bool Matches(JsonNode list) => list.Text("recordName") == Id && list.Text("recordType") == "List" &&
        !list.Flag("deleted") && list.Text("reason") != "deleted" && CloudKit.Int(list, "Deleted") == 0 && CloudKit.Int(list, "IsGroup") == 0 &&
        CloudKit.Text(list, "Name") == Title && J.Parse(CloudKit.Text(list, "Color")).Text("daHexString")?.ToUpperInvariant() == Color &&
        (CloudKit.Text(list, "BadgeEmblem") ?? "default") == Badge;
}
