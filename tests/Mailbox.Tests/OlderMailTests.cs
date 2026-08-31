using Mailbox.Protocols;
using Mailbox.Store;
using MailStore = Mailbox.Store.MailStore;

namespace Mailbox.Tests;

/// <summary>
/// The mail the offline window leaves on the server: the sync counting it, the footer's fetch
/// bringing the next batch home, and a search taken to the server for what the local index
/// cannot see. The contract under all of it: fetched-older mail is ordinary mail — stored,
/// kept across syncs, and never handed to the rules as if it had just arrived.
/// </summary>
public class OlderMailTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static (MailStore Store, MailRepository Repo, long AccountId) Imap()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Imap);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, account.Id);
    }

    private static AccountConnection Connection(int offlineMonths = 12) => new(
        1, "you@example.com",
        new ServerSettings("imap.example.com", 993),
        new ServerSettings("smtp.example.com", 587))
    {
        Protocol = MailProtocol.Imap,
        Sync = new ImapPolicy { OfflineMonths = offlineMonths },
    };

    private static ImapSynchronizer Sync(MailRepository repo, FakeImap server)
        => new(repo, () => Now) { SessionFactory = () => server };

    /// <summary>An arrival handler that must never be asked: backfill is not an arrival.</summary>
    private sealed class MustNotRun : IArrivalHandler
    {
        public long? Handle(MailRepository mail, Folder folder, long messageId, MimeKit.MimeMessage message)
            => throw new InvalidOperationException("The rules ran on backfilled mail.");
    }

    private static FakeImap DeepInbox()
    {
        var server = new FakeImap();
        server.Deliver("INBOX", "Old 1", arrived: Now.AddMonths(-20));
        server.Deliver("INBOX", "Old 2", arrived: Now.AddMonths(-18));
        server.Deliver("INBOX", "Old 3", arrived: Now.AddMonths(-15));
        server.Deliver("INBOX", "New 1", arrived: Now.AddMonths(-2));
        server.Deliver("INBOX", "New 2", arrived: Now.AddDays(-3));
        return server;
    }

    [Fact]
    public async Task TheSyncCountsWhatTheWindowLeavesOnTheServer()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;
        var server = DeepInbox();

        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        Assert.Equal(2, repo.Messages(inbox.Id).Count);
        Assert.Equal(3, repo.GetFolder(inbox.Id)!.ServerOlder);
    }

    [Fact]
    public async Task FetchOlderBringsTheNewestOfTheOldAndCountsDown()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;
        var server = DeepInbox();

        var sync = Sync(repo, server);
        sync.OnArrival = new MustNotRun();
        await sync.SyncAsync(Connection(), null, Ct);
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;

        var (downloaded, remaining, refused) =
            await sync.FetchOlderAsync(Connection(), inbox.Id, batch: 2, null, Ct);

        Assert.Null(refused);
        Assert.Equal(2, downloaded);
        Assert.Equal(1, remaining);
        Assert.Equal(1, repo.GetFolder(inbox.Id)!.ServerOlder);

        // Newest of the old first: the list grows downward the way a reader scrolls.
        var subjects = repo.Messages(inbox.Id).Select(m => m.Subject).ToList();
        Assert.Contains("Old 3", subjects);
        Assert.Contains("Old 2", subjects);
        Assert.DoesNotContain("Old 1", subjects);

        var (again, left, _) = await sync.FetchOlderAsync(Connection(), inbox.Id, batch: 2, null, Ct);
        Assert.Equal(1, again);
        Assert.Equal(0, left);
        Assert.Equal(5, repo.Messages(inbox.Id).Count);
    }

    [Fact]
    public async Task FetchedOlderMailSurvivesTheNextSync()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;
        var server = DeepInbox();

        var sync = Sync(repo, server);
        await sync.SyncAsync(Connection(), null, Ct);
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        await sync.FetchOlderAsync(Connection(), inbox.Id, batch: 3, null, Ct);

        await sync.SyncAsync(Connection(), null, Ct);

        // The window gates downloads, never keeps: nothing fetched below it is "vanished".
        Assert.Equal(5, repo.Messages(inbox.Id).Count);
        Assert.Equal(0, repo.GetFolder(inbox.Id)!.ServerOlder);
    }

    [Fact]
    public async Task ServerSearchDownloadsOnlyWhatTheStoreIsMissing()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;
        var server = DeepInbox();

        var sync = Sync(repo, server);
        sync.OnArrival = new MustNotRun();
        await sync.SyncAsync(Connection(), null, Ct);
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;

        // The server says three match: two old ones this store lacks, one already here.
        var stored = repo.Messages(inbox.Id);
        Assert.Equal(2, stored.Count);
        server.SearchAnswer.AddRange([1, 2, long.Parse(stored[0].ServerUid!)]);

        var (downloaded, moreMatches, refused) = await sync.SearchServerAsync(
            Connection(), inbox.Id,
            Mailbox.Core.Search.SearchQuery.Parse("old", Now), cap: 10, null, Ct);

        Assert.Null(refused);
        Assert.Equal(2, downloaded);
        Assert.Equal(0, moreMatches);
        Assert.Equal(4, repo.Messages(inbox.Id).Count);
        Assert.Single(server.SearchedFor);
    }

    [Fact]
    public async Task ASearchTheServerCannotAnswerNeverLeavesTheMachine()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;
        var server = DeepInbox();

        var sync = Sync(repo, server);
        await sync.SyncAsync(Connection(), null, Ct);
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;

        var (downloaded, _, refused) = await sync.SearchServerAsync(
            Connection(), inbox.Id,
            Mailbox.Core.Search.SearchQuery.Parse("category:red", Now), cap: 10, null, Ct);

        Assert.Equal(0, downloaded);
        Assert.NotNull(refused);
        Assert.Empty(server.SearchedFor);
    }

    [Fact]
    public void TheTranslatorKeepsRecallAndDropsWhatImapCannotSay()
    {
        var now = Now;

        // Words, senders, dates: server-answerable.
        Assert.NotNull(ImapSearchTranslator.Translate(
            Mailbox.Core.Search.SearchQuery.Parse("budget from:alice received:>2026-01-01", now)));

        // Categories and importance live only in this store; alone they translate to nothing.
        Assert.Null(ImapSearchTranslator.Translate(
            Mailbox.Core.Search.SearchQuery.Parse("category:red importance:high", now)));

        // Mixed: the untranslatable half is dropped, the rest still goes — recall over precision.
        Assert.NotNull(ImapSearchTranslator.Translate(
            Mailbox.Core.Search.SearchQuery.Parse("budget category:red", now)));
    }
}
