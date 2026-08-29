using System.Globalization;

namespace Mailbox.Scheduling;

/// <summary>The iTIP methods RFC 5546 defines, as an ordinary mail message can carry them.</summary>
public enum ItipMethod
{
    None,

    /// <summary>An invitation, or an update to one already accepted.</summary>
    Request,

    /// <summary>Somebody answering an invitation of ours.</summary>
    Reply,

    /// <summary>The organizer has called the meeting off.</summary>
    Cancel,

    /// <summary>An attendee proposing a different time.</summary>
    Counter,

    /// <summary>The organizer turning that proposal down.</summary>
    DeclineCounter,

    /// <summary>More occurrences added to a series already sent.</summary>
    Add,

    /// <summary>An attendee asking for the current version.</summary>
    Refresh,

    /// <summary>An appointment sent for information, with no reply expected.</summary>
    Publish,
}

/// <summary>What somebody said about an invitation.</summary>
public enum ItipResponse
{
    Accepted,
    Tentative,
    Declined,
}

/// <summary>
/// A scheduling message: what it asks for, and the appointment it is about.
/// </summary>
/// <param name="RawPayload">The iCalendar text exactly as it arrived, which a reply quotes from.</param>
public sealed record ItipMessage(ItipMethod Method, CalendarEvent Event, string RawPayload)
{
    public string Organizer => Event.Organizer;

    /// <summary>True when this message expects an answer.</summary>
    public bool WantsReply => Method is ItipMethod.Request or ItipMethod.Add or ItipMethod.Counter;

    /// <summary>The attendee entry for one address, or null when they were not asked.</summary>
    public EventAttendee? AttendeeFor(string address)
        => Event.Attendees.FirstOrDefault(a => string.Equals(Strip(a.Address), Strip(address), StringComparison.OrdinalIgnoreCase));

    internal static string Strip(string address)
    {
        var text = address.Trim();
        return text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? text[7..] : text;
    }
}

/// <summary>
/// iMIP (RFC 6047): scheduling over ordinary mail, which is what makes the calendar useful with
/// no scheduling server anywhere.
/// </summary>
/// <remarks>
/// The transport is MimeKit's and the grammar is Ical.Net's; the state machine is ours — which
/// of the seven methods arrived, what it means for the calendar this machine holds, and what
/// goes back. Kept here rather than in the application because none of it is about a window: a
/// reply is a payload, and who sends it is somebody else's business.
/// </remarks>
public static class Imip
{
    /// <summary>The MIME type an iMIP part carries.</summary>
    public const string MediaType = "text/calendar";

    /// <summary>Reads a <c>text/calendar</c> part. Null when it is not a scheduling message.</summary>
    public static ItipMessage? Read(string? calendarText)
    {
        if (string.IsNullOrWhiteSpace(calendarText)) return null;

        IReadOnlyList<CalendarEvent> events;
        try
        {
            events = ICalendarCodec.Parse(calendarText);
        }
        catch (FormatException)
        {
            return null;
        }

        if (events.Count == 0) return null;

        // The master rather than an override: an invitation to a series is about the series.
        var subject = events.FirstOrDefault(e => !e.IsOverride) ?? events[0];
        return new ItipMessage(MethodOf(calendarText), subject, calendarText);
    }

    /// <summary>The METHOD property's value, which the codec does not carry on an event.</summary>
    internal static ItipMethod MethodOf(string calendarText)
    {
        foreach (var line in calendarText.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("METHOD:", StringComparison.OrdinalIgnoreCase)) continue;
            return text[7..].Trim().ToUpperInvariant() switch
            {
                "REQUEST" => ItipMethod.Request,
                "REPLY" => ItipMethod.Reply,
                "CANCEL" => ItipMethod.Cancel,
                "COUNTER" => ItipMethod.Counter,
                "DECLINECOUNTER" => ItipMethod.DeclineCounter,
                "ADD" => ItipMethod.Add,
                "REFRESH" => ItipMethod.Refresh,
                "PUBLISH" => ItipMethod.Publish,
                _ => ItipMethod.None,
            };
        }

        // A part with no METHOD is a publication, which is what a .ics attachment is.
        return ItipMethod.Publish;
    }

    /// <summary>The PARTSTAT an answer writes.</summary>
    public static string PartStatOf(ItipResponse response) => response switch
    {
        ItipResponse.Accepted => "ACCEPTED",
        ItipResponse.Tentative => "TENTATIVE",
        _ => "DECLINED",
    };

    /// <summary>
    /// The <c>METHOD:REPLY</c> payload that answers an invitation: the same UID and
    /// RECURRENCE-ID, the organizer, and <em>only</em> the answering attendee.
    /// </summary>
    /// <remarks>
    /// Only that one attendee, as RFC 5546 requires: a reply carrying the whole list tells every
    /// other invitee's mail client that this machine speaks for them, and some of them believe it.
    /// </remarks>
    public static string Reply(ItipMessage invitation, string address, ItipResponse response, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var known = invitation.AttendeeFor(address);
        var answering = new EventAttendee(
            ItipMessage.Strip(address),
            displayName ?? known?.Name ?? string.Empty,
            known?.Role ?? "REQ-PARTICIPANT",
            PartStatOf(response));

        var reply = invitation.Event with
        {
            Attendees = [answering],
            // The sequence goes back as it came: a reply is about the version that was sent.
            Sequence = invitation.Event.Sequence,
            LastModified = DateTimeOffset.UtcNow,
        };

        return ICalendarCodec.SerializeCalendar([reply], "REPLY");
    }

    /// <summary>The <c>METHOD:REQUEST</c> payload an organizer sends for a meeting.</summary>
    public static string Request(CalendarEvent meeting, IReadOnlyList<CalendarEvent>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var family = overrides is { Count: > 0 } ? new List<CalendarEvent> { meeting }.Concat(overrides) : [meeting];
        return ICalendarCodec.SerializeCalendar(family, "REQUEST");
    }

    /// <summary>The <c>METHOD:CANCEL</c> payload that calls a meeting off.</summary>
    public static string Cancel(CalendarEvent meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        return ICalendarCodec.SerializeCalendar(
            [meeting with { Status = "CANCELLED", Sequence = meeting.Sequence + 1 }],
            "CANCEL");
    }

    /// <summary>
    /// What an arriving scheduling message does to the copy this machine already holds, if any.
    /// </summary>
    /// <param name="existing">The event already stored under the same UID, or null.</param>
    /// <param name="answer">What the reader said to it, when they were the one answering.</param>
    /// <param name="answeredBy">
    /// The address the answer was given from, so the stored copy records it. Without this the
    /// reply that went out said ACCEPTED and the appointment it was about still said
    /// NEEDS-ACTION — and it is the appointment, not the reply, that goes to the server.
    /// </param>
    /// <returns>The event to store, or null when the message means "remove it".</returns>
    public static CalendarEvent? Apply(
        ItipMessage message, CalendarEvent? existing, ItipResponse? answer = null, string? answeredBy = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (message.Method)
        {
            case ItipMethod.Cancel:
                return null;

            case ItipMethod.Reply:
            {
                // An answer to our own meeting: the one attendee it names is updated and every
                // other one is left exactly as it was.
                if (existing is null) return null;
                var replier = message.Event.Attendees.FirstOrDefault();
                if (replier is null) return existing;

                var attendees = existing.Attendees
                    .Select(a => string.Equals(ItipMessage.Strip(a.Address), ItipMessage.Strip(replier.Address), StringComparison.OrdinalIgnoreCase)
                        ? a with { PartStat = replier.PartStat }
                        : a)
                    .ToList();

                return existing with { Attendees = attendees, LastModified = DateTimeOffset.UtcNow };
            }

            case ItipMethod.Request:
            case ItipMethod.Add:
            case ItipMethod.Publish:
            {
                // An update older than what is held is a message that overtook another; ignoring
                // it is what stops a re-delivered invitation undoing a later change.
                if (existing is not null && message.Event.Sequence < existing.Sequence) return existing;

                var stored = message.Event;
                if (answer is { } response)
                {
                    stored = stored with
                    {
                        Busy = response == ItipResponse.Tentative ? BusyStatus.Tentative : BusyStatus.Busy,
                        Status = response == ItipResponse.Declined ? "CANCELLED" : "CONFIRMED",
                        Attendees = answeredBy is { Length: > 0 } me
                            ? [.. stored.Attendees.Select(a =>
                                string.Equals(ItipMessage.Strip(a.Address), ItipMessage.Strip(me), StringComparison.OrdinalIgnoreCase)
                                    ? a with { PartStat = PartStatOf(response), Rsvp = false }
                                    : a)]
                            : stored.Attendees,
                    };
                }

                return stored;
            }

            default:
                return existing;
        }
    }

    /// <summary>
    /// The one-line summary the reading pane's invitation bar leads with — when it is, and where.
    /// </summary>
    /// <param name="reader">
    /// The clock to state the time on. An invitation carries the organizer's wall time and the
    /// zone they wrote it in, and a bar that repeats it is telling a reader in Arizona that a
    /// meeting written in London is at two in the afternoon — then putting it on their calendar
    /// seven hours earlier when they accept. Null keeps the stated time, which is what a message
    /// being composed for somebody else wants.
    /// </param>
    public static string Describe(ItipMessage message, CultureInfo? culture = null, TimeZoneInfo? reader = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        culture ??= CultureInfo.CurrentCulture;
        var e = message.Event;

        // An all-day item is a date, not an instant: converting it would move a holiday onto the
        // evening before for anybody west of the zone it was written in.
        var start = reader is null || e.AllDay ? e.Start.Wall : Reading(e.Start, reader);
        var end = reader is null || e.AllDay ? e.End.Wall : Reading(e.End, reader);

        var when = e.AllDay
            ? start.ToString("dddd, d MMMM yyyy", culture)
            : $"{start.ToString("dddd, d MMMM yyyy HH:mm", culture)}–{end.ToString("HH:mm", culture)}";

        var where = e.Location.Length > 0 ? $"  ·  {e.Location}" : string.Empty;
        var repeats = e.Rrule is { Length: > 0 } rule ? $"  ·  {RecurrenceText.Describe(rule, e.Start, e.End, culture)}" : string.Empty;
        return when + where + repeats;
    }

    /// <summary>What a clock reads at the instant a stated time comes to.</summary>
    private static DateTime Reading(EventTime time, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(time.ToUtc(zone), zone).DateTime;

    /// <summary>What the bar says above that line, per method.</summary>
    public static string Headline(ItipMessage message, string? organizerName = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var who = organizerName is { Length: > 0 } name ? name : ItipMessage.Strip(message.Organizer);
        var what = message.Event.Summary is { Length: > 0 } summary ? $"“{summary}”" : "an appointment";

        return message.Method switch
        {
            ItipMethod.Request => who.Length > 0 ? $"{who} has invited you to {what}." : $"You have been invited to {what}.",
            ItipMethod.Cancel => $"{what} has been cancelled.",
            ItipMethod.Reply => $"Somebody has answered {what}.",
            ItipMethod.Counter => $"A different time has been proposed for {what}.",
            ItipMethod.DeclineCounter => $"The proposed new time for {what} was turned down.",
            ItipMethod.Add => $"More occurrences have been added to {what}.",
            ItipMethod.Refresh => $"The organizer has asked for the current version of {what}.",
            _ => $"This message carries {what}.",
        };
    }
}
