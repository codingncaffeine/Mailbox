using Avalonia.Media;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// Turns what the PIM store holds into what a view draws: the rows a span of time can show,
/// expanded into occurrences and tagged with the calendar each came from.
/// </summary>
/// <remarks>
/// A collection at a time, deliberately. Expansion groups events by UID to find a series and its
/// overrides, and the same UID in two calendars is one event subscribed to twice — expanding them
/// together would merge two calendars' copies into one series and lose whichever colour lost the
/// tie.
/// </remarks>
public sealed class CalendarSource(PimRepository repository)
{
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>The calendars, in the order the navigation pane lists them.</summary>
    public IReadOnlyList<Collection> Calendars() => _repository.Collections(CollectionKind.Events);

    /// <summary>
    /// Everything on the visible calendars between two instants, in start order.
    /// </summary>
    /// <param name="collectionIds">Only these calendars; null for every visible one.</param>
    /// <param name="zone">
    /// The clock the view reads: what a floating or all-day time is placed by, and what an
    /// appointment written in another zone is drawn at. The machine's own if null.
    /// </param>
    public IReadOnlyList<CalendarEntry> Between(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<long>? collectionIds = null,
        TimeZoneInfo? zone = null)
    {
        var entries = new List<CalendarEntry>();

        foreach (var calendar in Calendars())
        {
            if (collectionIds is { Count: > 0 } ? !collectionIds.Contains(calendar.Id) : !calendar.IsVisible) continue;

            var items = _repository.ItemsBetween(fromUtc, toUtc, [calendar.Id]);
            if (items.Count == 0) continue;

            var colour = Colour(calendar.Color);
            var events = new List<CalendarEvent>(items.Count);
            var ids = new Dictionary<(string Uid, string Recurrence), long>();

            foreach (var item in items)
            {
                var calendarEvent = PimEventCodec.FromItem(item);
                events.Add(calendarEvent);
                ids[(item.Uid, item.RecurrenceId ?? string.Empty)] = item.Id;
            }

            foreach (var occurrence in Recurrence.Expand(events, fromUtc, toUtc, zone))
            {
                var key = (occurrence.Event.Uid, occurrence.Event.RecurrenceId is { } rid
                    ? ICalendarCodec.RecurrenceIdText(rid)
                    : string.Empty);

                entries.Add(new CalendarEntry
                {
                    Occurrence = occurrence,
                    ItemId = ids.TryGetValue(key, out var id) ? id : 0,
                    CollectionId = calendar.Id,
                    CollectionName = calendar.DisplayName,
                    Colour = colour,
                    IsReadOnly = calendar.IsReadOnly,
                    Zone = zone ?? TimeZoneInfo.Local,
                });
            }
        }

        entries.Sort((a, b) =>
        {
            var byStart = a.StartUtc.CompareTo(b.StartUtc);
            if (byStart != 0) return byStart;
            return string.CompareOrdinal(a.Summary, b.Summary);
        });
        return entries;
    }

    /// <summary>The days in a span that have anything on them, which the navigator draws in bold.</summary>
    public IReadOnlySet<DateOnly> DaysWithItems(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo? zone = null)
    {
        var days = new HashSet<DateOnly>();
        foreach (var entry in Between(fromUtc, toUtc, zone: zone))
        {
            var (first, last) = entry.Days();
            for (var day = first; day <= last; day = day.AddDays(1)) days.Add(day);
        }

        return days;
    }

    private static Color? Colour(string text)
        => !string.IsNullOrWhiteSpace(text) && Color.TryParse(text, out var colour) ? colour : null;
}
