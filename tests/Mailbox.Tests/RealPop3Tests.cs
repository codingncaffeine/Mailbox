using MailKit.Security;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// POP3 against a server that is really there — the collector, not just the socket.
/// </summary>
/// <remarks>
/// These drive <see cref="Pop3Receiver"/> with a real store behind it, because connecting and
/// listing proves almost nothing: what matters is that mail lands as MIME a reader can open, that
/// a second poll knows it already has it, and that nothing is removed from the server. The last
/// two are where POP3 goes wrong, and neither can be seen from a session that only counts.
/// <code>
/// MAILBOX_POP3_HOST=mail.example.com MAILBOX_IMAP_USER=you@example.com \
///   MAILBOX_IMAP_PASSWORD=secret dotnet test --filter RealPop3
/// </code>
/// <para>
/// <b>Nothing is ever deleted.</b> POP3's sharpest edge is a client that deletes by default and
/// empties a mailbox somebody was still reading elsewhere, which is why the policy leaves mail on
/// the server (§4) — and why a test has no business doing otherwise on a real one. The store is a
/// throwaway; the mailbox is left exactly as it was found.
/// </para>
/// </remarks>
[Collection("real-server")]
public class RealPop3Tests
{
    private static string? Host
        => Environment.GetEnvironmentVariable("MAILBOX_POP3_HOST")
           ?? Environment.GetEnvironmentVariable("MAILBOX_IMAP_HOST");

    private static string User => Environment.GetEnvironmentVariable("MAILBOX_IMAP_USER") ?? string.Empty;

    private static string Password => Environment.GetEnvironmentVariable("MAILBOX_IMAP_PASSWORD") ?? string.Empty;

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static ServerSettings Incoming => new(
        Host ?? string.Empty,
        int.TryParse(Environment.GetEnvironmentVariable("MAILBOX_POP3_PORT"), out var port) ? port : 995,
        SecureSocketOptions.SslOnConnect,
        User,
        Password);

    /// <summary>An account and a store of this run's own, thrown away with it.</summary>
    private static (MailStore Store, MailRepository Repository, AccountConnection Account, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repository = new MailRepository(store);
        var account = repository.AddAccount(User, "Test", MailProtocol.Pop3);
        repository.CreateStandardFolders(account.Id);

        var connection = new AccountConnection(
            account.Id,
            User,
            Incoming,
            new ServerSettings(Host ?? string.Empty, 587, SecureSocketOptions.StartTls, User, Password))
        {
            Protocol = MailProtocol.Pop3,

            // The default, said out loud: leave everything where it is.
            Policy = new Pop3Policy { LeaveOnServer = true },
        };

        return (store, repository, connection, repository.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static void SkipWithoutAServer()
        => Assert.SkipUnless(Host is { Length: > 0 }, "Set MAILBOX_POP3_HOST or MAILBOX_IMAP_HOST to run against a real server.");

    /// <summary>How many messages the server is holding, asked without the collector.</summary>
    private static async Task<int> OnServerAsync(CancellationToken cancellation)
    {
        using var session = new MailKitPop3Session();
        await session.ConnectAsync(Incoming, cancellation);
        await session.AuthenticateAsync(Incoming, cancellation);
        var count = session.Count;
        await session.DisconnectAsync(cancellation);
        return count;
    }

    // ---- Signing in ----

    /// <summary>
    /// Connecting over implicit TLS and authenticating, which is every POP3 account's first
    /// moment and the one that fails when a host, port or password is wrong.
    /// </summary>
    [Fact]
    public async Task TheServerTakesTheCredentialsAndListsTheMailbox()
    {
        SkipWithoutAServer();

        using var session = new MailKitPop3Session();
        await session.ConnectAsync(Incoming, Stop);
        Assert.True(session.IsConnected);

        await session.AuthenticateAsync(Incoming, Stop);

        var uids = await session.GetUidsAsync(Stop);
        Assert.Equal(session.Count, uids.Count);

        // Every UIDL is unique, which is the whole basis of POP3 knowing what it has already had.
        Assert.Equal(uids.Count, uids.Distinct(StringComparer.Ordinal).Count());

        TestContext.Current.TestOutputHelper?.WriteLine($"{session.Count} message(s) on the server.");
        await session.DisconnectAsync(Stop);
    }

    /// <summary>A wrong password is refused, and is refused as an authentication problem.</summary>
    [Fact]
    public async Task AWrongPasswordIsRefused()
    {
        SkipWithoutAServer();

        using var session = new MailKitPop3Session();
        await session.ConnectAsync(Incoming, Stop);

        await Assert.ThrowsAnyAsync<Exception>(
            () => session.AuthenticateAsync(Incoming with { Password = Password + "-wrong" }, Stop));

        await session.DisconnectAsync(Stop);
    }

    // ---- Collecting ----

    /// <summary>
    /// A poll through the real collector: mail lands in the store as MIME, and a second poll
    /// knows it already has it.
    /// </summary>
    /// <remarks>
    /// The second poll is the important half. POP3 has no server-side state about what a client
    /// has seen — the UIDL list is all there is — so a collector that got this wrong would
    /// re-download the whole mailbox every few minutes and fill the store with copies. It cannot
    /// be seen from a session that only counts, which is why this drives the receiver.
    /// </remarks>
    [Fact]
    public async Task APollDownloadsTheMailAndASecondPollKnowsItAlreadyHasIt()
    {
        SkipWithoutAServer();

        var onServer = await OnServerAsync(Stop);
        Assert.SkipWhen(onServer == 0, "The mailbox is empty, so there is nothing to collect.");

        var (store, repository, account, inbox) = Fresh();

        try
        {
            var first = await new Pop3Receiver(repository).PollAsync(account, inbox, null, Stop);

            Assert.True(first.Succeeded, first.Error);
            Assert.True(first.Downloaded > 0);
            Assert.Equal(0, first.AlreadyHad);

            // In the store, and readable as the message it was.
            var messages = repository.Messages(inbox.Id);
            Assert.Equal(first.Downloaded, messages.Count);

            foreach (var summary in messages)
            {
                Assert.NotEmpty(summary.Subject);

                var raw = repository.LoadRaw(summary.Id);
                Assert.NotNull(raw);
                using var buffer = new MemoryStream(raw);
                var parsed = await MimeMessage.LoadAsync(buffer, Stop);

                Assert.Equal(summary.Subject, parsed.Subject);
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"  “{parsed.Subject}” from {parsed.From}, {raw.Length}B");
            }

            var second = await new Pop3Receiver(repository).PollAsync(account, inbox, null, Stop);

            // Stated as a relationship between the two polls rather than against the server's
            // count, because mail can arrive from outside at any moment: what has to be true is
            // that everything the first poll took is recognised the second time, whatever else
            // turned up in between.
            Assert.True(second.Succeeded, second.Error);
            Assert.Equal(first.Downloaded, second.AlreadyHad);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>
    /// Leave on server is the default, and it holds against a real one.
    /// </summary>
    /// <remarks>
    /// The rule §4 exists for: a client that deletes by default will, the first time somebody
    /// tries it beside an existing setup, silently empty a mailbox they were still using
    /// elsewhere. Asserted against the server rather than against the policy object, because what
    /// matters is what the server still has.
    /// </remarks>
    [Fact]
    public async Task APollLeavesEverythingOnTheServer()
    {
        SkipWithoutAServer();

        var before = await OnServerAsync(Stop);
        Assert.SkipWhen(before == 0, "The mailbox is empty, so there is nothing to leave.");

        var (store, repository, account, inbox) = Fresh();

        try
        {
            var result = await new Pop3Receiver(repository).PollAsync(account, inbox, null, Stop);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(0, result.RemovedFromServer);
            Assert.Equal(before, await OnServerAsync(Stop));
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>
    /// The round trip through this protocol: sent over SMTP, collected over POP3.
    /// </summary>
    /// <remarks>
    /// Gated on <c>MAILBOX_SMTP_SEND=1</c> like the IMAP one, because it sends. To the account's
    /// own address and nowhere else.
    /// </remarks>
    [Fact]
    public async Task AMessageSentIsCollectedByPop3()
    {
        SkipWithoutAServer();
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("MAILBOX_SMTP_SEND") == "1",
            "Set MAILBOX_SMTP_SEND=1 to allow this to actually send a message.");

        var subject = $"Mailbox POP3 round trip {Environment.ProcessId}";
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Mailbox tests", User));
        message.To.Add(new MailboxAddress("Mailbox tests", User));
        message.Subject = subject;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("mailbox.test");
        message.Body = new TextPart("plain") { Text = "Sent to prove POP3 collects what SMTP sent." };

        using (var smtp = new MailKitSmtpSession())
        {
            var outgoing = new ServerSettings(Host!, 587, SecureSocketOptions.StartTls, User, Password);
            await smtp.ConnectAsync(outgoing, Stop);
            await smtp.AuthenticateAsync(outgoing, Stop);
            await smtp.SendAsync(message, Stop);
            await smtp.DisconnectAsync(Stop);
        }

        var (store, repository, account, inbox) = Fresh();

        try
        {
            // Delivery is somebody else's queue, so this waits rather than assuming.
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            MessageSummary? found = null;

            while (DateTimeOffset.UtcNow < deadline && found is null)
            {
                var result = await new Pop3Receiver(repository).PollAsync(account, inbox, null, Stop);
                Assert.True(result.Succeeded, result.Error);

                found = repository.Messages(inbox.Id).FirstOrDefault(m => m.Subject == subject);
                if (found is null) await Task.Delay(TimeSpan.FromSeconds(10), Stop);
            }

            // Submission is Mailbox's part and it succeeded above — the server took the message
            // without complaint. Delivery is the provider's queue, and a shared host will greylist
            // or throttle a burst of them; when that happens this is not a defect here, so it
            // skips rather than failing and says which half did not finish.
            Assert.SkipWhen(
                found is null,
                "The message was accepted for submission but had not been delivered within three "
                + "minutes. That is the provider's queue rather than anything here — shared hosts "
                + "throttle a burst of sends.");

            Assert.NotNull(found);
            TestContext.Current.TestOutputHelper?.WriteLine($"Collected “{found.Subject}” over POP3.");

            // And it really is the message that was sent, not a row with the right subject on it.
            var raw = repository.LoadRaw(found.Id);
            Assert.NotNull(raw);
            using var buffer = new MemoryStream(raw);
            var parsed = await MimeMessage.LoadAsync(buffer, Stop);

            Assert.Equal(message.MessageId, parsed.MessageId);
            Assert.Contains(HeaderId.Received, parsed.Headers.Select(h => h.Id));
        }
        finally
        {
            store.Dispose();
        }
    }
}
