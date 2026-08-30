using MailKit.Net.Smtp;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

internal sealed class FakeSmtp : ISmtpSession
{
    public List<MimeMessage> Sent { get; } = [];
    public Queue<Exception> Failures { get; } = new();
    public bool IsConnected { get; private set; }

    /// <summary>What the server says it offers. Empty is a real configuration, not a fault.</summary>
    public HashSet<string> Advertises { get; init; } = ["PLAIN", "LOGIN"];

    IReadOnlySet<string> ISmtpSession.AuthenticationMechanisms => Advertises;
    public bool Authenticated { get; private set; }

    /// <summary>How many times a credential was offered. The probe must offer none.</summary>
    public int Authentications { get; private set; }

    /// <summary>Thrown instead of connecting, for the unreachable case.</summary>
    public Exception? FailOnConnect { get; init; }

    public Task ConnectAsync(ServerSettings s, CancellationToken c)
    {
        if (FailOnConnect is { } ex) throw ex;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task AuthenticateAsync(ServerSettings s, CancellationToken c)
    {
        Authenticated = true;
        Authentications++;
        return Task.CompletedTask;
    }

    public Task<string> SendAsync(MimeMessage message, CancellationToken c)
    {
        if (Failures.Count > 0) throw Failures.Dequeue();
        Sent.Add(message);

        // What a submission server's 250 looks like: an acknowledgement with a queue id in it.
        return Task.FromResult($"2.0.0 Ok: queued as FAKE{Sent.Count:D4}");
    }

    public Task DisconnectAsync(CancellationToken c)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

public class SmtpSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (MailStore Store, MailRepository Repo, long AccountId) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        return (store, repo, account.Id);
    }

    private static AccountConnection Connection(long id) => new(
        id, "you@example.com",
        new ServerSettings("pop.example.com", 995),
        new ServerSettings("smtp.example.com", 587, UserName: "you", Password: "secret"));

    private static MimeMessage Message(string subject = "Hello")
    {
        var m = new MimeMessage { Subject = subject };
        m.From.Add(new MailboxAddress("You", "you@example.com"));
        m.To.Add(new MailboxAddress("Alice", "alice@example.com"));
        m.Body = new TextPart("plain") { Text = "Body" };
        return m;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QueuedMailIsSentAndMarkedSent()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        var smtp = new FakeSmtp();
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        sender.Queue(id, Message(), Now);

        var sent = await sender.DrainAsync(Connection(id), Now, Ct);

        Assert.Equal(1, sent);
        Assert.Single(smtp.Sent);
        Assert.True(smtp.Authenticated);
        Assert.Equal(OutboxState.Sent, repo.Outbox(id).Single().State);
    }

    // ---- Sent Items -----------------------------------------------------------------------

    /// <summary>
    /// A message that went is filed in Sent Items, as the person who wrote it, already read.
    /// This once did not happen at all: the sender marked the row sent and stopped, and Sent
    /// Items stayed empty for as long as anyone used the application.
    /// </summary>
    [Fact]
    public async Task ASentMessageIsFiledInSentItems()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        repo.CreateStandardFolders(id);
        var sent = repo.FolderWithRole(id, FolderRole.Sent)!;

        var sender = new SmtpSender(repo) { SessionFactory = () => new FakeSmtp() };
        sender.Queue(id, Message("Filed"), Now);

        await sender.DrainAsync(Connection(id), Now, Ct);

        var filed = Assert.Single(repo.Messages(sent.Id));
        Assert.Equal("Filed", filed.Subject);
        Assert.True(filed.IsRead);
        Assert.Equal("you@example.com", filed.FromAddress);
    }

    /// <summary>The bytes filed are the bytes that went — the design's rule, and the only version that matches the recipient's.</summary>
    [Fact]
    public async Task WhatIsFiledIsWhatWent()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        repo.CreateStandardFolders(id);
        var sent = repo.FolderWithRole(id, FolderRole.Sent)!;

        var smtp = new FakeSmtp();
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        sender.Queue(id, Message("Same bytes"), Now);
        await sender.DrainAsync(Connection(id), Now, Ct);

        var filed = repo.LoadRaw(Assert.Single(repo.Messages(sent.Id)).Id)!;
        using var ms = new MemoryStream(filed);
        var reloaded = MimeMessage.Load(ms, Ct);

        Assert.Equal(smtp.Sent.Single().MessageId, reloaded.MessageId);
    }

    /// <summary>Off is a real preference — the reference offers it — and off files nothing.</summary>
    [Fact]
    public async Task FilingCanBeTurnedOff()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        repo.CreateStandardFolders(id);
        var sent = repo.FolderWithRole(id, FolderRole.Sent)!;

        var sender = new SmtpSender(repo) { SessionFactory = () => new FakeSmtp(), FileSentCopies = false };
        sender.Queue(id, Message(), Now);
        await sender.DrainAsync(Connection(id), Now, Ct);

        Assert.Empty(repo.Messages(sent.Id));
        Assert.Equal(OutboxState.Sent, repo.Outbox(id).Single().State);
    }

    /// <summary>A message that did not go is not filed as though it had.</summary>
    [Fact]
    public async Task ARejectedMessageIsNotFiled()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        repo.CreateStandardFolders(id);
        var sent = repo.FolderWithRole(id, FolderRole.Sent)!;

        var smtp = new FakeSmtp();
        smtp.Failures.Enqueue(new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable,
            new MailboxAddress("Alice", "alice@example.com"), "No such user"));
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        sender.Queue(id, Message(), Now);
        await sender.DrainAsync(Connection(id), Now, Ct);

        Assert.Empty(repo.Messages(sent.Id));
    }

    /// <summary>An account with no Sent folder — a bare test store — sends fine and files nowhere.</summary>
    [Fact]
    public async Task NoSentFolderIsNotAnError()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;

        var sender = new SmtpSender(repo) { SessionFactory = () => new FakeSmtp() };
        sender.Queue(id, Message(), Now);

        Assert.Equal(1, await sender.DrainAsync(Connection(id), Now, Ct));
    }

    /// <summary>
    /// Draining twice must not send twice. A timer calls this, and a message delivered again
    /// every minute is worse than one not delivered at all.
    /// </summary>
    [Fact]
    public async Task DrainingAgainDoesNotResend()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        var smtp = new FakeSmtp();
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        sender.Queue(id, Message(), Now);

        await sender.DrainAsync(Connection(id), Now, Ct);
        await sender.DrainAsync(Connection(id), Now.AddHours(1), Ct);

        Assert.Single(smtp.Sent);
    }

    /// <summary>A temporary failure keeps the message and schedules another attempt.</summary>
    [Fact]
    public async Task ATemporaryFailureIsRetriedLater()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        var smtp = new FakeSmtp();
        smtp.Failures.Enqueue(new System.Net.Sockets.SocketException());
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        sender.Queue(id, Message(), Now);

        await sender.DrainAsync(Connection(id), Now, Ct);
        var afterFailure = repo.Outbox(id).Single();

        Assert.Equal(OutboxState.Queued, afterFailure.State);
        Assert.Equal(1, afterFailure.Attempts);
        Assert.Equal(Now + SmtpSender.Backoff[0], afterFailure.NextTry);
        Assert.Contains("Could not reach", afterFailure.LastError);

        // Not due yet, so nothing happens; due, and it goes.
        Assert.Equal(0, await sender.DrainAsync(Connection(id), Now, Ct));
        Assert.Equal(1, await sender.DrainAsync(Connection(id), Now + SmtpSender.Backoff[0], Ct));
    }

    /// <summary>
    /// A rejection is permanent. Retrying a bad address forever is a way of never telling the
    /// user their mail did not go.
    /// </summary>
    [Fact]
    public async Task ARejectionFailsImmediatelyRatherThanRetrying()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        var smtp = new FakeSmtp();
        smtp.Failures.Enqueue(new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable,
            new MailboxAddress("Alice", "alice@example.com"), "No such user"));
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        sender.Queue(id, Message(), Now);

        await sender.DrainAsync(Connection(id), Now, Ct);
        var item = repo.Outbox(id).Single();

        Assert.Equal(OutboxState.Failed, item.State);
        Assert.Null(item.NextTry);
        Assert.Contains("alice@example.com", item.LastError);
    }

    [Fact]
    public async Task RetriesGiveUpEventuallyRatherThanForever()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        var smtp = new FakeSmtp();
        for (var i = 0; i < 10; i++) smtp.Failures.Enqueue(new TimeoutException());
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        sender.Queue(id, Message(), Now);

        var at = Now;
        for (var attempt = 0; attempt < SmtpSender.Backoff.Length; attempt++)
        {
            await sender.DrainAsync(Connection(id), at, Ct);
            at = at.AddHours(2);
        }

        Assert.Equal(OutboxState.Failed, repo.Outbox(id).Single().State);
    }

    /// <summary>Work Offline holds the queue; going back online releases it.</summary>
    [Fact]
    public async Task HeldMailIsNotSentUntilReleased()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        var smtp = new FakeSmtp();
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        sender.Queue(id, Message(), Now);
        repo.HoldOutbox(id);

        Assert.Equal(0, await sender.DrainAsync(Connection(id), Now, Ct));
        Assert.Empty(smtp.Sent);

        repo.ReleaseOutbox(id);

        Assert.Equal(1, await sender.DrainAsync(Connection(id), Now, Ct));
    }

    /// <summary>
    /// An item left mid-send by a process that died must be picked up again, not stranded.
    /// </summary>
    [Fact]
    public async Task MailStrandedMidSendIsPickedUpOnTheNextDrain()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        var smtp = new FakeSmtp();
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        var outboxId = sender.Queue(id, Message(), Now);
        repo.SetOutboxState(outboxId, OutboxState.Sending);   // as if the process died here

        Assert.Equal(1, await sender.DrainAsync(Connection(id), Now, Ct));
    }

    [Fact]
    public void QueueingKeepsTheMessageByteForByte()
    {
        var (store, repo, id) = Fresh();
        using var _ = store;
        var sender = new SmtpSender(repo);
        sender.Queue(id, Message("Byte for byte"), Now);

        var raw = repo.LoadBlob(repo.Outbox(id).Single().BlobId)!;

        Assert.Contains("Byte for byte", System.Text.Encoding.UTF8.GetString(raw));
    }

    [Theory]
    [InlineData(typeof(TimeoutException), true)]
    [InlineData(typeof(System.Net.Sockets.SocketException), true)]
    public void TransientProblemsAreWorthRetrying(Type exception, bool expected)
    {
        var result = SmtpSender.Classify((Exception)Activator.CreateInstance(exception)!);
        Assert.Equal(expected, result.WorthRetrying);
        Assert.False(result.Sent);
    }
}
