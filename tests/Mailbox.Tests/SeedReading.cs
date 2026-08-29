using System.Text;
using Mailbox.Core.Settings;
using Mailbox.Store;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

namespace Mailbox.Tests;

/// <summary>
/// A store shaped for the reading pane: real MIME, one message per thing the pane only draws
/// for certain mail.
/// </summary>
/// <remarks>
/// Every message here is written as the bytes that would have arrived, because the pane opens
/// the message rather than reading the row — a summary corpus exercises the list and nothing
/// below it. The list corpus next door is the opposite trade and says why.
/// <para>
/// The cases are chosen against what the code actually branches on rather than against a list of
/// things that sound like mail: <c>SenderTrust.Evaluate</c> raises one warning per rule, and
/// there is a message here for each of them, including the two that need a *pair* to prove
/// anything — a per-sender image allowance means nothing unless a second sender is still blocked.
/// </para>
/// <para>
/// Every address, name and domain is invented, and nothing is copied from a capture.
/// </para>
/// </remarks>
public class SeedReading
{
    /// <summary>The reader's own address, which makes example.com a familiar domain.</summary>
    private const string Reader = "you@example.com";

    [Fact]
    public void SeedReadingOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED_READING");
        if (string.IsNullOrWhiteSpace(target)) return;

        var order = new SettingsAccountOrder(
            new SettingsStore(Path.Combine(target, "settings.json")));

        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);
        var account = stores.Add(Reader, Reader, MailProtocol.Pop3);
        var mail = account.Mail;
        var inbox = mail.FolderWithRole(account.Account.Id, FolderRole.Inbox)!;

        var when = Mailbox.Core.PosedClock.Now;
        var uid = 0;

        void File(MimeMessage message)
        {
            message.Date = when;
            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();

            mail.AddMessage(
                inbox.Id,
                Mailbox.Protocols.MessageMapper.ToSummary(message, $"read-{uid++}", raw.Length, when),
                raw);

            when = when.AddMinutes(-9);
        }

        // ---- Authentication, one message per verdict the evaluator can reach ----------------
        File(Authenticated("Clean Sender", "clean@example.net", "Everything passes",
            "dkim=pass header.d=example.net; spf=pass smtp.mailfrom=example.net; dmarc=pass"));

        File(Authenticated("Failing Sender", "billing@example.org", "DMARC says no",
            "dkim=fail header.d=example.org; spf=fail smtp.mailfrom=example.org; dmarc=fail"));

        File(Authenticated("Forwarded Via List", "list@example.net", "SPF soft-failed",
            "dkim=none; spf=softfail smtp.mailfrom=relay.example.net; dmarc=none"));

        // No Authentication-Results header at all, which is most mail from a server that does
        // not add one — and must be quiet rather than alarming.
        File(Plain("Quiet Sender", "quiet@example.net", "No results header",
            "Nothing about this message has been checked by anybody, which is not the same as\n"
            + "having failed a check."));

        // The display name is itself an address, at a domain the message did not come from.
        File(Plain("accounts@example.com", "no-reply@delivery.invalid", "Your account",
            "The name on this message claims one domain and it was sent from another."));

        // One character away from example.com, which is the reader's own domain and therefore
        // familiar. This is the typosquat rule.
        File(Plain("A. Person", "a.person@exarnple.com", "Nearly your domain",
            "The domain this came from is not the one it looks like."));

        // A punycode domain: an encoded name, which the homograph rule refuses outright.
        File(Plain("Support", "support@xn--exmple-cua.com", "An encoded domain",
            "The domain is not written in the alphabet it appears to be."));

        // ---- Remote content, in the three messages it takes to prove the scoping ------------
        File(Remote("Newsletter One", "news@sender-one.example", "First from sender one",
            ["https://cdn.sender-one.example/banner.png", "https://pixel.sender-one.example/open?id=1"]));

        File(Remote("Newsletter One", "news@sender-one.example", "Second from sender one",
            ["https://cdn.sender-one.example/other.png"]));

        File(Remote("Newsletter Two", "news@sender-two.example", "From a different sender",
            ["https://cdn.sender-two.example/banner.png"]));

        // A stylesheet's background, which is the other kind of blocked resource.
        File(StyledRemote("Styled Sender", "styled@example.net", "A background from a stylesheet"));

        // ---- The encodings zoo ---------------------------------------------------------------
        File(Encoded("Latin One", "latin1@example.net", "Café, naïve, façade",
            "Une phrase avec des accents : é è ê ë à ù ç ô.", "iso-8859-1"));

        File(Encoded("Cyrillic Sender", "cyrillic@example.net", "Кириллица",
            "Проверка кодировки KOI8-R и того, как она читается.", "koi8-r"));

        File(Encoded("Japanese Sender", "japanese@example.net", "日本語の件名",
            "これは Shift_JIS で符号化された本文です。", "shift_jis"));

        File(Encoded("Emoji Sender", "emoji@example.net", "Coffee ☕ and a rocket 🚀",
            "Emoji in the body too: 🎉 🐛 ✅ — and an em dash.", "utf-8"));

        // Right to left, in the subject and the body, mixed with Latin — which is where the
        // ordering actually gets decided.
        File(Encoded("Hebrew Sender", "hebrew@example.net", "שלום mixed with English",
            "שורה בעברית ואחריה English words, then עברית again.", "utf-8"));

        File(Encoded("Arabic Sender", "arabic@example.net", "مرحبا bidirectional",
            "نص عربي مع English في المنتصف.", "utf-8"));

        // ---- The ugly-HTML corpus -------------------------------------------------------------
        File(UglyHtml());

        // ---- Attachments and inline pictures ---------------------------------------------------
        File(ManyAttachments());
        File(ForwardedAsAttachment());
        File(InlinePicture());
    }

    // ---- The messages -------------------------------------------------------------------------

    private static MimeMessage Envelope(string name, string address, string subject)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress(name, address));
        message.To.Add(new MailboxAddress("You", Reader));
        message.MessageId = MimeUtils.GenerateMessageId("example.com");
        return message;
    }

    private static MimeMessage Plain(string name, string address, string subject, string body)
    {
        var message = Envelope(name, address, subject);
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    /// <summary>A message carrying the verdicts a receiving server wrote into it.</summary>
    private static MimeMessage Authenticated(string name, string address, string subject, string results)
    {
        var message = Plain(name, address, subject,
            "The interesting part of this message is its Authentication-Results header.");

        message.Headers.Add("Authentication-Results", "mx.example.com; " + results);
        return message;
    }

    /// <summary>
    /// A message whose body asks for pictures from somewhere else — a banner and, usually, a
    /// one-pixel image whose only purpose is to report that the message was opened.
    /// </summary>
    private static MimeMessage Remote(string name, string address, string subject, string[] urls)
    {
        var message = Envelope(name, address, subject);

        var images = string.Concat(urls.Select(
            u => $"<p><img src=\"{u}\" width=\"400\" height=\"90\" alt=\"\"></p>\n"));

        message.Body = new TextPart("html")
        {
            Text = $"<html><body><p>Words above the pictures.</p>\n{images}</body></html>",
        };

        return message;
    }

    /// <summary>Remote content asked for by a stylesheet rather than by an img element.</summary>
    private static MimeMessage StyledRemote(string name, string address, string subject)
    {
        var message = Envelope(name, address, subject);
        message.Body = new TextPart("html")
        {
            Text = """
                <html><head><style>
                .hero { background-image: url(https://cdn.styled.example/hero.jpg); height: 120px; }
                </style></head>
                <body><div class="hero"></div><p>The picture is named by the stylesheet.</p></body></html>
                """,
        };

        return message;
    }

    /// <summary>A message in a charset that is not UTF-8, declared the way a real one declares it.</summary>
    private static MimeMessage Encoded(string name, string address, string subject, string body, string charset)
    {
        var message = Envelope(name, address, subject);

        // Round-tripped through the charset it claims, so a message that genuinely cannot hold a
        // character loses it here rather than being written as UTF-8 under another name — which
        // would make the corpus a test of MimeKit's writer instead of of the reader.
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            // The legacy code pages live in the extra provider rather than in the framework's
            // own set; registering it is what makes koi8-r and shift_jis resolvable at all.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(charset);
        }

        var part = new TextPart(TextFormat.Plain);
        part.SetText(encoding, body);
        message.Body = part;
        return message;
    }

    /// <summary>
    /// Everything a message should not be allowed to do, in one message.
    /// </summary>
    /// <remarks>
    /// A script, a frame, an event handler, a javascript: destination, an at-rule that fetches,
    /// a legacy CSS expression, a form, tags that are never closed and a nesting depth that a
    /// naive parser recurses through. The claim is that the pane draws the words and none of the
    /// rest — and that it does not fall over.
    /// </remarks>
    private static MimeMessage UglyHtml()
    {
        var message = Envelope("Ugly Markup", "ugly@example.net", "Everything at once");
        var deep = string.Concat(Enumerable.Repeat("<div>", 80))
                   + "Buried eighty deep."
                   + string.Concat(Enumerable.Repeat("</div>", 80));

        message.Body = new TextPart("html")
        {
            Text = $$"""
                <html><head>
                  <style>@import url(https://cdn.ugly.example/more.css);
                         body { width: expression(alert('x')); }</style>
                  <script>window.alert('this must not run');</script>
                </head>
                <body onload="alert('nor this')">
                  <p>The words a reader should still see.</p>
                  <p><a href="javascript:alert('nope')">A link that runs code</a></p>
                  <p><a href="https://example.com/real">A link that does not</a></p>
                  <iframe src="https://cdn.ugly.example/frame.html"></iframe>
                  <form action="https://cdn.ugly.example/collect"><input name="password"></form>
                  <p onclick="alert('handler')">A paragraph with a handler</p>
                  <table><tr><td><table><tr><td>Nested tables
                  <p><b>Unclosed bold and <i>italic
                  {{deep}}
                </body></html>
                """,
        };

        return message;
    }

    private static MimeMessage ManyAttachments()
    {
        var message = Envelope("Sam Reyes", "sam@example.net", "Four things attached");

        var body = new Multipart("mixed") { new TextPart("plain") { Text = "All four are attached." } };

        void Attach(string fileName, string type, int size)
            => body.Add(new MimePart(ContentType.Parse(type))
            {
                FileName = fileName,
                Content = new MimeContent(new MemoryStream(new byte[size])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
            });

        Attach("agenda.pdf", "application/pdf", 38_000);
        Attach("figures.csv", "text/csv", 2_400);
        Attach("photo.png", "image/png", 12_000);

        // A name that tries to escape the directory a reader picks, which SafeName exists for.
        Attach("../../.bashrc", "application/octet-stream", 300);

        message.Body = body;
        return message;
    }

    /// <summary>A whole message inside a message, which is not a MimePart and is easy to miss.</summary>
    private static MimeMessage ForwardedAsAttachment()
    {
        var original = Envelope("Dana Whitfield", "dana@example.org", "Venue options");
        original.Body = new TextPart("plain") { Text = "Three places can take us on the 14th." };

        var message = Envelope("Priya Raman", "priya@example.net", "FW: Venue options");
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Forwarding Dana's note." },
            new MessagePart { Message = original, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) },
        };

        return message;
    }

    /// <summary>A picture carried with the message and referred to by cid: — never blocked.</summary>
    private static MimeMessage InlinePicture()
    {
        var message = Envelope("Inline Pictures", "inline@example.net", "The picture is attached, not fetched");

        var picture = new MimePart("image", "png")
        {
            ContentId = "chart",
            FileName = "chart.png",
            Content = new MimeContent(new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10])),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
        };

        var html = new TextPart("html")
        {
            Text = "<html><body><p>Below is a picture that came with the message.</p>"
                   + "<p><img src=\"cid:chart\" width=\"200\" height=\"80\" alt=\"A chart\"></p></body></html>",
        };

        message.Body = new Multipart("related") { html, picture };
        return message;
    }
}
