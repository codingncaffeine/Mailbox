using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The journal as the module draws it: which rows are entries at all, what order they come in,
/// and how the Entry List groups them.
/// </summary>
public class JournalBookTests
{
    private static readonly DateOnly Today = new(2026, 8, 16);

    private static (PimStore Store, PimRepository Repository, JournalBook Book, Collection Journal) Fresh()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var journal = repository.AddCollection(CollectionKind.Journal, "Journal", "#8764B8", "you@example.net");
        return (store, repository, new JournalBook(repository), journal);
    }

    private static PimItem Add(
        PimRepository repository,
        long journalId,
        string subject,
        string type,
        DateOnly on,
        TimeSpan? took = null,
        string contact = "")
        => repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry
            {
                Uid = subject,
                Summary = subject,
                EntryType = type,
                When = EventTime.At(on.ToDateTime(new TimeOnly(10, 0)), "Europe/London"),
                Duration = took,
                Contacts = contact.Length > 0 ? [contact] : [],
            },
            journalId));

    private static void AddNote(PimRepository repository, long journalId, string body)
        => repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry { Uid = body, When = EventTime.At(Today.ToDateTime(new TimeOnly(9, 0)), "Europe/London") }.WithBody(body),
            journalId));

    [Fact]
    public void TheJournalHoldsEntriesAndNotNotes()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;

        Add(repository, journal.Id, "Rang A. Person", "Phone call", Today);
        AddNote(repository, journal.Id, "Milk and bread");

        Assert.Equal(["Rang A. Person"], book.Rows(JournalArrangement.EntryList, Today).Select(r => r.Subject));
    }

    [Fact]
    public void TheNewestEntryIsFirst()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;

        Add(repository, journal.Id, "Older", "Meeting", Today.AddDays(-2));
        Add(repository, journal.Id, "Newer", "Meeting", Today);

        Assert.Equal(["Newer", "Older"], book.Rows(JournalArrangement.EntryList, Today).Select(r => r.Subject));
    }

    [Fact]
    public void PhoneCallsKeepsTheCallsAndLastSevenDaysKeepsTheWeek()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;

        Add(repository, journal.Id, "A call", "Phone call", Today);
        Add(repository, journal.Id, "A meeting", "Meeting", Today);
        Add(repository, journal.Id, "An old call", "Phone call", Today.AddDays(-20));

        Assert.Equal(3, book.Rows(JournalArrangement.EntryList, Today).Count);
        Assert.Equal(["A call", "An old call"], book.Rows(JournalArrangement.PhoneCalls, Today).Select(r => r.Subject));
        Assert.Equal(["A call", "A meeting"], book.Rows(JournalArrangement.LastSevenDays, Today).Select(r => r.Subject));
    }

    [Fact]
    public void TheEntryListGroupsByTypeInTheReferencesOwnOrder()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;

        Add(repository, journal.Id, "Wrote it up", "Document", Today);
        Add(repository, journal.Id, "Rang them back", "Phone call", Today);
        Add(repository, journal.Id, "Sat through it", "Meeting", Today);
        Add(repository, journal.Id, "Something else", "Bicycle maintenance", Today);

        var groups = JournalBook.ByType(book.Rows(JournalArrangement.EntryList, Today));

        // The reference's own list first, in its order; anything else after it.
        Assert.Equal(["Phone call", "Meeting", "Document", "Bicycle maintenance"], groups.Select(g => g.Type));
        Assert.All(groups, g => Assert.Single(g.Rows));
    }

    [Fact]
    public void TheTimelinesBandByWhatTheirNamesSay()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;

        Add(repository, journal.Id, "Rang A", "Phone call", Today, contact: "A. Person");
        Add(repository, journal.Id, "Rang B", "Phone call", Today, contact: "B. Person");
        Add(repository, journal.Id, "Wrote it up", "Document", Today);

        var rows = book.Rows(JournalArrangement.ByType, Today);

        var byType = JournalBook.Grouped(rows, JournalArrangement.ByType);
        Assert.Equal(["Entry Type: Phone call", "Entry Type: Document"], byType.Select(g => g.Label));

        // One band per contact, and an entry that names nobody goes under "(none)" first.
        var byContact = JournalBook.Grouped(rows, JournalArrangement.ByContact);
        Assert.Equal(["Contact: (none)", "Contact: A. Person", "Contact: B. Person"], byContact.Select(g => g.Label));
        Assert.Equal(["Wrote it up"], byContact[0].Rows.Select(r => r.Subject));
    }

    [Fact]
    public void AnEntryWithTwoContactsHangsInBothBands()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;

        repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry
            {
                Uid = "both",
                Summary = "Conference call",
                EntryType = "Phone call",
                When = EventTime.At(Today.ToDateTime(new TimeOnly(10, 0)), "Europe/London"),
                Contacts = ["A. Person", "B. Person"],
            },
            journal.Id));

        var byContact = JournalBook.Grouped(book.Rows(JournalArrangement.ByContact, Today), JournalArrangement.ByContact);
        Assert.Equal(2, byContact.Count);
        Assert.All(byContact, g => Assert.Equal(["Conference call"], g.Rows.Select(r => r.Subject)));
    }

    [Fact]
    public void TheEntryListGroupsByCompany()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;

        repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry
            {
                Uid = "c1",
                Summary = "Rang the plumber",
                EntryType = "Phone call",
                Company = "Pipes Ltd",
                When = EventTime.At(Today.ToDateTime(new TimeOnly(10, 0)), "Europe/London"),
            },
            journal.Id));
        Add(repository, journal.Id, "No company", "Meeting", Today);

        var groups = JournalBook.Grouped(book.Rows(JournalArrangement.EntryList, Today), JournalArrangement.EntryList);
        Assert.Equal(["Company: (none)", "Company: Pipes Ltd"], groups.Select(g => g.Label));
    }

    [Fact]
    public void EveryEntryTypeHasAGlyphTheMapKnows()
    {
        foreach (var type in JournalBook.Types)
        {
            var name = JournalBook.IconName(type);
            Assert.True(Mailbox.Theming.Icons.IconGlyphs.Has(name),
                $"the “{type}” entry type asks for the '{name}' icon, which is not in the glyph map.");
        }

        // A type another client invented falls back to the module's own mark, and that mark exists.
        Assert.True(Mailbox.Theming.Icons.IconGlyphs.Has(JournalBook.IconName("Interpretive dance")));
    }

    [Fact]
    public void AnEntryWritesHowLongItTookAndWhoItWasWith()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;
        Add(repository, journal.Id, "Rang A. Person", "Phone call", Today, TimeSpan.FromMinutes(45), "A. Person");

        var row = Assert.Single(book.Rows(JournalArrangement.EntryList, Today));
        Assert.Equal("45 minutes", row.DurationText(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("A. Person", row.Contacts);
    }

    [Fact]
    public void AnEntryThatSaysNothingAboutHowLongWritesNothing()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;
        Add(repository, journal.Id, "Sent it round", "E-mail Message", Today);

        var row = Assert.Single(book.Rows(JournalArrangement.EntryList, Today));
        Assert.Empty(row.DurationText(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(row.Contacts);
    }

    [Fact]
    public void OpeningAnEntryParsesTheWholeOfIt()
    {
        var (store, repository, book, journal) = Fresh();
        using var _ = store;
        var row = repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry
            {
                Uid = "u@mailbox",
                Summary = "Read it back",
                Description = "Every word of it.",
                EntryType = "Meeting",
                When = EventTime.At(Today.ToDateTime(new TimeOnly(10, 0)), "Europe/London"),
            },
            journal.Id));

        Assert.Equal("Every word of it.", book.Open(row.Id)!.Description);
        Assert.Null(book.Open(row.Id + 100));
    }
}
