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
                "agenda.pdf", "application/pdf", 38_000));
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

    // ---- Writing them out ---------------------------------------------------------------------

    private static void Seed(AccountStores stores, string address, params MimeMessage[] messages)
    {
        var account = stores.Add(address, address, MailProtocol.Pop3);
        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox)!;
        var when = DateTimeOffset.UtcNow;

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
