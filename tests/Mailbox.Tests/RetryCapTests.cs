using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// What happens to a change the server will not take.
/// </summary>
/// <remarks>
/// Both queues counted their failures and kept what the server said, and nothing read either
/// column: a calendar change refused with a 403, or a move to a folder that no longer exists,
/// was sent again on every send/receive for as long as the account lived, in silence. What is
/// held here is the giving up — after five tries a change stops being offered, is still there to
/// be looked at, and can be put back by hand once whatever refused it has been dealt with.
/// </remarks>
public class RetryCapTests
{
    // ---- The calendar and contact queue ------------------------------------------------------

    private static (PimStore Store, PimRepository Repo, long Collection) Pim()
    {
        var store = PimStore.Transient();
        var repo = new PimRepository(store);
        return (store, repo, repo.AddCollection(CollectionKind.Events, "Calendar").Id);
    }

    [Fact]
    public void AChangeIsRetriedUpToTheCap()
    {
        var (store, repo, collection) = Pim();
        using var _ = store;

        var change = repo.Queue(collection, itemId: null, "put");

        for (var attempt = 1; attempt < PimRepository.MaxAttempts; attempt++)
        {
            repo.QueueFailed(change, "the server said no");
            Assert.Contains(repo.Queued(collection), q => q.Id == change);
        }

        repo.QueueFailed(change, "the server said no");

        Assert.DoesNotContain(repo.Queued(collection), q => q.Id == change);
        Assert.Empty(repo.Queued());
    }

    [Fact]
    public void AChangeThatHasGivenUpIsStillThereWithWhatWentWrong()
    {
        var (store, repo, collection) = Pim();
        using var _ = store;

        var change = repo.Queue(collection, itemId: null, "put");
        for (var attempt = 0; attempt < PimRepository.MaxAttempts; attempt++)
        {
            repo.QueueFailed(change, "403 Forbidden");
        }

        var stuck = Assert.Single(repo.Stuck());

        Assert.Equal(change, stuck.Id);
        Assert.Equal("403 Forbidden", stuck.LastError);
        Assert.Equal(PimRepository.MaxAttempts, stuck.Attempts);
    }

    [Fact]
    public void AStuckChangeCanBePutBack()
    {
        var (store, repo, collection) = Pim();
        using var _ = store;

        var change = repo.Queue(collection, itemId: null, "put");
        for (var attempt = 0; attempt < PimRepository.MaxAttempts; attempt++)
        {
            repo.QueueFailed(change, "the calendar was read-only");
        }

        repo.Retry(change);

        Assert.Contains(repo.Queued(collection), q => q.Id == change);
        Assert.Empty(repo.Stuck());
    }

    // ---- The mail store's own journal --------------------------------------------------------

    private static (MailStore Store, MailRepository Repo, long Op) Mail()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Imap);
        var inbox = repo.CreateStandardFolders(account.Id).First(f => f.Role == FolderRole.Inbox);

        // Written straight in: the journal is fed by folder operations on a synced IMAP folder,
        // and what is being tested is what happens to an entry once it is there.
        store.Execute(
            """
            INSERT INTO sync_ops (kind, folder_id, server_uid, created_utc)
            VALUES ('flags', $folder, '17', 0)
            """,
            ("$folder", inbox.Id));

        return (store, repo, store.LastInsertId);
    }

    [Fact]
    public void AnOperationIsReplayedUpToTheCap()
    {
        var (store, repo, op) = Mail();
        using var _ = store;

        for (var attempt = 1; attempt < MailRepository.MaxOpAttempts; attempt++)
        {
            repo.FailOps([op], "the server said no");
            Assert.Contains(repo.PendingOps(), o => o.Id == op);
        }

        repo.FailOps([op], "the server said no");

        Assert.Empty(repo.PendingOps());
    }

    [Fact]
    public void AnOperationThatHasGivenUpIsReportedWithItsError()
    {
        var (store, repo, op) = Mail();
        using var _ = store;

        for (var attempt = 0; attempt < MailRepository.MaxOpAttempts; attempt++)
        {
            repo.FailOps([op], "NO [TRYCREATE] Mailbox does not exist");
        }

        var stuck = Assert.Single(repo.StuckOps());

        Assert.Equal(op, stuck.Id);
        Assert.Equal("NO [TRYCREATE] Mailbox does not exist", stuck.LastError);

        repo.RetryOp(op);
        Assert.Empty(repo.StuckOps());
        Assert.Single(repo.PendingOps());
    }
}
