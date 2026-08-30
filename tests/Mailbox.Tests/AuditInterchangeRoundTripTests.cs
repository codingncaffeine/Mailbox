using Mailbox.Contacts;
using Mailbox.Import;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// What leaves as a file and comes back: an <c>.ics</c> and a <c>.vcf</c> written by the Save As
/// exports, read by the importers, and compared field by field rather than counted.
/// </summary>
/// <remarks>
/// A count survives almost anything. The audit found every exception to a series dropped on import
/// while the count still read right, and later a contact's postal address discarded on
/// save while the form read it back correctly first. So each of these builds the item, sends it
/// out through the same codec the export uses, brings it back through the real importer into a
/// store that has never seen it, and asks the store for every field — including the ones a
/// serializer is most likely to drop: the escaped ones, the repeated ones, and the ones that are
/// a second row rather than a column.
/// </remarks>
public class AuditInterchangeRoundTripTests : IDisposable
{
    private readonly PimStore _source = PimStore.Transient();
    private readonly PimStore _target = PimStore.Transient();

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- .ics ---------------------------------------------------------------------------------

    private static CalendarEvent Series() => new()
    {
        Uid = "series@example.com",
        Summary = "Damson pressing; weekly, with a comma, a semicolon; and a \\ backslash",
        Location = "The long shed, second door",
        Description = "Bring:\n  a ladder\n  a basket\nAnd note the — dash, the “quotes”, and ré­sumé.",
        Start = EventTime.At(new DateTime(2026, 8, 3, 9, 0, 0), "Europe/London"),
        End = EventTime.At(new DateTime(2026, 8, 3, 10, 30, 0), "Europe/London"),
        Rrule = "FREQ=WEEKLY;BYDAY=MO;COUNT=8",
        ExceptionDates = [EventTime.At(new DateTime(2026, 8, 17, 9, 0, 0), "Europe/London")],
        Busy = BusyStatus.Tentative,
        ReminderMinutes = 15,
        Categories = ["Green Category", "Orange Category"],
        Attendees =
        [
            new EventAttendee("a.person@example.com", "A. Person", "REQ-PARTICIPANT", "ACCEPTED", true),
            new EventAttendee("b.person@example.com", "B. Person, the second", "OPT-PARTICIPANT", "DECLINED"),
        ],
        Organizer = "you@example.com",
        Status = "CONFIRMED",
        Sequence = 3,
        IsPrivate = true,
        Urgency = TaskUrgency.High,
    };

    /// <summary>The moved occurrence: same UID as the series, which is the trap.</summary>
    private static CalendarEvent Exception() => new()
    {
        Uid = "series@example.com",
        Summary = "Damson pressing (moved)",
        Start = EventTime.At(new DateTime(2026, 8, 11, 14, 0, 0), "Europe/London"),
        End = EventTime.At(new DateTime(2026, 8, 11, 15, 0, 0), "Europe/London"),
        RecurrenceId = EventTime.At(new DateTime(2026, 8, 10, 9, 0, 0), "Europe/London"),
        Organizer = "you@example.com",
    };

    private static CalendarEvent AllDay() => new()
    {
        Uid = "allday@example.com",
        Summary = "Harvest week",
        Start = EventTime.Date(new DateOnly(2026, 8, 24)),
        End = EventTime.Date(new DateOnly(2026, 8, 29)),
        Busy = BusyStatus.OutOfOffice,
    };

    private static CalendarEvent AcrossZones() => new()
    {
        Uid = "zone@example.com",
        Summary = "Call with the other side of the world",
        Start = EventTime.At(new DateTime(2026, 8, 5, 22, 0, 0), "Pacific/Auckland"),
        End = EventTime.At(new DateTime(2026, 8, 5, 23, 0, 0), "Pacific/Auckland"),
    };

    /// <summary>
    /// An RRULE's parts as a set. RFC 5545 states no order for them and the serializer writes
    /// its own — FREQ=WEEKLY;BYDAY=MO;COUNT=8 comes back FREQ=WEEKLY;COUNT=8;BYDAY=MO — so the
    /// rule is compared by what it says rather than by how it is spelt.
    /// </summary>
    private static IReadOnlyList<string> Parts(string? rrule)
        => rrule is null ? [] : [.. rrule.Split(';', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    private List<CalendarEvent> ThereAndBack(IReadOnlyList<CalendarEvent> events)
    {
        // Out through the same codec ExportIcsAsync writes with, in through the real importer.
        var text = ICalendarCodec.SerializeCalendar(events);
        var report = new PimFileImporter(new PimRepository(_target)).Ics(text);
        Assert.Empty(report.Notes);

        var pim = new PimRepository(_target);
        var calendar = pim.Collections(CollectionKind.Events).First();
        return [.. pim.Items(calendar.Id).Select(PimEventCodec.FromItem)];
    }

    [Fact]
    public void AnIcsCarriesEverySeriesFieldOutAndBack()
    {
        var sent = Series();
        var back = Assert.Single(ThereAndBack([sent]));

        Assert.Equal(sent.Uid, back.Uid);
        Assert.Equal(sent.Summary, back.Summary);
        Assert.Equal(sent.Location, back.Location);
        Assert.Equal(sent.Description, back.Description);
        Assert.Equal(sent.Start, back.Start);
        Assert.Equal(sent.End, back.End);
        Assert.Equal(Parts(sent.Rrule), Parts(back.Rrule));
        Assert.Equal(sent.ExceptionDates, back.ExceptionDates);
        Assert.Equal(sent.Busy, back.Busy);
        Assert.Equal(sent.ReminderMinutes, back.ReminderMinutes);
        Assert.Equal(sent.Categories, back.Categories);
        Assert.Equal(sent.Organizer, back.Organizer);
        Assert.Equal(sent.Status, back.Status);
        Assert.Equal(sent.Sequence, back.Sequence);
        Assert.Equal(sent.IsPrivate, back.IsPrivate);
        Assert.Equal(sent.Urgency, back.Urgency);

        Assert.Equal(sent.Attendees.Count, back.Attendees.Count);
        foreach (var (was, now) in sent.Attendees.Zip(back.Attendees))
        {
            Assert.Equal(was.Address, now.Address);
            Assert.Equal(was.Name, now.Name);
            Assert.Equal(was.Role, now.Role);
            Assert.Equal(was.PartStat, now.PartStat);
            Assert.Equal(was.Rsvp, now.Rsvp);
        }
    }

    /// <summary>
    /// The series-exception data loss, held shut: a series and its exception share one UID, so an importer
    /// matching on the UID alone answers the exception with the master, calls it already here,
    /// and drops the one occurrence a reader would notice missing.
    /// </summary>
    [Fact]
    public void AnIcsKeepsASeriesAndItsMovedOccurrenceApart()
    {
        var back = ThereAndBack([Series(), Exception()]);

        Assert.Equal(2, back.Count);
        var master = Assert.Single(back, e => !e.IsOverride);
        var moved = Assert.Single(back, e => e.IsOverride);

        Assert.Equal("series@example.com", master.Uid);
        Assert.Equal("series@example.com", moved.Uid);
        Assert.Equal("Damson pressing (moved)", moved.Summary);
        Assert.Equal(EventTime.At(new DateTime(2026, 8, 10, 9, 0, 0), "Europe/London"), moved.RecurrenceId);
        Assert.Equal(EventTime.At(new DateTime(2026, 8, 11, 14, 0, 0), "Europe/London"), moved.Start);

        // And the master kept its own times rather than being written over by the exception.
        Assert.Equal(EventTime.At(new DateTime(2026, 8, 3, 9, 0, 0), "Europe/London"), master.Start);
        Assert.Equal(Parts("FREQ=WEEKLY;BYDAY=MO;COUNT=8"), Parts(master.Rrule));
    }

    [Fact]
    public void AnIcsKeepsAnAllDaySpanAndAZoneThatIsNotThisMachines()
    {
        var back = ThereAndBack([AllDay(), AcrossZones()]);

        var allDay = Assert.Single(back, e => e.Uid == "allday@example.com");
        Assert.True(allDay.AllDay);
        Assert.Equal(new DateTime(2026, 8, 24), allDay.Start.Wall);
        Assert.Equal(new DateTime(2026, 8, 29), allDay.End.Wall);
        Assert.Equal(BusyStatus.OutOfOffice, allDay.Busy);

        var zoned = Assert.Single(back, e => e.Uid == "zone@example.com");
        Assert.False(zoned.AllDay);
        // The wall time and the zone both, because the instant alone loses the appointment on a
        // DST change and is the thing a UTC-only store gets wrong.
        Assert.Equal(new DateTime(2026, 8, 5, 22, 0, 0), zoned.Start.Wall);
        Assert.Equal("Pacific/Auckland", zoned.Start.TzId);
    }

    /// <summary>Re-importing the same file must top up, not double.</summary>
    [Fact]
    public void ImportingTheSameIcsTwiceAddsNothingTheSecondTime()
    {
        var text = ICalendarCodec.SerializeCalendar([Series(), Exception(), AllDay()]);
        var importer = new PimFileImporter(new PimRepository(_target));

        var first = importer.Ics(text);
        var second = importer.Ics(text);

        Assert.Equal(3, first.Events);
        Assert.Equal(0, second.Events);
        Assert.Equal(3, second.AlreadyHere);

        var pim = new PimRepository(_target);
        Assert.Equal(3, pim.Items(pim.Collections(CollectionKind.Events).First().Id).Count);
    }

    // ---- .vcf ---------------------------------------------------------------------------------

    private static Contact Card() => new()
    {
        Uid = "card-one@example.com",
        DisplayName = "Bö Persson, the elder",
        FirstName = "Bö",
        MiddleName = "Anders",
        LastName = "Persson",
        Prefix = "Dr",
        Suffix = "PhD",
        NickName = "Bo",
        FileAs = "Persson, Bö",
        Company = "Example; Orchards, Ltd",
        Department = "Damsons",
        JobTitle = "Head of Pressing",
        Emails = [new ContactEmail("bo.persson@example.se"), new ContactEmail("bo@example.org")],
        Phones =
        [
            new ContactPhone("+46 8 123 456"),
            new ContactPhone("+46 70 000 0001", PhoneKind.Mobile),
            new ContactPhone("+46 8 123 999", PhoneKind.BusinessFax),
        ],
        Addresses =
        [
            new ContactAddress
            {
                Kind = AddressKind.Business,
                Street = "12 Orchard Lane\nSecond floor",
                City = "Nyköping",
                State = "Södermanland",
                PostalCode = "611 30",
                Country = "Sweden",
                PostOfficeBox = "PO 42",
            },
            new ContactAddress { Kind = AddressKind.Home, Street = "3 Plum Row", City = "Malmö", Country = "Sweden" },
        ],
        Urls = ["https://example.se/bo"],
        InstantMessaging = ["xmpp:bo@example.se"],
        Categories = ["Blue Category"],
        Notes = "Knows about damsons.\nAnd about quinces; both.",
        Birthday = new DateOnly(1974, 3, 9),
        Anniversary = new DateOnly(2001, 6, 21),
        Photo = new ContactPhoto([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png"),
        IsPrivate = true,
    };

    private Contact ThereAndBack(Contact contact)
    {
        var book = new ContactBook(new PimRepository(_source));
        book.Save(contact, book.Default().Id);

        var text = VCardCodec.SerializeMany(
            book.Rows([book.Default().Id]).Select(r => book.Full(r.Id) ?? r.Contact).ToList());

        var report = new PimFileImporter(new PimRepository(_target)).Vcf(text);
        Assert.Empty(report.Notes);
        Assert.Equal(1, report.Contacts);

        var landed = new ContactBook(new PimRepository(_target));
        var row = Assert.Single(landed.Rows([landed.Default().Id]));
        return landed.Full(row.Id) ?? row.Contact;
    }

    [Fact]
    public void AVcfCarriesEveryCardFieldOutAndBack()
    {
        var sent = Card();
        var back = ThereAndBack(sent);

        Assert.Equal(sent.Uid, back.Uid);
        Assert.Equal(sent.DisplayName, back.DisplayName);
        Assert.Equal(sent.FirstName, back.FirstName);
        Assert.Equal(sent.MiddleName, back.MiddleName);
        Assert.Equal(sent.LastName, back.LastName);
        Assert.Equal(sent.Prefix, back.Prefix);
        Assert.Equal(sent.Suffix, back.Suffix);
        Assert.Equal(sent.NickName, back.NickName);
        Assert.Equal(sent.FileAs, back.FileAs);
        Assert.Equal(sent.Company, back.Company);
        Assert.Equal(sent.Department, back.Department);
        Assert.Equal(sent.JobTitle, back.JobTitle);
        Assert.Equal(sent.Notes, back.Notes);
        Assert.Equal(sent.Birthday, back.Birthday);
        Assert.Equal(sent.Anniversary, back.Anniversary);
        Assert.Equal(sent.Categories, back.Categories);
        Assert.Equal(sent.Urls, back.Urls);
        Assert.Equal(sent.InstantMessaging, back.InstantMessaging);
        Assert.Equal(sent.IsPrivate, back.IsPrivate);

        Assert.Equal(sent.Emails.Select(e => e.Address), back.Emails.Select(e => e.Address));
        Assert.Equal(sent.Phones.Select(p => (p.Number, p.Kind)), back.Phones.Select(p => (p.Number, p.Kind)));
    }

    /// <summary>
    /// The postal address, on its own: the audit found one discarded on save with the form reading
    /// it back correctly first, and a card is where the same shape of loss would hide next.
    /// </summary>
    [Fact]
    public void AVcfCarriesEveryPartOfEveryPostalAddress()
    {
        var sent = Card();
        var back = ThereAndBack(sent);

        Assert.Equal(sent.Addresses.Count, back.Addresses.Count);
        foreach (var (was, now) in sent.Addresses.Zip(back.Addresses))
        {
            Assert.Equal(was.Kind, now.Kind);
            Assert.Equal(was.Street, now.Street);
            Assert.Equal(was.City, now.City);
            Assert.Equal(was.State, now.State);
            Assert.Equal(was.PostalCode, now.PostalCode);
            Assert.Equal(was.Country, now.Country);
            Assert.Equal(was.PostOfficeBox, now.PostOfficeBox);
        }
    }

    [Fact]
    public void AVcfCarriesAPhotographOutAndBack()
    {
        var back = ThereAndBack(Card());

        Assert.NotNull(back.Photo);
        Assert.Equal("image/png", back.Photo!.MediaType);
        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], back.Photo.Bytes!);
    }

    [Fact]
    public void ImportingTheSameVcfTwiceAddsNothingTheSecondTime()
    {
        var book = new ContactBook(new PimRepository(_source));
        book.Save(Card(), book.Default().Id);
        var text = VCardCodec.SerializeMany(
            book.Rows([book.Default().Id]).Select(r => book.Full(r.Id) ?? r.Contact).ToList());

        var importer = new PimFileImporter(new PimRepository(_target));
        Assert.Equal(1, importer.Vcf(text).Contacts);
        var second = importer.Vcf(text);

        Assert.Equal(0, second.Contacts);
        Assert.Equal(1, second.AlreadyHere);

        var landed = new ContactBook(new PimRepository(_target));
        Assert.Single(landed.Rows([landed.Default().Id]));
    }
}
