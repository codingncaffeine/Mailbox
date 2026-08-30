using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace Mailbox.Scheduling;

/// <summary>
/// The occurrences a set of events puts on the calendar between two instants: single events as
/// they are, series expanded by their RRULE with EXDATE taken out and RECURRENCE-ID overrides
/// standing in for the occurrences they replace. Wall times are the series' own — a weekly
/// 09:00 is 09:00 on both sides of a DST change — and instants follow from them.
/// </summary>
public static class Recurrence
{
    /// <summary>
    /// Every occurrence overlapping <c>[fromUtc, toUtc)</c>, in start order. Events sharing a UID
    /// are one series: its master and its overrides.
    /// </summary>
    /// <param name="floatingZone">The zone a floating or all-day time is placed in; the machine's own if null.</param>
    public static IReadOnlyList<Occurrence> Expand(IEnumerable<CalendarEvent> events, DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo? floatingZone = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (toUtc <= fromUtc) return [];
        var zone = floatingZone ?? TimeZoneInfo.Local;
        var result = new List<Occurrence>();

        foreach (var series in events.GroupBy(e => e.Uid, StringComparer.Ordinal))
        {
            var members = series.ToList();
            var master = members.FirstOrDefault(e => !e.IsOverride);
            var recurring = master?.Rrule is not null || members.Any(e => e.IsOverride);

            if (!recurring)
            {
                // Plain events — one per UID in a healthy store, but every one is shown.
                foreach (var e in members)
                {
                    var (startUtc, endUtc) = Instants(e.Start, e.End, zone);
                    if (Overlaps(startUtc, endUtc, fromUtc, toUtc))
                        result.Add(new Occurrence(e, e.Start, e.End, startUtc, endUtc, IsPartOfSeries: false, RecurrenceId: null));
                }
                continue;
            }

            // The master's own occurrences, less the ones an override stands in for; then the
            // overrides themselves, each at its own time. Done here rather than by asking
            // Ical.Net's calendar for the merged view, so an override that lands on the same
            // time as a regular occurrence is still shown beside it.
            var overrides = members.Where(e => e.IsOverride).ToList();
            var replaced = overrides.Select(o => o.RecurrenceId!.ToUtc(zone)).ToHashSet();

            if (master is not null)
            {
                var icalMaster = ICalendarCodec.ToIcal(master);
                // Ask from a little before the window so an all-day or floating occurrence at
                // its edge, which Ical.Net places by its own clock, is not missed; the overlap
                // test below is the one that decides.
                var askFrom = new CalDateTime(fromUtc.UtcDateTime.AddDays(-2), "UTC", true);
                var stopAt = toUtc.UtcDateTime.AddDays(2);

                foreach (var occurrence in icalMaster.GetOccurrences(askFrom, new EvaluationOptions()))
                {
                    var start = ICalendarCodec.FromCal(occurrence.Period.StartTime);
                    var startUtc = start.ToUtc(zone);
                    if (startUtc.UtcDateTime > stopAt) break;
                    if (replaced.Contains(startUtc)) continue;
                    var endCal = occurrence.Period.EffectiveEndTime ?? occurrence.Period.EndTime;
                    var end = endCal is not null ? ICalendarCodec.FromCal(endCal) : start.Add(master.End.ToUtc(zone) - master.Start.ToUtc(zone));
                    var (s, e) = Instants(start, end, zone);
                    if (!Overlaps(s, e, fromUtc, toUtc)) continue;
                    result.Add(new Occurrence(master, start, end, s, e, IsPartOfSeries: true, RecurrenceId: start));
                }
            }

            foreach (var o in overrides)
            {
                var (s, e) = Instants(o.Start, o.End, zone);
                if (Overlaps(s, e, fromUtc, toUtc))
                    result.Add(new Occurrence(o, o.Start, o.End, s, e, IsPartOfSeries: true, RecurrenceId: o.RecurrenceId));
            }
        }

        result.Sort((a, b) =>
        {
            var byStart = a.StartUtc.CompareTo(b.StartUtc);
            return byStart != 0 ? byStart : string.CompareOrdinal(a.Event.Uid, b.Event.Uid);
        });
        return result;
    }

    /// <summary>The next occurrence at or after an instant, or null when the series has run out.</summary>
    public static Occurrence? Next(IEnumerable<CalendarEvent> events, DateTimeOffset afterUtc, TimeZoneInfo? floatingZone = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events as IReadOnlyList<CalendarEvent> ?? events.ToList();
        // Widen the look-ahead until something is found or the horizon is far enough to mean "nothing".
        for (var days = 7; days <= 366 * 5; days *= 4)
        {
            var found = Expand(list, afterUtc, afterUtc.AddDays(days), floatingZone).FirstOrDefault(o => o.StartUtc >= afterUtc);
            if (found is not null) return found;
        }
        return null;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Instants(EventTime start, EventTime end, TimeZoneInfo zone)
    {
        var s = start.ToUtc(zone);
        var e = end.ToUtc(zone);
        if (e <= s) e = s.AddMinutes(start.AllDay ? 24 * 60 : 1);
        return (s, e);
    }

    private static bool Overlaps(DateTimeOffset start, DateTimeOffset end, DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => start < toUtc && end > fromUtc;
}
