namespace Mailbox.Scheduling;

/// <summary>
/// The three operations the edit-scope prompt chooses between, as changes to events rather
/// than as changes to rows.
/// </summary>
/// <remarks>
/// Kept apart from the store on purpose: "this occurrence" is an override event stored beside its
/// master, "delete this occurrence" is an EXDATE on the master, and "the series" is the master
/// itself — three different shapes of the same VEVENT family, and getting them right is a
/// property of the iCalendar model, not of SQLite. Tested against the model, applied by the shell.
/// </remarks>
public static class SeriesEditor
{
    /// <summary>
    /// The override that stands in for one occurrence: the same event at that occurrence's own
    /// time, carrying the RECURRENCE-ID of the occurrence it replaces and none of the pattern.
    /// </summary>
    public static CalendarEvent OverrideFor(CalendarEvent master, Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(occurrence);

        return master with
        {
            Rrule = null,
            ExceptionDates = [],
            RecurrenceId = occurrence.RecurrenceId ?? occurrence.Start,
            Start = occurrence.Start,
            End = occurrence.End,
            Sequence = master.Sequence,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Whether an edit has taken the pattern away, which orphans every override the series had.
    /// </summary>
    /// <remarks>
    /// An override is only meaningful beside a master that still generates the occurrence its
    /// RECURRENCE-ID names. Drop the RRULE and what is left is a row pointing at an occurrence
    /// nothing produces — unreadable to any other client and to a CalDAV server, and still drawn
    /// on its own day here, so an appointment told to stop repeating appears once more anyway.
    /// The reference discards them along with the pattern.
    /// <para>
    /// The predicate lives here because it is a fact about the iCalendar model; *which* rows to
    /// discard is a question for whoever holds the store.
    /// </para>
    /// </remarks>
    public static bool PatternDropped(CalendarEvent before, CalendarEvent after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return before.Rrule is { Length: > 0 } && string.IsNullOrEmpty(after.Rrule);
    }

    /// <summary>The master with one occurrence taken out of the series.</summary>
    public static CalendarEvent Exclude(CalendarEvent master, EventTime occurrenceStart)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(occurrenceStart);

        if (master.ExceptionDates.Any(d => d == occurrenceStart)) return master;

        return master with
        {
            ExceptionDates = [.. master.ExceptionDates, occurrenceStart],
            Sequence = master.Sequence + 1,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// The master ended the evening before an occurrence, which is what "this and all following"
    /// comes to: the series keeps everything up to that point and stops.
    /// </summary>
    public static CalendarEvent EndBefore(CalendarEvent master, EventTime occurrenceStart)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(occurrenceStart);
        if (master.Rrule is not { Length: > 0 } rule) return master;

        var until = occurrenceStart.ToUtc().AddSeconds(-1).UtcDateTime;
        var parts = rule
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith("COUNT=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        parts.Add("UNTIL=" + until.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture));

        return master with
        {
            Rrule = string.Join(";", parts),
            Sequence = master.Sequence + 1,
            LastModified = DateTimeOffset.UtcNow,
        };
    }
}
