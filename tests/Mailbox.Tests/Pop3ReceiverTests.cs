using Mailbox.Protocols;
using Mailbox.Security;
using Mailbox.Security.Dns;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>A POP3 server in memory.</summary>
internal sealed class FakePop3 : IPop3Session
{
    private readonly List<(string Uid, MimeMessage Message)> _messages = [];

    public List<string> Deleted { get; } = [];
    public int Fetches { get; private set; }
    public Exception? FailOnConnect { get; set; }
    public bool IsConnected { get; private set; }
    public int Count => _messages.Count;

    public FakePop3 With(string uid, string subject = "Hello")
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.MessageId = $"{uid}@example.com";
        message.Body = new TextPart("plain") { Text = $"Body of {subject}" };
        _messages.Add((uid, message));
        return this;
    }

    public Task ConnectAsync(ServerSettings s, CancellationToken c)
    {
        if (FailOnConnect is { } ex) throw ex;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task AuthenticateAsync(ServerSettings s, CancellationToken c) => Task.CompletedTask;

    public Task<IList<string>> GetUidsAsync(CancellationToken c)
        => Task.FromResult<IList<string>>([.. _messages.Select(m => m.Uid)]);

    /// <summary>Download Headers over POP3: TOP, which is the headers and the size.</summary>
    public Task<RemoteHeader?> GetHeadersAsync(int index, string uid, CancellationToken c)
    {
        var message = _messages[index].Message;
        var from = message.From.Mailboxes.FirstOrDefault();

        return Task.FromResult<RemoteHeader?>(new RemoteHeader(
            uid,
            message.MessageId,
            from?.Name ?? string.Empty,
            from?.Address ?? string.Empty,
            message.Subject ?? string.Empty,
            message.Date,
            message.Date,
            100,
            false,
            false));
    }

    public Task<MimeMessage> GetMessageAsync(int index, CancellationToken c)
    {
        Fetches++;
        return Task.FromResult(_messages[index].Message);
    }

    public Task DeleteAsync(IList<int> indexes, CancellationToken c)
    {
        Deleted.AddRange(indexes.Select(i => _messages[i].Uid));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken c)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

public class Pop3ReceiverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static AccountConnection Connection(Pop3Policy? policy = null) => new(
        1, "you@example.com",
        new ServerSettings("pop.example.com", 995),
        new ServerSettings("smtp.example.com", 587))
    { Policy = policy ?? new Pop3Policy() };

    private static Pop3Receiver Receiver(MailRepository repo, FakePop3 server)
        => new(repo) { SessionFactory = () => server };

    // ---- Signatures, checked as the mail arrives -------------------------------------------

    /// <summary>
    /// A poll is where a signature gets checked, because checking resolves a name the sender
    /// chose and no lookup is allowed on the path that draws a message. These fix that
    /// arrangement in place: the receiver records a verdict, and a receiver given no verifier
    /// asks nothing of anyone.
    /// </summary>
    [Fact]
    public async Task APollRecordsWhatTheSignatureCameTo()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var server = new FakePop3().With("uid-1", "Signed");
        var receiver = Receiver(repo, server);
        receiver.Authentication = new DkimVerification(new StubLookup());

        await receiver.PollAsync(Connection(), inbox, null, Ct);

        // The seeded message carries no signature, so there is nothing to record — and that is
        // the point: a message nobody could check has no row rather than a verdict of "none".
        var message = Assert.Single(repo.Messages(inbox.Id));
        Assert.Null(repo.Authentication(message.Id));
    }

    [Fact]
    public async Task AReceiverWithNoVerifierResolvesNothing()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var lookup = new StubLookup();
        var server = new FakePop3().With("uid-1");

        // Authentication left null, which is the default and what every other test here uses.
        await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);

        Assert.Equal(0, lookup.Lookups);
        Assert.Null(repo.Authentication(Assert.Single(repo.Messages(inbox.Id)).Id));
    }

    /// <summary>A verdict written by a poll is what the reading pane reads back.</summary>
    [Fact]
    public void ARecordedVerdictSurvivesAndReadsBack()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var id = repo.AddMessage(inbox.Id, Summary(inbox.Id), [1, 2, 3])!.Value;

        repo.RecordAuthentication(id, "pass", "example.com", DateTimeOffset.UnixEpoch);
        var stored = repo.Authentication(id);

        Assert.NotNull(stored);
        Assert.Equal("pass", stored.Dkim);
        Assert.Equal("example.com", stored.SigningDomain);
    }

    /// <summary>Re-checking replaces rather than duplicating: one message, one verdict.</summary>
    [Fact]
    public void RecordingTwiceReplaces()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var id = repo.AddMessage(inbox.Id, Summary(inbox.Id), [1])!.Value;

        repo.RecordAuthentication(id, "error", null, DateTimeOffset.UnixEpoch);
        repo.RecordAuthentication(id, "pass", "example.com", DateTimeOffset.UnixEpoch);

        Assert.Equal("pass", repo.Authentication(id)!.Dkim);
    }

    private static MessageSummary Summary(long folderId) => new(
        0, folderId, "uid-x", "x@example.com", "A. Person", "a@example.com",
        "Subject", "Preview", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        3, false, false, false);

    /// <summary>A lookup that counts what it was asked and answers nothing.</summary>
    private sealed class StubLookup : ITxtLookup
    {
        public int Lookups { get; private set; }

        public Task<DnsAnswer> TxtAsync(string name, CancellationToken cancellation = default)
        {
            Lookups++;
            return Task.FromResult(DnsAnswer.Empty);
        }
    }

    [Fact]
    public async Task DownloadsWhatIsThereAndFilesIt()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1", "First").With("uid-2", "Second");

        var result = await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(2, repo.Messages(inbox.Id).Count);
    }

    /// <summary>
    /// The behaviour POP3 lives or dies on. A second poll of an unchanged mailbox must download
    /// nothing — not re-file, not re-fetch. Fetching again would work but would cost the user
    /// their whole mailbox in bandwidth on every poll.
    /// </summary>
    [Fact]
    public async Task ASecondPollDownloadsNothingAndFetchesNothing()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1").With("uid-2");
        var receiver = Receiver(repo, server);

        await receiver.PollAsync(Connection(), inbox, null, Ct);
        var second = await receiver.PollAsync(Connection(), inbox, null, Ct);

        Assert.Equal(0, second.Downloaded);
        Assert.Equal(2, second.AlreadyHad);
        Assert.Equal(2, server.Fetches);           // not four
        Assert.Equal(2, repo.Messages(inbox.Id).Count);
    }

    [Fact]
    public async Task NewMailArrivingBesideOldIsTheOnlyThingDownloaded()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1");
        var receiver = Receiver(repo, server);
        await receiver.PollAsync(Connection(), inbox, null, Ct);

        server.With("uid-2", "Newly arrived");
        var second = await receiver.PollAsync(Connection(), inbox, null, Ct);

        Assert.Equal(1, second.Downloaded);
        Assert.Equal(1, second.AlreadyHad);
        Assert.Equal(2, server.Fetches);
    }

    /// <summary>
    /// The default has to be to leave mail alone. Someone trying Mailbox beside an existing
    /// client must not find their mailbox emptied.
    /// </summary>
    [Fact]
    public async Task NothingIsRemovedFromTheServerByDefault()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1").With("uid-2");

        var result = await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);

        Assert.Empty(server.Deleted);
        Assert.Equal(0, result.RemovedFromServer);
        Assert.True(Connection().Policy.LeaveOnServer);
    }

    [Fact]
    public async Task MailIsRemovedOnlyWhenAskedFor()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1").With("uid-2");
        var policy = new Pop3Policy { LeaveOnServer = false };

        var result = await Receiver(repo, server).PollAsync(Connection(policy), inbox, null, Ct);

        Assert.Equal(["uid-1", "uid-2"], server.Deleted);
        Assert.Equal(2, result.RemovedFromServer);
        Assert.Equal(2, repo.Messages(inbox.Id).Count);   // filed before removal
    }

    /// <summary>
    /// The cap bounds what is downloaded, not what is looked at.
    /// </summary>
    /// <remarks>
    /// It used to bound the walk itself, so a poll that filled up on new mail never reached the
    /// messages after it — and the two rules that take mail off the server, "remove after n
    /// days" and "do not leave a copy", quietly stopped applying to the tail of the mailbox for
    /// as long as the backlog lasted. The order matters here: the known messages sit after the
    /// new ones, which is where the old loop stopped looking.
    /// </remarks>
    [Fact]
    public async Task TheRemovalSweepReachesPastTheDownloadCap()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        // Four collected on an earlier poll, with the server keeping its copies.
        var first = new FakePop3();
        for (var i = 0; i < 4; i++) first.With($"old-{i}");
        await Receiver(repo, first).PollAsync(
            Connection(new Pop3Policy { LeaveOnServer = true }), inbox, null, Ct);

        // Now six new ones arrive ahead of them in the listing, and the reader has since said
        // not to leave copies. The cap is four.
        var second = new FakePop3();
        for (var i = 0; i < 6; i++) second.With($"new-{i}");
        for (var i = 0; i < 4; i++) second.With($"old-{i}");

        var result = await Receiver(repo, second).PollAsync(
            Connection(new Pop3Policy { MaxPerPoll = 4, LeaveOnServer = false }), inbox, null, Ct);

        Assert.Equal(4, result.Downloaded);

        // The four downloaded, and the four already held: everything the policy says to remove,
        // including the ones the cap stopped this poll from downloading.
        Assert.Equal(["new-0", "new-1", "new-2", "new-3", "old-0", "old-1", "old-2", "old-3"], second.Deleted);
    }

    [Fact]
    public async Task APollStopsAtTheCap()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3();
        for (var i = 0; i < 10; i++) server.With($"uid-{i}");

        var result = await Receiver(repo, server)
            .PollAsync(Connection(new Pop3Policy { MaxPerPoll = 4 }), inbox, null, Ct);

        Assert.Equal(4, result.Downloaded);
        Assert.Equal(4, server.Fetches);
    }

    /// <summary>
    /// A poll that fails reports rather than throws: a send/receive runs several accounts and
    /// one unreachable server must not stop the rest.
    /// </summary>
    [Fact]
    public async Task AFailedPollIsReportedNotThrown()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3
        {
            FailOnConnect = new System.Net.Sockets.SocketException(),
        };

        var result = await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("Could not reach the server", result.Error);
    }

    [Fact]
    public async Task CancellingAPollStopsIt()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3();
        for (var i = 0; i < 5; i++) server.With($"uid-{i}");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Receiver(repo, server).PollAsync(Connection(), inbox, null, cancel.Token));
    }

    [Fact]
    public async Task TheRawMessageIsKeptAlongsideTheParsedRow()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1", "Keep me");

        await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);
        var filed = repo.Messages(inbox.Id).Single();
        var raw = repo.LoadRaw(filed.Id)!;

        Assert.Contains("Keep me", System.Text.Encoding.UTF8.GetString(raw));
        Assert.Equal("alice@example.com", filed.FromAddress);
    }

    [Fact]
    public async Task AnEmptyMailboxIsNotAnError()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var result = await Receiver(repo, new FakePop3()).PollAsync(Connection(), inbox, null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Downloaded);
    }

    /// <summary>
    /// "Leave a copy, remove after 14 days" counts from the download, not from the message's own
    /// date — otherwise collecting a year-old message would delete the server's copy on the
    /// same poll that fetched it.
    /// </summary>
    [Fact]
    public async Task MailIsRemovedFromTheServerOnceItsCopyHereIsOldEnough()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1", "Old").With("uid-2", "Older");

        var day = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var policy = new Pop3Policy { LeaveOnServer = true, DeleteAfterDays = 14 };

        // Collected, and left alone: nothing here is old yet.
        var first = new Pop3Receiver(repo, () => day) { SessionFactory = () => server };
        await first.PollAsync(Connection(policy), inbox, null, Ct);
        Assert.Empty(server.Deleted);

        // Thirteen days on, still inside the window.
        var thirteen = new Pop3Receiver(repo, () => day.AddDays(13)) { SessionFactory = () => server };
        await thirteen.PollAsync(Connection(policy), inbox, null, Ct);
        Assert.Empty(server.Deleted);

        var later = new Pop3Receiver(repo, () => day.AddDays(15)) { SessionFactory = () => server };
        await later.PollAsync(Connection(policy), inbox, null, Ct);

        Assert.Equal(["uid-1", "uid-2"], server.Deleted);

        // The local copies stay. Removing the server's is the whole point of keeping ours.
        Assert.Equal(2, repo.Messages(inbox.Id).Count);
    }

    [Fact]
    public async Task WithNoAgeSetNothingIsEverRemoved()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1");

        var day = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var policy = new Pop3Policy { LeaveOnServer = true };

        await new Pop3Receiver(repo, () => day) { SessionFactory = () => server }
            .PollAsync(Connection(policy), inbox, null, Ct);

        await new Pop3Receiver(repo, () => day.AddYears(5)) { SessionFactory = () => server }
            .PollAsync(Connection(policy), inbox, null, Ct);

        Assert.Empty(server.Deleted);
    }

    /// <summary>
    /// A message the arrival handler moves to Junk lands there rather than in the inbox, and is
    /// not fetched again on the next poll: it is known by having been seen, not by where it is.
    /// </summary>
    [Fact]
    public async Task JunkIsFiledIntoJunkAndNotReDownloaded()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var junk = repo.FolderWithRole(inbox.AccountId, FolderRole.Junk)!;

        var server = new FakePop3().With("uid-spam", "Cheap pills").With("uid-good", "Re: lunch");

        // Flag exactly the spam by subject.
        var handler = new SubjectJunk("Cheap pills");
        var receiver = new Pop3Receiver(repo) { SessionFactory = () => server, OnArrival = handler };

        var first = await receiver.PollAsync(Connection(), inbox, null, Ct);
        Assert.Equal(2, first.Downloaded);

        Assert.Equal("Re: lunch", Assert.Single(repo.Messages(inbox.Id)).Subject);
        Assert.Equal("Cheap pills", Assert.Single(repo.Messages(junk.Id)).Subject);

        // Only what stayed in the inbox is an arrival worth a toast.
        Assert.Equal([repo.Messages(inbox.Id)[0].Id], first.Arrived);

        // A second poll of the unchanged mailbox downloads nothing: both are known, one in the
        // inbox and one in Junk.
        var second = await new Pop3Receiver(repo) { SessionFactory = () => server, OnArrival = handler }
            .PollAsync(Connection(), inbox, null, Ct);

        Assert.Equal(0, second.Downloaded);
        Assert.Single(repo.Messages(junk.Id));
    }

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
    /// The trap this table exists for: with leave-on-server (the default) a message deleted here
    /// for good — Deleted Items emptied — must not come back as new mail on the next poll.
    /// </summary>
    [Fact]
    public async Task AMessageDeletedHereIsNotFetchedAgain()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var server = new FakePop3().With("uid-1", "Keep me").With("uid-2", "Bin me");
        await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);
        Assert.Equal(2, repo.Messages(inbox.Id).Count);

        var binned = repo.Messages(inbox.Id).Single(m => m.Subject == "Bin me");
        repo.DeleteMessage(binned.Id);
        Assert.Single(repo.Messages(inbox.Id));

        var again = await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);
        Assert.Equal(0, again.Downloaded);
        Assert.Equal(2, again.AlreadyHad);
        Assert.Equal("Keep me", Assert.Single(repo.Messages(inbox.Id)).Subject);
    }

    /// <summary>The seen list tracks the mailbox: what the server no longer lists is forgotten.</summary>
    [Fact]
    public async Task TheSeenListIsPrunedToWhatTheServerStillLists()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var server = new FakePop3().With("uid-1", "One").With("uid-2", "Two");
        await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);
        Assert.Equal(2, repo.SeenUidls().Count);

        // The server drops one — someone else collected it, or it expired there.
        var smaller = new FakePop3().With("uid-1", "One");
        await Receiver(repo, smaller).PollAsync(Connection(), inbox, null, Ct);
        Assert.Equal(["uid-1"], repo.SeenUidls().Order());
    }
}
