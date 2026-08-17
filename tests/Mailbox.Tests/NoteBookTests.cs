using Mailbox.Scheduling;
using Mailbox.Store.Pim;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// The wall of notes as the module draws it: which rows are notes at all, what order they come
/// in, and what a square says on its face.
/// </summary>
public class NoteBookTests
{
    private static readonly DateOnly Today = new(2026, 8, 16);

    private static (PimStore Store, PimRepository Repository, NoteBook Book, Collection Folder) Fresh()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var folder = repository.AddCollection(CollectionKind.Journal, "Notes", "#F2C811", "you@example.net");
        return (store, repository, new NoteBook(repository), folder);
    }

    private static PimItem AddNote(PimRepository repository, long folderId, string body, DateOnly on, params string[] categories)
        => repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry
            {
                Uid = body,
                When = EventTime.At(on.ToDateTime(new TimeOnly(9, 0)), "Europe/London"),
                Categories = categories,
            }.WithBody(body),
            folderId));

    private static void AddEntry(PimRepository repository, long folderId, string subject, DateOnly on)
        => repository.AddItem(PimJournalCodec.ToItem(
            new JournalEntry
            {
                Uid = subject,
                Summary = subject,
                EntryType = "Phone call",
                When = EventTime.At(on.ToDateTime(new TimeOnly(9, 0)), "Europe/London"),
            },
            folderId));

    [Fact]
    public void TheWallHoldsNotesAndNotJournalEntries()
    {
        var (store, repository, book, folder) = Fresh();
        using var _ = store;

        AddNote(repository, folder.Id, "Milk and bread", Today);
        AddEntry(repository, folder.Id, "Rang A. Person", Today);

        // One kind of collection holds both, so what separates them is what each says it is.
        Assert.Equal(["Milk and bread"], book.Rows(NoteArrangement.Icons, Today).Select(r => r.Title));
    }

    [Fact]
    public void TheNewestNoteIsFirst()
    {
        var (store, repository, book, folder) = Fresh();
        using var _ = store;

        AddNote(repository, folder.Id, "Oldest", Today.AddDays(-3));
        AddNote(repository, folder.Id, "Newest", Today);
        AddNote(repository, folder.Id, "Middle", Today.AddDays(-1));

        Assert.Equal(["Newest", "Middle", "Oldest"], book.Rows(NoteArrangement.Icons, Today).Select(r => r.Title));
    }

    [Fact]
    public void LastSevenDaysDropsWhatIsOlder()
    {
        var (store, repository, book, folder) = Fresh();
        using var _ = store;

        AddNote(repository, folder.Id, "This week", Today.AddDays(-2));
        AddNote(repository, folder.Id, "Last month", Today.AddDays(-30));

        Assert.Equal(2, book.Rows(NoteArrangement.Icons, Today).Count);
        Assert.Equal(["This week"], book.Rows(NoteArrangement.LastSevenDays, Today).Select(r => r.Title));
    }

    [Fact]
    public void ANotesTitleIsItsFirstLineAndItsPreviewIsTheRest()
    {
        var (store, repository, book, folder) = Fresh();
        using var _ = store;
        AddNote(repository, folder.Id, "Shopping\nmilk, bread\nand coffee", Today);

        var row = Assert.Single(book.Rows(NoteArrangement.Icons, Today));
        Assert.Equal("Shopping", row.Title);
        Assert.Equal("milk, bread and coffee", row.Preview);
    }

    [Fact]
    public void ANoteWithNothingInItStillHasSomethingToShow()
    {
        var (store, repository, book, folder) = Fresh();
        using var _ = store;
        AddNote(repository, folder.Id, string.Empty, Today);

        var row = Assert.Single(book.Rows(NoteArrangement.Icons, Today));
        Assert.Equal(JournalEntry.Untitled, row.Title);
        Assert.Empty(row.Preview);
    }

    [Fact]
    public void ANoteMadeTodayIsWrittenAsATimeAndAnOlderOneAsADate()
    {
        var (store, repository, book, folder) = Fresh();
        using var _ = store;
        AddNote(repository, folder.Id, "Today", Today);
        AddNote(repository, folder.Id, "Before", Today.AddDays(-1));

        var rows = book.Rows(NoteArrangement.Icons, Today);
        Assert.DoesNotContain("2026", rows[0].MadeText(Today, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("2026", rows[1].MadeText(Today, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AHiddenFolderIsNotOnTheWall()
    {
        var (store, repository, book, folder) = Fresh();
        using var _ = store;
        AddNote(repository, folder.Id, "Out of sight", Today);

        repository.SetCollectionVisible(folder.Id, false);
        Assert.Empty(book.Rows(NoteArrangement.Icons, Today));
        Assert.Single(book.Rows(NoteArrangement.Icons, Today, collectionIds: [folder.Id]));
    }

    [Fact]
    public void OpeningANoteParsesTheWholeOfIt()
    {
        var (store, repository, book, folder) = Fresh();
        using var _ = store;
        var row = AddNote(repository, folder.Id, "Kept\nevery word of it.", Today);

        Assert.Equal("Kept\nevery word of it.", book.Open(row.Id)!.Description);
        Assert.Null(book.Open(row.Id + 100));
    }

    // ---- What colours one ----------------------------------------------------------------------

    [Theory]
    [InlineData("Blue Category", "category.blue")]
    [InlineData("red category", "category.red")]
    [InlineData("Purple", "category.purple")]
    [InlineData("Invoices", null)]
    [InlineData("", null)]
    public void ACategoryNamesAColourOrItDoesNot(string category, string? token)
        => Assert.Equal(token, CategoryTokens.For(category));

    [Fact]
    public void TheFirstCategoryThatNamesAColourIsTheOneUsed()
    {
        Assert.Equal("category.green", CategoryTokens.First(["Invoices", "Green Category", "Blue Category"]));
        Assert.Null(CategoryTokens.First(["Invoices", "Receipts"]));
        Assert.Null(CategoryTokens.First([]));
    }
}
