using Mailbox.Rendering;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Reply, Reply All and Forward: who, what it is called, and what it quotes.
/// </summary>
/// <remarks>
/// Every rule here is one whose failure is visible to somebody else's mailbox — a reply that
/// copies its own author, a subject that says RE: RE: RE:, a thread the recipient's client
/// cannot join. So they are pinned individually.
/// </remarks>
public class ReplyTests
{
    private static readonly string[] Me = ["you@example.com", "work@example.net"];

    private static MimeMessage Original(
        string from = "\"A. Person\" <a@example.com>",
        string to = "you@example.com",
        string? cc = null,
        string? replyTo = null,
        string subject = "Q3 numbers",
        string? messageId = "orig@example.com")
    {
        var m = new MimeMessage { Subject = subject };
        m.From.Add(MailboxAddress.Parse(from));
        foreach (var t in to.Split(',', StringSplitOptions.RemoveEmptyEntries)) m.To.Add(MailboxAddress.Parse(t.Trim()));
        if (cc is not null) foreach (var c in cc.Split(',')) m.Cc.Add(MailboxAddress.Parse(c.Trim()));
        if (replyTo is not null) m.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        if (messageId is not null) m.MessageId = messageId;
        m.Date = new DateTimeOffset(2026, 8, 15, 9, 30, 0, TimeSpan.Zero);
        m.Body = new TextPart("plain") { Text = "Line one.\nLine two." };
        return m;
    }

    private static ReplyOptions Options(QuoteStyle style = QuoteStyle.Include, bool plain = false)
        => new() { OwnAddresses = Me, Style = style, PlainText = plain };

    // ---- Who ------------------------------------------------------------------------------

    [Fact]
    public void AReplyGoesToTheAuthor()
    {
        var draft = Reply.Build(Original(), ReplyKind.Reply, Options());

        Assert.Equal(["A. Person <a@example.com>"], draft.To);
        Assert.Empty(draft.Cc);
    }

    /// <summary>Reply-To is what the header is for, and it outranks From.</summary>
    [Fact]
    public void ReplyToOutranksFrom()
    {
        var draft = Reply.Build(Original(replyTo: "list@example.org"), ReplyKind.Reply, Options());

        Assert.Equal(["list@example.org"], draft.To);
    }

    /// <summary>The single most annoying reply-all failure: copying yourself.</summary>
    [Fact]
    public void ReplyAllKeepsEveryoneButTheReader()
    {
        var draft = Reply.Build(
            Original(to: "you@example.com, b@example.com", cc: "c@example.com, work@example.net"),
            ReplyKind.ReplyAll, Options());

        Assert.Equal(["A. Person <a@example.com>", "b@example.com"], draft.To);
        Assert.Equal(["c@example.com"], draft.Cc);
        Assert.DoesNotContain(draft.To.Concat(draft.Cc), r => Me.Any(me => r.Contains(me, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ReplyAllDoesNotListTheAuthorTwice()
    {
        var draft = Reply.Build(
            Original(from: "a@example.com", to: "you@example.com, a@example.com"),
            ReplyKind.ReplyAll, Options());

        Assert.Equal(["a@example.com"], draft.To);
    }

    /// <summary>Replying to your own sent message replies to the people it went to.</summary>
    [Fact]
    public void ReplyingToYourOwnMessageGoesToItsRecipients()
    {
        var draft = Reply.Build(
            Original(from: "you@example.com", to: "b@example.com"),
            ReplyKind.Reply, Options());

        Assert.Equal(["b@example.com"], draft.To);
    }

    [Fact]
    public void AForwardGoesToNobodyYet()
    {
        var draft = Reply.Build(Original(), ReplyKind.Forward, Options());

        Assert.Empty(draft.To);
        Assert.Empty(draft.Cc);
    }

    // ---- What it is called ---------------------------------------------------------------------

    [Theory]
    [InlineData("Q3 numbers", "RE", "RE: Q3 numbers")]
    [InlineData("RE: Q3 numbers", "RE", "RE: Q3 numbers")]
    [InlineData("Re: Q3 numbers", "RE", "Re: Q3 numbers")]
    [InlineData("AW: Q3 numbers", "RE", "AW: Q3 numbers")]
    [InlineData("FW: Q3 numbers", "RE", "RE: Q3 numbers")]
    [InlineData("Q3 numbers", "FW", "FW: Q3 numbers")]
    [InlineData("FW: Q3 numbers", "FW", "FW: Q3 numbers")]
    [InlineData("RE: Q3 numbers", "FW", "FW: Q3 numbers")]
    [InlineData("", "RE", "RE: ")]
    public void TheSubjectGainsOnePrefixAndNeverTwo(string subject, string prefix, string expected)
        => Assert.Equal(expected, Reply.Prefixed(subject, prefix));

    // ---- Threading -------------------------------------------------------------------------------

    /// <summary>What lets the recipient's client put the reply under the original.</summary>
    [Fact]
    public void AReplyCarriesInReplyToAndReferences()
    {
        var original = Original();
        original.References.Add("older@example.com");

        var draft = Reply.Build(original, ReplyKind.Reply, Options());

        Assert.Equal("orig@example.com", draft.InReplyTo);
        Assert.Equal(["older@example.com", "orig@example.com"], draft.References);
    }

    [Fact]
    public void AForwardStartsAThreadOfItsOwn()
    {
        var draft = Reply.Build(Original(), ReplyKind.Forward, Options());

        Assert.Null(draft.InReplyTo);
        Assert.Empty(draft.References);
    }

    // ---- What it quotes ---------------------------------------------------------------------------

    [Fact]
    public void TheQuoteCarriesTheHeaderBlockAndTheMessage()
    {
        var draft = Reply.Build(Original(), ReplyKind.Reply, Options());

        Assert.Contains("<hr />", draft.QuotedHtml, StringComparison.Ordinal);
        Assert.Contains("<b>From:</b>", draft.QuotedHtml, StringComparison.Ordinal);
        Assert.Contains("A. Person", draft.QuotedHtml, StringComparison.Ordinal);
        Assert.Contains("<b>Subject:</b> Q3 numbers", draft.QuotedHtml, StringComparison.Ordinal);
        Assert.Contains("Line one.", draft.QuotedHtml, StringComparison.Ordinal);
    }

    /// <summary>A text-only original keeps its lines, which a fragment with no stylesheet would lose.</summary>
    [Fact]
    public void APlainTextOriginalKeepsItsLineBreaks()
    {
        var draft = Reply.Build(Original(), ReplyKind.Reply, Options());

        Assert.Contains("<p>Line one.</p><p>Line two.</p>", draft.QuotedHtml, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stranger's markup is about to be loaded into an editor and sent on. It goes through the
    /// same sanitizer the reading pane uses, and nothing that could act on it survives.
    /// </summary>
    [Fact]
    public void AnHtmlOriginalIsSanitizedOnTheWayIn()
    {
        var original = Original();
        original.Body = new TextPart("html")
        {
            Text = "<p>Hello</p><script>alert(1)</script><img src=\"https://tracker.example/p.gif\">",
        };

        var draft = Reply.Build(original, ReplyKind.Reply, Options());

        Assert.Contains("Hello", draft.QuotedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", draft.QuotedHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example", draft.QuotedHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndentedStyleWrapsTheOriginalInAQuote()
    {
        var draft = Reply.Build(Original(), ReplyKind.Reply, Options(QuoteStyle.IncludeIndented));

        Assert.Contains("<blockquote", draft.QuotedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneQuotesNothing()
    {
        var draft = Reply.Build(Original(), ReplyKind.Reply, Options(QuoteStyle.None));

        Assert.Empty(draft.QuotedHtml);
        Assert.Empty(draft.QuotedText);
    }

    /// <summary>The plain-text convention: every line marked, header lines included.</summary>
    [Fact]
    public void PrefixStyleMarksEveryLine()
    {
        var draft = Reply.Build(Original(), ReplyKind.Reply,
            Options(QuoteStyle.Prefix, plain: true) with { Prefix = ">" });

        Assert.Empty(draft.QuotedHtml);
        Assert.Contains("> From: ", draft.QuotedText, StringComparison.Ordinal);
        Assert.Contains("> Line one.", draft.QuotedText, StringComparison.Ordinal);
        Assert.Contains("> Line two.", draft.QuotedText, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlainTextQuoteHasTheOriginalMessageBlock()
    {
        var draft = Reply.Build(Original(), ReplyKind.Reply, Options(plain: true));

        Assert.Contains("-----Original Message-----", draft.QuotedText, StringComparison.Ordinal);
        Assert.Contains("From: ", draft.QuotedText, StringComparison.Ordinal);
        Assert.Contains("Line one.", draft.QuotedText, StringComparison.Ordinal);
    }

    // ---- What travels ---------------------------------------------------------------------------

    /// <summary>A forward carries the attachments; a reply does not — the author already has them.</summary>
    [Fact]
    public void AForwardCarriesTheAttachmentsAndAReplyDoesNot()
    {
        var original = Original();
        original.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "See attached." },
            new MimePart("application", "pdf")
            {
                FileName = "agenda.pdf",
                Content = new MimeContent(new MemoryStream(new byte[64])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
        };

        var forward = Reply.Build(original, ReplyKind.Forward, Options());
        var reply = Reply.Build(original, ReplyKind.Reply, Options());

        Assert.Equal("agenda.pdf", Assert.Single(forward.Attachments).Name);
        Assert.Empty(reply.Attachments);
    }

    [Fact]
    public void AttachStyleCarriesTheWholeOriginalAndQuotesNothing()
    {
        var draft = Reply.Build(Original(), ReplyKind.Reply, Options(QuoteStyle.Attach));

        var carried = Assert.Single(draft.Attachments);
        Assert.Equal("Q3 numbers.eml", carried.Name);
        Assert.Equal("message/rfc822", carried.MimeType);
        Assert.IsType<MessagePart>(carried.Entity);
        Assert.Empty(draft.QuotedHtml);
    }
}
