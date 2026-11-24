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

    public static bool IsToday(string? dueDate, bool allDay, DateTimeOffset now, TimeZoneInfo zone) =>
        LocalDue(dueDate, allDay, zone)?.Date == TimeZoneInfo.ConvertTime(now, zone).Date;

    public static (string DueDate, bool AllDay)? MoveToToday(string? dueDate, bool allDay, DateTimeOffset now, TimeZoneInfo zone)
    {
        var target = FromToday(now, zone, 0);
        var due = LocalDue(dueDate, allDay, zone);
        if (due?.Date == target.Date) return null;
        var useAllDay = allDay || due is null;
        return (Format(target, useAllDay, due?.TimeOfDay ?? TimeSpan.Zero)!, useAllDay);
    }

    private static DateTime? LocalDue(string? dueDate, bool allDay, TimeZoneInfo zone) =>
        DateTimeOffset.TryParse(dueDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)
            ? allDay ? due.Date : TimeZoneInfo.ConvertTime(due, zone).DateTime
            : null;
}
