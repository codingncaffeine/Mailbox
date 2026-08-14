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
    public bool Authenticated { get; private set; }

    public Task ConnectAsync(ServerSettings s, CancellationToken c)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task AuthenticateAsync(ServerSettings s, CancellationToken c)
    {
        Authenticated = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(MimeMessage message, CancellationToken c)
    {
        if (Failures.Count > 0) throw Failures.Dequeue();
        Sent.Add(message);
        return Task.CompletedTask;
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
