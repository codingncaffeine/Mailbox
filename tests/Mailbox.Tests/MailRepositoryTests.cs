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
    /// Selecting a folder's worth of mail and acting on it is ordinary. The bulk paths exist so
    /// that is one statement rather than a thousand, and they have to agree with the single-row
    /// ones about what they did.
    /// </summary>
    [Fact]
    public void MarkingManyAsReadTakesEffectOnAllOfThem()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var ids = Enumerable.Range(0, 50)
            .Select(i => repo.AddMessage(inbox.Id, Sample($"uid-{i}"))!.Value)
            .ToList();

        Assert.Equal(50, repo.SetRead(ids, read: true));
        Assert.Equal(0, repo.GetFolder(inbox.Id)!.Unread);

        Assert.Equal(50, repo.SetRead(ids, read: false));
        Assert.Equal(50, repo.GetFolder(inbox.Id)!.Unread);
    }

    [Fact]
    public void FlaggingManyWorksTheSameWay()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var ids = Enumerable.Range(0, 5)
            .Select(i => repo.AddMessage(inbox.Id, Sample($"uid-{i}"))!.Value)
            .ToList();

        repo.SetFlagged(ids, flagged: true);

        Assert.All(repo.Messages(inbox.Id), m => Assert.True(m.IsFlagged));
    }

    [Fact]
    public void MovingManyLeavesTheSourceEmpty()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var archive = repo.FolderWithRole(inbox.AccountId, FolderRole.Archive)!;
        var ids = Enumerable.Range(0, 8)
            .Select(i => repo.AddMessage(inbox.Id, Sample($"uid-{i}"))!.Value)
            .ToList();

        Assert.Equal(8, repo.MoveMessages(ids, archive.Id));
        Assert.Empty(repo.Messages(inbox.Id));
        Assert.Equal(8, repo.Messages(archive.Id).Count);
    }

    /// <summary>Bulk delete must take the raw copies too, or the store grows without bound.</summary>
    [Fact]
    public void DeletingManyTakesTheirBlobs()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var ids = Enumerable.Range(0, 6)
            .Select(i => repo.AddMessage(inbox.Id, Sample($"uid-{i}"), [1, 2, 3])!.Value)
            .ToList();

        Assert.Equal(6, store.ScalarLong("SELECT count(*) FROM blobs"));

        repo.DeleteMessages(ids);

        Assert.Empty(repo.Messages(inbox.Id));
        Assert.Equal(0, store.ScalarLong("SELECT count(*) FROM blobs"));
        Assert.Empty(store.CheckIntegrity());
    }

    [Fact]
    public void DeletingSomeLeavesTheRestAndTheirBlobs()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var keep = repo.AddMessage(inbox.Id, Sample("keep"), [1, 2, 3])!.Value;
        var drop = repo.AddMessage(inbox.Id, Sample("drop"), [4, 5, 6])!.Value;

        repo.DeleteMessages([drop]);

        Assert.Single(repo.Messages(inbox.Id));
        Assert.Equal(1, store.ScalarLong("SELECT count(*) FROM blobs"));
        Assert.NotNull(repo.LoadRaw(keep));
    }

    [Fact]
    public void TheBulkPathsDoNothingWhenGivenNothing()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        repo.AddMessage(inbox.Id, Sample("uid-1"));

        Assert.Equal(0, repo.SetRead([], true));
        Assert.Equal(0, repo.SetFlagged([], true));
        Assert.Equal(0, repo.MoveMessages([], inbox.Id));
        Assert.Equal(0, repo.DeleteMessages([]));
        Assert.Single(repo.Messages(inbox.Id));
    }
}
