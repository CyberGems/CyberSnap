using System.Globalization;
using CyberSnap.Models;

namespace CyberSnap.Helpers;

/// <summary>
/// Scheduled notification mute: when enabled and the local clock is inside the window,
/// only critical (<c>IsError</c>) toasts are shown.
/// </summary>
internal static class QuietHours
{
    public const string DefaultStart = "22:00";
    public const string DefaultEnd = "07:00";

    public static bool IsActive(AppSettings? settings, DateTime? now = null)
    {
        if (settings is null || !settings.QuietHoursEnabled)
            return false;
        return IsInWindow(now ?? DateTime.Now, settings.QuietHoursStart, settings.QuietHoursEnd);
    }

    public static bool IsInWindow(DateTime now, string? start, string? end)
    {
        var startTod = ParseOrDefault(start, DefaultStart);
        var endTod = ParseOrDefault(end, DefaultEnd);
        if (startTod == endTod)
            return false;

        var t = now.TimeOfDay;
        if (startTod < endTod)
            return t >= startTod && t < endTod;

        // Overnight (e.g. 22:00–07:00): active from start through midnight and until end.
        return t >= startTod || t < endTod;
    }

    public static string Normalize(string? hhmm, string fallback)
    {
        return TryParse(hhmm, out var t)
            ? Format(t)
            : (TryParse(fallback, out var fb) ? Format(fb) : DefaultStart);
    }

    public static bool TryParse(string? hhmm, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(hhmm))
            return false;

        if (!TimeSpan.TryParseExact(
                hhmm.Trim(),
                ["hh\\:mm", "h\\:mm"],
                CultureInfo.InvariantCulture,
                out time))
            return false;

        return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    public static IEnumerable<string> HalfHourSlots()
    {
        for (int minutes = 0; minutes < 24 * 60; minutes += 30)
            yield return Format(TimeSpan.FromMinutes(minutes));
    }

    public static string Format(TimeSpan time)
        => $"{(int)time.TotalHours:D2}:{time.Minutes:D2}";

    private static TimeSpan ParseOrDefault(string? hhmm, string fallback)
    {
        if (TryParse(hhmm, out var parsed))
            return parsed;
        return TryParse(fallback, out var fb) ? fb : new TimeSpan(22, 0, 0);
    }
}
