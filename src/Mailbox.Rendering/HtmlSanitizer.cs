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
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "html", "i", "img",
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
        "fieldset", "legend", "base", "link", "meta", "title", "head", "svg", "math",
        "template", "canvas", "audio", "video", "source", "track", "param", "map", "area",
        "portal", "slot", "dialog", "marquee",
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

        return _out.ToString();
    }

    private void Text(HtmlToken token)
    {
        var text = token.ToString() ?? string.Empty;

        if (_inStyle)
        {
            _style.Append(text);
            return;
        }

        if (_skipping is not null) return;
        _out.Append(text);
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
            if (tag.IsEndTag || tag.IsEmptyElement) return;
            _skipping = name;
            _skipDepth = 1;
            return;
        }

        if (!Allowed.Contains(name)) return;

        // html and body would nest inside the document this is wrapped in.
        if (name is "html" or "body") return;

        if (tag.IsEndTag)
        {
            _out.Append("</").Append(name).Append('>');
            return;
        }

        _out.Append('<').Append(name);
        foreach (var attribute in tag.Attributes) Attribute(name, attribute);
        _out.Append(tag.IsEmptyElement ? " />" : ">");
    }

    private void CloseStyle()
    {
        _inStyle = false;

        var css = CssScrubber.Scrub(_style.ToString(), url => Resolve(url, BlockedResourceKind.Style));
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
            var css = CssScrubber.Scrub(value, url => Resolve(url, BlockedResourceKind.Style));
            if (css.Trim().Length > 0) Write("style", css);
            return;
        }

        if (string.Equals(name, "href", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(element, "a", StringComparison.OrdinalIgnoreCase)) return;
            if (!UrlSafety.IsSafeLink(value)) return;

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

        if (resources.Resolve(trimmed) is { } part)
        {
            return ResourceMap.DataUri(part, options.MaxInlineBytes);
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
