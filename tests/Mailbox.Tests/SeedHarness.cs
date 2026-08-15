using Mailbox.Core.Settings;
using Mailbox.Store;
using MimeKit;
using MimeKit.Utils;

namespace Mailbox.Tests;

/// <summary>
/// Writes a populated multi-account directory for looking at by hand. Skipped in an ordinary
/// run; set MAILBOX_SEED to a directory to produce one.
/// </summary>
/// <remarks>
/// Each message is written as real MIME rather than as a summary, because the reading pane
/// renders what was received: a store of summaries exercises the list and nothing below it.
/// The bodies are shaped to reach the surfaces that only appear for certain mail — an inline
/// image, a tracking pixel, a spoofed display name — since none of those can be photographed
/// without a message that has one.
/// <para>
/// Every address and name here is invented. The reference captures are the owner's real mail
/// and nothing from them belongs in sample data.
/// </para>
/// </remarks>
public class SeedHarness
{
    /// <summary>An 8-byte PNG header, which is enough to be a distinct inline part.</summary>
    private static readonly byte[] TinyPng = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void SeedOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED");
        if (string.IsNullOrWhiteSpace(target)) return;

        var order = new SettingsAccountOrder(
            new SettingsStore(Path.Combine(target, "settings.json")));

        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);

        Seed(stores, "you@example.com",
            Plain("Alice Chen", "alice@example.com", "Re: Q3 numbers",
                "Thanks for pulling those together.\n\nThe variance on line 14 is the one I'd "
                + "want to talk through before Thursday. Everything else reconciles against "
                + "what finance sent over last week.\n\nSee https://example.com/q3 for the "
                + "worksheet.\n\nAlice"),

            Marketing("The Weekly", "news@newsletter.example", "Your Tuesday briefing"),

            Phishing(),

            Plain("Build Notifications", "builds@example.com", "mailbox/main — build passed",
                "Commit 4f2a1c9 built successfully on linux-x64.\n\n0 warnings, 0 errors.\n"
                + "Elapsed 00:00:04.62"));

        Seed(stores, "work@example.net",
            Plain("Priya Raman", "priya@example.net", "Font substitution question",
                "Confirmed — Carlito is metric-compatible with Calibri, so the layout holds "
                + "either way."),

            WithAttachment("Sam Reyes", "sam@example.net", "Draft agenda attached",
                "Rough cut for Monday. Shout if there's anything you want added before I send "
                + "it round.",
                "agenda.pdf", "application/pdf", 38_000),

            Forwarded());

        SeedImap(stores, "imap@example.org");
    }

    /// <summary>
    /// An IMAP account, so the folder pane shows the nesting and the "IMAP/SMTP" type a POP3
    /// account does not have. Its mail is filed with server UIDs, and a mapped sub-folder sits
    /// under its parent to exercise the tree indent.
    /// </summary>
    private static void SeedImap(AccountStores stores, string address)
    {
        var account = stores.Add(address, address, MailProtocol.Imap);
        var accountId = account.Account.Id;

        // Map the role folders to server paths, as a first sync would, and nest one folder.
        var inbox = account.Mail.FolderWithRole(accountId, FolderRole.Inbox)!;
        account.Mail.MapFolder(inbox.Id, "INBOX", "Inbox", null);
        var projects = account.Mail.AddFolder(accountId, "Projects", FolderRole.None, null, "Projects");
        account.Mail.AddFolder(accountId, "Mailbox", FolderRole.None, projects.Id, "Projects/Mailbox");

        var when = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            Plain("Dana Okafor", "dana@example.org", "Server-side folders",
                "The whole tree syncs now — try dragging this into Projects and watch it move "
                + "on the server."),
            Plain("CI", "ci@example.org", "IDLE is live",
                "New mail turns up without waiting for the timer."),
        };

        var uid = 1;
        foreach (var message in messages)
        {
            message.Date = when;
            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();
            var summary = Mailbox.Protocols.MessageMapper.ToSummary(
                message, uid.ToString(), raw.Length, when);
            account.Mail.AddMessage(inbox.Id, summary, raw);
            uid++;
            when = when.AddMinutes(-25);
        }

        account.Mail.SetFolderSyncState(inbox.Id, 1, uid, null);
    }

    // ---- The messages ----------------------------------------------------------------------

    private static MimeMessage Plain(string name, string address, string subject, string body)
    {
        var message = Envelope(name, address, subject);
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private static MimeMessage WithAttachment(
        string name, string address, string subject, string body,
        string fileName, string type, int size)
    {
        var message = Envelope(name, address, subject);

        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = body },
            new MimePart(ContentType.Parse(type))
            {
                FileName = fileName,
                Content = new MimeContent(new MemoryStream(new byte[size])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };

        return message;
    }

    /// <summary>
    /// A message forwarded as an attachment, which is a whole message inside a message.
    /// </summary>
    /// <remarks>
    /// Here because the attachment strip has a case for it that no other seeded message reaches:
    /// a <c>message/rfc822</c> part is not a <c>MimePart</c>, and a strip that matches only the
    /// latter shows nothing at all for the commonest way of passing mail on.
    /// </remarks>
    private static MimeMessage Forwarded()
    {
        var original = Envelope("Dana Whitfield", "dana@example.org", "Venue options");
        original.Body = new TextPart("plain")
        {
            Text = "Three places can take us on the 14th. Costs attached.\n\nDana",
        };

        var message = Envelope("Priya Raman", "priya@example.net", "FW: Venue options");
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Forwarding Dana's note — see what you think." },
            new MessagePart
            {
                Message = original,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
        };

        return message;
    }

    /// <summary>
    /// The shape most commercial mail takes: a stylesheet, an inline logo, and a pixel whose
    /// only purpose is to report that the message was opened.
    /// </summary>
    private static MimeMessage Marketing(string name, string address, string subject)
    {
        var message = Envelope(name, address, subject);

        var logo = new MimePart("image", "png")
        {
            ContentId = "logo",
            Content = new MimeContent(new MemoryStream(TinyPng)),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
        };

        var html = new TextPart("html")
        {
            Text = """
                <html><head><style>
                .wrap{font-family:Georgia,serif;max-width:560px}
                .lead{font-size:15px;line-height:1.5}
                .rule{border-top:1px solid #cccccc;margin:18px 0}
                </style></head><body>
                <div class="wrap">
                  <p><img src="cid:logo" alt="The Weekly" width="120" height="32"></p>
                  <p class="lead">Three things worth your time this week, and one that is not.</p>
                  <div class="rule"></div>
                  <p><a href="https://example.com/story/1">The first story</a></p>
                  <p><a href="https://example.com/story/2">The second story</a></p>
                  <p><img src="https://pixel.tracker.example/open?id=42" width="1" height="1"></p>
                  <p><img src="https://cdn.images.example/banner.png" width="560" height="120"></p>
                </div>
                </body></html>
                """,
        };

        message.Body = new Multipart("related") { html, logo };
        return message;
    }

    /// <summary>
    /// The pattern the trust bar exists for: a display name claiming one domain, sent from
    /// another, and failing the claimed domain's own policy.
    /// </summary>
    private static MimeMessage Phishing()
    {
        var message = Envelope("billing@yourbank.example", "no-reply@delivery.invalid",
            "Action required: confirm your details");

        message.Headers.Add("Authentication-Results",
            "mx.example.com; dkim=fail; spf=fail smtp.mailfrom=delivery.invalid; dmarc=fail");

        message.Body = new TextPart("plain")
        {
            Text = "Your account will be suspended unless you confirm your details today.\n\n"
                   + "https://yourbank.example.confirm-now.invalid/login",
        };

        return message;
    }

    private static MimeMessage Envelope(string name, string address, string subject)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress(name, address));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.MessageId = MimeUtils.GenerateMessageId("example.com");
        return message;
    }

    /// <summary>
    /// Mail waiting to go out, including one the server refused for good. The Outbox view is
    /// the only place a permanent failure is visible, so it cannot be looked at without one.
    /// </summary>
    private static void SeedOutbox(OpenAccount account)
    {
        var sender = new Mailbox.Protocols.SmtpSender(account.Mail);
        var now = DateTimeOffset.UtcNow;

        var waiting = Envelope("You", "you@example.com", "Re: Thursday");
        waiting.To.Clear();
        waiting.To.Add(new MailboxAddress("Alice Chen", "alice@example.com"));
        waiting.Body = new TextPart("plain") { Text = "Works for me." };
        sender.Queue(account.Account.Id, waiting, now);

        var refused = Envelope("You", "you@example.com", "Expenses, March");
        refused.To.Clear();
        refused.To.Add(new MailboxAddress("Accounts", "accounts@example.invalid"));
        refused.Body = new TextPart("plain") { Text = "Attached." };

        var id = sender.Queue(account.Account.Id, refused, now.AddMinutes(-90));
        account.Mail.FailOutbox(id, "The recipient's address was rejected: no such mailbox.");
    }

    // ---- Writing them out ---------------------------------------------------------------------

    private static void Seed(AccountStores stores, string address, params MimeMessage[] messages)
    {
        var account = stores.Add(address, address, MailProtocol.Pop3);
        SeedOutbox(account);
        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox)!;
        var when = DateTimeOffset.UtcNow;

        // Everyone the seeded mail is from has been written to, so the To line has names to
        // offer — the Auto-Complete List is fed by sending, and a seed has sent nothing.
        account.Mail.RecordRecipients(
            messages.SelectMany(m => m.From.Mailboxes).Select(m => (m.Address, (string?)m.Name)),
            when);

        foreach (var message in messages)
        {
            message.Date = when;

            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();

            var summary = Mailbox.Protocols.MessageMapper.ToSummary(
                message, Guid.NewGuid().ToString("n"), raw.Length, when);

            account.Mail.AddMessage(inbox.Id, summary, raw);
            when = when.AddMinutes(-37);
        }
    }
}
