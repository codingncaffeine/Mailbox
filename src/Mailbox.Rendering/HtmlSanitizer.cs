using System.Text;
using MimeKit;
using MimeKit.Text;

namespace Mailbox.Rendering;

/// <summary>
/// Rewrites a message's HTML into something safe to hand to a rendering engine.
/// </summary>
/// <remarks>
/// An allowlist: elements, attributes and URL schemes are kept only if they are named here, so
/// anything new or unrecognised is dropped by default rather than by having been thought of.
/// <para>
/// The same walk resolves resources. A <c>cid:</c> reference becomes the part's bytes as a
/// <c>data:</c> URI; a remote reference becomes a placeholder and an entry in the tracker
/// report, unless the caller has already fetched it. What comes out has no remote URL left in
/// it, which is what makes the blocking in §11 something that cannot fail open — there is
/// nothing left for the engine to request.
/// </para>
/// </remarks>
internal sealed class HtmlSanitizer(ResourceMap resources, RenderOptions options)
{
    /// <summary>Elements that may appear. Everything else is dropped, content kept.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote",
        "body", "br", "caption", "center", "cite", "code", "col", "colgroup", "dd", "del",
        "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font", "footer",
        "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hgroup", "hr", "html", "i", "img",
        "ins", "kbd", "li", "main", "mark", "nav", "ol", "p", "pre", "q", "rp", "rt", "ruby",
        "s", "samp", "section", "small", "span", "strike", "strong", "sub", "summary", "sup",
        "table", "tbody", "td", "tfoot", "th", "thead", "time", "tr", "tt", "u", "ul", "var",
        "wbr",
    };

    /// <summary>
    /// Elements dropped along with everything inside them.
    /// </summary>
    /// <remarks>
    /// Distinct from merely unrecognised: the text inside a <c>&lt;script&gt;</c> is code and
    /// the text inside a <c>&lt;title&gt;</c> belongs to a document we are not rendering, so
    /// keeping the content and dropping the tag would put both on screen.
    /// </remarks>
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "noscript", "iframe", "frame", "frameset", "object", "embed", "applet",
        "form", "input", "button", "select", "option", "optgroup", "textarea", "label",
        "fieldset", "legend", "base", "link", "meta", "title", "svg", "math",
        "template", "canvas", "audio", "video", "source", "track", "param", "map", "area",
        "portal", "slot", "dialog", "marquee",
    };

    /// <summary>
    /// Elements that never have an end tag, whatever a message writes.
    /// </summary>
    /// <remarks>
    /// Load-bearing for the ones that are also dropped. Skipping runs until the matching end tag
    /// arrives, and for <c>&lt;meta&gt;</c> or <c>&lt;link&gt;</c> it never does — so without this
    /// a single stray one swallows the whole of the rest of the message, silently, and the reader
    /// sees a message that simply stops. Self-closing them (<c>&lt;meta /&gt;</c>) happened to
    /// work, which is exactly why nobody noticed: HTML is not usually written that way.
    /// </remarks>
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "frame", "hr", "img", "input", "link", "meta",
        "param", "source", "track", "wbr",
    };

    /// <summary>
    /// Attributes that may appear on any element. Presentational HTML is over-represented
    /// because email is written in it.
    /// </summary>
    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "cite", "class",
        "color", "cols", "colspan", "datetime", "dir", "face", "height", "hspace", "id",
        "lang", "nowrap", "rows", "rowspan", "size", "span", "start", "summary", "title",
        "type", "valign", "vspace", "width",
    };

    /// <summary>Attributes naming a resource, which go through the resolver.</summary>
    private static readonly HashSet<string> ResourceAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "background", "poster",
    };

    private readonly List<BlockedResource> _blocked = [];
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _style = new();

    private string? _skipping;
    private int _skipDepth;
    private bool _inStyle;

    internal IReadOnlyList<BlockedResource> Blocked => _blocked;

    internal string Sanitize(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var tokenizer = new HtmlTokenizer(new StringReader(html));

        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token.Kind)
            {
                case HtmlTokenKind.Tag:
                    Tag((HtmlTagToken)token);
                    break;

                case HtmlTokenKind.Data:
                case HtmlTokenKind.ScriptData:
                    Text(token);
                    break;

                // Comments, doctypes and CDATA carry nothing worth rendering, and conditional
                // comments are a way of hiding markup from a sanitizer that reads them as text.
                default:
                    break;
            }
        }

        // A stylesheet whose end tag never arrives holds the rest of the message, which is what a
        // browser does with one too. Without this the sheet — and every host it named — was
        // dropped on the floor and the tracker report said the message asked for nothing.
        if (_inStyle) CloseStyle();

        return _out.ToString();
    }

    /// <summary>
    /// Text between tags, written out escaped.
    /// </summary>
    /// <remarks>
    /// <b>Escaped here rather than taken as the tokenizer wrote it.</b> The tokenizer hands back
    /// two different things under one token kind: from the data state it hands back text with its
    /// character references already re-encoded, and from the raw-text and plain-text states — the
    /// insides of <c>xmp</c>, <c>noembed</c>, <c>noframes</c> and everything after
    /// <c>&lt;plaintext&gt;</c> — it hands back the source verbatim, markup and all. Appending that
    /// verbatim wrote a stranger's tags straight into the document: those four elements are not on
    /// the allowlist, so the tag was dropped and its "text" kept, and
    /// <c>&lt;xmp&gt;&lt;script&gt;…&lt;/script&gt;&lt;/xmp&gt;</c> reached the engine as a script
    /// element the sanitizer had never seen. Encoding the <em>decoded</em> value closes the class
    /// whatever the tokenizer decides is raw text next: what goes out is text, always, and it says
    /// what the sender wrote.
    /// </remarks>
    private void Text(HtmlToken token)
    {
        // HtmlDataToken.Data is the decoded value; ToString() is the re-encoded one. Encoding the
        // decoded value round-trips ordinary text and neutralises raw text, which is the point.
        var text = token is HtmlDataToken data ? data.Data : token.ToString() ?? string.Empty;

        if (_inStyle)
        {
            // A stylesheet is raw text by definition: HTML does not decode references inside one,
            // so this is the source as written and must stay that way for the scrubber to read.
            _style.Append(text);
            return;
        }

        if (_skipping is not null) return;
        _out.Append(Encode(text));
    }

    private void Tag(HtmlTagToken tag)
    {
        var name = tag.Name;

        // Inside something being dropped, the only tag that matters is the one that ends it.
        if (_skipping is not null)
        {
            if (!string.Equals(name, _skipping, StringComparison.OrdinalIgnoreCase)) return;

            if (tag.IsEndTag)
            {
                _skipDepth--;
                if (_skipDepth > 0) return;

                if (_inStyle) CloseStyle();
                _skipping = null;
            }
            else if (!tag.IsEmptyElement)
            {
                _skipDepth++;
            }

            return;
        }

        // A stylesheet is dropped as an element and scrubbed as text: the rules are worth
        // keeping and everything dangerous in them is not.
        if (string.Equals(name, "style", StringComparison.OrdinalIgnoreCase) && !tag.IsEndTag)
        {
            _skipping = name;
            _skipDepth = 1;
            _inStyle = true;
            _style.Clear();
            return;
        }

        if (Dropped.Contains(name))
        {
            if (tag.IsEndTag || tag.IsEmptyElement || Void.Contains(name)) return;
            _skipping = name;
            _skipDepth = 1;
            return;
        }

        // A legacy display element: the header fields an encrypted message's composer kept off the
        // outside, written into the body for a client that cannot read them anywhere else. This one
        // can, so RFC 9788 §4.5.3 says not to draw it — and names a sanitizer as where that belongs.
        // Only ever inside a cryptographic payload; see RenderOptions.HideLegacyDisplay.
        if (options.HideLegacyDisplay && !tag.IsEndTag && !tag.IsEmptyElement && LegacyDisplay(tag))
        {
            _skipping = name;
            _skipDepth = 1;
            return;
        }

        if (!Allowed.Contains(name)) return;

        // html, head and body would nest inside the document this is wrapped in. head is dropped
        // as a tag rather than with its contents on purpose: everything in one that must not
        // survive — title, meta, link, base, script — is named above and dropped on its own
        // merits, and the one thing worth keeping is the stylesheet, which is where most real
        // mail keeps its styling.
        if (name is "html" or "head" or "body") return;

        if (tag.IsEndTag)
        {
            _out.Append("</").Append(name).Append('>');
            return;
        }

        _out.Append('<').Append(name);
        foreach (var attribute in tag.Attributes) Attribute(name, attribute);
        _out.Append(tag.IsEmptyElement ? " />" : ">");
    }

    /// <summary>
    /// Whether this tag opens the <c>div</c> RFC 9788 §4.5.3.3 names.
    /// </summary>
    /// <remarks>
    /// The class is matched as one of the element's classes rather than as the whole attribute, an
    /// element being allowed more than one; the name is spelled out here rather than shared with the
    /// security project, which this one does not reference and should not.
    /// </remarks>
    private static bool LegacyDisplay(HtmlTagToken tag)
    {
        if (!string.Equals(tag.Name, "div", StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var attribute in tag.Attributes)
        {
            if (!string.Equals(attribute.Name, "class", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var css in (attribute.Value ?? string.Empty).Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(css, "header-protection-legacy-display", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void CloseStyle()
    {
        _inStyle = false;

        var css = CssScrubber.Scrub(_style.ToString(), url => Resolve(url, BlockedResourceKind.Style), options.Isolated);
        if (css.Trim().Length == 0) return;

        // Written back as a stylesheet rather than folded into the elements: keeping the
        // cascade is the whole reason for not dropping it.
        _out.Append("<style>").Append(css).Append("</style>");
    }

    private void Attribute(string element, HtmlAttribute attribute)
    {
        var name = attribute.Name;
        var value = attribute.Value ?? string.Empty;

        // Event handlers are the whole of the scripting surface in a document with no script
        // elements left, and there are too many to name individually.
        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(name, "style", StringComparison.OrdinalIgnoreCase))
        {
            var css = CssScrubber.Scrub(value, url => Resolve(url, BlockedResourceKind.Style), options.Isolated);
            if (css.Trim().Length > 0) Write("style", css);
            return;
        }

        if (string.Equals(name, "href", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(element, "a", StringComparison.OrdinalIgnoreCase)) return;
            if (!UrlSafety.IsSafeLink(value)) return;

            // A message in Junk keeps its link text and loses the destination: the anchor is
            // written without an href, which every engine draws as plain text.
            if (options.DisableLinks) return;

            Write("href", value);

            // Opened outside the pane, which the navigation policy enforces; the attributes
            // say so as well, for an engine that honours them.
            Write("target", "_blank");
            Write("rel", "noopener noreferrer");
            return;
        }

        if (ResourceAttributes.Contains(name))
        {
            var kind = string.Equals(name, "src", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(element, "img", StringComparison.OrdinalIgnoreCase)
                ? BlockedResourceKind.Image
                : BlockedResourceKind.Style;

            if (Resolve(value, kind) is { } resolved) Write(name, resolved);
            return;
        }

        if (!AllowedAttributes.Contains(name)) return;
        if (UrlSafety.IsDangerousScheme(value)) return;

        Write(name, value);
    }

    /// <summary>
    /// What a resource reference becomes: the bytes, a placeholder, or nothing.
    /// </summary>
    /// <remarks>
    /// The one place a URL in a message is turned into a URL in the document, so it is also the
    /// one place the tracker report is written. A reference that is not resolvable here does
    /// not survive into the output at all.
    /// </remarks>
    private string? Resolve(string url, BlockedResourceKind kind)
    {
        var trimmed = url.Trim();
        if (trimmed.Length == 0) return null;

        if (UrlSafety.IsDangerousScheme(trimmed)) return null;

        // A part the message carries. Only a picture: every attribute that reaches here draws an
        // image, and inlining a part by the type it declares would let a cid: reference to a
        // text/html part become a data: document — which is the thing the rule below exists to
        // refuse, reached by the other road. A cid: also resolves by file name, so without this
        // `<img src="cid:agenda.pdf">` inlines the attachment.
        if (resources.Resolve(trimmed) is { } part)
        {
            return part.ContentType?.MediaType is { } media
                   && media.Equals("image", StringComparison.OrdinalIgnoreCase)
                ? ResourceMap.DataUri(part, options.MaxInlineBytes)
                : null;
        }

        // Already inline. Images only: a data: URI naming any other type is a document, and a
        // document inside a document is how a sanitizer gets walked around.
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return UrlSafety.IsInlinedImage(trimmed) ? trimmed : null;
        }

        if (!UrlSafety.IsRemote(trimmed)) return null;

        // Fetched already, by the application rather than by the engine.
        if (options.Inlined.TryGetValue(trimmed, out var inlined)) return inlined;

        _blocked.Add(new BlockedResource(trimmed, UrlSafety.HostOf(trimmed), kind));
        return kind == BlockedResourceKind.Image ? Placeholder.DataUri(options.Style) : null;
    }

    private void Write(string name, string value)
    {
        _out.Append(' ').Append(name).Append("=\"").Append(Encode(value)).Append('"');
    }

    private static string Encode(string value)
    {
        var encoded = new StringBuilder(value.Length + 8);

        foreach (var c in value)
        {
            switch (c)
            {
                case '&': encoded.Append("&amp;"); break;
                case '<': encoded.Append("&lt;"); break;
                case '>': encoded.Append("&gt;"); break;
                case '"': encoded.Append("&quot;"); break;
                case '\'': encoded.Append("&#39;"); break;
                default: encoded.Append(c); break;
            }
        }

        return encoded.ToString();
    }
}
