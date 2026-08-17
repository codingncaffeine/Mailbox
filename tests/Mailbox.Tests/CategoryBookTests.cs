using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// One set of colour categories across the modules: where it lives, what keeps the mail accounts
/// in step with it, and what a rename or a delete leaves behind for the caller to write.
/// </summary>
public class CategoryBookTests
{
    private static (PimStore Pim, PimRepository Repository, MailStore Mail, MailRepository Account, CategoryBook Book) Fresh(bool withAccount = true)
    {
        var pim = PimStore.Transient();
        var repository = new PimRepository(pim);
        var mail = MailStore.Transient();
        var account = new MailRepository(mail);
        if (withAccount) account.AddAccount("you@example.com", "You", MailProtocol.Pop3);

        var book = new CategoryBook(repository, () => withAccount ? [account] : []);
        return (pim, repository, mail, account, book);
    }

    private static PimItem AddNote(PimRepository repository, long collectionId, string title, params string[] categories)
        => repository.AddItem(new PimItem
        {
            CollectionId = collectionId,
            Uid = title,
            Kind = CollectionKind.Journal,
            RawPayload = $"BEGIN:VJOURNAL\r\nUID:{title}\r\nSUMMARY:{title}\r\nEND:VJOURNAL",
            Summary = title,
            Categories = string.Join(",", categories),
            LastModified = DateTimeOffset.UtcNow,
        });

    [Fact]
    public void AnEmptySetTakesTheSixTheReferenceShips()
    {
        var (pim, _, mail, _, book) = Fresh(withAccount: false);
        using var _p = pim;
        using var _m = mail;

        book.EnsureDefaults();

        Assert.Equal(6, book.All().Count);
        Assert.All(book.All(), c => Assert.StartsWith("category.", c.ColourToken));
        Assert.Contains(book.All(), c => c.Name == "Blue Category");
    }

    [Fact]
    public void AnAccountThatAlreadyHadCategoriesHasThemAdopted()
    {
        var (pim, _, mail, account, book) = Fresh();
        using var _p = pim;
        using var _m = mail;

        account.AddCategory("Invoices", "category.orange");
        book.EnsureDefaults();

        // The account's own six arrive with it, and so does the one somebody made: adoption
        // rather than replacement is what keeps mail that was coloured coloured.
        Assert.Contains(book.All(), c => c.Name == "Invoices");
        Assert.Contains(book.All(), c => c.Name == "Red Category");
    }

    [Fact]
    public void AddingToTheSetPutsItInEveryAccount()
    {
        var (pim, _, mail, account, book) = Fresh();
        using var _p = pim;
        using var _m = mail;

        book.EnsureDefaults();
        book.Add("Receipts", "category.green");

        Assert.Contains(account.Categories(), c => c.Name == "Receipts" && c.ColourToken == "category.green");
    }

    [Fact]
    public void RecolouringAndShortcuttingReachTheMirror()
    {
        var (pim, _, mail, account, book) = Fresh();
        using var _p = pim;
        using var _m = mail;

        book.EnsureDefaults();
        var made = book.Add("Receipts", "category.green");

        book.Recolour(made.Id, "category.purple");
        book.SetShortcut(made.Id, "Ctrl+F4");

        var mirrored = Assert.Single(account.Categories(), c => c.Name == "Receipts");
        Assert.Equal("category.purple", mirrored.ColourToken);
        Assert.Equal("Ctrl+F4", mirrored.Shortcut);
    }

    [Fact]
    public void RenamingKeepsTheMessagesThatWereCategorised()
    {
        var (pim, _, mail, account, book) = Fresh();
        using var _p = pim;
        using var _m = mail;

        book.EnsureDefaults();
        var made = book.Add("Receipts", "category.green");
        var mirroredId = account.Categories().First(c => c.Name == "Receipts").Id;

        book.Rename(made.Id, "Expenses");

        // The mirror row is renamed rather than replaced, so every message pointing at it still
        // does — a delete-and-add would have lost the assignments.
        var mirrored = Assert.Single(account.Categories(), c => c.Name == "Expenses");
        Assert.Equal(mirroredId, mirrored.Id);
        Assert.DoesNotContain(account.Categories(), c => c.Name == "Receipts");
    }

    [Fact]
    public void RenamingHandsBackTheItemsThatCarriedTheName()
    {
        var (pim, repository, mail, _, book) = Fresh();
        using var _p = pim;
        using var _m = mail;

        var folder = repository.AddCollection(CollectionKind.Journal, "Notes").Id;
        AddNote(repository, folder, "Shopping", "Green Category");
        AddNote(repository, folder, "Ideas", "Blue Category");
        book.EnsureDefaults();

        var green = book.All().First(c => c.Name == "Green Category");
        var carried = book.Rename(green.Id, "Errands");

        // The item's own text is where its categories live, so the book can only say which items
        // need writing again — the shell does the writing, through the codecs.
        Assert.Equal(["Shopping"], carried.Select(i => i.Summary));
        Assert.Equal("Errands", book.All().First(c => c.Id == green.Id).Name);
    }

    [Fact]
    public void DeletingTakesTheMirrorAndHandsBackTheItems()
    {
        var (pim, repository, mail, account, book) = Fresh();
        using var _p = pim;
        using var _m = mail;

        var folder = repository.AddCollection(CollectionKind.Journal, "Notes").Id;
        AddNote(repository, folder, "Shopping", "Green Category");
        book.EnsureDefaults();

        var green = book.All().First(c => c.Name == "Green Category");
        var carried = book.Delete(green.Id);

        Assert.Equal(["Shopping"], carried.Select(i => i.Summary));
        Assert.DoesNotContain(book.All(), c => c.Name == "Green Category");
        Assert.DoesNotContain(account.Categories(), c => c.Name == "Green Category");
    }

    [Fact]
    public void OnlyTheItemsWithThatCategoryComeBack()
    {
        var (pim, repository, mail, _, book) = Fresh();
        using var _p = pim;
        using var _m = mail;

        var folder = repository.AddCollection(CollectionKind.Journal, "Notes").Id;
        AddNote(repository, folder, "One", "Blue Category");
        AddNote(repository, folder, "Two", "Blue Category", "Green Category");
        AddNote(repository, folder, "Three", "Blueprints");
        AddNote(repository, folder, "Four");

        // Bounded by the commas: "Blue Category" must not find "Blueprints", and a name in the
        // middle of a list must still be found.
        Assert.Equal(["One", "Two"], repository.ItemsWithCategory("Blue Category").Select(i => i.Summary));
        Assert.Equal(["Two"], repository.ItemsWithCategory("Green Category").Select(i => i.Summary));
        Assert.Empty(repository.ItemsWithCategory("Blue"));
    }

    [Fact]
    public void ANameIsUniqueWhateverItsCase()
    {
        var (pim, repository, mail, _, book) = Fresh();
        using var _p = pim;
        using var _m = mail;

        var first = book.Add("Receipts", "category.green");
        var again = book.Add("receipts", "category.red");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("category.green", again.ColourToken);
        Assert.Single(repository.Categories());
    }

    // ---- What an item should carry afterwards ---------------------------------------------------

    [Fact]
    public void ARenameSwapsTheNameAndADeleteDropsIt()
    {
        Assert.Equal(["Home", "Work"], CategoryBook.Rewrite(["Errands", "Work"], "Errands", "Home"));
        Assert.Equal(["Work"], CategoryBook.Rewrite(["Errands", "Work"], "Errands", null));
        Assert.Equal(["Work"], CategoryBook.Rewrite(["Work"], "Errands", "Home"));
    }

    [Fact]
    public void ARenameOntoSomethingTheItemAlreadyCarriesLeavesOneOfIt()
        => Assert.Equal(["Home"], CategoryBook.Rewrite(["Home", "Errands"], "Errands", "Home"));

    [Fact]
    public void ARenameMatchesWhateverTheCase()
        => Assert.Equal(["Home"], CategoryBook.Rewrite(["ERRANDS"], "errands", "Home"));
}
