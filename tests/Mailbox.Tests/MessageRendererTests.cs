using Mailbox.Rendering;
using MimeKit;

namespace Mailbox.Tests;

public class HtmlSanitizerTests
{
    /// <summary>
    /// Just the sanitized body. The document around it carries a content policy and a charset
    /// of its own, and an assertion about what a message may contain must not be able to trip
    /// over the frame the message was put in.
    /// </summary>
    private static string Body(string html)
    {
        var document = MessageRenderer.RenderHtml(html).Html;
        var start = document.IndexOf("<body>", StringComparison.Ordinal) + "<body>".Length;
        var end = document.LastIndexOf("</body>", StringComparison.Ordinal);
        return document[start..end];
    }

    private static RenderedMessage Render(string html)
        => MessageRenderer.RenderHtml(html);

    // ---- What must not survive ---------------------------------------------------------

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<SCRIPT>alert(1)</SCRIPT>")]
    [InlineData("<iframe src='https://evil.example/x'></iframe>")]
    [InlineData("<object data='x.swf'></object>")]
    [InlineData("<embed src='x.swf'>")]
    [InlineData("<form action='https://evil.example'><input name='p'></form>")]
    [InlineData("<base href='https://evil.example/'>")]
    [InlineData("<link rel='stylesheet' href='https://evil.example/x.css'>")]
    [InlineData("<meta http-equiv='refresh' content='0;url=https://evil.example'>")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    public void DangerousElementsAreDropped(string html)
    {
        var body = Body(html);

        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<embed", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<base", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<meta http-equiv", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The text inside a script is code, so dropping the tag and keeping the content would put
    /// the code on screen as prose. These elements take their contents with them.
    /// </summary>
    [Fact]
    public void ADroppedElementTakesItsContentWithIt()
    {
        var body = Body("<p>Before</p><script>var secret = 1;</script><p>After</p>");

        Assert.Contains("Before", body, StringComparison.Ordinal);
        Assert.Contains("After", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<p onclick=\"alert(1)\">x</p>", "onclick")]
    [InlineData("<p ONMOUSEOVER='alert(1)'>x</p>", "onmouseover")]
    [InlineData("<img src='cid:none' onerror='alert(1)'>", "onerror")]
    public void EventHandlersAreDropped(string html, string handler)
        => Assert.DoesNotContain(handler, Body(html), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("<a href='javascript:alert(1)'>x</a>")]
    [InlineData("<a href='JaVaScRiPt:alert(1)'>x</a>")]
    [InlineData("<a href='java\tscript:alert(1)'>x</a>")]
    [InlineData("<a href='vbscript:msgbox'>x</a>")]
    [InlineData("<a href='data:text/html,<script>alert(1)</script>'>x</a>")]
    public void ScriptSchemesNeverSurviveOnALink(string html)
    {
        var body = Body(html);

        Assert.Contains("<a", body, StringComparison.Ordinal);
        Assert.DoesNotContain("href", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A data URI that is not an image is a document, and a document inside a document is how a
    /// sanitizer gets walked around.
    /// </summary>
    [Fact]
    public void OnlyImageDataUrisSurviveAsResources()
    {
        Assert.DoesNotContain("data:text/html", Body("<img src='data:text/html,<b>x</b>'>"),
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("data:image/png", Body("<img src='data:image/png;base64,AAAA'>"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownElementsAreDroppedButTheirTextIsKept()
    {
        var body = Body("<blink>Still readable</blink><marquee>Gone</marquee>");

        Assert.Contains("Still readable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<blink", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gone", body, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownAttributesAreDropped()
    {
        var body = Body("<p srcset='x' formaction='y' align='center'>x</p>");

        Assert.Contains("align=\"center\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("srcset", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("formaction", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommentsAreDropped()
        => Assert.DoesNotContain("hidden", Body("<p>x</p><!-- hidden --><p>y</p>"),
            StringComparison.Ordinal);

    // ---- Stylesheets --------------------------------------------------------------------

    /// <summary>
    /// Dropping stylesheets outright would be safer and would make most real mail look broken,
    /// so they are kept and scrubbed.
    /// </summary>
    [Fact]
    public void StyleRulesAreKept()
    {
        var body = Body("<style>.lead{color:#336699;font-weight:bold}</style><p class='lead'>x</p>");

        Assert.Contains("#336699", body, StringComparison.Ordinal);
        Assert.Contains("class=\"lead\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<style>@import url('https://evil.example/x.css');</style>", "evil.example")]
    [InlineData("<style>@font-face{src:url('https://evil.example/f.woff')}</style>", "evil.example")]
    [InlineData("<style>.x{behavior:url(#default#time2)}</style>", "behavior")]
    [InlineData("<style>.x{width:expression(alert(1))}</style>", "expression")]
    [InlineData("<style>.x{-moz-binding:url(https://evil.example/x.xml)}</style>", "binding")]
    public void StylesheetsCannotFetchOrExecute(string html, string forbidden)
        => Assert.DoesNotContain(forbidden, Body(html), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AStyleAttributeIsScrubbedTheSameWay()
    {
        var body = Body("<p style=\"color:red;behavior:url(x)\">x</p>");

        Assert.Contains("color:red", body, StringComparison.Ordinal);
        Assert.DoesNotContain("behavior", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Remote content -------------------------------------------------------------------

    [Fact]
    public void RemoteImagesAreReplacedAndCounted()
    {
        var result = Render("<img src='https://tracker.example/pixel.gif?id=42'>");

        Assert.DoesNotContain("tracker.example", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/svg+xml", result.Html, StringComparison.Ordinal);

        Assert.Equal(1, result.BlockedImages);
        Assert.Equal(["tracker.example"], result.Hosts);
        Assert.True(result.HasRemoteContent);
    }

    [Fact]
    public void RemoteResourcesInStylesAreCountedToo()
    {
        var result = Render("<style>.x{background:url(https://cdn.example/bg.png)}</style>");

        Assert.DoesNotContain("cdn.example", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["cdn.example"], result.Hosts);
        Assert.Equal(0, result.BlockedImages);
    }

    [Fact]
    public void ProtocolRelativeUrlsAreRemoteToo()
        => Assert.Equal(["tracker.example"], Render("<img src='//tracker.example/p.gif'>").Hosts);

    [Fact]
    public void AMessageWithNoRemoteContentSaysSo()
    {
        var result = Render("<p>Just words.</p>");

        Assert.False(result.HasRemoteContent);
        Assert.Empty(result.Hosts);
    }

    /// <summary>
    /// "Allow once" and the per-sender list work by the application fetching and handing the
    /// bytes back, so the document still reaches the engine with nothing left to request.
    /// </summary>
    [Fact]
    public void AResourceTheCallerFetchedIsInlinedInsteadOfBlocked()
    {
        const string url = "https://cdn.example/logo.png";

        var options = new RenderOptions
        {
            Inlined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [url] = "data:image/png;base64,AAAA",
            },
        };

        var result = MessageRenderer.RenderHtml($"<img src='{url}'>", options: options);

        Assert.Contains("data:image/png;base64,AAAA", result.Html, StringComparison.Ordinal);
        Assert.False(result.HasRemoteContent);
        Assert.DoesNotContain("cdn.example", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Inline parts ---------------------------------------------------------------------

    [Fact]
    public void CidReferencesResolveOutOfTheMessage()
    {
        var message = WithInlineImage("logo", out _);

        var result = MessageRenderer.Render(message);

        Assert.Contains("data:image/png;base64,", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("cid:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.HasRemoteContent);
    }

    [Fact]
    public void ACidReferenceTheMessageDoesNotCarryIsDropped()
    {
        var message = WithInlineImage("logo", out _);
        message.Body = Multipart(message, "<img src=\"cid:missing\">");

        var result = MessageRenderer.Render(message);

        Assert.DoesNotContain("cid:missing", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("src=", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void APartTooLargeToInlineIsDroppedRatherThanEmbedded()
    {
        var message = WithInlineImage("logo", out _);

        var result = MessageRenderer.Render(message, new RenderOptions { MaxInlineBytes = 4 });

        Assert.DoesNotContain("data:image", result.Html, StringComparison.Ordinal);
    }

    // ---- Plain text -------------------------------------------------------------------------

    [Fact]
    public void PlainTextIsEscapedAndKeptAsWritten()
    {
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "a < b & c\n  indented" } };

        var result = MessageRenderer.Render(message);

        Assert.False(result.WasHtml);
        Assert.Contains("a &lt; b &amp; c", result.Html, StringComparison.Ordinal);
        Assert.Contains("white-space:pre-wrap", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextLinksAreMadeClickable()
    {
        var message = new MimeMessage
        {
            Body = new TextPart("plain") { Text = "See https://example.com/x for more." },
        };

        Assert.Contains("<a href=\"https://example.com/x\"", MessageRenderer.Render(message).Html,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A plain-text body is not markup, so anything that looks like a tag in it is text.
    /// </summary>
    [Fact]
    public void MarkupInAPlainTextBodyIsNotMarkup()
    {
        var message = new MimeMessage
        {
            Body = new TextPart("plain") { Text = "<script>alert(1)</script>" },
        };

        var html = MessageRenderer.Render(message).Html;

        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    // ---- The document ------------------------------------------------------------------------

    [Fact]
    public void TheDocumentCarriesTheStyleItWasGiven()
    {
        var style = new RenderStyle("#101010", "#F0F0F0", "#8AB4F8", "#767676", "Georgia", 15);

        var html = MessageRenderer.RenderHtml("<p>x</p>", options: new RenderOptions { Style = style }).Html;

        Assert.Contains("background:#101010", html, StringComparison.Ordinal);
        Assert.Contains("color:#F0F0F0", html, StringComparison.Ordinal);
        Assert.Contains("font-family:Georgia", html, StringComparison.Ordinal);
        Assert.Contains("font-size:15px", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Belt and braces over the sanitizer: nothing that could act on a policy should have
    /// survived it, so these exist to be wrong twice before anything leaks.
    /// </summary>
    [Fact]
    public void TheDocumentDeniesEveryFetchOfItsOwn()
    {
        var document = MessageRenderer.RenderHtml("<p>x</p>").Html;

        Assert.Contains("content=\"no-referrer\"", document, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("img-src data:", document, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", document, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static MimeMessage WithInlineImage(string contentId, out MimePart image)
    {
        image = new MimePart("image", "png")
        {
            ContentId = contentId,
            Content = new MimeContent(new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10])),
            ContentTransferEncoding = ContentEncoding.Base64,
        };

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("A. Person", "you@example.com"));
        message.Body = Multipart(image, $"<img src=\"cid:{contentId}\">");
        return message;
    }

    private static Multipart Multipart(MimePart image, string html) => new("related")
    {
        new TextPart("html") { Text = html },
        image,
    };

    private static Multipart Multipart(MimeMessage carrier, string html)
        => Multipart(carrier.BodyParts.OfType<MimePart>().First(p => p.ContentId is not null), html);
}
