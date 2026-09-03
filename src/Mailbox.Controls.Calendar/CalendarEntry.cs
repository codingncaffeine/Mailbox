using Avalonia.Media;
using Mailbox.Scheduling;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// One thing on a calendar view: an occurrence, and which collection put it there.
/// </summary>
/// <remarks>
/// The views take these rather than <see cref="Occurrence"/> because a chip is drawn in its
/// calendar's colour, and an occurrence does not know which calendar it came from — several
/// overlaid calendars is the case that matters, and it is the only thing that tells two
/// identical-looking appointments apart. A null <see cref="Color"/> means the collection has
/// none of its own and the view falls back to <c>calendar.chip.default</c>.
/// </remarks>
public sealed record CalendarEntry
{
    public required Occurrence Occurrence { get; init; }

    /// <summary>The row in the PIM store this came from, so acting on a chip can find it again.</summary>
    public long ItemId { get; init; }

    public long CollectionId { get; init; }

    public string CollectionName { get; init; } = string.Empty;

    /// <summary>The collection's own colour, or null to take the theme's default.</summary>
    public Color? Colour { get; init; }

    /// <summary>A read-only collection's items cannot be dragged or edited in place.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// The zone the view is drawing in, which is what a time on its grid means.
    /// </summary>
    /// <remarks>
    /// An appointment states its own wall time in its own zone, and a nine o'clock meeting
    /// in New York is not at nine o'clock on a calendar in London. The grid is one clock, so an
    /// entry is placed by what that clock reads at the appointment's instant.
    /// </remarks>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Whether this occurrence asks to be reminded of, which the views draw a bell for.</summary>
    public bool HasReminder => Occurrence.Event.ReminderMinutes is not null;

    public string Summary => Occurrence.Event.Summary;
    public string Location => Occurrence.Event.Location;
    public BusyStatus Busy => Occurrence.Event.Busy;
    public bool AllDay => Occurrence.AllDay;
    public DateTimeOffset StartUtc => Occurrence.StartUtc;
    public DateTimeOffset EndUtc => Occurrence.EndUtc;

    /// <summary>The start on the view's own clock, which is what a view positions by.</summary>
    public DateTime StartWall => Reading(Occurrence.Start, StartUtc);

    public DateTime EndWall => Reading(Occurrence.End, EndUtc);

    /// <summary>
    /// What the view's clock reads at an instant. An all-day item is a date rather than an
    /// instant and keeps the one it was written with: converting it would put a holiday on the
    /// evening before.
    /// </summary>
    private DateTime Reading(EventTime time, DateTimeOffset instant)
        => time.AllDay
            ? time.Wall
            : DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, Zone).DateTime, DateTimeKind.Unspecified);

    /// <summary>The days this entry touches, as the view's own dates.</summary>
    public (DateOnly First, DateOnly Last) Days()
    {
        var first = DateOnly.FromDateTime(StartWall);
        // An all-day item's end is exclusive — the day after the last — and a timed item that
        // ends at midnight belongs to the day before, not to the one it touches for no time.
        var endWall = EndWall;
        var last = endWall <= StartWall
            ? first
            : DateOnly.FromDateTime(AllDay ? endWall.AddDays(-1) : endWall.AddTicks(-1));
        return (first, last < first ? first : last);
    }

    /// <summary>True when it covers whole days: an all-day item, or one running over midnight.</summary>
    public bool IsMultiDay
    {
        get
        {
            var (first, last) = Days();
            return AllDay || last > first;
        }
    }

    /// <summary>What the reference writes on a month-view chip: the time, the subject, the place.</summary>
    public string MonthLabel(IFormatProvider culture)
    {
        var text = AllDay || IsMultiDay
            ? Summary
            : StartWall.ToString("h:mmtt", culture).ToLowerInvariant() + " " + Summary;
        return Location.Length > 0 ? text + "; " + Location : text;
    }
}
