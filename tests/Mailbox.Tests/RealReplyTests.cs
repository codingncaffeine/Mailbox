using MailKit.Security;
using Mailbox.Protocols;
using Mailbox.Rendering;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Replying to a message that really arrived, over both collectors.
/// </summary>
/// <remarks>
/// The last thing a fixture cannot produce. Everything up to here has replied to messages this
/// project wrote itself, which agree with it about encodings, headers and structure by
/// construction. A message from a real correspondent's real client does not: it has a
/// multipart/alternative that somebody else laid out, a Message-ID somebody else minted, its own
/// References chain, and whatever a transfer agent did to it on the way.
/// <para>
/// <b>These send to whoever wrote the message being replied to</b>, which is the one thing the
/// other real-server tests refuse to do — so they are gated on <c>MAILBOX_REPLY_TO_REAL_MAIL=1</c>
/// as well as on the usual two, and they reply only to a message already sitting in the mailbox.
/// Nothing here can start a conversation with anybody.
/// </para>
/// <code>
/// MAILBOX_IMAP_HOST=mail.example.com MAILBOX_IMAP_USER=you@example.com \
///   MAILBOX_IMAP_PASSWORD=secret MAILBOX_SMTP_SEND=1 MAILBOX_REPLY_TO_REAL_MAIL=1 \
///   dotnet test --filter RealReply
/// </code>
/// </remarks>
[Collection("real-server")]
public class RealReplyTests
{
    private static string? Host => Environment.GetEnvironmentVariable("MAILBOX_IMAP_HOST");

    private static string User => Environment.GetEnvironmentVariable("MAILBOX_IMAP_USER") ?? string.Empty;

    private static string Password => Environment.GetEnvironmentVariable("MAILBOX_IMAP_PASSWORD") ?? string.Empty;

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static ServerSettings Imap => new(
        Host ?? string.Empty, 993, SecureSocketOptions.SslOnConnect, User, Password);

    private static ServerSettings Pop => new(
        Environment.GetEnvironmentVariable("MAILBOX_POP3_HOST") ?? Host ?? string.Empty,
        995, SecureSocketOptions.SslOnConnect, User, Password);

    private static ServerSettings Smtp => new(
        Environment.GetEnvironmentVariable("MAILBOX_SMTP_HOST") ?? Host ?? string.Empty,
        587, SecureSocketOptions.StartTls, User, Password);

    private static void SkipUnlessAllowed()
    {
        Assert.SkipUnless(Host is { Length: > 0 }, "Set MAILBOX_IMAP_HOST to run against a real server.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("MAILBOX_SMTP_SEND") == "1",
            "Set MAILBOX_SMTP_SEND=1 to allow these to send.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("MAILBOX_REPLY_TO_REAL_MAIL") == "1",
            "Set MAILBOX_REPLY_TO_REAL_MAIL=1 to allow a reply to somebody who is not this account.");
    }

    /// <summary>
    /// A message in the mailbox that came from somewhere else.
    /// </summary>
    /// <remarks>
    /// Mail this project sent itself is skipped, because replying to it would prove nothing that
    /// the round-trip tests have not already: the whole point is a message written by another
    /// client.
    /// </remarks>
    private static bool FromOutside(MimeMessage message)
        => message.From.Mailboxes.Any(m => !string.Equals(m.Address, User, StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the reply this application would build, and puts it on the wire.</summary>
    private static async Task<MimeMessage> ReplyToAsync(MimeMessage original, string over, CancellationToken cancellation)
    {
        // The real builder, so what is tested is what the compose window would produce: the
        // recipients it picks, the subject prefix it applies, and the threading headers.
        var draft = Reply.Build(original, ReplyKind.Reply, new ReplyOptions { OwnAddresses = [User] });

        Assert.NotEmpty(draft.To);
        Assert.StartsWith("RE:", draft.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.MessageId, draft.InReplyTo);
        Assert.Contains(original.MessageId, draft.References);

        var reply = new MimeMessage();
        reply.From.Add(new MailboxAddress("Mailbox tests", User));
        foreach (var to in draft.To) reply.To.Add(MailboxAddress.Parse(to));
        foreach (var cc in draft.Cc) reply.Cc.Add(MailboxAddress.Parse(cc));

        reply.Subject = draft.Subject;
        reply.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("mailbox.test");

        // The headers that make a reply a reply rather than a new message with a similar subject.
        reply.InReplyTo = draft.InReplyTo;
        foreach (var reference in draft.References) reply.References.Add(reference);

        reply.Body = new TextPart("plain")
        {
            Text = $"This is an automated reply from Mailbox's own tests, sent after collecting "
                   + $"your message over {over}. Threading, quoting and the send path are what it "
                   + $"is checking; no answer is needed.\r\n\r\n{draft.QuotedText}",
        };

        using (var smtp = new MailKitSmtpSession())
        {
            await smtp.ConnectAsync(Smtp, cancellation);
            await smtp.AuthenticateAsync(Smtp, cancellation);
            await smtp.SendAsync(reply, cancellation);
            await smtp.DisconnectAsync(cancellation);
        }

        return reply;
    }

    /// <summary>Replying to a real message collected over IMAP.</summary>
    [Fact]
    public async Task AMessageCollectedOverImapCanBeRepliedTo()
    {
        SkipUnlessAllowed();

        MimeMessage? original = null;

        using (var session = new MailKitImapSession())
        {
            await session.ConnectAsync(Imap, Stop);
            await session.AuthenticateAsync(Imap, Stop);

            var folders = await session.ListFoldersAsync(Stop);
            await session.OpenAsync(folders.First(f => f.Role == FolderRole.Inbox).Path, Stop);

            foreach (var uid in (await session.SearchAllAsync(Stop)).Reverse())
            {
                var candidate = await session.GetMessageAsync(uid, Stop);
                if (candidate is not null && FromOutside(candidate)) { original = candidate; break; }
            }

            await session.DisconnectAsync(Stop);
        }

        Assert.SkipWhen(original is null, "No message from outside this account is in the mailbox to reply to.");

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Replying over IMAP to “{original.Subject}” from {original.From}");

        var reply = await ReplyToAsync(original, "IMAP", Stop);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"  sent {reply.MessageId} to {reply.To}, in-reply-to {reply.InReplyTo}");
    }

    /// <summary>
    /// The same over POP3, which is a different collector and hands back the same MIME.
    /// </summary>
    /// <remarks>
    /// Worth doing separately rather than assuming: POP3 has no folders and no flags, and what it
    /// gives a client is the whole message and nothing else. If the two collectors disagreed about
    /// the bytes, a reply built from one would thread and a reply built from the other would not.
    /// </remarks>
    [Fact]
    public async Task AMessageCollectedOverPop3CanBeRepliedTo()
    {
        SkipUnlessAllowed();

        MimeMessage? original = null;

        using (var pop = new MailKitPop3Session())
        {
            await pop.ConnectAsync(Pop, Stop);
            await pop.AuthenticateAsync(Pop, Stop);

            for (var i = pop.Count - 1; i >= 0 && original is null; i--)
            {
                var candidate = await pop.GetMessageAsync(i, Stop);
                if (FromOutside(candidate)) original = candidate;
            }

            await pop.DisconnectAsync(Stop);
        }

        Assert.SkipWhen(original is null, "No message from outside this account is in the mailbox to reply to.");

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Replying over POP3 to “{original.Subject}” from {original.From}");

        var reply = await ReplyToAsync(original, "POP3", Stop);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"  sent {reply.MessageId} to {reply.To}, in-reply-to {reply.InReplyTo}");
    }

    /// <summary>
    /// Both collectors hand back the same message, byte for byte.
    /// </summary>
    /// <remarks>
    /// Sends nothing, so it needs only a server. It is the claim the two tests above rest on: a
    /// reply is built from the message, and if IMAP and POP3 disagreed about what the message is
    /// then everything downstream — quoting, threading, signature checking — could differ by which
    /// way the mail happened to arrive.
    /// </remarks>
    [Fact]
    public async Task BothCollectorsHandBackTheSameBytes()
    {
        Assert.SkipUnless(Host is { Length: > 0 }, "Set MAILBOX_IMAP_HOST to run against a real server.");

        var byPop = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        using (var pop = new MailKitPop3Session())
        {
            await pop.ConnectAsync(Pop, Stop);
            await pop.AuthenticateAsync(Pop, Stop);

            for (var i = 0; i < pop.Count; i++)
            {
                var message = await pop.GetMessageAsync(i, Stop);
                if (message.MessageId is not { Length: > 0 } id) continue;

                using var buffer = new MemoryStream();
                await message.WriteToAsync(buffer, Stop);
                byPop[id] = buffer.ToArray();
            }

            await pop.DisconnectAsync(Stop);
        }

        Assert.SkipWhen(byPop.Count == 0, "The mailbox is empty, so there is nothing to compare.");

        var compared = 0;

        using (var session = new MailKitImapSession())
        {
            await session.ConnectAsync(Imap, Stop);
            await session.AuthenticateAsync(Imap, Stop);

            var folders = await session.ListFoldersAsync(Stop);
            await session.OpenAsync(folders.First(f => f.Role == FolderRole.Inbox).Path, Stop);

            foreach (var uid in await session.SearchAllAsync(Stop))
            {
                var message = await session.GetMessageAsync(uid, Stop);
                if (message?.MessageId is not { Length: > 0 } id) continue;
                if (!byPop.TryGetValue(id, out var theirs)) continue;

                using var buffer = new MemoryStream();
                await message.WriteToAsync(buffer, Stop);

                Assert.Equal(theirs, buffer.ToArray());
                compared++;
            }

            await session.DisconnectAsync(Stop);
        }

        Assert.True(compared > 0, "No message was in both collectors' answers to compare.");
        TestContext.Current.TestOutputHelper?.WriteLine($"{compared} message(s) identical over both collectors.");
    }
}
