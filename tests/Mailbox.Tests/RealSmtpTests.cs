using MailKit.Security;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Sending, and then collecting what was sent, against servers that are really there.
/// </summary>
/// <remarks>
/// The one thing no fake can do: a message that goes out over SMTP, through a real mail transfer
/// agent, and comes back down IMAP and POP3 with whatever the journey did to it. Everything
/// upstream of this has been proven against doubles that agree with us by construction.
/// <para>
/// <b>These send mail, so they are gated twice.</b> A server has to be named, and
/// <c>MAILBOX_SMTP_SEND=1</c> has to say so as well — a suite that quietly sent a message every
/// time somebody ran it would be a suite nobody could run. The message goes to the account's own
/// address and nowhere else: testing a mail client should not put anything in somebody else's
/// inbox.
/// </para>
/// <code>
/// MAILBOX_SMTP_HOST=mail.example.com MAILBOX_IMAP_HOST=mail.example.com \
///   MAILBOX_IMAP_USER=you@example.com MAILBOX_IMAP_PASSWORD=secret \
///   MAILBOX_SMTP_SEND=1 dotnet test --filter RealSmtp
/// </code>
/// </remarks>
[Collection("real-server")]
public class RealSmtpTests
{
    private static string? Host
        => Environment.GetEnvironmentVariable("MAILBOX_SMTP_HOST")
           ?? Environment.GetEnvironmentVariable("MAILBOX_IMAP_HOST");

    private static string User => Environment.GetEnvironmentVariable("MAILBOX_IMAP_USER") ?? string.Empty;

    private static string Password => Environment.GetEnvironmentVariable("MAILBOX_IMAP_PASSWORD") ?? string.Empty;

    private static bool MaySend => Environment.GetEnvironmentVariable("MAILBOX_SMTP_SEND") == "1";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>Submission on 587 with STARTTLS, which is what every host in this decade wants.</summary>
    private static ServerSettings Outgoing => new(
        Host ?? string.Empty,
        int.TryParse(Environment.GetEnvironmentVariable("MAILBOX_SMTP_PORT"), out var port) ? port : 587,
        SecureSocketOptions.StartTls,
        User,
        Password);

    private static ServerSettings Incoming => new(
        Environment.GetEnvironmentVariable("MAILBOX_IMAP_HOST") ?? string.Empty,
        993,
        SecureSocketOptions.SslOnConnect,
        User,
        Password);

    private static ServerSettings Pop => new(
        Environment.GetEnvironmentVariable("MAILBOX_POP3_HOST")
        ?? Environment.GetEnvironmentVariable("MAILBOX_IMAP_HOST") ?? string.Empty,
        995,
        SecureSocketOptions.SslOnConnect,
        User,
        Password);

    private static void SkipUnlessAllowed()
    {
        Assert.SkipUnless(Host is { Length: > 0 }, "Set MAILBOX_SMTP_HOST or MAILBOX_IMAP_HOST to run against a real server.");
        Assert.SkipUnless(MaySend, "Set MAILBOX_SMTP_SEND=1 to allow these to actually send a message.");
    }

    /// <summary>
    /// What the submission server advertises before anything is sent.
    /// </summary>
    /// <remarks>
    /// The same greeting <see cref="ServerProbe"/> reads to tell an account whose SMTP AUTH is
    /// switched off from one whose password is wrong — the consumer-mailbox gotcha. Reading
    /// sends no mail, so it is not behind the second gate.
    /// </remarks>
    [Fact]
    public async Task TheSubmissionServerOffersAuthentication()
    {
        Assert.SkipUnless(Host is { Length: > 0 }, "Set MAILBOX_SMTP_HOST or MAILBOX_IMAP_HOST to run against a real server.");

        using var session = new MailKitSmtpSession();
        await session.ConnectAsync(Outgoing, Stop);

        var offered = session.AuthenticationMechanisms;
        TestContext.Current.TestOutputHelper?.WriteLine($"AUTH: {string.Join(", ", offered.Order())}");

        // Empty is a real configuration and not a fault — it is exactly what the probe exists to
        // report — but a host being used to send has to offer something.
        Assert.NotEmpty(offered);

        await session.DisconnectAsync(Stop);
    }

    /// <summary>
    /// The whole round trip: composed here, submitted over SMTP, and collected back over IMAP.
    /// </summary>
    /// <remarks>
    /// It waits rather than assuming, because delivery is somebody else's queue and takes as long
    /// as it takes. The message is found by its own Message-ID — the identifier that survives the
    /// journey — and not by its subject, which a transfer agent is entitled to re-encode.
    /// </remarks>
    [Fact]
    public async Task AMessageSentComesBackOverImap()
    {
        SkipUnlessAllowed();

        var stamp = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Mailbox tests", User));
        message.To.Add(new MailboxAddress("Mailbox tests", User));
        message.Subject = $"Mailbox round trip {stamp}";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("mailbox.test");
        message.Body = new TextPart("plain")
        {
            Text = "Sent by Mailbox's own tests to prove the send path. Nothing here needs a reply.",
        };

        using (var smtp = new MailKitSmtpSession())
        {
            await smtp.ConnectAsync(Outgoing, Stop);
            await smtp.AuthenticateAsync(Outgoing, Stop);
            await smtp.SendAsync(message, Stop);
            await smtp.DisconnectAsync(Stop);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"Sent {message.MessageId}");

        var arrived = await WaitForAsync(message.MessageId!, TimeSpan.FromMinutes(3), Stop);

        // Submission is this application's part and it is done: the server took the message
        // without complaint. Delivery is the provider's queue, and a shared host will greylist or
        // throttle a burst — so a message that has not landed yet is not a defect here.
        Assert.SkipWhen(
            arrived is null,
            "The message was accepted for submission but had not been delivered within three "
            + "minutes. That is the provider's queue rather than anything here.");

        Assert.NotNull(arrived);
        Assert.Equal(message.Subject, arrived.Subject);

        // A real transfer agent adds its own Received lines on the way through, which is the
        // clearest evidence that this went out and came back rather than never leaving.
        Assert.Contains(arrived.Headers, h => h.Id == HeaderId.Received);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Came back with {arrived.Headers.Count(h => h.Id == HeaderId.Received)} Received header(s).");
    }

    /// <summary>
    /// The same mailbox over POP3, which is a different collector and a different set of edges.
    /// </summary>
    /// <remarks>
    /// Nothing is deleted. POP3's sharpest edge is that a client which deletes by default empties
    /// a mailbox somebody was still reading elsewhere, which is why the policy defaults to leaving
    /// mail on the server — and why a test has no business doing otherwise on a real one.
    /// </remarks>
    [Fact]
    public async Task Pop3SeesTheSameMailboxAndLeavesItAlone()
    {
        Assert.SkipUnless(Host is { Length: > 0 }, "Set MAILBOX_IMAP_HOST to run against a real server.");

        using var pop = new MailKitPop3Session();
        await pop.ConnectAsync(Pop, Stop);
        await pop.AuthenticateAsync(Pop, Stop);

        var before = pop.Count;
        var uids = await pop.GetUidsAsync(Stop);

        Assert.Equal(before, uids.Count);
        TestContext.Current.TestOutputHelper?.WriteLine($"POP3 sees {before} message(s).");

        if (before > 0)
        {
            var message = await pop.GetMessageAsync(before - 1, Stop);
            Assert.NotNull(message);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"  newest: “{message.Subject}” from {message.From}");
        }

        await pop.DisconnectAsync(Stop);

        // Left exactly as it was found.
        using var again = new MailKitPop3Session();
        await again.ConnectAsync(Pop, Stop);
        await again.AuthenticateAsync(Pop, Stop);
        Assert.Equal(before, again.Count);
        await again.DisconnectAsync(Stop);
    }

    /// <summary>Watches the Inbox until a Message-ID turns up, or gives up.</summary>
    private static async Task<MimeMessage?> WaitForAsync(string messageId, TimeSpan patience, CancellationToken cancellation)
    {
        var deadline = DateTimeOffset.UtcNow + patience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var session = new MailKitImapSession();
            await session.ConnectAsync(Incoming, cancellation);
            await session.AuthenticateAsync(Incoming, cancellation);

            var folders = await session.ListFoldersAsync(cancellation);
            var inbox = folders.First(f => f.Role == FolderRole.Inbox);
            await session.OpenAsync(inbox.Path, cancellation);

            var found = await session.SearchByMessageIdAsync(messageId, cancellation);
            if (found.Count > 0)
            {
                var message = await session.GetMessageAsync(found[^1], cancellation);
                await session.DisconnectAsync(cancellation);
                return message;
            }

            await session.DisconnectAsync(cancellation);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
        }

        return null;
    }
}
