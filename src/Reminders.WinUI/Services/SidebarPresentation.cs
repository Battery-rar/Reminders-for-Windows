using System.Text.Json;
using Reminders.Core;

namespace Reminders.Windows.Services;

internal readonly record struct SidebarIcon(string Glyph, bool IsEmoji = false, bool IsFallback = false);

internal static class SidebarPresentation
{
    public const string Manual = "manual";
    public const string NameAscending = "name_asc";
    public const string NameDescending = "name_desc";
    public static readonly SidebarIcon DefaultIcon = new("\uE8FD", IsFallback: true);

    private static readonly Dictionary<string, string> WindowsGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = "\uE8FD", ["list"] = "\uE8FD", ["list.bullet"] = "\uE8FD", ["checklist"] = "\uE73A",
        ["tray"] = "\uE715", ["envelope"] = "\uE715", ["briefcase"] = "\uE821", ["work"] = "\uE821",
        ["house"] = "\uE80F", ["lifestyle"] = "\uE80F", ["book"] = "\uE7BE", ["education"] = "\uE7BE",
        ["graduationcap"] = "\uE7BE", ["cart"] = "\uE7BF", ["bag"] = "\uE7BF", ["shopping"] = "\uE7BF",
        ["airplane"] = "\uE709", ["vacation"] = "\uE709", ["car"] = "\uE804", ["transport"] = "\uE804",
        ["heart"] = "\uEB51", ["health"] = "\uEB51", ["star"] = "\uE734", ["bookmark"] = "\uE8A4", ["bookmarks"] = "\uE8A4",
        ["calendar"] = "\uE787", ["clock"] = "\uE823", ["bell"] = "\uEA8F", ["flag"] = "\uE7C1",
        ["folder"] = "\uE8B7", ["gift"] = "\uED55", ["celebration"] = "\uED55",
        ["dollarsign"] = "\uE8C7", ["finance"] = "\uE8C7", ["music.note"] = "\uEC4F", ["media"] = "\uEC4F",
        ["person"] = "\uE77B", ["people"] = "\uE716", ["location"] = "\uE81D", ["mappin"] = "\uE81D",
        ["fork.knife"] = "🍴", ["food"] = "🍴", ["wine"] = "🍷",
        ["figure.run"] = "🏃", ["fitness"] = "🏃", ["sport"] = "⚽",
        ["sun.max"] = "\uE706", ["weather"] = "\uE706", ["leaf"] = "\uE8BE", ["nature"] = "\uE8BE",
        ["pawprint"] = "🐾", ["pet"] = "🐾"
    };

    public static IEnumerable<T> Order<T>(IEnumerable<T> lists, string? sort, IEnumerable<string> manualOrder,
        Func<T, string> id, Func<T, string> title)
    {
        if (sort == NameAscending) return lists.OrderBy(title, StringComparer.CurrentCultureIgnoreCase);
        if (sort == NameDescending) return lists.OrderByDescending(title, StringComparer.CurrentCultureIgnoreCase);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in manualOrder)
            if (key is not null) positions.TryAdd(key, positions.Count);
        return lists.OrderBy(list => positions.GetValueOrDefault(id(list), int.MaxValue));
    }

    public static List<string>? Reorder(IEnumerable<string> ids, string moving, string anchor, bool after)
    {
        if (moving == anchor) return null;
        var order = ids.ToList();
        var previous = order.IndexOf(moving);
        if (previous < 0 || !order.Contains(anchor)) return null;
        order.RemoveAt(previous);
        var destination = order.IndexOf(anchor) + (after ? 1 : 0);
        if (destination == previous) return null;
        order.Insert(destination, moving);
        return order;
    }

    public static SidebarIcon ResolveIcon(string? badge)
    {
        var value = badge?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 1024) return DefaultIcon;
        if (value[0] == '{')
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                var icon = document.RootElement;
                if (icon.TryGetProperty("Emoji", out var emoji))
                    return emoji.ValueKind == JsonValueKind.String && EmojiGlyph.IsValid(emoji.GetString())
                        ? new(emoji.GetString()!, true) : DefaultIcon;
                value = icon.TryGetProperty("SymbolName", out var symbol) && symbol.ValueKind == JsonValueKind.String ? symbol.GetString()! : "";
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { return DefaultIcon; }
        }
        if (EmojiGlyph.IsValid(value)) return new(value, true);
        if (value.Length == 1 && WindowsGlyphs.ContainsValue(value)) return new(value);
        value = value.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        while (value.EndsWith(".fill", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".circle", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".square", StringComparison.OrdinalIgnoreCase))
            value = value[..value.LastIndexOf('.')];
        return WindowsGlyphs.TryGetValue(value, out var glyph) ? new(glyph, EmojiGlyph.IsValid(glyph)) : DefaultIcon;
    }
}
