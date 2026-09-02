using Mailbox.Contacts;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// A journal entry joined to a person, and the person's side of that join.
/// </summary>
/// <remarks>
/// A journal is kept to answer "what have I had to do with this person", and until this the
/// entries recorded who they were about in free text that nothing resolved and nothing could
/// search. Two halves are asked for here: that a link survives being written out and read back —
/// through the entry's own text as well as through the column that mirrors it — and that asking
/// from the contact's end finds both the entries that carry its UID and the entries that only
/// spell its name.
/// </remarks>
public class JournalLinkTests
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
        IReadOnlyList<string> contacts,
        IReadOnlyList<string> links)
        => repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry
            {
                Uid = subject,
                Summary = subject,
                EntryType = "Phone call",
                When = EventTime.At(Today.ToDateTime(new TimeOnly(10, 0)), "Europe/London"),
                Contacts = contacts,
                Links = links,
            },
            journalId));

    [Fact]
    public void ALinkSurvivesTheEntrysOwnText()
    {
        var entry = new JournalEntry
        {
            Uid = "one",
            Summary = "Rang A. Person",
            EntryType = "Phone call",
            Contacts = ["A. Person"],
            Links = ["a.person@example.com"],
        };

        var text = JournalCodec.Serialize(entry);

        // The name is what a server and every other client is told; the link travels beside it as
        // the property the address book already uses for one card linked to another.
        Assert.Contains("CONTACT:A. Person", text, StringComparison.Ordinal);
        Assert.Contains("X-MAILBOX-LINK:a.person@example.com", text, StringComparison.Ordinal);

        var back = JournalCodec.Parse(text).Single();
        Assert.Equal(["A. Person"], back.Contacts);
        Assert.Equal(["a.person@example.com"], back.Links);
    }

    [Fact]
    public void ALinkIsMirroredIntoTheColumnThatIsSearched()
    {
        var (store, repository, _, journal) = Fresh();
        using var _s = store;

        var item = Add(repository, journal.Id, "Rang A. Person", ["A. Person"], ["a.person@example.com"]);

        // The column, so a lookup from a contact reads it without parsing every entry.
        Assert.Equal(["a.person@example.com"], repository.Item(item.Id)!.Links);

        // And back off the columns alone, which is the path a row takes when its text will not parse.
        Assert.Equal(["a.person@example.com"], PimJournalCodec.FromColumns(repository.Item(item.Id)!).Links);
    }

    [Fact]
    public void TheLinkedEntriesAreFound()
    {
        var (store, repository, book, journal) = Fresh();
        using var _s = store;

        Add(repository, journal.Id, "Rang A. Person", ["A. Person"], ["a.person@example.com"]);
        Add(repository, journal.Id, "Rang somebody else", ["B. Other"], ["b.other@example.com"]);

        Assert.Equal(
            ["Rang A. Person"],
            book.About("a.person@example.com").Select(r => r.Subject));
    }

    /// <summary>
    /// An entry typed by hand, or written by another client, is still about them — and a page
    /// that listed only the linked ones would look empty and be wrong.
    /// </summary>
    [Fact]
    public void AnEntryThatOnlySpellsTheNameIsFoundToo()
    {
        var (store, repository, book, journal) = Fresh();
        using var _s = store;

        Add(repository, journal.Id, "Rang A. Person", ["A. Person"], []);

        Assert.Empty(book.About("a.person@example.com"));
        Assert.Equal(
            ["Rang A. Person"],
            book.About("a.person@example.com", ["A. Person"]).Select(r => r.Subject));
    }

    /// <summary>
    /// The link is what tells a renamed card from a stale name: the entry still answers for the
    /// person even when the name on it has stopped matching anything.
    /// </summary>
    [Fact]
    public void ARenamedCardKeepsItsEntries()
    {
        var (store, repository, book, journal) = Fresh();
        using var _s = store;

        Add(repository, journal.Id, "Rang them", ["A. Person"], ["a.person@example.com"]);

        Assert.Equal(
            ["Rang them"],
            book.About("a.person@example.com", ["A. Person-Smith"]).Select(r => r.Subject));
    }

    [Fact]
    public void ANoteIsNotAnActivity()
    {
        var (store, repository, book, journal) = Fresh();
        using var _s = store;

        repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry
            {
                Uid = "note",
                When = EventTime.At(Today.ToDateTime(new TimeOnly(9, 0)), "Europe/London"),
                Contacts = ["A. Person"],
                Links = ["a.person@example.com"],
            }.WithBody("Ring them back"),
            journal.Id));

        Assert.Empty(book.About("a.person@example.com", ["A. Person"]));
    }

    [Fact]
    public void ADeletedEntryIsNotAnActivity()
    {
        var (store, repository, book, journal) = Fresh();
        using var _s = store;

        var item = Add(repository, journal.Id, "Rang A. Person", ["A. Person"], ["a.person@example.com"]);
        repository.UpdateItem(item with { SyncState = PimSyncState.Deleted });

        Assert.Empty(book.About("a.person@example.com", ["A. Person"]));
    }

    /// <summary>Nothing to go on is nothing found, rather than everything found.</summary>
    [Fact]
    public void ACardWithNoUidAndNoNameMatchesNothing()
    {
        var (store, repository, book, journal) = Fresh();
        using var _s = store;

        Add(repository, journal.Id, "Rang A. Person", ["A. Person"], ["a.person@example.com"]);

        Assert.Empty(book.About(string.Empty));
        Assert.Empty(book.About(null, ["  "]));
    }

    /// <summary>An entry naming somebody twice over is still one entry on the page.</summary>
    [Fact]
    public void AnEntryLinkedAndNamedIsListedOnce()
    {
        var (store, repository, book, journal) = Fresh();
        using var _s = store;

        Add(repository, journal.Id, "Rang A. Person", ["A. Person"], ["a.person@example.com"]);

        Assert.Single(book.About("a.person@example.com", ["A. Person"]));
    }

    // ---- The other end: turning a name into a card ------------------------------------------

    private static (PimStore Store, ContactBook Book, Collection Contacts) FreshBook()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var contacts = repository.AddCollection(CollectionKind.Contacts, "Contacts");
        return (store, new ContactBook(repository), contacts);
    }

    [Fact]
    public void ANameResolvesToTheOneCardThatCarriesIt()
    {
        var (store, book, contacts) = FreshBook();
        using var _s = store;

        book.Save(new Contact { Uid = "a.person@example.com", FirstName = "A.", LastName = "Person" }, contacts.Id);

        Assert.Equal("a.person@example.com", book.NamedExactly("A. Person")?.Contact.Uid);
        Assert.Equal("a.person@example.com", book.NamedExactly("  a. person  ")?.Contact.Uid);
        Assert.Null(book.NamedExactly("Somebody Else"));
        Assert.Null(book.NamedExactly(string.Empty));
    }

    /// <summary>
    /// Two people called the same thing is the case a link exists to tell apart, so guessing
    /// between them would file one person's calls under the other silently.
    /// </summary>
    [Fact]
    public void AnAmbiguousNameResolvesToNobody()
    {
        var (store, book, contacts) = FreshBook();
        using var _s = store;

        book.Save(new Contact { Uid = "one@example.com", FirstName = "A.", LastName = "Person" }, contacts.Id);
        book.Save(new Contact { Uid = "two@example.net", FirstName = "A.", LastName = "Person" }, contacts.Id);

        Assert.Null(book.NamedExactly("A. Person"));
    }
}
