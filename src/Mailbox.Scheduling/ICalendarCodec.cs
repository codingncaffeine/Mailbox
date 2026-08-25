using System.Globalization;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Ical.Net.Serialization.DataTypes;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Mailbox.Scheduling;

/// <summary>
/// The application's appointments to and from RFC 5545 text. The text is the truth the store and
/// the server share; these mappings are the only place the application reads or writes it, so
/// what the application does not model (a VALARM with a sound, an X- property from another
/// client) is lost only where the application rewrites a VEVENT — and a VEVENT it never edits
/// is passed through untouched.
/// </summary>
public static class ICalendarCodec
{
    /// <summary>The PRODID written into calendars this application makes.</summary>
    public const string ProductId = "-//Mailbox//Mailbox Calendar//EN";

    private const string BusyProperty = "X-MICROSOFT-CDO-BUSYSTATUS";

    /// <summary>What CLASS says about an appointment the reference calls Private.</summary>
    private const string PrivateClass = "PRIVATE";

    /// <summary>One VEVENT block — <c>BEGIN:VEVENT</c> to <c>END:VEVENT</c> — as the store keeps a row.</summary>
    public static string Serialize(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return new ComponentSerializer().SerializeToString(ToIcal(calendarEvent)) ?? string.Empty;
    }

    /// <summary>
    /// A whole VCALENDAR — the events given (a master and its overrides share a UID) with a
    /// VTIMEZONE for every zone they name — as a server is sent one, or a .ics file holds one.
    /// </summary>
    public static string SerializeCalendar(IEnumerable<CalendarEvent> events, string? method = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var calendar = new IcalCalendar { ProductId = ProductId, Method = method };
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            calendar.Events.Add(ToIcal(e));
            foreach (var tz in new[] { e.Start.TzId, e.End.TzId, e.RecurrenceId?.TzId })
                if (tz is not null && !string.Equals(tz, "UTC", StringComparison.OrdinalIgnoreCase)) zones.Add(tz);
        }
        foreach (var tz in zones)
        {
            try { calendar.AddTimeZone(tz); }
            catch (Exception) { /* a zone this machine does not know is still named on the DTSTART; the reader resolves it. */ }
        }
        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    /// <summary>
    /// The events in RFC 5545 text: a VCALENDAR, or a bare VEVENT block as the store keeps one.
    /// A series comes back as its master and then its overrides.
    /// </summary>
    /// <exception cref="FormatException">The text is not iCalendar.</exception>
    public static IReadOnlyList<CalendarEvent> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            trimmed = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:" + ProductId + "\r\n" + trimmed.TrimEnd() + "\r\nEND:VCALENDAR\r\n";

        // Reading the components is inside the guard as well as loading them. Ical.Net accepts a
        // VEVENT with no DTSTART and then throws from the property getter, so a block that is
        // missing one gets past Load and takes the caller down two lines later — which is the
        // damaged row PimEventCodec promises to survive by falling back to the columns.
        try
        {
            var calendar = IcalCalendar.Load(trimmed);
            if (calendar is null) throw new FormatException("The text is not an iCalendar object.");

            return calendar.Events
                .Select(FromIcal)
                .OrderBy(e => e.Uid, StringComparer.Ordinal)
                .ThenBy(e => e.IsOverride ? 1 : 0)
                .ThenBy(e => e.RecurrenceId?.Wall ?? DateTime.MinValue)
                .ToList();
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FormatException("The text is not an iCalendar object.", ex);
        }
    }

    /// <summary>The application's event as Ical.Net holds one, for serialising or expanding.</summary>
    internal static IcalEvent ToIcal(CalendarEvent e)
    {
        var ical = new IcalEvent
        {
            Uid = e.Uid,
            Summary = e.Summary,
            DtStart = ToCal(e.Start),
            DtEnd = ToCal(e.End),
            Sequence = e.Sequence,
            LastModified = new CalDateTime(e.LastModified.UtcDateTime, "UTC", true),
            DtStamp = new CalDateTime(e.LastModified.UtcDateTime, "UTC", true),
            Transparency = e.Busy == BusyStatus.Free ? TransparencyType.Transparent : TransparencyType.Opaque,
        };
        if (e.Location.Length > 0) ical.Location = e.Location;
        if (e.Description.Length > 0) ical.Description = e.Description;
        if (e.Status.Length > 0) ical.Status = e.Status;
        if (!string.IsNullOrWhiteSpace(e.Rrule)) ical.RecurrenceRule = new RecurrenceRule(e.Rrule);
        foreach (var ex in e.ExceptionDates) ical.ExceptionDates.Add(ToCal(ex));
        if (e.RecurrenceId is not null) ical.RecurrenceIdentifier = new RecurrenceIdentifier(ToCal(e.RecurrenceId), null);
        if (e.Organizer.Length > 0) ical.Organizer = new Organizer(WithMailto(e.Organizer));
        foreach (var a in e.Attendees)
        {
            ical.Attendees.Add(new Attendee(WithMailto(a.Address))
            {
                CommonName = a.Name.Length > 0 ? a.Name : null,
                Role = a.Role,
                ParticipationStatus = a.PartStat,
                Rsvp = a.Rsvp,
            });
        }
        foreach (var c in e.Categories) ical.Categories.Add(c);

        // CLASS and PRIORITY are written only when they say something. PUBLIC and 5 are the
        // standard's own defaults, so writing them would add a property to every appointment in
        // the file to state what its absence already states.
        if (e.IsPrivate) ical.Class = PrivateClass;
        if (e.Urgency != TaskUrgency.Normal) ical.Priority = e.PriorityNumber;
        ical.Properties.Add(new CalendarProperty(BusyProperty, e.Busy switch
        {
            BusyStatus.Free => "FREE",
            BusyStatus.Tentative => "TENTATIVE",
            BusyStatus.OutOfOffice => "OOF",
            _ => "BUSY",
        }));
        if (e.ReminderMinutes is { } minutes)
        {
            ical.Alarms.Add(new Alarm
            {
                Action = AlarmAction.Display,
                Description = e.Summary.Length > 0 ? e.Summary : "Reminder",
                Trigger = new Trigger(Duration.FromMinutes(-minutes)),
            });
        }
        return ical;
    }

    /// <summary>Ical.Net's event as the application holds one.</summary>
    internal static CalendarEvent FromIcal(IcalEvent ical)
    {
        var start = ical.DtStart is { } s ? FromCal(s) : new EventTime(DateTime.MinValue, null);
        var end = ical.DtEnd is { } de ? FromCal(de)
            : ical.EffectiveDuration is { } dur ? FromCal(ical.DtStart!.Add(dur))
            : start.AllDay ? start.Add(TimeSpan.FromDays(1)) : start;

        var busy = ParseBusy(ical.Properties.Get<string>(BusyProperty))
            ?? (string.Equals(ical.Transparency, TransparencyType.Transparent, StringComparison.OrdinalIgnoreCase) ? BusyStatus.Free
                : string.Equals(ical.Status, EventStatus.Tentative, StringComparison.OrdinalIgnoreCase) ? BusyStatus.Tentative
                : BusyStatus.Busy);

        int? reminder = null;
        foreach (var alarm in ical.Alarms)
        {
            if (alarm.Trigger is { IsRelative: true, Duration: { } d })
            {
                var span = d.ToTimeSpanUnspecified();
                var minutes = (int)Math.Round(-span.TotalMinutes);
                if (minutes >= 0 && (reminder is null || minutes > reminder)) reminder = minutes;
            }
        }

        string? rrule = null;
        if (ical.RecurrenceRule is { } rule)
        {
            rrule = new RecurrenceRuleSerializer(new SerializationContext()).SerializeToString(rule);
            if (string.IsNullOrEmpty(rrule)) rrule = null;
        }

        return new CalendarEvent
        {
            Uid = ical.Uid ?? CalendarEvent.NewUid(),
            Summary = ical.Summary ?? string.Empty,
            Location = ical.Location ?? string.Empty,
            Description = ical.Description ?? string.Empty,
            Start = start,
            End = end,
            Rrule = rrule,
            ExceptionDates = ical.ExceptionDates.GetAllDates().Select(FromCal).ToList(),
            RecurrenceId = ical.RecurrenceIdentifier is { } rid ? FromCal(rid.StartTime) : null,
            Busy = busy,
            ReminderMinutes = reminder,
            // CONFIDENTIAL reads as private for the reason a task's does: both mean "not for
            // whoever else can see this calendar", and the reference offers only the one mark.
            IsPrivate = ical.Class is { Length: > 0 } klass
                && !string.Equals(klass, "PUBLIC", StringComparison.OrdinalIgnoreCase),
            Urgency = TaskItem.UrgencyFor(ical.Priority),
            Categories = ical.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).ToList(),
            Attendees = ical.Attendees.Select(a => new EventAttendee(
                WithoutMailto(a.Value?.ToString() ?? string.Empty),
                a.CommonName ?? string.Empty,
                a.Role ?? "REQ-PARTICIPANT",
                a.ParticipationStatus ?? "NEEDS-ACTION",
                a.Rsvp)).ToList(),
            Organizer = ical.Organizer?.Value is { } org ? WithoutMailto(org.ToString()) : string.Empty,
            Sequence = ical.Sequence,
            Status = ical.Status ?? string.Empty,
            LastModified = ical.LastModified is { } lm ? new DateTimeOffset(lm.AsUtc, TimeSpan.Zero)
                : ical.DtStamp is { } ds ? new DateTimeOffset(ds.AsUtc, TimeSpan.Zero)
                : DateTimeOffset.UtcNow,
        };
    }

    internal static CalDateTime ToCal(EventTime time)
    {
        if (time.AllDay) return new CalDateTime(DateOnly.FromDateTime(time.Wall));
        var wall = DateTime.SpecifyKind(time.Wall, DateTimeKind.Unspecified);
        return time.TzId is null ? new CalDateTime(wall, hasTime: true) : new CalDateTime(wall, time.TzId, hasTime: true);
    }

    internal static EventTime FromCal(CalDateTime cal)
    {
        var wall = DateTime.SpecifyKind(cal.Value, DateTimeKind.Unspecified);
        if (!cal.HasTime) return new EventTime(wall.Date, null, AllDay: true);
        return new EventTime(wall, cal.IsUtc ? "UTC" : cal.TzId);
    }

    private static BusyStatus? ParseBusy(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "FREE" => BusyStatus.Free,
        "TENTATIVE" => BusyStatus.Tentative,
        "BUSY" => BusyStatus.Busy,
        "OOF" or "WORKINGELSEWHERE" => BusyStatus.OutOfOffice,
        _ => null,
    };

    /// <summary>The store's word for a status: free · tentative · busy · oof.</summary>
    public static string BusyWord(BusyStatus busy) => busy switch
    {
        BusyStatus.Free => "free",
        BusyStatus.Tentative => "tentative",
        BusyStatus.OutOfOffice => "oof",
        _ => "busy",
    };

    /// <summary>The store's word back into a status; anything unknown is busy.</summary>
    public static BusyStatus BusyFromWord(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "free" => BusyStatus.Free,
        "tentative" => BusyStatus.Tentative,
        "oof" => BusyStatus.OutOfOffice,
        _ => BusyStatus.Busy,
    };

    private static string WithMailto(string address)
        => address.Contains(':', StringComparison.Ordinal) ? address : "mailto:" + address;

    private static string WithoutMailto(string uri)
        => uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? uri["mailto:".Length..] : uri;

    /// <summary>A RECURRENCE-ID value as the store's column keeps it: <c>20260406T090000</c>, or <c>20260406</c> for a day.</summary>
    public static string RecurrenceIdText(EventTime time)
        => time.AllDay
            ? time.Wall.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : time.Wall.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) + (string.Equals(time.TzId, "UTC", StringComparison.OrdinalIgnoreCase) ? "Z" : string.Empty);
}
