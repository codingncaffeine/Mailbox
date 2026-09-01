using System.Globalization;

namespace Mailbox.Scheduling;

/// <summary>
/// Reads a time somebody typed into an appointment's Start or End box.
/// </summary>
/// <remarks>
/// The two boxes offer every half hour, which is the list people pick from and is no use at all
/// for a stand-up at 9:15 or a train at 07:42. Typing is how an odd time is said, and what gets
/// typed is whatever the reader is used to writing: <c>9</c>, <c>9:15</c>, <c>9.15</c>,
/// <c>9:15 am</c>, <c>9am</c>, <c>0915</c>, <c>21:45</c>.
/// <para>
/// Deliberately not <see cref="TimeOnly.TryParse(string, out TimeOnly)"/> alone. That reads the
/// current culture's shapes and nothing else, so a bare <c>9</c> — the commonest thing anybody
/// types into a time box — is refused outright, and <c>0915</c> is read as a number of some kind
/// on several cultures. It is still tried first, so every local shape a reader expects still
/// works; these are the additions.
/// </para>
/// </remarks>
public static class TypedTime
{
    /// <summary>
    /// The shapes tried after the culture's own, in order.
    /// </summary>
    /// <remarks>
    /// <c>%H</c> and <c>%h</c>, not <c>H</c> and <c>h</c>. A one-character format string is read as
    /// a <em>standard</em> specifier — of which neither is one — and the parser throws a
    /// <see cref="FormatException"/> on the format rather than returning false for the input, so a
    /// bare hour took the whole box down with it.
    /// </remarks>
    private static readonly string[] Formats =
    [
        "H:mm", "H.mm", "HHmm", "%H", "HH",
        "h:mm tt", "h.mm tt", "h:mmtt", "h.mmtt",
        "h tt", "htt", "hmm tt", "hmmtt",
    ];

    /// <summary>
    /// What a typed time means, or null when it means nothing.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess. A box that quietly turned an unreadable entry into midnight
    /// would write an appointment nobody asked for, and the caller's answer to null is to put
    /// back what was there — which is the only safe thing to do with a time that was not
    /// understood.
    /// </remarks>
    public static TimeOnly? Read(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;

        // Trimmed, and the space before am/pm made optional, so "9 : 15pm" and "9:15 PM" are the
        // same thing. A non-breaking space is what some keyboards and some locales produce.
        var text = typed.Trim().Replace(' ', ' ');
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (TimeOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local))
        {
            return local;
        }

        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (TimeOnly.TryParseExact(text, Formats, culture, DateTimeStyles.None, out var exact))
            {
                return exact;
            }
        }

        // A bare number, which the formats above cover only where the culture agrees about how
        // many digits a 24-hour clock has. 9 is nine in the morning; 915 and 0915 are a quarter
        // past; 2145 is a quarter to ten at night. Four digits or fewer, and a real time or
        // nothing.
        if (text.All(char.IsAsciiDigit) && text.Length is > 0 and <= 4)
        {
            var value = int.Parse(text, CultureInfo.InvariantCulture);
            var (hour, minute) = text.Length <= 2 ? (value, 0) : (value / 100, value % 100);

            if (hour is >= 0 and < 24 && minute is >= 0 and < 60) return new TimeOnly(hour, minute);
        }

        return null;
    }

    /// <summary>How a time is written back into the box, matching what the list offers.</summary>
    public static string Write(TimeOnly time) => time.ToString("h:mm tt", CultureInfo.CurrentCulture);
}
