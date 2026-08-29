using System.Globalization;

namespace Mailbox.Core;

/// <summary>
/// What day the application believes it is, and which moment of it — pinnable, so a capture is
/// the same picture next year.
/// </summary>
/// <remarks>
/// The calendar has had a pinned today since its first capture: without one every month view
/// shades a different half of itself and no reference comparison holds. The mail list needs the
/// same thing for a different reason. Its date arrangement groups into Today, Yesterday, the
/// named days of the past week and then counted weeks, and its Received column writes a time for
/// today and a weekday within the week — so a seeded corpus dated against a pinned day drifts one
/// bucket further from "Today" every day that passes, and the wording of those buckets cannot be
/// photographed or read back the same way twice.
/// <para>
/// One parse rather than two: the calendar's own <c>CalendarToday</c> read this variable
/// separately, and a second copy of a clock is how two halves of one application come to disagree
/// about what day it is.
/// </para>
/// <para>
/// Live when nothing is pinned — deliberately a property rather than a value captured at type
/// initialisation, so an application left running overnight does not still believe it is
/// yesterday.
/// </para>
/// </remarks>
public static class PosedClock
{
    public const string Variable = "MAILBOX_TODAY";

    /// <summary>The pinned day, or null when the real clock is in charge.</summary>
    /// <remarks>
    /// Parsed once: the environment does not change under a running process, and the parse is on
    /// the path of every row the list draws.
    /// </remarks>
    public static DateOnly? Pinned { get; } =
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } pinned
        && DateOnly.TryParseExact(pinned, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    public static bool IsPinned => Pinned is not null;

    /// <summary>Today, as everything that groups or labels by date should believe it.</summary>
    public static DateOnly Today => Pinned ?? DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// The moment "now" stands at: half past two on a pinned day, the real clock otherwise.
    /// </summary>
    /// <remarks>
    /// The same moment the seed stamps its newest message with, so the newest thing in a seeded
    /// folder is not in the future — a row dated after now falls in the "Later" band, which is
    /// not the bucket a capture of a seeded inbox is meant to be showing.
    /// </remarks>
    public static DateTimeOffset Now
    {
        get
        {
            if (Pinned is not { } day) return DateTimeOffset.Now;
            var wall = day.ToDateTime(new TimeOnly(14, 30));
            return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
        }
    }

    /// <summary>The same instant as <see cref="Now"/>, written in UTC.</summary>
    /// <remarks>
    /// Stores stamp in UTC and are queried in it, so the alternative to this was every caller
    /// reaching for <c>DateTimeOffset.UtcNow</c> beside a pinned clock — which is how the
    /// reminders queue came to compute "overdue by" from the machine's date while the window
    /// around it was posed. Offering the pinned moment in the form those queries want is what
    /// keeps one clock one clock.
    /// </remarks>
    public static DateTimeOffset UtcNow => Now.ToUniversalTime();
}
