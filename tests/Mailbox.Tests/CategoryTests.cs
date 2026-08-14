using Mailbox.Store;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// Categories are named colours, and the name is the point: a category storing #FF0000 would be
/// invisible on the Black theme with nothing to be done about it. So the store keeps a token and
/// every theme has to define one for each.
/// </summary>
public class CategoryTests
{
    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static long Add(MailRepository repo, Folder inbox, string uid)
        => repo.AddMessage(inbox.Id, new MessageSummary(
            0, 0, uid, null, "Alice", "alice@example.com", "Subject", "Preview",
            null, DateTimeOffset.UnixEpoch, 100, false, false, false))!.Value;

    [Fact]
    public void TheSixCategoriesArriveWithTheStore()
    {
        var (store, repo, _) = Fresh();
        using var _s = store;

        var categories = repo.Categories();

        Assert.Equal(6, categories.Count);
        Assert.Equal("Red Category", categories[0].Name);
        Assert.All(categories, c => Assert.StartsWith("category.", c.ColourToken));
    }

    /// <summary>A colour value in the store could not be corrected by a theme.</summary>
    [Fact]
    public void CategoriesStoreATokenNameRatherThanAColour()
    {
        var (store, repo, _) = Fresh();
        using var _s = store;

        Assert.All(repo.Categories(), c => Assert.DoesNotContain("#", c.ColourToken));
    }

    /// <summary>Every theme has to define all six, or a category is invisible in one of them.</summary>
    [Fact]
    public void EveryThemeDefinesEveryCategoryColour()
    {
        foreach (var id in OfficeThemes.All)
        {
            var tokens = OfficeThemes.Build(id).Resolve();

            foreach (var token in TokenKeys.Category.All)
            {
                Assert.True(tokens.TryGetString(token, out var value),
                    $"{id} does not define {token}.");
                Assert.StartsWith("#", value);
            }
        }
    }

    [Fact]
    public void AMessageCanCarrySeveral()
    {
        var (store, repo, inbox) = Fresh();
        using var _s = store;
        var message = Add(repo, inbox, "uid-1");
        var categories = repo.Categories();

        repo.Assign([message], categories[0].Id);
        repo.Assign([message], categories[4].Id);

        var found = repo.CategoriesFor([message])[message];

        Assert.Equal(2, found.Count);
        Assert.Equal(["Red Category", "Blue Category"], found.Select(c => c.Name));
    }

    [Fact]
    public void AssigningTwiceDoesNotDuplicate()
    {
        var (store, repo, inbox) = Fresh();
        using var _s = store;
        var message = Add(repo, inbox, "uid-1");
        var red = repo.Categories()[0].Id;

        repo.Assign([message], red);
        repo.Assign([message], red);

        Assert.Single(repo.CategoriesFor([message])[message]);
    }

    [Fact]
    public void CategoriesComeBackForManyMessagesInOneQuery()
    {
        var (store, repo, inbox) = Fresh();
        using var _s = store;
        var first = Add(repo, inbox, "uid-1");
        var second = Add(repo, inbox, "uid-2");
        var third = Add(repo, inbox, "uid-3");
        var red = repo.Categories()[0].Id;

        repo.Assign([first, third], red);

        var found = repo.CategoriesFor([first, second, third]);

        Assert.Equal(2, found.Count);
        Assert.False(found.ContainsKey(second));
    }

    [Fact]
    public void UnassigningRemovesOnlyThatCategory()
    {
        var (store, repo, inbox) = Fresh();
        using var _s = store;
        var message = Add(repo, inbox, "uid-1");
        var categories = repo.Categories();
        repo.Assign([message], categories[0].Id);
        repo.Assign([message], categories[3].Id);

        repo.Unassign([message], categories[0].Id);

        Assert.Equal(["Green Category"],
            repo.CategoriesFor([message])[message].Select(c => c.Name));
    }

    /// <summary>Deleting a message must not leave its category assignments behind.</summary>
    [Fact]
    public void DeletingAMessageTakesItsAssignments()
    {
        var (store, repo, inbox) = Fresh();
        using var _s = store;
        var message = Add(repo, inbox, "uid-1");
        repo.Assign([message], repo.Categories()[0].Id);

        repo.DeleteMessages([message]);

        Assert.Equal(0, store.ScalarLong("SELECT count(*) FROM message_categories"));
        Assert.Empty(store.CheckIntegrity());
    }
}
