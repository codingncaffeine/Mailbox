using Mailbox.Core.Settings;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// A file per account is only worth the duplication if the files really are independent: one
/// can be deleted without touching another, an unreadable one does not stop the rest opening,
/// and a file carries enough to say whose it is after being moved.
/// </summary>
public class AccountStoresTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-accounts-tests", Guid.NewGuid().ToString("n"));

    private string Accounts => Path.Combine(_root, "accounts");

    private SettingsAccountOrder Order() =>
        new(new SettingsStore(Path.Combine(_root, "settings.json")));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EachAccountGetsItsOwnFileNamedAfterIt()
    {
        using var stores = new AccountStores(Accounts, Order());

        stores.Add("one@example.com", "One", MailProtocol.Pop3);
        stores.Add("two@example.net", "Two", MailProtocol.Imap);

        Assert.True(File.Exists(Path.Combine(Accounts, "one@example.com.db")));
        Assert.True(File.Exists(Path.Combine(Accounts, "two@example.net.db")));
        Assert.Equal(2, stores.All.Count);
    }

    /// <summary>The file names are the label someone looks for when backing one up.</summary>
    [Theory]
    [InlineData("you@example.com", "you@example.com.db")]
    [InlineData("first.last@example.co.uk", "first.last@example.co.uk.db")]
    [InlineData("odd/name@example.com", "odd_name@example.com.db")]
    [InlineData("", "account.db")]
    public void FileNamesStayReadable(string address, string expected)
        => Assert.Equal(expected, AccountStores.FileNameFor(address));

    [Fact]
    public void MailFiledUnderOneAccountIsNotVisibleFromAnother()
    {
        using var stores = new AccountStores(Accounts, Order());
        var one = stores.Add("one@example.com", "One", MailProtocol.Pop3);
        var two = stores.Add("two@example.net", "Two", MailProtocol.Pop3);

        var inbox = one.Mail.FolderWithRole(one.Account.Id, FolderRole.Inbox)!;
        one.Mail.AddMessage(inbox.Id, new MessageSummary(
            0, 0, "uid-1", null, "Alice", "alice@example.com", "Only in one", "",
            null, DateTimeOffset.UnixEpoch, 10, false, false, false));

        var otherInbox = two.Mail.FolderWithRole(two.Account.Id, FolderRole.Inbox)!;

        Assert.Single(one.Mail.Messages(inbox.Id));
        Assert.Empty(two.Mail.Messages(otherInbox.Id));
    }

    [Fact]
    public void AccountsComeBackWhenTheDirectoryIsReopened()
    {
        using (var first = new AccountStores(Accounts, Order()))
        {
            first.Add("one@example.com", "One", MailProtocol.Pop3);
            first.Add("two@example.net", "Two", MailProtocol.Pop3);
        }

        using var second = new AccountStores(Accounts, Order());

        Assert.Equal(["one@example.com", "two@example.net"],
            second.All.Select(a => a.Account.Address).OrderBy(a => a));
    }

    /// <summary>Removing an account takes its file, and nothing else.</summary>
    [Fact]
    public void RemovingAnAccountDeletesOnlyItsFile()
    {
        using var stores = new AccountStores(Accounts, Order());
        stores.Add("one@example.com", "One", MailProtocol.Pop3);
        stores.Add("two@example.net", "Two", MailProtocol.Pop3);

        stores.Remove("one@example.com");

        Assert.False(File.Exists(Path.Combine(Accounts, "one@example.com.db")));
        Assert.True(File.Exists(Path.Combine(Accounts, "two@example.net.db")));
        Assert.Single(stores.All);
    }

    /// <summary>
    /// The reason for the whole arrangement: a file that has gone bad costs one account, not
    /// every account.
    /// </summary>
    [Fact]
    public void AnUnreadableFileDoesNotStopTheOthersOpening()
    {
        using (var first = new AccountStores(Accounts, Order()))
        {
            first.Add("good@example.com", "Good", MailProtocol.Pop3);
        }

        File.WriteAllText(Path.Combine(Accounts, "broken@example.com.db"), "not a database");

        using var second = new AccountStores(Accounts, Order());

        Assert.Single(second.All);
        Assert.Equal("good@example.com", second.All[0].Account.Address);
    }

    [Fact]
    public void AddingAnAddressTwiceIsRefused()
    {
        using var stores = new AccountStores(Accounts, Order());
        stores.Add("one@example.com", "One", MailProtocol.Pop3);

        Assert.Throws<InvalidOperationException>(
            () => stores.Add("one@example.com", "Again", MailProtocol.Pop3));
    }

    [Fact]
    public void TheFirstAccountIsTheDefaultAndAnotherCanTakeOver()
    {
        var order = Order();
        using var stores = new AccountStores(Accounts, order);
        stores.Add("one@example.com", "One", MailProtocol.Pop3);
        stores.Add("two@example.net", "Two", MailProtocol.Pop3);

        Assert.Equal("one@example.com", stores.Default!.Account.Address);

        order.DefaultAddress = "two@example.net";

        Assert.Equal("two@example.net", stores.Default!.Account.Address);
        Assert.True(stores.Find("two@example.net")!.IsDefault);
    }

    [Fact]
    public void RemovingTheDefaultPromotesAnother()
    {
        var order = Order();
        using var stores = new AccountStores(Accounts, order);
        stores.Add("one@example.com", "One", MailProtocol.Pop3);
        stores.Add("two@example.net", "Two", MailProtocol.Pop3);

        stores.Remove("one@example.com");

        Assert.Equal("two@example.net", order.DefaultAddress);
        Assert.Equal("two@example.net", stores.Default!.Account.Address);
    }

    [Fact]
    public void RemovingTheLastAccountLeavesNoDefault()
    {
        var order = Order();
        using var stores = new AccountStores(Accounts, order);
        stores.Add("only@example.com", "Only", MailProtocol.Pop3);

        stores.Remove("only@example.com");

        Assert.Null(order.DefaultAddress);
        Assert.Null(stores.Default);
        Assert.Empty(stores.All);
    }

    /// <summary>Order is by address, not by row id, so it survives a file being restored.</summary>
    [Fact]
    public void AccountsKeepTheOrderTheyAreGiven()
    {
        var order = Order();
        using var stores = new AccountStores(Accounts, order);
        stores.Add("one@example.com", "One", MailProtocol.Pop3);
        stores.Add("two@example.net", "Two", MailProtocol.Pop3);
        stores.Add("three@example.org", "Three", MailProtocol.Pop3);

        order.Move("three@example.org", -2);

        Assert.Equal(["three@example.org", "one@example.com", "two@example.net"],
            stores.All.Select(a => a.Account.Address));
    }

    [Fact]
    public void MovingPastEitherEndDoesNothing()
    {
        var order = Order();
        using var stores = new AccountStores(Accounts, order);
        stores.Add("one@example.com", "One", MailProtocol.Pop3);
        stores.Add("two@example.net", "Two", MailProtocol.Pop3);

        order.Move("one@example.com", -1);

        Assert.Equal(["one@example.com", "two@example.net"],
            stores.All.Select(a => a.Account.Address));
    }
}
