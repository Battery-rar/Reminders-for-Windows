using System.Globalization;
using System.Text;

namespace Reminders.Core;

public static class EmojiGlyph
{
    public static bool IsValid(string? text)
    {
        if (text is null || text.Length is 0 or > 64 || StringInfo.GetNextTextElementLength(text) != text.Length ||
            !Rune.TryGetRuneAt(text, 0, out var first) || Rune.GetUnicodeCategory(first) == UnicodeCategory.OtherNotAssigned ||
            text.EndsWith('\u200D')) return false;
        if (first.Value is '#' or '*' or (>= '0' and <= '9')) return text.EndsWith('\u20E3');
        return first.Value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF
            or >= 0x23E9 and <= 0x23FA or >= 0x2194 and <= 0x2199 or >= 0x2B05 and <= 0x2B07
            or 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x21A9 or 0x21AA
            or 0x231A or 0x231B or 0x2328 or 0x23CF or 0x24C2 or 0x2934 or 0x2935 or 0x2B1B
            or 0x2B1C or 0x2B50 or 0x2B55 or 0x3030 or 0x303D or 0x3297 or 0x3299;
    }
}
