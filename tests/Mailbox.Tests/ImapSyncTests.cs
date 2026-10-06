using MailKit;
using MailStore = Mailbox.Store.MailStore;
using Mailbox.Protocols;
using Mailbox.Store;
using MessageSummary = Mailbox.Store.MessageSummary;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// The IMAP sync, against a server in memory. What is checked is the contract: the
/// store is authoritative, so local changes are played to the server before the pull, a
/// UIDVALIDITY change is handled rather than trusted, and a folder that is a view of mail held
/// elsewhere is not pulled into a second copy.
/// </summary>
public class ImapSyncTests
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

    private static AccountConnection Connection(int offlineMonths = 0) => new(
        1, "you@example.com",
        new ServerSettings("imap.example.com", 993),
        new ServerSettings("smtp.example.com", 587))
    {
        Protocol = MailProtocol.Imap,
        Sync = new ImapPolicy { OfflineMonths = offlineMonths },
    };

    private static ImapSynchronizer Sync(MailRepository repo, FakeImap server)
        => new(repo, () => Now) { SessionFactory = () => server };

    // ---- Arrival ---------------------------------------------------------------------------

    /// <summary>Moves a message whose subject matches to Junk; the shape the junk filter has.</summary>
    private sealed class SubjectJunk(string subject) : IArrivalHandler
    {
        public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
        {
            if (message.Subject != subject) return folder.Id;
            var junk = mail.FolderWithRole(folder.AccountId, FolderRole.Junk)!;
            mail.MoveMessages([messageId], junk.Id);
            return junk.Id;
        }
    }

    /// <summary>
    /// A message the arrival handler moves to Junk is stored where the server had it and then
    /// moved, so the move is journalled and played to the server on the next sync — the same
    /// path a drag into Junk takes. Only what stayed in the Inbox is an arrival.
    /// </summary>
    [Fact]
    public async Task ArrivingJunkIsMovedToJunkAndTheMoveReachesTheServer()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("Junk", FolderRole.Junk);
        server.Deliver("INBOX", "Cheap pills");
        server.Deliver("INBOX", "Re: lunch");

        var handler = new SubjectJunk("Cheap pills");
        var sync = Sync(repo, server);
        sync.OnArrival = handler;
        var first = await sync.SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var junk = repo.FolderWithRole(accountId, FolderRole.Junk)!;
        Assert.Equal(2, first.Downloaded);
        Assert.Equal("Re: lunch", Assert.Single(repo.Messages(inbox.Id)).Subject);
        Assert.Equal("Cheap pills", Assert.Single(repo.Messages(junk.Id)).Subject);
        Assert.Equal([repo.Messages(inbox.Id)[0].Id], first.Arrived);

        // The move is waiting for the server, and the next sync plays it: the server's Junk
        // folder has the message and its INBOX does not.
        Assert.Contains(repo.PendingOps(), o => o.Kind == SyncOpKind.Move);
        var again = Sync(repo, server);
        again.OnArrival = handler;
        await again.SyncAsync(Connection(), null, Ct);

        Assert.Empty(repo.PendingOps());
        Assert.Equal("Re: lunch", Assert.Single(server.Contents("INBOX")).Message.Subject);
        Assert.Equal("Cheap pills", Assert.Single(server.Contents("Junk")).Message.Subject);
        Assert.Single(repo.Messages(junk.Id));
    }

    /// <summary>Mail pulled into any folder but the Inbox is the server catching up, not an arrival.</summary>
    [Fact]
    public async Task OnlyInboxMailIsHandedToTheArrivalHandler()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("Junk", FolderRole.Junk);
        server.Deliver("Sent", "Cheap pills");

        var sync = Sync(repo, server);
        sync.OnArrival = new SubjectJunk("Cheap pills");
        var result = await sync.SyncAsync(Connection(), null, Ct);

        var sent = repo.FolderWithRole(accountId, FolderRole.Sent)!;
        Assert.Equal("Cheap pills", Assert.Single(repo.Messages(sent.Id)).Subject);
        Assert.Empty(result.Arrived);
        Assert.Empty(repo.PendingOps());
    }

    // ---- Pulling --------------------------------------------------------------------------

    [Fact]
    public async Task AFirstSyncMapsTheServerFoldersAndDownloadsTheInbox()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Hello", flags: MessageFlags.Seen);
        server.Deliver("INBOX", "Second");

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Downloaded);

        // The local Inbox created at account setup was tied to the server's, not doubled.
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        Assert.Equal("INBOX", inbox.ImapPath);
        Assert.Single(repo.Folders(accountId), f => f.Role == FolderRole.Inbox);

        var messages = repo.Messages(inbox.Id);
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Subject == "Hello" && m.IsRead);
        Assert.Contains(messages, m => m.Subject == "Second" && !m.IsRead);
    }

    [Fact]
    public async Task ASecondSyncDownloadsOnlyWhatIsNewAndDropsWhatIsGone()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        var keep = server.Deliver("INBOX", "Keep");
        var drop = server.Deliver("INBOX", "Drop");

        await Sync(repo, server).SyncAsync(Connection(), null, Ct);
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        Assert.Equal(2, repo.Messages(inbox.Id).Count);

        // On the server: one expunged elsewhere, one newly arrived.
        server.Expunge("INBOX", drop.Uid);
        server.Deliver("INBOX", "Fresh");

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(1, result.Removed);

        var subjects = repo.Messages(inbox.Id).Select(m => m.Subject).ToHashSet();
        Assert.Equal(["Keep", "Fresh"], subjects);
    }

    [Fact]
    public async Task AChangedUidValidityRefetchesTheFolderRatherThanTrustingOldUids()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Before");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        Assert.Equal("Before", repo.Messages(inbox.Id).Single().Subject);

        // The server reassigns UIDs: same folder, new UIDVALIDITY, a different message where the
        // old one was. A fresh server folder stands in for that.
        server.Folder("INBOX", FolderRole.Inbox).UidValidity = 999;
        server.Deliver("INBOX", "After");

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        // The old row is gone and the folder is fetched afresh — not a stale row kept under a
        // UID that now means something else.
        // The old row's server_uid ("1") now names a different message, and the folder was
        // fetched afresh rather than kept — so what stands at UID 1 is "After", not "Before".
        var messages = repo.Messages(inbox.Id);
        Assert.Equal("After", messages.Single().Subject);
        Assert.Equal(1, result.Downloaded);
    }

    [Fact]
    public async Task AViewFolderIsMappedButNeverPulled()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("[Gmail]/All Mail", FolderRole.None, isView: true);
        server.Deliver("[Gmail]/All Mail", "A copy of everything");
        server.Deliver("INBOX", "Real");

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        // The view is a folder here, so a move into it is understood — but its mail is not a
        // second copy in the store.
        var all = repo.FolderByPath(accountId, "[Gmail]/All Mail");
        Assert.NotNull(all);
        Assert.False(all!.Synced);
        Assert.Empty(repo.Messages(all.Id));
        Assert.Equal(1, result.Downloaded);
    }

    [Fact]
    public async Task TheOfflineWindowStopsAtOldMail()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Recent", arrived: Now.AddDays(-10));
        server.Deliver("INBOX", "Ancient", arrived: Now.AddMonths(-8));

        // Keep three months. The older message is on the server but past the window.
        var result = await Sync(repo, server).SyncAsync(Connection(offlineMonths: 3), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        Assert.Equal("Recent", repo.Messages(inbox.Id).Single().Subject);
        Assert.Equal(1, result.Downloaded);
    }

    // ---- Playing the journal --------------------------------------------------------------

    [Fact]
    public async Task LocalFlagChangesArePlayedToTheServer()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Unread");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var message = repo.Messages(inbox.Id).Single();

        repo.SetRead(message.Id, true);
        repo.SetFlagged(message.Id, true);

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Equal(2, result.OpsPlayed);
        var onServer = server.Contents("INBOX").Single();
        Assert.True(onServer.Flags.HasFlag(MessageFlags.Seen));
        Assert.True(onServer.Flags.HasFlag(MessageFlags.Flagged));
        Assert.Empty(repo.PendingOps());
    }

    [Fact]
    public async Task AnsweredAndForwardedReachTheServerAsFlagAndKeyword()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "To be replied to");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var message = repo.Messages(inbox.Id).Single();

        repo.SetAnswered([message.Id]);
        repo.SetForwarded([message.Id]);

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Equal(2, result.OpsPlayed);
        var onServer = server.Contents("INBOX").Single();
        Assert.True(onServer.Flags.HasFlag(MessageFlags.Answered));
        Assert.True(onServer.Forwarded);
        Assert.Contains(server.KeywordStores, k => k.Keyword == "$Forwarded" && k.Set);
        Assert.Empty(repo.PendingOps());
    }

    [Fact]
    public async Task TheServersAnsweredAndForwardedMarksComeHome()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Answered elsewhere");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var before = repo.Messages(inbox.Id).Single();
        Assert.False(before.IsAnswered);
        Assert.False(before.IsForwarded);

        // Another client replied to and forwarded the message on the server.
        var onServer = server.Contents("INBOX").Single();
        onServer.Flags |= MessageFlags.Answered;
        onServer.Forwarded = true;

        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var after = repo.Messages(inbox.Id).Single();
        Assert.True(after.IsAnswered);
        Assert.True(after.IsForwarded);
    }

    [Fact]
    public async Task AServerWithoutTheKeywordDoesNotUnforwardTheStore()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Forwarded here, unmarked there");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var message = repo.Messages(inbox.Id).Single();

        repo.SetForwarded([message.Id]);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        // The server dropped the keyword — some do — and reports the message bare.
        server.Contents("INBOX").Single().Forwarded = false;
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.True(repo.Messages(inbox.Id).Single().IsForwarded);
    }

    [Fact]
    public async Task AMoveIsPlayedAndTheMessageKeepsItsIdentityInTheNewFolder()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "To be filed");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        // The archive is only local until mapped; make it a server folder first.
        server.Folder("Archive", FolderRole.Archive);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);
        archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;

        var message = repo.Messages(inbox.Id).Single();
        repo.MoveMessage(message.Id, archive.Id);

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Equal(1, result.OpsPlayed);
        Assert.Empty(server.Contents("INBOX"));
        Assert.Single(server.Contents("Archive"));

        // Not downloaded a second time on the pull that follows the move.
        Assert.Single(repo.Messages(archive.Id));
        Assert.Empty(repo.Messages(inbox.Id));
        Assert.Empty(repo.PendingOps());
    }

    [Fact]
    public async Task AMoveSurvivesAServerWithoutUidplusRatherThanDuplicating()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap { Features = ImapFeatures.CondStore, ReturnMoveMap = false };
        server.Folder("Archive", FolderRole.Archive);
        server.Deliver("INBOX", "Filed without UIDPLUS");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        repo.MoveMessage(repo.Messages(inbox.Id).Single().Id, archive.Id);

        // Two syncs: the move, then a pull that would re-download the moved message if its new
        // identity had been lost.
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Single(repo.Messages(archive.Id));
        Assert.Empty(repo.Messages(inbox.Id));
    }

    [Fact]
    public async Task ADeleteIsExpungedOnTheServer()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Delete me");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        repo.DeleteMessage(repo.Messages(inbox.Id).Single().Id);

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Equal(1, result.OpsPlayed);
        Assert.Empty(server.Contents("INBOX"));
        Assert.Empty(repo.PendingOps());
    }

    [Fact]
    public async Task AMessageWrittenLocallyIsAppendedToTheServer()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("Sent", FolderRole.Sent);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        // A sent copy filed into the mapped Sent folder while "online" is journalled for append.
        var sent = repo.FolderWithRole(accountId, FolderRole.Sent)!;
        var raw = System.Text.Encoding.ASCII.GetBytes(
            "From: you@example.com\r\nTo: a@example.com\r\nSubject: Filed\r\nMessage-Id: <x@example.com>\r\n\r\nBody\r\n");
        var summary = new MessageSummary(0, sent.Id, null, "<x@example.com>", "You", "you@example.com",
            "Filed", "Body", Now, Now, raw.Length, true, false, false);
        repo.AddMessage(sent.Id, summary, raw);

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Equal(1, result.OpsPlayed);
        Assert.Single(server.Contents("Sent"));
        Assert.Empty(repo.PendingOps());
    }

    [Fact]
    public async Task NothingIsJournalledForAPop3Account()
    {
        var store = MailStore.Transient();
        using var _ = store;
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);

        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;
        var summary = new MessageSummary(0, inbox.Id, "uid-1", null, "A", "a@example.com",
            "Hi", "Body", Now, Now, 10, false, false, false);
        var id = repo.AddMessage(inbox.Id, summary)!.Value;

        repo.SetRead(id, true);
        repo.MoveMessage(id, repo.FolderWithRole(account.Id, FolderRole.Archive)!.Id);

        // A POP3 store has no server to sync to, so no journal is written for any of it.
        Assert.Empty(repo.PendingOps());
    }

    // ---- Archive ---------------------------------------------------------------------------

    /// <summary>
    /// A server with no Archive folder. Archive used to file the message into the local-only
    /// Archive every account is created with, and a move into a folder that is not on the
    /// server was played as an expunge — the server's copy destroyed, the mail left on this
    /// machine alone. It now makes an Archive folder on the server and moves the message there.
    /// </summary>
    [Fact]
    public async Task ArchivingWhereTheServerHasNoArchiveKeepsTheMessageOnTheServer()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Keep me");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        Assert.Null(archive.ImapPath);

        repo.MoveMessages([repo.Messages(inbox.Id).Single().Id], archive.Id);
        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(server.Contents("INBOX"));
        Assert.True(server.Has("Archive"));
        Assert.Equal("Keep me", Assert.Single(server.Contents("Archive")).Message.Subject);

        // One Archive, now the server's, holding the one message under its server UID.
        archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        Assert.Equal("Archive", archive.ImapPath);
        Assert.True(archive.Synced);
        Assert.Single(repo.Folders(accountId), f => f.Role == FolderRole.Archive);
        var kept = Assert.Single(repo.Messages(archive.Id));
        Assert.Equal(server.Contents("Archive").Single().Uid.ToString(), kept.ServerUid);
        Assert.Empty(repo.PendingOps());

        // And a further sync leaves it as it is: no second copy pulled, nothing lost.
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);
        Assert.Single(repo.Messages(archive.Id));
        Assert.Single(server.Contents("Archive"));
    }

    /// <summary>
    /// A server that already has a folder called Archive without marking it as one is given
    /// the message there, rather than a second folder of the same name.
    /// </summary>
    [Fact]
    public async Task AnUnmarkedArchiveFolderOnTheServerIsTheOneUsed()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("Archive");
        server.Deliver("INBOX", "Keep me");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        repo.MoveMessages([repo.Messages(inbox.Id).Single().Id], archive.Id);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Single(server.Contents("Archive"));
        Assert.Single(repo.Folders(accountId), f => string.Equals(f.Name, "Archive", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Archive", repo.FolderWithRole(accountId, FolderRole.Archive)!.ImapPath);
    }

    /// <summary>
    /// Gmail archives by taking a message out of the Inbox, and keeps it in All Mail. An
    /// Archive folder there would be a label called Archive on every archived message.
    /// </summary>
    [Fact]
    public async Task ArchivingOnGmailTakesTheMessageOutOfTheInboxAndMakesNoLabel()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap { Features = ImapFeatures.Move | ImapFeatures.UidPlus | ImapFeatures.GMail };
        server.Folder("[Gmail]/All Mail", FolderRole.None, isView: true);
        server.Deliver("INBOX", "Keep me");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        repo.MoveMessages([repo.Messages(inbox.Id).Single().Id], archive.Id);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Empty(server.Contents("INBOX"));
        Assert.False(server.Has("Archive"));
        Assert.Single(repo.Messages(repo.FolderWithRole(accountId, FolderRole.Archive)!.Id));
    }

    /// <summary>
    /// Mail archived before this was fixed sits in the local-only Archive with no copy left on
    /// the server. When the Archive comes to be on the server, that mail goes up with it.
    /// </summary>
    [Fact]
    public async Task MailArchivedOnlyHereGoesUpWhenTheArchiveReachesTheServer()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "New");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        var archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        var raw = System.Text.Encoding.ASCII.GetBytes(
            "From: a@example.com\r\nTo: you@example.com\r\nSubject: Archived long ago\r\nMessage-Id: <old@example.com>\r\n\r\nBody\r\n");
        repo.AddMessage(archive.Id, new MessageSummary(0, archive.Id, null, "<old@example.com>", "A", "a@example.com",
            "Archived long ago", "Body", Now, Now, raw.Length, true, false, false), raw);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        repo.MoveMessages([repo.Messages(inbox.Id).Single().Id], archive.Id);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Equal(["Archived long ago", "New"],
            server.Contents("Archive").Select(m => m.Message.Subject).Order());
        Assert.All(repo.Messages(repo.FolderWithRole(accountId, FolderRole.Archive)!.Id), m => Assert.NotNull(m.ServerUid));
    }

    /// <summary>Nothing archived, nothing made: a server is not given a folder nobody uses.</summary>
    [Fact]
    public async Task AServerIsNotGivenAnArchiveNobodyUses()
    {
        var (store, repo, _) = Imap();
        using var __ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Hello");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.False(server.Has("Archive"));
    }

    /// <summary>
    /// An account synced before Archive was taken by name knows a server's "Archives" — the name
    /// Thunderbird gives the folder — as an ordinary folder beside the local-only Archive. The
    /// first archive makes them one, the server's, and its mail comes back under it.
    /// </summary>
    [Fact]
    public async Task AnArchiveKnownAsAnOrdinaryFolderBecomesTheArchive()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("Archives");
        server.Deliver("Archives", "Archived elsewhere");
        server.Deliver("INBOX", "Keep me");

        // The state an earlier sync left: the server's Archives as a plain folder of its own.
        var plain = repo.AddFolder(accountId, "Archives", FolderRole.None, null, "Archives");
        repo.SetFolderSynced(plain.Id, true);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);
        Assert.Single(repo.Messages(plain.Id));

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        Assert.Null(archive.ImapPath);
        repo.MoveMessages([repo.Messages(inbox.Id).Single().Id], archive.Id);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.False(server.Has("Archive"));
        Assert.Equal(["Archived elsewhere", "Keep me"], server.Contents("Archives").Select(m => m.Message.Subject).Order());
        Assert.Null(repo.GetFolder(plain.Id));
        archive = repo.FolderWithRole(accountId, FolderRole.Archive)!;
        Assert.Equal("Archives", archive.ImapPath);
        Assert.Equal(["Archived elsewhere", "Keep me"], repo.Messages(archive.Id).Select(m => m.Subject).Order());
    }

    /// <summary>
    /// A server with an unmarked top-level folder called exactly Archive. It used to be added as
    /// a second folder of the name the account's own Archive already had, which the store
    /// refuses — and the whole sync failed with it, every time.
    /// </summary>
    [Fact]
    public async Task AnUnmarkedArchiveOnTheServerDoesNotStopTheSync()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("Archive");
        server.Deliver("INBOX", "Hello");

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal("Archive", repo.FolderWithRole(accountId, FolderRole.Archive)!.ImapPath);
    }

    /// <summary>
    /// The same for the other roles: a server without SPECIAL-USE whose drafts and junk are
    /// plain folders gets them as the account's own, rather than failing on a second Drafts.
    /// </summary>
    [Fact]
    public async Task UnmarkedRoleFoldersAreTakenByName()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("Drafts");
        server.Folder("Spam");
        server.Deliver("INBOX", "Hello");

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Drafts", repo.FolderWithRole(accountId, FolderRole.Drafts)!.ImapPath);
        Assert.Equal("Spam", repo.FolderWithRole(accountId, FolderRole.Junk)!.ImapPath);
        Assert.Single(repo.Folders(accountId), f => f.Role == FolderRole.Drafts);
        Assert.Single(repo.Folders(accountId), f => f.Role == FolderRole.Junk);
    }

    /// <summary>A role the server marks is never moved to another folder by its name.</summary>
    [Fact]
    public async Task AMarkedRoleIsNotOverriddenByAName()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();          // Trash is marked as Deleted.
        server.Folder("Deleted Messages");
        await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.Equal("Trash", repo.FolderWithRole(accountId, FolderRole.Deleted)!.ImapPath);
        Assert.Equal(FolderRole.None, repo.FolderByPath(accountId, "Deleted Messages")!.Role);
    }

    /// <summary>The sync records whether the server is one whose folders are labels.</summary>
    [Fact]
    public async Task TheSyncRecordsWhetherFoldersAreLabels()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        await Sync(repo, new FakeImap { Features = ImapFeatures.GMail }).SyncAsync(Connection(), null, Ct);
        Assert.True(repo.UsesLabels(accountId));

        await Sync(repo, new FakeImap()).SyncAsync(Connection(), null, Ct);
        Assert.False(repo.UsesLabels(accountId));
    }

    /// <summary>
    /// A server with a folder called Outbox. The account's own Outbox is local by design — mail
    /// waits there to be sent — so the server's could not take it over, was added beside it
    /// under the same name, which the store refuses for two top-level folders, and every sync of
    /// the account failed. The server's is kept, under a name of its own.
    /// </summary>
    [Fact]
    public async Task AServerOutboxSitsBesideTheLocalOneRatherThanStoppingTheSync()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Folder("Outbox");
        server.Deliver("Outbox", "Left there by another program");
        server.Deliver("INBOX", "Hello");

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.Downloaded);

        var outbox = repo.FolderWithRole(accountId, FolderRole.Outbox)!;
        Assert.Null(outbox.ImapPath);
        Assert.Equal("Outbox", outbox.Name);

        var theirs = repo.FolderByPath(accountId, "Outbox")!;
        Assert.NotEqual(outbox.Id, theirs.Id);
        Assert.Equal("Outbox (server)", theirs.Name);
        Assert.Single(repo.Messages(theirs.Id));

        // And the next sync finds it where it left it.
        Assert.True((await Sync(repo, server).SyncAsync(Connection(), null, Ct)).Succeeded);
        Assert.Single(repo.Folders(accountId), f => f.ImapPath == "Outbox");
    }

    /// <summary>
    /// The general case: a folder made here only, and one of the same name made on the server
    /// from somewhere else.
    /// </summary>
    [Fact]
    public async Task AServerFolderNamedLikeALocalOneDoesNotStopTheSync()
    {
        var (store, repo, accountId) = Imap();
        using var _ = store;

        repo.AddFolder(accountId, "Projects");
        var server = new FakeImap();
        server.Folder("Projects");

        var result = await Sync(repo, server).SyncAsync(Connection(), null, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Projects (server)", repo.FolderByPath(accountId, "Projects")!.Name);
    }
}
