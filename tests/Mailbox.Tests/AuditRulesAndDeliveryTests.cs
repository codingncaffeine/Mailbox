using MailKit;
using Mailbox.Core.Rules;
using Mailbox.Protocols;
using Mailbox.Store;
using MailStore = Mailbox.Store.MailStore;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Three contracts the audit's rules and send/receive sweep found nothing holding: which side
/// runs a server-side rule, whether a message asked to go later really waits, and which side wins
/// when both have changed the same message.
/// </summary>
/// <remarks>
/// All three are written down in the code and were until now believed rather than checked.
/// <see cref="RulesHandler"/> skips a rule marked for the server only while the server has the
/// current script — otherwise the rule would stop working the moment a publish failed;
/// <c>ScheduleOutbox</c> is kept apart from <c>DeferOutbox</c> so delayed delivery does not spend
/// a retry budget waiting; and <see cref="ImapSynchronizer"/> plays the journal before it pulls,
/// which is the whole of what "the store is authoritative" means in practice.
/// </remarks>
public class AuditRulesAndDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static (MailStore Store, MailRepository Repo, long AccountId) Fresh(MailProtocol protocol)
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", protocol);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, account.Id);
    }

    private static MimeMessage From(string address, string subject)
    {
        var message = new MimeMessage { Subject = subject, Date = Now };
        message.From.Add(new MailboxAddress("A. Person", address));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.Body = new TextPart("plain") { Text = "Body." };
        return message;
    }

    // ---- The client/server split -------------------------------------------------------------

    /// <summary>
    /// A rule marked "run on the mail server" is the server's while the server has the current
    /// script, and this computer's again the moment it does not.
    /// </summary>
    /// <remarks>
    /// The failure this catches is silent in both directions: a rule that runs on both sides files
    /// a message twice, and one that runs on neither leaves a reader wondering why their filing
    /// stopped after a publish failed.
    /// </remarks>
    [Fact]
    public void AServerSideRuleRunsHereOnlyWhileTheServerIsBehind()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Imap);
        using var _ = store;

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var receipts = repo.AddFolder(accountId, "Receipts", FolderRole.None, inbox.Id, "INBOX/Receipts");

        repo.AddRule(new MailRule
        {
            Name = "Receipts",
            ServerSide = true,
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["@shop.example"] }],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = receipts.Id, FolderName = "Receipts" }],
        }, Now);

        var rules = new RulesHandler(() => Now);

        long Arrive(string subject)
        {
            var message = From("orders@shop.example", subject);
            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();
            var id = repo.AddMessage(inbox.Id, MessageMapper.ToSummary(message, null, raw.Length, Now), raw)!.Value;
            rules.Handle(repo, inbox, id, message);
            return id;
        }

        // Nothing published yet: the server cannot be running it, so this computer does.
        Assert.False(repo.ServerRulesCurrent());
        var here = Arrive("Ran here");
        Assert.Equal(receipts.Id, repo.GetMessage(here)!.FolderId);

        // Published: the rule has already run by the time the message arrives, so running it
        // again here would file a message the server has already filed.
        repo.SetSieveState("# script", null, Now);
        Assert.True(repo.ServerRulesCurrent());
        var theirs = Arrive("Left to the server");
        Assert.Equal(inbox.Id, repo.GetMessage(theirs)!.FolderId);

        // The server is behind — a rule was edited, or a publish failed. It runs here again.
        repo.MarkSieveStale();
        Assert.False(repo.ServerRulesCurrent());
        var again = Arrive("Ran here again");
        Assert.Equal(receipts.Id, repo.GetMessage(again)!.FolderId);
    }

    /// <summary>
    /// A rule that is switched off never runs on arrival, whichever side it is marked for.
    /// </summary>
    [Fact]
    public void ADisabledServerSideRuleRunsNowhere()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Imap);
        using var _ = store;

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var receipts = repo.AddFolder(accountId, "Receipts", FolderRole.None, inbox.Id, "INBOX/Receipts");

        repo.AddRule(new MailRule
        {
            Name = "Receipts",
            Enabled = false,
            ServerSide = true,
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["@shop.example"] }],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = receipts.Id, FolderName = "Receipts" }],
        }, Now);

        var message = From("orders@shop.example", "Still in the Inbox");
        using var buffer = new MemoryStream();
        message.WriteTo(buffer, Ct);
        var raw = buffer.ToArray();
        var id = repo.AddMessage(inbox.Id, MessageMapper.ToSummary(message, null, raw.Length, Now), raw)!.Value;

        new RulesHandler(() => Now).Handle(repo, inbox, id, message);

        Assert.Equal(inbox.Id, repo.GetMessage(id)!.FolderId);
    }

    // ---- What a failed account tells the reader ------------------------------------------------

    /// <summary>
    /// A refused sign-in and a refused certificate reach the run's result as sentences a reader
    /// can act on, naming the account — not as the exception's own words.
    /// </summary>
    /// <remarks>
    /// This is the text the Send/Receive Progress dialog's Errors tab shows and the only thing a
    /// reader is given when a poll fails. "RemoteCertificateNameMismatch" in that box is a support
    /// call; the sentences below are a fix. Nothing held the wording, and it is the kind of string
    /// that gets replaced by <c>ex.Message</c> in a hurry and never noticed.
    /// </remarks>
    [Fact]
    public async Task ARefusedSignInAndARefusedCertificateAreSaidInWords()
    {
        static async Task<string> ErrorFrom(Exception fault)
        {
            using var store = MailStore.Transient();
            var repo = new MailRepository(store);
            var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
            repo.CreateStandardFolders(account.Id);

            var server = new FakePop3 { FailOnConnect = fault };
            var service = new SendReceiveService(
                mail => new Pop3Receiver(mail) { SessionFactory = () => server },
                mail => new SmtpSender(mail) { SessionFactory = () => new FakeSmtp() });

            var target = new TransferTarget(
                new AccountConnection(
                    account.Id, "you@example.com",
                    new ServerSettings("pop.example.com", 995),
                    new ServerSettings("smtp.example.com", 587)),
                repo);

            var result = await service.RunAsync([target], Now, TestContext.Current.CancellationToken);
            return result.Accounts.Single().Error ?? string.Empty;
        }

        Assert.Equal(
            "The server rejected the username or password.",
            await ErrorFrom(new MailKit.Security.AuthenticationException("535 5.7.8")));

        var tls = await ErrorFrom(new MailKit.Security.SslHandshakeException("handshake"));
        Assert.StartsWith("The secure connection could not be established.", tls);
        Assert.Contains("certificate may not be trusted", tls);

        Assert.Equal(
            "Could not reach the server. Check the address, the port, and the network.",
            await ErrorFrom(new System.Net.Sockets.SocketException()));

        Assert.Equal("The server did not answer in time.", await ErrorFrom(new TimeoutException()));
    }

    // ---- Delayed delivery, through a real drain ------------------------------------------------

    /// <summary>
    /// A message asked to go later is left alone by the send/receive that happens before its time
    /// and goes on the next one after it, without a retry being counted against it.
    /// </summary>
    /// <remarks>
    /// The store's own contract is held by <c>AHeldMessageIsNotDueYet</c>, which asks
    /// <c>DueOutbox</c>; this asks the sender, which is what actually decides whether anything
    /// leaves. The attempts count is part of the claim: <c>ScheduleOutbox</c> exists separately
    /// from <c>DeferOutbox</c> so a message waiting until Thursday does not spend its retry budget
    /// getting there, and nothing else checks that.
    /// </remarks>
    [Fact]
    public async Task ADelayedMessageIsSkippedUntilItsTimeAndThenGoesWithoutSpendingARetry()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Pop3);
        using var _ = store;

        var smtp = new FakeSmtp();
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };

        var message = new MimeMessage { Subject = "Thursday", Date = Now };
        message.From.Add(new MailboxAddress("You", "you@example.com"));
        message.To.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.Body = new TextPart("plain") { Text = "Later." };

        var outboxId = sender.Queue(accountId, message, Now);
        repo.ScheduleOutbox(outboxId, Now.AddHours(48));

        var connection = new AccountConnection(
            accountId, "you@example.com",
            new ServerSettings("pop.example.com", 995),
            new ServerSettings("smtp.example.com", 587));

        // Before its time: nothing goes, the row is still queued, and no attempt is recorded.
        Assert.Equal(0, await sender.DrainAsync(connection, Now.AddHours(1), Ct));
        Assert.Empty(smtp.Sent);
        var waiting = repo.Outbox(accountId).Single();
        Assert.Equal(OutboxState.Queued, waiting.State);
        Assert.Equal(0, waiting.Attempts);
        Assert.Null(waiting.LastError);

        // After it: it goes, exactly once.
        Assert.Equal(1, await sender.DrainAsync(connection, Now.AddHours(49), Ct));
        Assert.Equal("Thursday", Assert.Single(smtp.Sent).Subject);
        Assert.Equal(OutboxState.Sent, repo.Outbox(accountId).Single().State);
    }

    // ---- The journal against a conflicting server --------------------------------------------

    private static AccountConnection Connection() => new(
        1, "you@example.com",
        new ServerSettings("imap.example.com", 993),
        new ServerSettings("smtp.example.com", 587))
    {
        Protocol = MailProtocol.Imap,
    };

    /// <summary>
    /// Both sides changed the same message between syncs. The local change is played first, so it
    /// is on the server before the pull reads the server back — and the reader's own action is
    /// what survives.
    /// </summary>
    /// <remarks>
    /// Pulling first would read the server's older flags over the local ones and then play a
    /// journal entry against a row that had already been overwritten: the message would flicker
    /// back to unread and the reader would be told their own click did not happen. The ordering in
    /// <c>SyncAsync</c> is what prevents that, and this is the test that holds it there.
    /// </remarks>
    [Fact]
    public async Task ALocalChangeIsPlayedBeforeThePullSoItWinsAConflict()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Imap);
        using var _ = store;

        var server = new FakeImap();
        var delivered = server.Deliver("INBOX", "Both sides touched this");
        var sync = new ImapSynchronizer(repo, () => Now) { SessionFactory = () => server };

        await sync.SyncAsync(Connection(), null, Ct);

        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var message = repo.Messages(inbox.Id).Single();
        Assert.False(message.IsRead);

        // Here: read. There, at the same time: flagged, and still unread as far as the server
        // knows — which is the disagreement.
        repo.SetRead(message.Id, true);
        delivered.Flags |= MessageFlags.Flagged;

        var result = await sync.SyncAsync(Connection(), null, Ct);

        // Played, not merely queued: the server was told before it was read.
        Assert.Equal(1, result.OpsPlayed);
        Assert.Contains(server.FlagStores, s => s.Uid == delivered.Uid && s.Flag == MessageFlags.Seen && s.Set);
        Assert.Empty(repo.PendingOps());

        // Both changes stand: the reader's read mark survived the pull, and the server's flag
        // arrived. Nothing was lost in either direction and nothing was downloaded twice.
        var after = repo.Messages(inbox.Id).Single();
        Assert.True(after.IsRead);
        Assert.True(after.IsFlagged);
        Assert.True(server.Contents("INBOX").Single().Flags.HasFlag(MessageFlags.Seen));
    }
}
