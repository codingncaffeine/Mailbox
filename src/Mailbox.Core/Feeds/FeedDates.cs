using System.Globalization;

namespace Mailbox.Core.Feeds;

/// <summary>
/// The dates feeds actually carry.
/// </summary>
/// <remarks>
/// RSS says RFC 822 and Atom says RFC 3339, and between them publishers write neither often
/// enough to matter. <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles,
/// out DateTimeOffset)"/> takes both when the zone is numeric; what it will not take is the
/// alphabetic zone RFC 822 also allows — <c>EST</c>, <c>GMT</c>, the single-letter military
/// ones — and those are common enough in the wild that dropping the date is the wrong answer.
/// <para>
/// A date that cannot be read at all comes back null rather than as "now": an entry stamped with
/// the moment it was downloaded sorts to the top of the list every time it is downloaded, which
/// is worse than an entry with no date.
/// </para>
/// </remarks>
public static class FeedDates
{
    /// <summary>The alphabetic zones RFC 822 allows, and what they are worth in hours.</summary>
    /// <remarks>
    /// The obsolete North American ones are here because feeds still use them. The military
    /// letters are not: RFC 822 got their sign backwards, half the world's software copied the
    /// error and the other half did not, so a letter zone means one of two times twelve hours
    /// apart. RFC 2822 says to treat them as <c>-0000</c> — an unknown zone — and so does this.
    /// </remarks>
    private static readonly Dictionary<string, int> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0,
        ["EST"] = -5, ["EDT"] = -4,
        ["CST"] = -6, ["CDT"] = -5,
        ["MST"] = -7, ["MDT"] = -6,
        ["PST"] = -8, ["PDT"] = -7,
    };

    /// <summary>The instant the text names, or null when it does not name one.</summary>
    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim();
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed;
        }

        if (Rewritten(value) is { } rewritten
            && DateTimeOffset.TryParse(rewritten, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            return parsed;
        }

        // A bare date with no time at all — "2026-08-16" reads above, but "16 Aug 2026" does not.
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date)
            ? new DateTimeOffset(date, TimeSpan.Zero)
            : null;
    }

    /// <summary>
    /// The same date with its alphabetic zone written as an offset, or null when the last word
    /// was not a zone this knows.
    /// </summary>
    private static string? Rewritten(string value)
    {
        var space = value.LastIndexOf(' ');
        if (space <= 0) return null;

        var zone = value[(space + 1)..].Trim();
        if (!Zones.TryGetValue(zone, out var hours))
        {
            // An unknown alphabetic zone is dropped to UTC rather than losing the date with it,
            // which is what RFC 2822 asks for and what every other reader does.
            return zone.Length is > 0 and <= 5 && zone.All(char.IsAsciiLetter)
                ? value[..space] + " +0000"
                : null;
        }

        return $"{value[..space]} {(hours < 0 ? '-' : '+')}{Math.Abs(hours):00}00";
    }
}
