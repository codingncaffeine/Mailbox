using System.Globalization;

namespace Mailbox.Scheduling;

/// <summary>
/// A moment on a calendar as an appointment states it: the wall-clock time and the zone it
/// was written in, or a date alone for an all-day item. Kept this way, not as a UTC instant,
/// because a 09:00 weekly meeting stays at 09:00 across a DST change (§9); the instant is
/// derived when a view or a query needs one.
/// </summary>
/// <param name="Wall">The date and time as written, <see cref="DateTimeKind.Unspecified"/>; the date alone for all-day.</param>
/// <param name="TzId">The IANA zone the wall time is in — "Europe/London" — or "UTC", or null for a floating time that means "wherever you are".</param>
/// <param name="AllDay">A date rather than a time: the item spans whole days.</param>
public sealed record EventTime(DateTime Wall, string? TzId, bool AllDay = false)
{
    /// <summary>A whole-day marker for a date.</summary>
    public static EventTime Date(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), null, AllDay: true);

    /// <summary>A timed moment in a zone.</summary>
    public static EventTime At(DateTime wall, string tzId) => new(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), tzId);

    /// <summary>The instant, in UTC. An all-day or floating time is taken in the given zone, or the machine's.</summary>
    public DateTimeOffset ToUtc(TimeZoneInfo? floatingZone = null)
    {
        var zone = Zone(floatingZone);
        var wall = DateTime.SpecifyKind(Wall, DateTimeKind.Unspecified);
        // A wall time that does not exist on the day the clocks go forward is moved with them.
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        var offset = zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    /// <summary>The zone this time is stated in, resolved on this machine; the machine's own for a floating time.</summary>
    public TimeZoneInfo Zone(TimeZoneInfo? floatingZone = null)
    {
        if (TzId is null) return floatingZone ?? TimeZoneInfo.Local;
        if (string.Equals(TzId, "UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TzId);
        }
        catch (TimeZoneNotFoundException)
        {
            return floatingZone ?? TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return floatingZone ?? TimeZoneInfo.Local;
        }
    }

    /// <summary>The wall time as the store keeps it: <c>yyyy-MM-ddTHH:mm:ss</c>.</summary>
    public string ToLocalText() => Wall.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The store's text back into a time, in the zone given.</summary>
    public static EventTime? FromLocalText(string? text, string? tzId, bool allDay)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)
            ? new EventTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), allDay ? null : tzId, allDay)
            : null;
    }

    public EventTime Add(TimeSpan span) => this with { Wall = Wall + span };
}

/// <summary>Someone asked to an appointment, and what they said.</summary>
public sealed record EventAttendee(string Address, string Name = "", string Role = "REQ-PARTICIPANT", string PartStat = "NEEDS-ACTION", bool Rsvp = false);

/// <summary>Show As — what an appointment says about the time it takes.</summary>
public enum BusyStatus
{
    Free,
    Tentative,
    Busy,
    OutOfOffice,
}

/// <summary>
/// An appointment as the application thinks of it: what, where, when, and how it repeats.
/// One of these per VEVENT — a series' master with its <see cref="Rrule"/>, or an override
/// of one occurrence with its <see cref="RecurrenceId"/>.
/// </summary>
public sealed record CalendarEvent
{
    public required string Uid { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public required EventTime Start { get; init; }
    /// <summary>Exclusive. For an all-day item, the day after the last day.</summary>
    public required EventTime End { get; init; }
    public bool AllDay => Start.AllDay;

    /// <summary>The RRULE without its property name — <c>FREQ=WEEKLY;BYDAY=MO</c> — for a series' master.</summary>
    public string? Rrule { get; init; }

    /// <summary>Occurrences taken out of the series.</summary>
    public IReadOnlyList<EventTime> ExceptionDates { get; init; } = [];

    /// <summary>For an override: the occurrence it stands in for, as the master would have started it.</summary>
    public EventTime? RecurrenceId { get; init; }
    public bool IsOverride => RecurrenceId is not null;

    public BusyStatus Busy { get; init; } = BusyStatus.Busy;
    /// <summary>Minutes before the start a reminder is due, or null for none.</summary>
    public int? ReminderMinutes { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    /// Kept to oneself when the calendar is shared — RFC 5545's <c>CLASS:PRIVATE</c>, which the
    /// reference's own Private button sets on an appointment exactly as it does on a task.
    /// </summary>
    /// <remarks>
    /// Same caveat as a task's: CLASS is a request to whoever renders the calendar, not
    /// encryption. A server that ignores it shows the appointment to everyone it shows the
    /// calendar to, and that is what the property means in the standard.
    /// </remarks>
    public bool IsPrivate { get; init; }

    /// <summary>
    /// The reference's two Importance buttons, over RFC 5545's PRIORITY — the same three states a
    /// task carries, reconciled with the standard's nine by <see cref="PriorityNumber"/>.
    /// </summary>
    public TaskUrgency Urgency { get; init; } = TaskUrgency.Normal;

    /// <summary>The PRIORITY a VEVENT carries: 1 for high, 5 for normal, 9 for low.</summary>
    public int PriorityNumber => TaskItem.PriorityFor(Urgency);

    public IReadOnlyList<EventAttendee> Attendees { get; init; } = [];
    public string Organizer { get; init; } = string.Empty;
    public int Sequence { get; init; }
    /// <summary>TENTATIVE · CONFIRMED · CANCELLED, or empty.</summary>
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UtcNow;

    public TimeSpan Duration => End.ToUtc() - Start.ToUtc();

    /// <summary>A fresh UID, as RFC 5545 wants one: unique across the world, opaque.</summary>
    public static string NewUid() => Guid.NewGuid().ToString("D") + "@mailbox";

    // Two events are the same when they say the same things — the lists by their contents,
    // not by which list object holds them — so a round trip through text or the store compares.
    public bool Equals(CalendarEvent? other)
        => other is not null
           && Uid == other.Uid && Summary == other.Summary && Location == other.Location && Description == other.Description
           && Start == other.Start && End == other.End && Rrule == other.Rrule
           && ExceptionDates.SequenceEqual(other.ExceptionDates)
           && Equals(RecurrenceId, other.RecurrenceId)
           && Busy == other.Busy && ReminderMinutes == other.ReminderMinutes
           && IsPrivate == other.IsPrivate && Urgency == other.Urgency
           && Categories.SequenceEqual(other.Categories, StringComparer.Ordinal)
           && Attendees.SequenceEqual(other.Attendees)
           && Organizer == other.Organizer && Sequence == other.Sequence && Status == other.Status
           && LastModified == other.LastModified;

    public override int GetHashCode() => HashCode.Combine(Uid, Summary, Start, End, Rrule, RecurrenceId, Sequence, LastModified);
}

/// <summary>
/// One occurrence of an event on the calendar — the event itself, or one repeat of a series —
/// with the instants a view lays it out by and the wall times a dialog shows.
/// </summary>
public sealed record Occurrence(
    CalendarEvent Event,
    EventTime Start,
    EventTime End,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool IsPartOfSeries,
    EventTime? RecurrenceId)
{
    public bool AllDay => Start.AllDay;
    public string Summary => Event.Summary;
}
