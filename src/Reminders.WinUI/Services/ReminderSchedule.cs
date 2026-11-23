using System.Globalization;

namespace Reminders.Windows.Services;

internal static class ReminderSchedule
{
    public static DateTimeOffset FromToday(DateTimeOffset now, TimeZoneInfo zone, int days)
    {
        var date = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(days);
        return new DateTimeOffset(date, zone.GetUtcOffset(date));
    }

    public static string? Format(DateTimeOffset? date, bool allDay, TimeSpan time) => date is not { } value ? null
        : allDay ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : value.Date.Add(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
