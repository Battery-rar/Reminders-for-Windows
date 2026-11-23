using System.Globalization;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal static class TimeUtil
{
    private const string CalendarDateFormat = "yyyy-MM-dd";

    public static TimeZoneInfo Zone(string? name = null)
    {
        if (name is not null) { try { return TimeZoneInfo.FindSystemTimeZoneById(name); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { } }
        return TimeZoneInfo.Local;
    }
    public static DateTimeOffset Anchor(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        for (var i = 0; i < 180 && zone.IsInvalidTime(wall); i++) wall = wall.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }
    public static DateTimeOffset Parse(string value)
    {
        var explicitZone = value.EndsWith('Z') || (value.Length > 10 && (value[10..].Contains('+') || value[10..].Contains('-')));
        if (explicitZone && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)) return instant.ToUniversalTime();
        if (DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return Anchor(wall, Zone());
        throw CoreException.Bad("invalid due date").WithPresentation("ErrorInvalidDueDate");
    }
    public static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    public static string Now => Iso(DateTimeOffset.UtcNow);
    public static string CalendarDate(DateTime wall) => wall.ToString(CalendarDateFormat, CultureInfo.InvariantCulture);
    public static bool TryCalendarDate(string? value, out DateTime date) =>
        DateTime.TryParseExact(value, CalendarDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    public static DateTime AllDayDate(string due, TimeZoneInfo zone) =>
        TryCalendarDate(due, out var date) ? date : TimeZoneInfo.ConvertTime(Parse(due), zone).Date;
    public static DateTimeOffset FromFloating(long ms, string? zone) => Anchor(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, Zone(zone));
    public static long ToFloating(DateTimeOffset instant, string? zone) => new DateTimeOffset(DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, Zone(zone)).DateTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    public static string FromCloudDue(long ms, bool allDay, string? zone) => allDay
        ? CalendarDate(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime)
        : Iso(FromFloating(ms, zone));
    public static long ToCloudDue(string due, bool allDay, string? zone) => allDay
        ? new DateTimeOffset(AllDayDate(due, Zone()), TimeSpan.Zero).ToUnixTimeMilliseconds()
        : ToFloating(Parse(due), zone);
    public static string? Normalize(JsonNode? value, bool allDay)
    {
        if (value is null || value.ToString() == "") return null;
        DateTimeOffset instant;
        if (value is JsonValue v && v.TryGetValue<long>(out var ms))
        {
            try { instant = DateTimeOffset.FromUnixTimeMilliseconds(ms); }
            catch (ArgumentOutOfRangeException) { throw CoreException.Bad("due date is outside the supported range").WithPresentation("ErrorDueDateOutsideRange"); }
        }
        else if (value is JsonValue text && text.TryGetValue<string>(out var raw))
        {
            if (allDay && TryCalendarDate(raw, out var date)) return CalendarDate(date);
            instant = Parse(raw);
            if (allDay && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
                return CalendarDate(withOffset.Date);
        }
        else throw CoreException.Bad("due date must be ISO-8601 or epoch millis").WithPresentation("ErrorInvalidDueDateFormat");
        return allDay ? CalendarDate(TimeZoneInfo.ConvertTime(instant, Zone()).Date) : Iso(instant);
    }
    public static DateTimeOffset NotifyAt(string due, bool allDay, TimeZoneInfo? zone = null)
    {
        if (!allDay) return Parse(due);
        zone ??= Zone();
        return Anchor(AllDayDate(due, zone).AddHours(9), zone);
    }
}
