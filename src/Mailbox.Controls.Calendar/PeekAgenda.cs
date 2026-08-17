using System.Globalization;

namespace Mailbox.Controls.Calendar;

/// <summary>One entry in the peek's agenda: when it is, what it is, and where.</summary>
/// <param name="Time">The start on the peek's own clock, or "All day".</param>
/// <param name="Subject">What the appointment is called.</param>
/// <param name="Detail">Where it is, which the reference draws under the subject. May be empty.</param>
public sealed record PeekAgendaRow(string Time, string Subject, string Detail, CalendarEntry Entry)
{
    /// <summary>One line, or two when the appointment says where it is.</summary>
    public int Lines => Detail.Length > 0 ? 2 : 1;
}

/// <summary>
/// The day's appointments as the peek writes them.
/// </summary>
/// <remarks>
/// A day at a time and a whole day at a time: the peek shows what a date holds rather than what
/// is still to come, so an appointment that has already finished is on it. All-day items lead,
/// as they do in the month view's cells, and the rest follow the clock.
/// </remarks>
public static class PeekAgenda
{
    /// <summary>What the reference writes in the time column of an item that has no time.</summary>
    public const string AllDayLabel = "All day";

    public static IReadOnlyList<PeekAgendaRow> For(
        IEnumerable<CalendarEntry> entries,
        DateOnly day,
        IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var format = culture ?? CultureInfo.CurrentCulture;

        var rows = new List<(CalendarEntry Entry, bool Banner)>();
        foreach (var entry in entries)
        {
            var (first, last) = entry.Days();
            if (day < first || day > last) continue;
            rows.Add((entry, entry.AllDay || last > first));
        }

        rows.Sort((a, b) =>
        {
            // A banner is not at a time, so it cannot be sorted by one: they lead, in start
            // order among themselves, and the timed items follow on the clock.
            if (a.Banner != b.Banner) return a.Banner ? -1 : 1;
            var byStart = a.Entry.StartUtc.CompareTo(b.Entry.StartUtc);
            if (byStart != 0) return byStart;
            return string.CompareOrdinal(a.Entry.Summary, b.Entry.Summary);
        });

        return [.. rows.Select(r => new PeekAgendaRow(
            r.Banner ? AllDayLabel : r.Entry.StartWall.ToString("h:mm tt", format),
            r.Entry.Summary,
            r.Entry.Location,
            r.Entry))];
    }
}
