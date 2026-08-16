using System.Globalization;

namespace Mailbox.Core.Settings;

/// <summary>
/// The time zones a picker offers, and the two ways one is written: the long name in a list, and
/// the short offset over a ruler.
/// </summary>
/// <remarks>
/// The machine's own zone database rather than a table of our own — .NET already writes each one
/// the way the reference's list does, "(UTC-05:00) Eastern Time (New York)", and a zone table
/// shipped in an application is a zone table that goes stale between releases.
/// </remarks>
public static class TimeZoneChoices
{
    private static IReadOnlyList<TimeZoneInfo>? _all;

    /// <summary>Every zone the machine knows, west to east as the reference's list runs.</summary>
    public static IReadOnlyList<TimeZoneInfo> All => _all ??= TimeZoneInfo.GetSystemTimeZones()
        .OrderBy(z => z.BaseUtcOffset)
        .ThenBy(z => z.DisplayName, StringComparer.CurrentCulture)
        .ToList();

    /// <summary>How a zone reads in a list.</summary>
    public static string Describe(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return zone.DisplayName is { Length: > 0 } name ? name : zone.Id;
    }

    /// <summary>The zone an id names, or null when this machine has never heard of it.</summary>
    public static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a zone is called over a column of hours: <c>UTC+1</c>, and <c>UTC+5:30</c> where the
    /// offset is not a whole hour. Read at an instant, because half the world's zones change it
    /// twice a year.
    /// </summary>
    public static string ShortLabel(TimeZoneInfo zone, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var offset = zone.GetUtcOffset(at);
        if (offset == TimeSpan.Zero) return "UTC";

        var sign = offset < TimeSpan.Zero ? "−" : "+";
        var span = offset.Duration();
        return span.Minutes == 0
            ? $"UTC{sign}{span.Hours.ToString(CultureInfo.CurrentCulture)}"
            : $"UTC{sign}{span.Hours.ToString(CultureInfo.CurrentCulture)}:{span.Minutes.ToString("00", CultureInfo.CurrentCulture)}";
    }
}
