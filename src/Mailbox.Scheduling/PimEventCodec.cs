using System.Globalization;
using Mailbox.Store.Pim;

namespace Mailbox.Scheduling;

/// <summary>
/// An appointment to and from the row the PIM store keeps for it. The row's raw VEVENT text
/// is the truth; every other column is derived from the event here, so a query on the columns
/// and a parse of the text always agree.
/// </summary>
public static class PimEventCodec
{
    /// <summary>
    /// The row for an event. <paramref name="existing"/> carries the identity and sync bookkeeping
    /// (Id, DavHref, Etag) forward when a stored event is edited; a new event gets a new row.
    /// </summary>
    public static PimItem ToItem(CalendarEvent calendarEvent, long collectionId, PimItem? existing = null, PimSyncState? syncState = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var e = calendarEvent;
        return new PimItem
        {
            Id = existing?.Id ?? 0,
            CollectionId = collectionId,
            Uid = e.Uid,
            Kind = CollectionKind.Events,
            RawPayload = ICalendarCodec.Serialize(e),
            Summary = e.Summary,
            Description = e.Description,
            Location = e.Location,
            StartsUtc = e.Start.ToUtc(),
            EndsUtc = e.End.ToUtc(),
            StartsLocal = e.Start.ToLocalText(),
            EndsLocal = e.End.ToLocalText(),
            TzId = e.AllDay ? null : e.Start.TzId,
            AllDay = e.AllDay,
            Status = e.Status,
            Rrule = e.Rrule,
            RecurrenceId = e.RecurrenceId is { } rid ? ICalendarCodec.RecurrenceIdText(rid) : null,
            IsOverride = e.IsOverride,
            Sequence = e.Sequence,
            Organizer = e.Organizer,
            Busy = ICalendarCodec.BusyWord(e.Busy),
            ReminderMinutes = e.ReminderMinutes,
            Categories = string.Join(",", e.Categories),
            LastModified = e.LastModified,
            SyncState = syncState ?? (existing is null ? PimSyncState.New : existing.SyncState == PimSyncState.New ? PimSyncState.New : PimSyncState.Modified),
            DavHref = existing?.DavHref,
            Etag = existing?.Etag,
        };
    }

    /// <summary>
    /// The event a row holds: parsed from its VEVENT text, or — when the text will not parse —
    /// rebuilt from the columns, so a damaged row still shows on the calendar and can be fixed.
    /// </summary>
    public static CalendarEvent FromItem(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            var parsed = ICalendarCodec.Parse(item.RawPayload);
            var match = parsed.FirstOrDefault(e => item.IsOverride ? e.IsOverride : !e.IsOverride) ?? parsed.FirstOrDefault();
            if (match is not null && match.Start.Wall != DateTime.MinValue) return match;
        }
        catch (FormatException)
        {
            // Fall through to the columns.
        }
        return FromColumns(item);
    }

    /// <summary>The event the columns alone describe.</summary>
    public static CalendarEvent FromColumns(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var start = EventTime.FromLocalText(item.StartsLocal, item.TzId, item.AllDay)
            ?? (item.StartsUtc is { } su ? new EventTime(su.UtcDateTime, "UTC", item.AllDay) : new EventTime(DateTime.MinValue, null, item.AllDay));
        var end = EventTime.FromLocalText(item.EndsLocal, item.TzId, item.AllDay)
            ?? (item.EndsUtc is { } eu ? new EventTime(eu.UtcDateTime, "UTC", item.AllDay) : start.Add(item.AllDay ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(30)));

        EventTime? recurrenceId = null;
        if (item.IsOverride && !string.IsNullOrWhiteSpace(item.RecurrenceId))
        {
            var text = item.RecurrenceId.Trim();
            var utc = text.EndsWith('Z');
            if (utc) text = text[..^1];
            if (DateTime.TryParseExact(text, ["yyyyMMdd'T'HHmmss", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
                recurrenceId = text.Length == 8 ? new EventTime(wall, null, AllDay: true) : new EventTime(wall, utc ? "UTC" : item.TzId);
        }

        return new CalendarEvent
        {
            Uid = item.Uid,
            Summary = item.Summary,
            Description = item.Description,
            Location = item.Location,
            Start = start,
            End = end,
            Rrule = string.IsNullOrWhiteSpace(item.Rrule) ? null : item.Rrule,
            RecurrenceId = recurrenceId,
            Busy = ICalendarCodec.BusyFromWord(item.Busy),
            ReminderMinutes = item.ReminderMinutes,
            Categories = item.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Organizer = item.Organizer,
            Sequence = item.Sequence,
            Status = item.Status,
            LastModified = item.LastModified,
        };
    }
}
