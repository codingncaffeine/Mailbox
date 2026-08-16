namespace Mailbox.Scheduling;

/// <summary>
/// The three operations the edit-scope prompt chooses between (§9), as changes to events rather
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
