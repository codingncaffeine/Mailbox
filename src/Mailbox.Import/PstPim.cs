using System.Buffers.Binary;
using System.Text;
using Mailbox.Contacts;
using Mailbox.Pst;
using Mailbox.Pst.Messaging;
using Mailbox.Scheduling;

namespace Mailbox.Import;

/// <summary>
/// Turns a PST's calendar, contact, task, note and journal items into this application's own
/// records — the other half of what <see cref="PstMime"/> does for mail.
/// </summary>
/// <remarks>
/// Two conventions run through every mapping. Times: the file stores UTC instants, so timed
/// values become UTC-stated <see cref="EventTime"/>s — the instant is exact, and only a series
/// that crosses a daylight-saving change in its original zone drifts, the file not carrying a
/// zone this reader can name. Dates: an all-day value is a original-machine midnight expressed
/// in UTC, so the date is recovered by rounding — a time-of-day past noon means the writer sat
/// east of Greenwich and the date is the next one.
/// </remarks>
internal static class PstPim
{
    private static DateOnly AsDate(DateTimeOffset utc) =>
        DateOnly.FromDateTime(utc.UtcDateTime.TimeOfDay.TotalHours >= 12
            ? utc.UtcDateTime.Date.AddDays(1)
            : utc.UtcDateTime.Date);

    private static string Text(IStoredMessage m, PstNamedProperties names, Guid set, uint lid) =>
        m.Named(names, set, lid)?.AsString() ?? string.Empty;

    private static IReadOnlyList<string> Categories(IStoredMessage m, PstNamedProperties names)
    {
        if (names.IdOf(PstPropertySets.PublicStrings, "Keywords") is not { } id) return [];
        if (m.Property(id) is not { IsMultiValued: true } keywords) return [];
        return [.. keywords.Elements().Select(k => k.AsString()).Where(k => k.Length > 0)];
    }

    // ---- Appointments --------------------------------------------------------------------

    /// <summary>
    /// An appointment as one or more events: the master, and an override for each occurrence
    /// its recurrence blob moved. Null when the item has no times to stand on.
    /// </summary>
    public static IReadOnlyList<CalendarEvent>? ToEvents(IStoredMessage m, PstNamedProperties names, string uid, List<string> notes)
    {
        var start = m.Named(names, PstPropertySets.Appointment, 0x820D)?.AsTime();
        var end = m.Named(names, PstPropertySets.Appointment, 0x820E)?.AsTime();
        if (start is null || end is null) return null;
        if (end < start) end = start;

        var allDay = m.Named(names, PstPropertySets.Appointment, 0x8215)?.AsBoolean() ?? false;
        var busy = m.Named(names, PstPropertySets.Appointment, 0x8205)?.AsInteger32() ?? 2;
        var reminderSet = m.Named(names, PstPropertySets.Common, 0x8503)?.AsBoolean() ?? false;

        var master = new CalendarEvent
        {
            Uid = uid,
            Summary = m.Subject,
            Location = Text(m, names, PstPropertySets.Appointment, 0x8208),
            Description = m.BodyText,
            Start = allDay ? EventTime.Date(AsDate(start.Value)) : EventTime.At(start.Value.UtcDateTime, "UTC"),
            End = allDay ? EventTime.Date(AsDate(end.Value)) : EventTime.At(end.Value.UtcDateTime, "UTC"),
            // 4 is "working elsewhere", which blocks nothing — the nearest of the four words is Free.
            Busy = busy is >= 0 and <= 3 ? (BusyStatus)busy : BusyStatus.Free,
            ReminderMinutes = reminderSet ? m.Named(names, PstPropertySets.Common, 0x8501)?.AsInteger32() : null,
            Categories = Categories(m, names),
            Attendees = [.. m.Recipients()
                .Where(r => r.Address.Contains('@'))
                .Select(r => new EventAttendee(r.Address, r.Name, r.Type == PstRecipient.Cc ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT"))],
        };

        var recurring = m.Named(names, PstPropertySets.Appointment, 0x8223)?.AsBoolean() ?? false;
        var blob = m.Named(names, PstPropertySets.Appointment, 0x8216)?.AsBinary();
        if (!recurring || blob is not { Length: > 0 }) return [master];

        PstRecurrence? recurrence;
        try
        {
            recurrence = PstRecurrence.Parse(blob);
        }
        catch (PstException ex)
        {
            notes.Add($"“{m.Subject}” repeats in a way that could not be read ({ex.Message}) and was kept as a single appointment.");
            return [master];
        }

        if (recurrence is null)
        {
            notes.Add($"“{m.Subject}” repeats on a calendar an RRULE cannot state and was kept as a single appointment.");
            return [master];
        }

        // The blob speaks the original machine's wall clock; the master start is the same
        // moment as a UTC instant. The difference recovers that clock's offset, which is what
        // places removed and moved occurrences on the timeline the master lives on.
        var utcMinutes = (int)start.Value.UtcDateTime.TimeOfDay.TotalMinutes;
        var offset = recurrence.StartMinutes - utcMinutes;
        if (offset > 840) offset -= 1440;
        else if (offset < -720) offset += 1440;

        var exceptions = recurrence.RemovedDates
            .Select(date => allDay
                ? EventTime.Date(date)
                : EventTime.At(date.ToDateTime(TimeOnly.MinValue).AddMinutes(recurrence.StartMinutes - offset), "UTC"))
            .ToList();

        var events = new List<CalendarEvent>
        {
            master with { Rrule = recurrence.Rrule, ExceptionDates = exceptions },
        };

        foreach (var moved in recurrence.Overrides)
        {
            events.Add(master with
            {
                Start = allDay
                    ? EventTime.Date(DateOnly.FromDateTime(moved.Start))
                    : EventTime.At(moved.Start.AddMinutes(-offset), "UTC"),
                End = allDay
                    ? EventTime.Date(DateOnly.FromDateTime(moved.End))
                    : EventTime.At(moved.End.AddMinutes(-offset), "UTC"),
                RecurrenceId = allDay
                    ? EventTime.Date(DateOnly.FromDateTime(moved.OriginalStart))
                    : EventTime.At(moved.OriginalStart.AddMinutes(-offset), "UTC"),
            });
        }

        return events;
    }

    // ---- Tasks ---------------------------------------------------------------------------

    public static TaskItem ToTask(IStoredMessage m, PstNamedProperties names, string uid, List<string> notes)
    {
        var status = m.Named(names, PstPropertySets.Task, 0x8101)?.AsInteger32() ?? 0;
        var percent = m.Named(names, PstPropertySets.Task, 0x8102)?.Raw is { Length: 8 } raw
            ? BinaryPrimitives.ReadDoubleLittleEndian(raw)
            : 0.0;
        var importance = m.Property(0x0017)?.AsInteger32() ?? 1;

        string? rrule = null;
        if (m.Named(names, PstPropertySets.Task, 0x8116)?.AsBinary() is { Length: > 0 } blob)
        {
            try
            {
                rrule = PstRecurrence.ParseBare(blob)?.Rrule;
                if (rrule is null)
                    notes.Add($"“{m.Subject}” repeats on a calendar an RRULE cannot state and was kept as a single task.");
            }
            catch (PstException ex)
            {
                notes.Add($"“{m.Subject}” repeats in a way that could not be read ({ex.Message}) and was kept as a single task.");
            }
        }

        return new TaskItem
        {
            Uid = uid,
            Summary = m.Subject,
            Description = m.BodyText,
            Start = m.Named(names, PstPropertySets.Task, 0x8104)?.AsTime() is { } begin ? EventTime.Date(AsDate(begin)) : null,
            Due = m.Named(names, PstPropertySets.Task, 0x8105)?.AsTime() is { } due ? EventTime.Date(AsDate(due)) : null,
            CompletedUtc = m.Named(names, PstPropertySets.Task, 0x810F)?.AsTime(),
            Progress = status is >= 0 and <= 4 ? (TaskProgress)status : TaskProgress.NotStarted,
            PercentComplete = (int)Math.Clamp(Math.Round(percent * 100), 0, 100),
            Urgency = importance switch { 0 => TaskUrgency.Low, 2 => TaskUrgency.High, _ => TaskUrgency.Normal },
            IsPrivate = m.Named(names, PstPropertySets.Common, 0x8506)?.AsBoolean() ?? false,
            Rrule = rrule,
            Categories = Categories(m, names),
        };
    }

    // ---- Contacts ------------------------------------------------------------------------

    public static Contact ToContact(IStoredMessage m, PstNamedProperties names, string uid)
    {
        if (m.MessageClass.StartsWith("IPM.DistList", StringComparison.OrdinalIgnoreCase))
            return ToGroup(m, names, uid);

        var emails = new List<ContactEmail>();
        foreach (var (address, display) in new[] { (0x8083u, 0x8080u), (0x8093u, 0x8090u), (0x80A3u, 0x80A0u) })
        {
            var value = Text(m, names, PstPropertySets.Address, address);
            if (value.Length > 0) emails.Add(new ContactEmail(value, Text(m, names, PstPropertySets.Address, display)));
        }

        var phones = new List<ContactPhone>();
        foreach (var (id, kind) in new[]
        {
            ((ushort)0x3A08, PhoneKind.Business), ((ushort)0x3A09, PhoneKind.Home),
            ((ushort)0x3A1C, PhoneKind.Mobile), ((ushort)0x3A24, PhoneKind.BusinessFax),
            ((ushort)0x3A25, PhoneKind.HomeFax), ((ushort)0x3A21, PhoneKind.Pager),
            ((ushort)0x3A1F, PhoneKind.Other),
        })
        {
            if (m.Property(id)?.AsString() is { Length: > 0 } number) phones.Add(new ContactPhone(number, kind));
        }

        var addresses = new List<ContactAddress>();
        var work = new ContactAddress
        {
            Kind = AddressKind.Business,
            Street = Text(m, names, PstPropertySets.Address, 0x8045),
            City = Text(m, names, PstPropertySets.Address, 0x8046),
            State = Text(m, names, PstPropertySets.Address, 0x8047),
            PostalCode = Text(m, names, PstPropertySets.Address, 0x8048),
            Country = Text(m, names, PstPropertySets.Address, 0x8049),
        };
        if (work != new ContactAddress { Kind = AddressKind.Business }) addresses.Add(work);

        var home = new ContactAddress
        {
            Kind = AddressKind.Home,
            Street = m.Property(0x3A5D)?.AsString() ?? string.Empty,
            City = m.Property(0x3A59)?.AsString() ?? string.Empty,
            State = m.Property(0x3A5C)?.AsString() ?? string.Empty,
            PostalCode = m.Property(0x3A5B)?.AsString() ?? string.Empty,
            Country = m.Property(0x3A5A)?.AsString() ?? string.Empty,
        };
        if (home != new ContactAddress { Kind = AddressKind.Home }) addresses.Add(home);

        var urls = new[] { m.Property(0x3A51)?.AsString(), m.Property(0x3A50)?.AsString() }
            .Where(url => url is { Length: > 0 }).Select(url => url!).ToList();
        var im = Text(m, names, PstPropertySets.Address, 0x8062);

        // The photograph rides as an attachment that says it is one (PidTagAttachmentContactPhoto).
        var photo = m.Attachments()
            .Where(a => a.Property(0x7FFF)?.AsBoolean() ?? false)
            .Select(a => (a.Content, a.MimeType))
            .FirstOrDefault(p => p.Content.Length > 0);

        return new Contact
        {
            Uid = uid,
            DisplayName = m.Property(0x3001)?.AsString() ?? m.Subject,
            FirstName = m.Property(0x3A06)?.AsString() ?? string.Empty,
            MiddleName = m.Property(0x3A44)?.AsString() ?? string.Empty,
            LastName = m.Property(0x3A11)?.AsString() ?? string.Empty,
            Prefix = m.Property(0x3A45)?.AsString() ?? string.Empty,
            Suffix = m.Property(0x3A05)?.AsString() ?? string.Empty,
            NickName = m.Property(0x3A4F)?.AsString() ?? string.Empty,
            FileAs = Text(m, names, PstPropertySets.Address, 0x8005),
            Company = m.Property(0x3A16)?.AsString() ?? string.Empty,
            Department = m.Property(0x3A18)?.AsString() ?? string.Empty,
            JobTitle = m.Property(0x3A17)?.AsString() ?? string.Empty,
            Emails = emails,
            Phones = phones,
            Addresses = addresses,
            Urls = urls,
            InstantMessaging = im.Length > 0 ? [im] : [],
            Categories = Categories(m, names),
            Notes = m.BodyText,
            Birthday = m.Property(0x3A42)?.AsTime() is { } birthday ? AsDate(birthday) : null,
            Anniversary = m.Property(0x3A41)?.AsTime() is { } anniversary ? AsDate(anniversary) : null,
            Photo = photo.Content is { Length: > 0 }
                ? new ContactPhoto(photo.Content, photo.MimeType is { Length: > 0 } ? photo.MimeType : "image/jpeg")
                : null,
        };
    }

    /// <summary>The sixteen bytes every one-off EntryID carries as its provider.</summary>
    private static ReadOnlySpan<byte> OneOffProviderUid =>
        [0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02];

    private static Contact ToGroup(IStoredMessage m, PstNamedProperties names, string uid)
    {
        var members = new List<GroupMember>();
        if (m.Named(names, PstPropertySets.Address, 0x8055) is { IsMultiValued: true } list)
        {
            foreach (var element in list.Elements())
            {
                if (OneOffMember(element.Raw) is { } member) members.Add(member);
            }
        }

        var name = Text(m, names, PstPropertySets.Address, 0x8053);
        return new Contact
        {
            Uid = uid,
            IsGroup = true,
            DisplayName = name.Length > 0 ? name : m.Subject,
            FileAs = Text(m, names, PstPropertySets.Address, 0x8005),
            Members = members,
            Notes = m.BodyText,
            Categories = Categories(m, names),
        };
    }

    /// <summary>
    /// A member out of a one-off EntryID ([MS-OXCDATA] §2.2.5.1): display name, address type
    /// and address as three terminated strings after a fixed header. A member stored any other
    /// way — a pointer back into the address book — is skipped rather than guessed at.
    /// </summary>
    private static GroupMember? OneOffMember(byte[] raw)
    {
        if (raw.Length < 26) return null;

        // Wrapped members prefix the one-off with their own header; find the provider inside.
        var at = raw.AsSpan().IndexOf(OneOffProviderUid);
        if (at < 4) return null;

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 18));
        var text = raw.AsSpan(at + 20);
        var unicode = (flags & 0x8000) != 0;

        var parts = unicode
            ? Encoding.Unicode.GetString(text).Split('\0')
            : Encoding.Latin1.GetString(text).Split('\0');
        if (parts.Length < 3) return null;

        var address = parts[2];
        return address.Contains('@') ? new GroupMember(address, parts[0]) : null;
    }

    // ---- Notes and journal ---------------------------------------------------------------

    private static readonly string[] NoteColours = ["Blue Category", "Green Category", "Pink Category", "Yellow Category", "White Category"];

    public static JournalEntry ToNote(IStoredMessage m, PstNamedProperties names, string uid)
    {
        var colour = m.Named(names, PstPropertySets.Note, 0x8B00)?.AsInteger32();
        var categories = Categories(m, names);
        if (categories.Count == 0 && colour is >= 0 and <= 4) categories = [NoteColours[colour.Value]];

        return new JournalEntry { Uid = uid, Categories = categories }
            .WithBody(m.BodyText.Length > 0 ? m.BodyText : m.Subject) with
        {
            When = m.Property(0x3007)?.AsTime() is { } made ? EventTime.At(made.UtcDateTime, "UTC") : null,
        };
    }

    public static JournalEntry ToJournal(IStoredMessage m, PstNamedProperties names, string uid)
    {
        var type = Text(m, names, PstPropertySets.Log, 0x8700);
        var minutes = m.Named(names, PstPropertySets.Log, 0x8707)?.AsInteger32();

        return new JournalEntry
        {
            Uid = uid,
            Summary = m.Subject,
            Description = m.BodyText,
            EntryType = type.Length > 0 ? type : "Phone call",
            When = m.Named(names, PstPropertySets.Log, 0x8706)?.AsTime() is { } began
                ? EventTime.At(began.UtcDateTime, "UTC")
                : m.Property(0x3007)?.AsTime() is { } made ? EventTime.At(made.UtcDateTime, "UTC") : null,
            Duration = minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null,
            Categories = Categories(m, names),
        };
    }
}
