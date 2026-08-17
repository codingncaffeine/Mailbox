using Mailbox.Core.Settings;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Contacts;
using Mailbox.Store.Pim;
using MimeKit;
using MimeKit.Utils;

namespace Mailbox.Tests;

/// <summary>
/// Writes a populated multi-account directory for looking at by hand. Skipped in an ordinary
/// run; set MAILBOX_SEED to a directory to produce one.
/// </summary>
/// <remarks>
/// Each message is written as real MIME rather than as a summary, because the reading pane
/// renders what was received: a store of summaries exercises the list and nothing below it.
/// The bodies are shaped to reach the surfaces that only appear for certain mail — an inline
/// image, a tracking pixel, a spoofed display name — since none of those can be photographed
/// without a message that has one.
/// <para>
/// Every address and name here is invented. The reference captures are the owner's real mail
/// and nothing from them belongs in sample data.
/// </para>
/// </remarks>
public class SeedHarness
{
    /// <summary>
    /// The day the seed is dated against: <c>MAILBOX_TODAY</c> when it is set, so a seed and a
    /// pinned clock agree and a capture is the same picture next year.
    /// </summary>
    private static DateOnly SeedToday()
        => Environment.GetEnvironmentVariable("MAILBOX_TODAY") is { Length: > 0 } pinned
           && DateOnly.TryParseExact(pinned, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)
            ? day
            : DateOnly.FromDateTime(DateTime.Today);

    /// <summary>An 8-byte PNG header, which is enough to be a distinct inline part.</summary>
    private static readonly byte[] TinyPng = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void SeedOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED");
        if (string.IsNullOrWhiteSpace(target)) return;

        var order = new SettingsAccountOrder(
            new SettingsStore(Path.Combine(target, "settings.json")));

        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);

        Seed(stores, "you@example.com",
            Plain("Alice Chen", "alice@example.com", "Re: Q3 numbers",
                "Thanks for pulling those together.\n\nThe variance on line 14 is the one I'd "
                + "want to talk through before Thursday. Everything else reconciles against "
                + "what finance sent over last week.\n\nSee https://example.com/q3 for the "
                + "worksheet.\n\nAlice"),

            Marketing("The Weekly", "news@newsletter.example", "Your Tuesday briefing"),

            Phishing(),

            Plain("Build Notifications", "builds@example.com", "mailbox/main — build passed",
                "Commit 4f2a1c9 built successfully on linux-x64.\n\n0 warnings, 0 errors.\n"
                + "Elapsed 00:00:04.62"));

        Seed(stores, "work@example.net",
            Plain("Priya Raman", "priya@example.net", "Font substitution question",
                "Confirmed — Carlito is metric-compatible with Calibri, so the layout holds "
                + "either way."),

            WithAttachment("Sam Reyes", "sam@example.net", "Draft agenda attached",
                "Rough cut for Monday. Shout if there's anything you want added before I send "
                + "it round.",
                "agenda.pdf", "application/pdf", 38_000),

            Invitation("Priya Raman", "priya@example.net", "work@example.net", SeedToday().AddDays(4)),

            Forwarded());

        SeedImap(stores, "imap@example.org");
        SeedCalendar(Path.Combine(target, "pim.db"));
        SeedContacts(Path.Combine(target, "pim.db"));
        SeedTasks(Path.Combine(target, "pim.db"));
        SeedNotesAndJournal(Path.Combine(target, "pim.db"));
    }

    /// <summary>
    /// A calendar to look at: <c>pim.db</c> beside the accounts directory, exactly where the
    /// application looks for it when <c>MAILBOX_STORE</c> poses one.
    /// </summary>
    /// <remarks>
    /// Shaped to reach the parts of the month view that only appear for certain items: a series
    /// with an override and an exception, an all-day item, one running over two days, one of each
    /// Show As so all four chip treatments are on screen, and a day with more than fits so the
    /// overflow mark is drawn. Every subject, place and name is invented.
    /// <para>
    /// Dated against <c>MAILBOX_TODAY</c> when it is set, so a seed and a pinned clock agree; the
    /// reference capture's own today is 2026-08-16.
    /// </para>
    /// </remarks>
    private static void SeedCalendar(string path)
    {
        var today = SeedToday();

        if (File.Exists(path)) File.Delete(path);
        using var store = new PimStore(path);
        var pim = new PimRepository(store);

        var calendar = pim.AddCollection(CollectionKind.Events, "Calendar", "#0078D4").Id;
        var team = pim.AddCollection(CollectionKind.Events, "Team", "#107C10").Id;
        var zone = TimeZoneInfo.Local.Id;

        void Add(long collection, CalendarEvent calendarEvent)
            => pim.AddItem(PimEventCodec.ToItem(calendarEvent, collection));

        CalendarEvent At(string summary, string location, DateOnly on, int hour, int minutes, BusyStatus busy)
            => new()
            {
                Uid = CalendarEvent.NewUid(),
                Summary = summary,
                Location = location,
                Start = EventTime.At(on.ToDateTime(new TimeOnly(hour, 0)), zone),
                End = EventTime.At(on.ToDateTime(new TimeOnly(hour, 0)).AddMinutes(minutes), zone),
                Busy = busy,
                ReminderMinutes = 15,
            };

        // The four Show As treatments, so a capture has one of each side by side.
        Add(calendar, At("Design review", "https://example.com/meet/design-review", today.AddDays(-4), 17, 60, BusyStatus.Tentative));
        Add(calendar, At("Dentist", "Fern Street Practice", today.AddDays(-4), 18, 45, BusyStatus.Busy));
        Add(calendar, At("Gym", "", today.AddDays(-11), 7, 60, BusyStatus.Free));
        Add(calendar, At("Away", "", today.AddDays(9), 9, 480, BusyStatus.OutOfOffice));

        // A day with more than the cell can hold, so the overflow mark is drawn.
        var busyDay = today.AddDays(-5);
        Add(calendar, At("Standup", "Room 2", busyDay, 9, 15, BusyStatus.Busy));
        Add(calendar, At("Interview: platform engineer", "Room 4 | Ground floor | Building A", busyDay, 11, 60, BusyStatus.Busy));
        Add(calendar, At("Lunch with A. Person", "The Corner Cafe, 14 Bridge Street", busyDay, 13, 60, BusyStatus.Free));
        Add(calendar, At("Release readiness", "https://example.com/meet/release", busyDay, 15, 30, BusyStatus.Tentative));
        Add(calendar, At("Retro", "Room 2", busyDay, 16, 60, BusyStatus.Busy));

        // An all-day item and one that runs over two days, so the month view's bands are drawn.
        Add(calendar, new CalendarEvent
        {
            Uid = CalendarEvent.NewUid(),
            Summary = "Public holiday",
            Start = EventTime.Date(today.AddDays(4)),
            End = EventTime.Date(today.AddDays(5)),
            Busy = BusyStatus.Free,
        });

        Add(team, new CalendarEvent
        {
            Uid = CalendarEvent.NewUid(),
            Summary = "Offsite",
            Location = "Riverside Centre",
            Start = EventTime.Date(today.AddDays(11)),
            End = EventTime.Date(today.AddDays(14)),
            Busy = BusyStatus.OutOfOffice,
        });

        // A weekly series with one occurrence moved and one taken out, which is the pair that
        // exercises overrides and EXDATE together.
        var seriesStart = today.AddDays(-14);
        var master = new CalendarEvent
        {
            Uid = CalendarEvent.NewUid(),
            Summary = "Weekly sync",
            Location = "https://example.com/meet/weekly",
            Start = EventTime.At(seriesStart.ToDateTime(new TimeOnly(10, 0)), zone),
            End = EventTime.At(seriesStart.ToDateTime(new TimeOnly(10, 30)), zone),
            Rrule = "FREQ=WEEKLY;BYDAY=" + Code(seriesStart.DayOfWeek),
            ExceptionDates = [EventTime.At(seriesStart.AddDays(7).ToDateTime(new TimeOnly(10, 0)), zone)],
            Busy = BusyStatus.Busy,
            ReminderMinutes = 15,
        };
        Add(calendar, master);

        var movedFrom = seriesStart.AddDays(21);
        Add(calendar, master with
        {
            Rrule = null,
            ExceptionDates = [],
            RecurrenceId = EventTime.At(movedFrom.ToDateTime(new TimeOnly(10, 0)), zone),
            Start = EventTime.At(movedFrom.ToDateTime(new TimeOnly(14, 0)), zone),
            End = EventTime.At(movedFrom.ToDateTime(new TimeOnly(15, 0)), zone),
            Summary = "Weekly sync (moved)",
        });

        static string Code(DayOfWeek day) => day switch
        {
            DayOfWeek.Sunday => "SU",
            DayOfWeek.Monday => "MO",
            DayOfWeek.Tuesday => "TU",
            DayOfWeek.Wednesday => "WE",
            DayOfWeek.Thursday => "TH",
            DayOfWeek.Friday => "FR",
            _ => "SA",
        };
    }

    /// <summary>
    /// A to-do list to look at, in the same <c>pim.db</c> the calendar and the address book are in.
    /// </summary>
    /// <remarks>
    /// Shaped to reach every band the list draws: one already late, one due today, one tomorrow,
    /// one later this week and one next month, plus one with no date at all and one already
    /// finished — so a capture shows the headings, the red of a late row, and the tick.
    /// </remarks>
    private static void SeedTasks(string path)
    {
        var today = SeedToday();
        using var store = new PimStore(path);
        var pim = new PimRepository(store);
        var list = pim.AddCollection(CollectionKind.Tasks, "Tasks", "#0078D4").Id;

        void Add(
            string summary,
            DateOnly? due,
            TaskProgress progress = TaskProgress.NotStarted,
            int percent = 0,
            TaskUrgency urgency = TaskUrgency.Normal,
            int? reminder = null)
            => pim.AddItem(PimTodoCodec.ToItem(
                new TaskItem
                {
                    Uid = TaskItem.NewUid(),
                    Summary = summary,
                    Due = due is { } d ? EventTime.Date(d) : null,
                    Progress = progress,
                    PercentComplete = percent,
                    Urgency = urgency,
                    ReminderMinutes = reminder,
                    LastModified = DateTimeOffset.UtcNow,
                },
                list));

        // The late one carries a reminder, so the Reminders window has a task to show: a task's
        // alarm does not stop when its date passes, which is the difference worth being able to
        // photograph.
        Add("Send the quarterly numbers", today.AddDays(-2), TaskProgress.InProgress, 40, TaskUrgency.High, reminder: 15);
        Add("Book the meeting room", today);
        Add("Read the draft agenda", today.AddDays(1));
        Add("Renew the domain", today.AddDays(4));
        Add("Plan the offsite", today.AddDays(28));
        Add("Think about the newsletter", null);
        Add("File the receipts", today.AddDays(-6), TaskProgress.Completed, 100);
    }

    /// <summary>
    /// Notes and journal entries to look at, in the same <c>pim.db</c> as everything else.
    /// </summary>
    /// <remarks>
    /// One pass for both because they are one component in one kind of collection, split by what
    /// each says it is: a note says nothing, and an entry names the sort of thing it was.
    /// <para>
    /// Shaped to reach what the two modules only draw for certain items: notes in four of the
    /// theme's six colours and one with no category at all, one note older than a week so that
    /// Last 7 Days is a different list from Icons, a note with several lines so the wall has a
    /// title to shorten and the rows have something to preview, entries of four types so the
    /// Entry List has more than one heading, and two calls so the Phone Calls view is not one row.
    /// Every subject, name and number is invented.
    /// </para>
    /// </remarks>
    private static void SeedNotesAndJournal(string path)
    {
        var today = SeedToday();
        var zone = TimeZoneInfo.Local.Id;

        using var store = new PimStore(path);
        var pim = new PimRepository(store);
        var notes = pim.AddCollection(CollectionKind.Journal, "Notes", "#F2C811").Id;
        var journal = pim.AddCollection(CollectionKind.Journal, "Journal", "#8764B8").Id;

        void Note(string body, DateOnly on, TimeOnly at, params string[] categories)
            => pim.AddItem(PimJournalCodec.ToItem(
                new JournalEntry
                {
                    Uid = JournalEntry.NewUid(),
                    When = EventTime.At(on.ToDateTime(at), zone),
                    Categories = categories,
                    LastModified = DateTimeOffset.UtcNow,
                }.WithBody(body),
                notes));

        void Entry(string subject, string type, DateOnly on, TimeOnly at, TimeSpan? took, string contact = "", string body = "", params string[] categories)
            => pim.AddItem(PimJournalCodec.ToItem(
                new JournalEntry
                {
                    Uid = JournalEntry.NewUid(),
                    Summary = subject,
                    Description = body,
                    EntryType = type,
                    When = EventTime.At(on.ToDateTime(at), zone),
                    Duration = took,
                    Contacts = contact.Length > 0 ? [contact] : [],
                    Categories = categories,
                    LastModified = DateTimeOffset.UtcNow,
                },
                journal));

        Note("Wi-Fi in the studio\nssid: studio-guest, and the key is taped inside the cupboard door.", today, new TimeOnly(9, 20));
        Note("Newsletter ideas\n— a short piece on metric-compatible fonts\n— what the reading pane is for", today, new TimeOnly(11, 5), "Blue Category");
        Note("Shopping\nmilk, bread, coffee, and something for Sunday", today.AddDays(-1), new TimeOnly(18, 40), "Green Category");
        Note("Ring the plumber back — Tuesday after four", today.AddDays(-2), new TimeOnly(8, 15), "Red Category");
        Note("Reading list\nThe one A. Person kept going on about, and the sequel.", today.AddDays(-12), new TimeOnly(21, 0), "Purple Category");

        Entry("A. Person about the release", "Phone call", today, new TimeOnly(10, 0), TimeSpan.FromMinutes(45), "A. Person",
            "Agreed to cut the release once the last two are in.");
        Entry("Design review", "Meeting", today, new TimeOnly(14, 0), TimeSpan.FromHours(1), "Sam Reyes", categories: ["Green Category"]);
        Entry("Drafted the quarterly numbers", "Document", today.AddDays(-1), new TimeOnly(9, 30), TimeSpan.FromHours(2), categories: ["Orange Category"]);
        Entry("Sent the agenda round", "E-mail Message", today.AddDays(-2), new TimeOnly(16, 45), null, "Priya Raman");
        Entry("Priya Raman about the font substitution", "Phone call", today.AddDays(-3), new TimeOnly(11, 15), TimeSpan.FromMinutes(15), "Priya Raman");
        Entry("Offsite planning", "Meeting", today.AddDays(-9), new TimeOnly(13, 0), TimeSpan.FromHours(3));
    }

    /// <summary>
    /// An address book to look at, in the same <c>pim.db</c> the calendar is in.
    /// </summary>
    /// <remarks>
    /// Shaped to reach the parts of the People list that only appear for certain contacts: names
    /// spread over the index so several of its letters are live, one filed under a digit, a
    /// contact with a photograph and one without, a group with two members, and somebody with
    /// every field the card can show. Every name, address and number is invented.
    /// </remarks>
    private static void SeedContacts(string path)
    {
        using var store = new PimStore(path);
        var book = new ContactBook(new PimRepository(store));
        var contacts = book.Default();
        var team = new PimRepository(store).AddCollection(CollectionKind.Contacts, "Team");

        void Add(Contact contact, long collection) => book.Save(contact, collection);

        Add(
            new Contact
            {
                Uid = "a.person@example.com",
                DisplayName = "A. Person",
                FirstName = "A.",
                LastName = "Person",
                Company = "Example Ltd.",
                Department = "Research",
                JobTitle = "Principal Engineer",
                Emails = [new ContactEmail("a.person@example.com"), new ContactEmail("a.person@example.net")],
                Phones =
                [
                    new ContactPhone("+44 20 7946 0000"),
                    new ContactPhone("+44 7700 900000", PhoneKind.Mobile),
                    new ContactPhone("+44 20 7946 0001", PhoneKind.BusinessFax),
                ],
                Addresses =
                [
                    new ContactAddress { Street = "1 Example Street", City = "London", PostalCode = "EC1A 1AA", Country = "United Kingdom" },
                ],
                Urls = ["https://example.com/a.person"],
                Notes = "Prefers e-mail.",
                Birthday = new DateOnly(1980, 4, 1),
                Categories = ["Colleagues"],
                Photo = new ContactPhoto(Portrait(), "image/png"),
            },
            contacts.Id);

        Add(
            new Contact
            {
                Uid = "b.other@example.com",
                DisplayName = "B. Other",
                FirstName = "B.",
                LastName = "Other",
                Company = "Another Ltd.",
                JobTitle = "Buyer",
                Emails = [new ContactEmail("b.other@example.com")],
                Phones = [new ContactPhone("+44 161 496 0002", PhoneKind.Home)],
            },
            contacts.Id);

        Add(
            new Contact
            {
                Uid = "c.reader@example.org",
                DisplayName = "C. Reader",
                FirstName = "C.",
                LastName = "Reader",
                Company = "Example Ltd.",
                Emails = [new ContactEmail("c.reader@example.org")],
            },
            team.Id);

        Add(
            new Contact
            {
                Uid = "3hills@example.net",
                DisplayName = "3 Hills Catering",
                Company = "3 Hills Catering",
                FileAs = "3 Hills Catering",
                Emails = [new ContactEmail("orders@example.net")],
                Phones = [new ContactPhone("+44 20 7946 0100")],
            },
            contacts.Id);

        Add(
            new Contact
            {
                Uid = "research-team@example.com",
                DisplayName = "Research team",
                IsGroup = true,
                Members =
                [
                    new GroupMember(Uid: "a.person@example.com"),
                    new GroupMember("c.reader@example.org", "C. Reader"),
                ],
            },
            contacts.Id);
    }

    /// <summary>
    /// A contact's photograph: a tiny PNG drawn here rather than shipped, so the seed carries no
    /// picture of anybody real.
    /// </summary>
    private static byte[] Portrait()
    {
        // A 2x2 PNG in one flat colour. It exists to prove a photograph reaches the card and the
        // list, not to look like a person.
        const string Base64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAGElEQVR42mPo33wTK2KgokR0/TKsiIoSAMs4c2FyFDbnAAAAAElFTkSuQmCC";

        return Convert.FromBase64String(Base64);
    }

    /// <summary>
    /// An IMAP account, so the folder pane shows the nesting and the "IMAP/SMTP" type a POP3
    /// account does not have. Its mail is filed with server UIDs, and a mapped sub-folder sits
    /// under its parent to exercise the tree indent.
    /// </summary>
    private static void SeedImap(AccountStores stores, string address)
    {
        var account = stores.Add(address, address, MailProtocol.Imap);
        var accountId = account.Account.Id;

        // Map the role folders to server paths, as a first sync would, and nest one folder.
        var inbox = account.Mail.FolderWithRole(accountId, FolderRole.Inbox)!;
        account.Mail.MapFolder(inbox.Id, "INBOX", "Inbox", null);
        var projects = account.Mail.AddFolder(accountId, "Projects", FolderRole.None, null, "Projects");
        account.Mail.AddFolder(accountId, "Mailbox", FolderRole.None, projects.Id, "Projects/Mailbox");

        var when = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            Plain("Dana Okafor", "dana@example.org", "Server-side folders",
                "The whole tree syncs now — try dragging this into Projects and watch it move "
                + "on the server."),
            Plain("CI", "ci@example.org", "IDLE is live",
                "New mail turns up without waiting for the timer."),
        };

        var uid = 1;
        foreach (var message in messages)
        {
            message.Date = when;
            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();
            var summary = Mailbox.Protocols.MessageMapper.ToSummary(
                message, uid.ToString(), raw.Length, when);
            account.Mail.AddMessage(inbox.Id, summary, raw);
            uid++;
            when = when.AddMinutes(-25);
        }

        account.Mail.SetFolderSyncState(inbox.Id, 1, uid, null);
    }

    // ---- The messages ----------------------------------------------------------------------

    private static MimeMessage Plain(string name, string address, string subject, string body)
    {
        var message = Envelope(name, address, subject);
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    /// <summary>
    /// A meeting invitation as iMIP carries one (RFC 6047): words for a person and a
    /// <c>METHOD:REQUEST</c> part for a client, so the reading pane's invitation bar has
    /// something to draw.
    /// </summary>
    private static MimeMessage Invitation(string name, string address, string to, DateOnly on)
    {
        var message = Envelope(name, address, "Design review");
        message.To.Clear();
        message.To.Add(new MailboxAddress("You", to));

        var payload = $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Example//EN
            METHOD:REQUEST
            BEGIN:VEVENT
            UID:seed-invitation@example.net
            DTSTAMP:{on.ToDateTime(TimeOnly.MinValue):yyyyMMdd}T080000Z
            DTSTART:{on.ToDateTime(TimeOnly.MinValue):yyyyMMdd}T140000Z
            DTEND:{on.ToDateTime(TimeOnly.MinValue):yyyyMMdd}T150000Z
            SUMMARY:Design review
            LOCATION:Room 2
            SEQUENCE:1
            ORGANIZER;CN={name}:mailto:{address}
            ATTENDEE;CN=You;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:{to}
            END:VEVENT
            END:VCALENDAR
            """.ReplaceLineEndings("\r\n");

        var calendar = new TextPart("calendar") { Text = payload };
        calendar.ContentType.Parameters["method"] = "REQUEST";
        calendar.ContentType.Parameters["charset"] = "utf-8";

        message.Body = new Multipart("alternative")
        {
            new TextPart("plain")
            {
                Text = "Putting an hour in for the review. Shout if that clashes with anything.",
            },
            calendar,
        };

        return message;
    }

    private static MimeMessage WithAttachment(
        string name, string address, string subject, string body,
        string fileName, string type, int size)
    {
        var message = Envelope(name, address, subject);

        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = body },
            new MimePart(ContentType.Parse(type))
            {
                FileName = fileName,
                Content = new MimeContent(new MemoryStream(new byte[size])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };

        return message;
    }

    /// <summary>
    /// A message forwarded as an attachment, which is a whole message inside a message.
    /// </summary>
    /// <remarks>
    /// Here because the attachment strip has a case for it that no other seeded message reaches:
    /// a <c>message/rfc822</c> part is not a <c>MimePart</c>, and a strip that matches only the
    /// latter shows nothing at all for the commonest way of passing mail on.
    /// </remarks>
    private static MimeMessage Forwarded()
    {
        var original = Envelope("Dana Whitfield", "dana@example.org", "Venue options");
        original.Body = new TextPart("plain")
        {
            Text = "Three places can take us on the 14th. Costs attached.\n\nDana",
        };

        var message = Envelope("Priya Raman", "priya@example.net", "FW: Venue options");
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Forwarding Dana's note — see what you think." },
            new MessagePart
            {
                Message = original,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
        };

        return message;
    }

    /// <summary>
    /// The shape most commercial mail takes: a stylesheet, an inline logo, and a pixel whose
    /// only purpose is to report that the message was opened.
    /// </summary>
    private static MimeMessage Marketing(string name, string address, string subject)
    {
        var message = Envelope(name, address, subject);

        var logo = new MimePart("image", "png")
        {
            ContentId = "logo",
            Content = new MimeContent(new MemoryStream(TinyPng)),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
        };

        var html = new TextPart("html")
        {
            Text = """
                <html><head><style>
                .wrap{font-family:Georgia,serif;max-width:560px}
                .lead{font-size:15px;line-height:1.5}
                .rule{border-top:1px solid #cccccc;margin:18px 0}
                </style></head><body>
                <div class="wrap">
                  <p><img src="cid:logo" alt="The Weekly" width="120" height="32"></p>
                  <p class="lead">Three things worth your time this week, and one that is not.</p>
                  <div class="rule"></div>
                  <p><a href="https://example.com/story/1">The first story</a></p>
                  <p><a href="https://example.com/story/2">The second story</a></p>
                  <p><img src="https://pixel.tracker.example/open?id=42" width="1" height="1"></p>
                  <p><img src="https://cdn.images.example/banner.png" width="560" height="120"></p>
                </div>
                </body></html>
                """,
        };

        message.Body = new Multipart("related") { html, logo };
        return message;
    }

    /// <summary>
    /// The pattern the trust bar exists for: a display name claiming one domain, sent from
    /// another, and failing the claimed domain's own policy.
    /// </summary>
    private static MimeMessage Phishing()
    {
        var message = Envelope("billing@yourbank.example", "no-reply@delivery.invalid",
            "Action required: confirm your details");

        message.Headers.Add("Authentication-Results",
            "mx.example.com; dkim=fail; spf=fail smtp.mailfrom=delivery.invalid; dmarc=fail");

        message.Body = new TextPart("plain")
        {
            Text = "Your account will be suspended unless you confirm your details today.\n\n"
                   + "https://yourbank.example.confirm-now.invalid/login",
        };

        return message;
    }

    private static MimeMessage Envelope(string name, string address, string subject)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress(name, address));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.MessageId = MimeUtils.GenerateMessageId("example.com");
        return message;
    }

    /// <summary>
    /// Mail waiting to go out, including one the server refused for good. The Outbox view is
    /// the only place a permanent failure is visible, so it cannot be looked at without one.
    /// </summary>
    private static void SeedOutbox(OpenAccount account)
    {
        var sender = new Mailbox.Protocols.SmtpSender(account.Mail);
        var now = DateTimeOffset.UtcNow;

        var waiting = Envelope("You", "you@example.com", "Re: Thursday");
        waiting.To.Clear();
        waiting.To.Add(new MailboxAddress("Alice Chen", "alice@example.com"));
        waiting.Body = new TextPart("plain") { Text = "Works for me." };
        sender.Queue(account.Account.Id, waiting, now);

        var refused = Envelope("You", "you@example.com", "Expenses, March");
        refused.To.Clear();
        refused.To.Add(new MailboxAddress("Accounts", "accounts@example.invalid"));
        refused.Body = new TextPart("plain") { Text = "Attached." };

        var id = sender.Queue(account.Account.Id, refused, now.AddMinutes(-90));
        account.Mail.FailOutbox(id, "The recipient's address was rejected: no such mailbox.");
    }

    // ---- Writing them out ---------------------------------------------------------------------

    private static void Seed(AccountStores stores, string address, params MimeMessage[] messages)
    {
        var account = stores.Add(address, address, MailProtocol.Pop3);
        SeedOutbox(account);
        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox)!;
        var when = DateTimeOffset.UtcNow;

        // Everyone the seeded mail is from has been written to, so the To line has names to
        // offer — the Auto-Complete List is fed by sending, and a seed has sent nothing.
        account.Mail.RecordRecipients(
            messages.SelectMany(m => m.From.Mailboxes).Select(m => (m.Address, (string?)m.Name)),
            when);

        foreach (var message in messages)
        {
            message.Date = when;

            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();

            var summary = Mailbox.Protocols.MessageMapper.ToSummary(
                message, Guid.NewGuid().ToString("n"), raw.Length, when);

            var id = account.Mail.AddMessage(inbox.Id, summary, raw);

            // A flag with a date on two of them, so the to-do list has the mail half of what the
            // reference's own holds: one due today and one already late, which is the pair that
            // shows the red as well as the band.
            if (id is { } written && Flagged.TryGetValue(message.Subject ?? string.Empty, out var due))
            {
                account.Mail.SetFollowUp([written], SeedToday().AddDays(due).ToDateTime(TimeOnly.MinValue));
            }

            when = when.AddMinutes(-37);
        }
    }

    /// <summary>Which seeded subjects carry a follow-up flag, and how many days off it is due.</summary>
    private static readonly Dictionary<string, int> Flagged = new(StringComparer.Ordinal)
    {
        ["Draft agenda attached"] = 0,
        ["Re: Q3 numbers"] = -3,
    };
}
