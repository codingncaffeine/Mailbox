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

    /// <summary>
    /// One colour for every calendar, whatever each says about itself — the Options page's
    /// "Use this colour on all calendars".
    /// </summary>
    /// <remarks>
    /// Applied here rather than written into the collections, because the setting is a way of
    /// looking at the calendars rather than a change to them: turning it off has to give every
    /// calendar its own colour back, and a sweep that overwrote them would have nothing to give.
    /// Empty means the default chip colour, which is what a calendar with no colour of its own
    /// already draws as.
    /// </remarks>
    public string? ForcedColour { get; set; }

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
    /// <summary>
    /// Resolves a colour category's name to the colour the theme draws it, handed in by the
    /// application — the views cannot see the category list, and the category is the one thing
    /// that outranks the calendar's own colour on a chip, as the reference draws it.
    /// </summary>
    public Func<string, Color?>? CategoryColour { get; set; }

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

            var colour = Colour(ForcedColour ?? calendar.Color);
            var events = new List<CalendarEvent>(items.Count);
            var ids = new Dictionary<(string Uid, string Recurrence), long>();

            foreach (var item in items)
            {
                var calendarEvent = PimEventCodec.FromItem(item);

                // A declined meeting is written CANCELLED and kept — the row survives so a
                // re-invitation can find it — but the reference takes it off the calendar, and
                // a chip here would count the reader busy for an hour they said no to.
                if (string.Equals(calendarEvent.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                events.Add(calendarEvent);
                ids[(item.Uid, item.RecurrenceId ?? string.Empty)] = item.Id;
            }

            foreach (var occurrence in Recurrence.Expand(events, fromUtc, toUtc, zone))
            {
                var key = (occurrence.Event.Uid, occurrence.Event.RecurrenceId is { } rid
                    ? ICalendarCodec.RecurrenceIdText(rid)
                    : string.Empty);

                // The last category assigned wins, which is the reference's own rule for a
                // block carrying several.
                var categorised = CategoryColour is { } lookup
                    ? occurrence.Event.Categories
                        .Select(lookup)
                        .LastOrDefault(c => c is not null)
                    : null;

                entries.Add(new CalendarEntry
                {
                    Occurrence = occurrence,
                    ItemId = ids.TryGetValue(key, out var id) ? id : 0,
                    CollectionId = calendar.Id,
                    CollectionName = calendar.DisplayName,
                    Colour = categorised ?? colour,
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

    /// <summary>The days in a span that are claimed, which the navigator draws in bold.</summary>
    /// <remarks>
    /// An appointment shown as Free does not make its day bold. That is the reference's own rule,
    /// and its captures show it: the one day in <c>calendar/colorful.png</c> that carries an item
    /// and is not bold carries exactly one, drawn hollow — the way that build draws Free. A day
    /// whose only entry is somebody's lunch is not a claimed day, and bolding it makes the bold
    /// mean nothing.
    /// </remarks>
    public IReadOnlySet<DateOnly> DaysWithItems(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo? zone = null)
    {
        var days = new HashSet<DateOnly>();
        foreach (var entry in Between(fromUtc, toUtc, zone: zone))
        {
            if (entry.Busy == BusyStatus.Free) continue;
            var (first, last) = entry.Days();
            for (var day = first; day <= last; day = day.AddDays(1)) days.Add(day);
        }

        return days;
    }

    private static Color? Colour(string text)
        => !string.IsNullOrWhiteSpace(text) && Color.TryParse(text, out var colour) ? colour : null;
}
