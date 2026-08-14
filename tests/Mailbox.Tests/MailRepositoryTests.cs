using Mailbox.Store;

namespace Mailbox.Tests;

public class MailRepositoryTests
{
    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static MessageSummary Sample(string uid, string subject = "Hello",
        string from = "alice@example.com", bool read = false) => new(
        0, 0, uid, $"<{uid}@example.com>", "Alice", from, subject, "Preview text",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1024, read, false, false);

    [Fact]
    public void AnAccountGetsTheStandardFolders()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        Assert.Equal(FolderRole.Inbox, inbox.Role);
        Assert.NotNull(repo.FolderWithRole(inbox.AccountId, FolderRole.Sent));
        Assert.NotNull(repo.FolderWithRole(inbox.AccountId, FolderRole.Outbox));
        Assert.Equal(7, repo.Folders(inbox.AccountId).Count);
    }

    /// <summary>
    /// The guard that stops a re-poll re-delivering an inbox: the second attempt is refused
    /// quietly, and reports that it filed nothing.
    /// </summary>
    [Fact]
    public void FilingTheSameServerIdTwiceIsIgnoredNotDuplicated()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        Assert.NotNull(repo.AddMessage(inbox.Id, Sample("uid-1")));
        Assert.Null(repo.AddMessage(inbox.Id, Sample("uid-1")));
        Assert.Single(repo.Messages(inbox.Id));
    }

    /// <summary>
    /// A duplicate must not leave its raw copy behind. Nothing points at that blob, so it would
    /// grow the store by the size of every message ever re-polled.
    /// </summary>
    [Fact]
    public void ADuplicateLeavesNoOrphanedBlob()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var raw = System.Text.Encoding.UTF8.GetBytes("From: a@example.com\r\n\r\nBody");

        repo.AddMessage(inbox.Id, Sample("uid-1"), raw);
        repo.AddMessage(inbox.Id, Sample("uid-1"), raw);

        Assert.Equal(1, store.ScalarLong("SELECT count(*) FROM blobs"));
    }

    [Fact]
    public void TheRawMessageComesBackByteForByte()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var raw = System.Text.Encoding.UTF8.GetBytes(
            "From: alice@example.com\r\nSubject: Hello\r\n\r\n" + new string('x', 5000));

        var id = repo.AddMessage(inbox.Id, Sample("uid-1"), raw)!.Value;

        Assert.Equal(raw, repo.LoadRaw(id));
    }

    [Fact]
    public void ALargeMessageIsStoredCompressed()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var raw = System.Text.Encoding.UTF8.GetBytes(new string('a', 20_000));

        repo.AddMessage(inbox.Id, Sample("uid-1"), raw);

        Assert.Equal("deflate", store.Query(
            "SELECT compression FROM blobs", r => r.GetString(0)).Single());
        Assert.True(store.ScalarLong("SELECT length(bytes) FROM blobs") < raw.Length);
    }

    [Fact]
    public void DeletingAMessageTakesItsBlob()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var id = repo.AddMessage(inbox.Id, Sample("uid-1"), [1, 2, 3])!.Value;

        repo.DeleteMessage(id);

        Assert.Equal(0, store.ScalarLong("SELECT count(*) FROM blobs"));
    }

    [Fact]
    public void FoldersReportWhatTheyHold()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        repo.AddMessage(inbox.Id, Sample("uid-1", read: false));
        repo.AddMessage(inbox.Id, Sample("uid-2", read: true));

        var refreshed = repo.GetFolder(inbox.Id)!;

        Assert.Equal(2, refreshed.Total);
        Assert.Equal(1, refreshed.Unread);
    }

    [Fact]
    public void KnownServerIdsComeBackForWorkingOutWhatIsNew()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        repo.AddMessage(inbox.Id, Sample("uid-1"));
        repo.AddMessage(inbox.Id, Sample("uid-2"));

        Assert.Equal(["uid-1", "uid-2"], repo.ServerUids(inbox.Id).OrderBy(x => x));
        Assert.True(repo.HasServerUid(inbox.Id, "uid-1"));
        Assert.False(repo.HasServerUid(inbox.Id, "uid-9"));
    }

    [Fact]
    public void SearchRanksAndScopes()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var sent = repo.FolderWithRole(inbox.AccountId, FolderRole.Sent)!;
        repo.AddMessage(inbox.Id, Sample("uid-1", "Quarterly numbers"));
        repo.AddMessage(sent.Id, Sample("uid-2", "Quarterly review"));

        Assert.Equal(2, repo.Search("quarterly").Count);
        Assert.Single(repo.Search("quarterly", inbox.Id));
        Assert.Empty(repo.Search("   "));
    }

    /// <summary>
    /// A search box takes whatever is typed. Unbalanced quotes and stray operators are FTS5
    /// syntax and would throw at the user rather than finding nothing.
    /// </summary>
    [Theory]
    [InlineData("\"unbalanced")]
    [InlineData("NEAR(")]
    [InlineData("a OR")]
    [InlineData("*")]
    [InlineData("^")]
    public void SearchSurvivesWhateverIsTyped(string term)
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        repo.AddMessage(inbox.Id, Sample("uid-1", "Quarterly numbers"));

        var found = repo.Search(term);        // must not throw

        Assert.NotNull(found);
    }

    [Theory]
    [InlineData("Re: Quarterly numbers", "quarterly numbers")]
    [InlineData("RE: FW: Quarterly numbers", "quarterly numbers")]
    [InlineData("Fwd: Re: Quarterly numbers", "quarterly numbers")]
    [InlineData("Quarterly numbers", "quarterly numbers")]
    public void RepliesThreadWithWhatTheyReplyTo(string subject, string expected)
        => Assert.Equal(expected, MailRepository.ThreadKey(subject));

    [Fact]
    public void MovingAMessageChangesTheFolderItCounts()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var archive = repo.FolderWithRole(inbox.AccountId, FolderRole.Archive)!;
        var id = repo.AddMessage(inbox.Id, Sample("uid-1"))!.Value;

        repo.MoveMessage(id, archive.Id);

        Assert.Empty(repo.Messages(inbox.Id));
        Assert.Single(repo.Messages(archive.Id));
    }

    /// <summary>
    /// Exactly one account is the default, always. Nothing knows where to send from otherwise,
    /// and the database enforces it rather than trusting every caller to.
    /// </summary>
    [Fact]
    public void TheFirstAccountBecomesTheDefault()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);

        var first = repo.AddAccount("one@example.com", "One", MailProtocol.Pop3);
        var second = repo.AddAccount("two@example.com", "Two", MailProtocol.Pop3);

        Assert.True(repo.GetAccount(first.Id)!.IsDefault);
        Assert.False(repo.GetAccount(second.Id)!.IsDefault);
        Assert.Equal(first.Id, repo.DefaultAccount()!.Id);
    }

    [Fact]
    public void MakingAnotherAccountTheDefaultReplacesTheFirst()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var first = repo.AddAccount("one@example.com", "One", MailProtocol.Pop3);
        var second = repo.AddAccount("two@example.com", "Two", MailProtocol.Pop3);

        repo.SetDefaultAccount(second.Id);

        Assert.False(repo.GetAccount(first.Id)!.IsDefault);
        Assert.Equal(second.Id, repo.DefaultAccount()!.Id);
    }

    /// <summary>Removing the default has to leave one behind, not none.</summary>
    [Fact]
    public void RemovingTheDefaultPromotesAnother()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var first = repo.AddAccount("one@example.com", "One", MailProtocol.Pop3);
        var second = repo.AddAccount("two@example.com", "Two", MailProtocol.Pop3);

        repo.RemoveAccount(first.Id);

        Assert.Equal(second.Id, repo.DefaultAccount()!.Id);
    }

    [Fact]
    public void RemovingTheLastAccountLeavesNoDefaultRatherThanADanglingOne()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var only = repo.AddAccount("one@example.com", "One", MailProtocol.Pop3);

        repo.RemoveAccount(only.Id);

        Assert.Null(repo.DefaultAccount());
        Assert.Empty(repo.Accounts());
    }

    [Fact]
    public void AccountsCanBeReorderedAndRenamed()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var first = repo.AddAccount("one@example.com", "One", MailProtocol.Pop3);
        var second = repo.AddAccount("two@example.com", "Two", MailProtocol.Pop3);

        repo.MoveAccount(second.Id, -1);
        repo.RenameAccount(first.Id, "Renamed");

        Assert.Equal(["two@example.com", "one@example.com"],
            repo.Accounts().Select(a => a.Address));
        Assert.Equal("Renamed", repo.GetAccount(first.Id)!.DisplayName);
    }

    [Fact]
    public void MovingPastEitherEndDoesNothing()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var first = repo.AddAccount("one@example.com", "One", MailProtocol.Pop3);
        repo.AddAccount("two@example.com", "Two", MailProtocol.Pop3);

        repo.MoveAccount(first.Id, -1);

        Assert.Equal(["one@example.com", "two@example.com"],
            repo.Accounts().Select(a => a.Address));
    }
}
