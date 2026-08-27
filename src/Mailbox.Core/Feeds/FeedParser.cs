using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using static Mailbox.Core.Feeds.FeedXml;

namespace Mailbox.Core.Feeds;

/// <summary>
/// Reads a feed, whichever of the four shapes it is written in.
/// </summary>
/// <remarks>
/// RSS 2.0, RSS 1.0 (RDF), Atom and JSON Feed. They differ in their element names and in nothing
/// else that matters here: a feed has a title and a list of entries, and an entry has an
/// identity, a title, a date, a link and some markup.
/// <para>
/// Namespace-agnostic on purpose — matched by local name — because feeds in the wild put their
/// elements in namespaces the specification does not mention, and a reader that insists on the
/// right namespace reads nothing at all. The exception is the extension modules (Media RSS,
/// iTunes, Dublin Core, syndication), where the namespace is the only thing telling
/// <c>media:content</c> from Atom's <c>content</c>; those are matched loosely on a fragment of
/// their URI. What is <em>not</em> relaxed is the parse itself: text that is not a feed is
/// refused rather than guessed at, as the calendar and vCard codecs refuse theirs — though what
/// counts as XML is generous, for the reasons in <see cref="FeedXml"/>.
/// </para>
/// <para>
/// Every address a parse hands back is absolute where it can be made so, resolved through any
/// <c>xml:base</c> in scope and then against the address the feed was fetched from. A feed whose
/// entries link to "/2026/08/post" is common, and a reader that files those verbatim gives every
/// article a link that goes nowhere.
/// </para>
/// </remarks>
public static partial class FeedParser
{
    /// <summary>Media RSS, matched on the one word its four published namespaces share.</summary>
    private const string MediaModule = "mrss";

    /// <summary>The alternative spelling of the same module, used by the RSS Advisory Board.</summary>
    private const string MediaModuleAlternative = "media";

    /// <summary>The feed the text holds.</summary>
    /// <param name="text">The document, as text. Decoding bytes to text is the caller's job.</param>
    /// <param name="baseUrl">
    /// Where the feed was fetched from, for resolving relative addresses. Null when it is not
    /// known, in which case a relative address is kept as written.
    /// </param>
    /// <exception cref="FormatException">The text is not a feed this can read.</exception>
    public static FeedChannel Parse(string text, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var origin = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) ? parsed : null;

        // JSON Feed is told apart by the only thing that could tell it apart. A document that
        // opens with a brace is not XML and never will be, so this is a fork rather than a guess.
        return text.AsSpan().TrimStart().StartsWith("{")
            ? Json(text, origin)
            : Xml(text, origin);
    }

    // ---- XML: RSS, RDF and Atom ---------------------------------------------------------------

    private static FeedChannel Xml(string text, Uri? origin)
    {
        var root = FeedXml.Load(text).Root ?? throw new FormatException("The text is not a feed.");

        // Atom keeps its entries at the root; RSS and RDF wrap theirs in a channel.
        var channel = Child(root, "channel") ?? root;
        var entries = Entries(root);

        if (entries.Count == 0 && !IsFeedRoot(root))
        {
            throw new FormatException("The text is not a feed.");
        }

        return new FeedChannel(
            Text(Child(channel, "title")) is { Length: > 0 } title ? Plain(title) : "Feed",
            LinkOf(channel, origin),
            [.. entries.Select(e => Item(e, origin))])
        {
            Description = Text(Child(channel, "description")) is { Length: > 0 } described
                ? described
                : Text(Child(channel, "subtitle")),
            IconUrl = IconOf(channel, origin),
            SelfUrl = Relation(channel, "self", origin),
            UpdateLimit = LimitOf(channel),
            Language = Text(Child(channel, "language")),
        };
    }

    private static bool IsFeedRoot(XElement root)
        => Name(root).ToLowerInvariant() is "rss" or "feed" or "rdf";

    /// <summary>
    /// Every entry in the document, in the order it was written.
    /// </summary>
    /// <remarks>
    /// Descendants rather than children because RDF puts its items beside the channel rather than
    /// inside it, and because a handful of publishers nest theirs one level deeper than the
    /// specification allows. What that would otherwise sweep up is an <c>&lt;item&gt;</c> inside
    /// an entry's own markup — a feed about feeds, or an escaped example — so anything standing
    /// inside a content element is not an entry.
    /// </remarks>
    private static List<XElement> Entries(XElement root)
        => [.. root.Descendants()
            .Where(e => (Is(e, "item") || Is(e, "entry")) && !InsideContent(e))];

    private static bool InsideContent(XElement element)
        => element.Ancestors().Any(a =>
            Is(a, "content") || Is(a, "description") || Is(a, "encoded") || Is(a, "summary"));

    private static FeedItem Item(XElement entry, Uri? origin)
    {
        var link = LinkOf(entry, origin);
        var html = MarkupOf(entry);
        var published = FirstDate(entry, "pubDate", "published", "date", "created");
        var updated = FirstDate(entry, "updated", "modified");
        var title = Plain(Text(Child(entry, "title")));

        var enclosures = EnclosuresOf(entry, origin);

        return new FeedItem(
            Identity(entry, link, title, html, updated ?? published),
            title,
            AuthorOf(entry),
            published,
            link,
            html)
        {
            Updated = updated,
            Summary = SummaryOf(entry, html),
            Categories = CategoriesOf(entry),
            Enclosures = enclosures,
            ImageUrl = ImageOf(entry, enclosures, origin),
        };
    }

    /// <summary>
    /// What tells this entry from the others, and from the copy of it already delivered.
    /// </summary>
    /// <remarks>
    /// A GUID, an Atom id, the link — and, when a feed offers none of the three, a fingerprint of
    /// what the entry says. The last case is not hypothetical: a feed generated from a database
    /// query commonly has no per-entry identity at all, and an entry with no identity was
    /// previously dropped on the floor, so the feed appeared to deliver nothing.
    /// </remarks>
    private static string Identity(XElement entry, string link, string title, string html, DateTimeOffset? stamp)
    {
        if (Text(Child(entry, "guid")) is { Length: > 0 } guid) return guid;
        if (Text(Child(entry, "id")) is { Length: > 0 } atom) return atom;
        if (Text(Child(entry, "identifier")) is { Length: > 0 } dublin) return dublin;
        if (link.Length > 0) return link;

        return FeedItem.Fingerprint(title, link, html, stamp);
    }

    /// <summary>
    /// The entry's markup: Atom's content, then its summary, then RSS's encoded content, then its
    /// description — the order every reader uses, richest first.
    /// </summary>
    private static string MarkupOf(XElement entry)
    {
        foreach (var name in (string[])["encoded", "content", "description", "summary"])
        {
            // Atom's content may point at the article instead of holding it; that is a link, not
            // markup, and taking its empty body would lose the description standing beside it.
            if (Child(entry, name) is not { } element) continue;
            if (Attribute(element, "src").Length > 0) continue;
            if (Markup(element) is { Length: > 0 } markup) return markup;
        }

        return string.Empty;
    }

    /// <summary>
    /// One element's markup, honouring what Atom says it is.
    /// </summary>
    /// <remarks>
    /// Three kinds. <c>xhtml</c> is markup written as markup, so its children are the content and
    /// its text alone would be the article with every tag stripped out. <c>text</c> is plain text,
    /// so it is escaped on the way in — without that, an entry whose summary contains a less-than
    /// sign loses everything after it when the reading pane renders it as a tag. Everything else,
    /// including RSS's description and the common <c>type="html"</c>, is markup already escaped
    /// once, which is what the element's text holds.
    /// </remarks>
    private static string Markup(XElement element) => Attribute(element, "type").ToLowerInvariant() switch
    {
        "xhtml" or "application/xhtml+xml" => Inner(element),
        "text" or "text/plain" => WebUtility.HtmlEncode(Text(element)),
        _ => Text(element),
    };

    /// <summary>The markup inside an element, unwrapping the single div Atom wraps XHTML in.</summary>
    private static string Inner(XElement element)
    {
        var content = element.Elements().Count() == 1 && Child(element, "div") is { } div ? div : element;
        return string.Concat(content.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))).Trim();
    }

    /// <summary>
    /// The entry's own short form, for the list's snippet line.
    /// </summary>
    /// <remarks>
    /// The publisher's summary when there is one standing beside a fuller body; otherwise the
    /// body with its markup taken off. A snippet is what makes an article list readable, and
    /// falling back rather than leaving it empty is what makes it readable for every feed rather
    /// than for the well-behaved half.
    /// </remarks>
    private static string SummaryOf(XElement entry, string html)
    {
        foreach (var name in (string[])["summary", "description", "subtitle"])
        {
            if (Child(entry, name) is not { } element) continue;

            var text = PlainText(Markup(element));
            if (text.Length > 0 && text != PlainText(html)) return text;
        }

        return PlainText(html);
    }

    /// <summary>Who wrote it: RSS says so plainly, Atom wraps a name in an author element.</summary>
    private static string AuthorOf(XElement entry)
    {
        if (Text(Child(entry, "creator")) is { Length: > 0 } creator) return Plain(creator);

        foreach (var name in (string[])["author", "contributor"])
        {
            if (Child(entry, name) is not { } author) continue;
            if (Text(Child(author, "name")) is { Length: > 0 } named) return Plain(named);
            if (Text(author) is { Length: > 0 } plain) return Plain(plain);
        }

        return string.Empty;
    }

    /// <summary>
    /// A title or a name as a reader should see it, undoing one layer of encoding the publisher
    /// applied on top of the one XML already undid.
    /// </summary>
    /// <remarks>
    /// A title is plain text by specification and HTML-escaped by practice: publishers write
    /// <c>&amp;amp;#8217;</c> where they mean an apostrophe, so what survives the XML parse is the
    /// literal text "&amp;#8217;" and the article arrives with that in its subject line. Seen on
    /// The Verge, and it is not unusual.
    /// <para>
    /// Only applied when something entity-shaped is still there after the XML parse, so a title
    /// that genuinely contains an ampersand keeps it: at that point the ampersand has already
    /// been decoded once and there is nothing left that looks like a reference.
    /// </para>
    /// </remarks>
    private static string Plain(string text)
        => text.Contains('&') && EntityShaped().IsMatch(text) ? WebUtility.HtmlDecode(text) : text;

    [System.Text.RegularExpressions.GeneratedRegex(@"&(#\d{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial System.Text.RegularExpressions.Regex EntityShaped();

    /// <summary>
    /// The tags the publisher filed the entry under. RSS writes them as text, Atom as a term
    /// attribute with an optional human-readable label.
    /// </summary>
    private static IReadOnlyList<string> CategoriesOf(XElement entry)
        => [.. Children(entry, "category")
            .Select(c => Attribute(c, "label") is { Length: > 0 } label ? label
                : Attribute(c, "term") is { Length: > 0 } term ? term
                : Text(c))
            .Concat(Children(entry, "subject").Select(Text))
            .Where(t => t.Length is > 0 and <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The files the entry carries: RSS's enclosure, Atom's enclosure link, and Media RSS's
    /// content — which is how video and podcast feeds have published for a decade.
    /// </summary>
    private static IReadOnlyList<FeedEnclosure> EnclosuresOf(XElement entry, Uri? origin)
    {
        var found = new List<FeedEnclosure>();

        void Add(string url, string type, string length, string title)
        {
            var absolute = Resolve(url, BaseOf(entry, origin));
            if (absolute.Length == 0 || found.Any(e => e.Url == absolute)) return;

            found.Add(new FeedEnclosure(
                absolute,
                type,
                long.TryParse(length, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 ? bytes : 0,
                title));
        }

        foreach (var enclosure in Children(entry, "enclosure"))
        {
            Add(Attribute(enclosure, "url"), Attribute(enclosure, "type"), Attribute(enclosure, "length"), string.Empty);
        }

        foreach (var link in Children(entry, "link").Where(l => Attribute(l, "rel") == "enclosure"))
        {
            Add(Attribute(link, "href"), Attribute(link, "type"), Attribute(link, "length"), Attribute(link, "title"));
        }

        foreach (var media in Media(entry, "content"))
        {
            Add(Attribute(media, "url"), MediaType(media), Attribute(media, "fileSize"), string.Empty);
        }

        return found;
    }

    /// <summary>
    /// The picture to show beside the entry.
    /// </summary>
    /// <remarks>
    /// A thumbnail the publisher chose, then a media item that says it is a picture, then an
    /// enclosure that is one, then the first image in the markup. The last is the case that makes
    /// an article list look like an article list for the many feeds that publish no metadata at
    /// all and simply open the body with the picture.
    /// </remarks>
    private static string ImageOf(XElement entry, IReadOnlyList<FeedEnclosure> enclosures, Uri? origin)
    {
        var relativeTo = BaseOf(entry, origin);

        foreach (var thumbnail in Media(entry, "thumbnail"))
        {
            if (Resolve(Attribute(thumbnail, "url"), relativeTo) is { Length: > 0 } url) return url;
        }

        foreach (var media in Media(entry, "content").Where(m => MediaType(m).StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            if (Resolve(Attribute(media, "url"), relativeTo) is { Length: > 0 } url) return url;
        }

        foreach (var itunes in Children(entry, "image"))
        {
            var url = Attribute(itunes, "href") is { Length: > 0 } href ? href : Text(Child(itunes, "url"));
            if (Resolve(url, relativeTo) is { Length: > 0 } resolved) return resolved;
        }

        if (enclosures.FirstOrDefault(e => e.IsImage) is { } picture) return picture.Url;

        return Resolve(FirstImageIn(MarkupOf(entry)), relativeTo);
    }

    /// <summary>The src of the first img tag in some markup, or empty.</summary>
    /// <remarks>
    /// A deliberately small reader rather than a parse: this runs over the body of every entry of
    /// every feed on every poll, it is looking for one attribute, and a wrong answer costs a
    /// missing thumbnail rather than a wrong article.
    /// </remarks>
    private static string FirstImageIn(string html)
    {
        var at = html.IndexOf("<img", StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var end = html.IndexOf('>', at);
            if (end < 0) break;

            var tag = html[at..end];
            var src = tag.IndexOf("src", StringComparison.OrdinalIgnoreCase);
            if (src > 0)
            {
                var quote = tag.IndexOfAny(['"', '\''], src);
                if (quote > 0 && quote - src <= 6)
                {
                    var close = tag.IndexOf(tag[quote], quote + 1);
                    if (close > quote) return WebUtility.HtmlDecode(tag[(quote + 1)..close]).Trim();
                }
            }

            at = html.IndexOf("<img", end, StringComparison.OrdinalIgnoreCase);
        }

        return string.Empty;
    }

    private static IEnumerable<XElement> Media(XElement entry, string name)
        => InModule(entry, name, MediaModule).Concat(InModule(entry, name, MediaModuleAlternative)).Distinct();

    /// <summary>
    /// What a Media RSS element says it is. The type attribute when it carries one, and the
    /// coarser medium when it does not — many feeds write only <c>medium="image"</c>.
    /// </summary>
    private static string MediaType(XElement media)
        => Attribute(media, "type") is { Length: > 0 } type
            ? type
            : Attribute(media, "medium") is { Length: > 0 } medium ? $"{medium.ToLowerInvariant()}/" : string.Empty;

    /// <summary>The feed's own picture: Atom's icon or logo, RSS's image, a podcast's artwork.</summary>
    private static string IconOf(XElement channel, Uri? origin)
    {
        var relativeTo = BaseOf(channel, origin);

        foreach (var name in (string[])["icon", "logo"])
        {
            if (Resolve(Text(Child(channel, name)), relativeTo) is { Length: > 0 } url) return url;
        }

        if (Child(channel, "image") is { } image)
        {
            var url = Text(Child(image, "url")) is { Length: > 0 } inner ? inner : Attribute(image, "href");
            if (Resolve(url, relativeTo) is { Length: > 0 } resolved) return resolved;
        }

        return string.Empty;
    }

    /// <summary>
    /// How often the publisher asks not to be asked again: RSS's <c>ttl</c> in minutes, or the
    /// syndication module's period divided by its frequency.
    /// </summary>
    private static TimeSpan? LimitOf(XElement channel)
    {
        if (int.TryParse(Text(Child(channel, "ttl")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttl) && ttl > 0)
        {
            return TimeSpan.FromMinutes(ttl);
        }

        var period = Text(Child(channel, "updatePeriod")).ToLowerInvariant() switch
        {
            "hourly" => TimeSpan.FromHours(1),
            "daily" => TimeSpan.FromDays(1),
            "weekly" => TimeSpan.FromDays(7),
            "monthly" => TimeSpan.FromDays(30),
            "yearly" => TimeSpan.FromDays(365),
            _ => TimeSpan.Zero,
        };

        if (period == TimeSpan.Zero) return null;

        var frequency = int.TryParse(Text(Child(channel, "updateFrequency")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var times) && times > 0
            ? times
            : 1;

        return period / frequency;
    }

    /// <summary>The href of an Atom link with the given relation, absolute.</summary>
    private static string Relation(XElement element, string relation, Uri? origin)
        => Resolve(
            Children(element, "link")
                .Where(l => string.Equals(Attribute(l, "rel"), relation, StringComparison.OrdinalIgnoreCase))
                .Select(l => Attribute(l, "href"))
                .FirstOrDefault(h => h.Length > 0) ?? string.Empty,
            BaseOf(element, origin));

    /// <summary>
    /// The element's own address. RSS puts it in the element's text and Atom in an href
    /// attribute, and Atom may offer several — the one that says it is the entry itself wins.
    /// </summary>
    private static string LinkOf(XElement element, Uri? origin)
    {
        var links = Children(element, "link").ToList();
        var relativeTo = BaseOf(element, origin);

        // A permalink GUID is a link in everything but name, and some feeds publish nothing else.
        if (links.Count == 0)
        {
            return Child(element, "guid") is { } guid && !string.Equals(Attribute(guid, "isPermaLink"), "false", StringComparison.OrdinalIgnoreCase)
                   && Text(guid).StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? Resolve(Text(guid), relativeTo)
                : string.Empty;
        }

        var alternate = links.FirstOrDefault(l => Attribute(l, "rel") is "alternate" or "")
                        ?? links.FirstOrDefault(l => Attribute(l, "rel") is not ("self" or "hub" or "enclosure" or "replies"))
                        ?? links[0];

        var href = Attribute(alternate, "href") is { Length: > 0 } value ? value : Text(alternate);
        return Resolve(href, relativeTo);
    }

    private static DateTimeOffset? FirstDate(XElement entry, params string[] names)
    {
        foreach (var name in names)
        {
            if (FeedDates.Parse(Text(Child(entry, name))) is { } parsed) return parsed;
        }

        return null;
    }

    // ---- Addresses ----------------------------------------------------------------------------

    /// <summary>
    /// What a relative address in this element is relative to: every <c>xml:base</c> from the
    /// root down, applied in turn, over the address the feed was fetched from.
    /// </summary>
    private static Uri? BaseOf(XElement element, Uri? origin)
    {
        var result = origin;

        foreach (var ancestor in element.AncestorsAndSelf().Reverse())
        {
            if (Attribute(ancestor, "base") is not { Length: > 0 } declared) continue;
            if (Uri.TryCreate(result, declared, out var combined)) result = combined;
        }

        return result;
    }

    /// <summary>
    /// An absolute address, or the value as written when it cannot be made one.
    /// </summary>
    /// <remarks>
    /// An address that is already absolute is returned exactly as it was written rather than
    /// round-tripped through <see cref="Uri"/>, which would normalise the case of the host and
    /// re-encode the path — harmless for fetching it, and not harmless for an identity that is
    /// compared against what was stored last time.
    /// </remarks>
    private static string Resolve(string value, Uri? relativeTo)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (HasScheme(trimmed)) return trimmed;

        return relativeTo is not null && Uri.TryCreate(relativeTo, trimmed, out var absolute)
            ? absolute.AbsoluteUri
            : trimmed;
    }

    /// <summary>
    /// True when the address carries a scheme of its own, and is therefore already absolute.
    /// </summary>
    /// <remarks>
    /// Read off the text rather than asked of <see cref="Uri"/>, which answers this question
    /// differently on different platforms: on a Unix machine <c>Uri.TryCreate</c> reads a leading
    /// slash as an absolute <em>file</em> path and says yes, so every relative "/2026/08/post" a
    /// feed publishes would be taken for absolute and never resolved — every article in the feed
    /// filed with a link that goes nowhere, on Linux only. A scheme is what RFC 3986 says it is:
    /// a letter, then letters, digits and three punctuation marks, then a colon.
    /// </remarks>
    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(value[0])) return false;

        for (var at = 1; at < colon; at++)
        {
            if (!char.IsAsciiLetterOrDigit(value[at]) && value[at] is not ('+' or '-' or '.')) return false;
        }

        return true;
    }

    // ---- JSON Feed ------------------------------------------------------------------------------

    /// <summary>
    /// JSON Feed 1.1, which is the same idea written in the format most publishing software
    /// already speaks.
    /// </summary>
    private static FeedChannel Json(string text, Uri? origin)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The text is not a feed.", ex);
        }

        if (node is not JsonObject feed || feed["items"] is not JsonArray items)
        {
            throw new FormatException("The text is not a feed.");
        }

        return new FeedChannel(
            Str(feed, "title") is { Length: > 0 } title ? title : "Feed",
            Str(feed, "home_page_url"),
            [.. items.OfType<JsonObject>().Select(i => JsonItem(i, origin))])
        {
            Description = Str(feed, "description"),
            IconUrl = Str(feed, "icon") is { Length: > 0 } icon ? icon : Str(feed, "favicon"),
            SelfUrl = Str(feed, "feed_url"),
            Language = Str(feed, "language"),
        };
    }

    private static FeedItem JsonItem(JsonObject entry, Uri? origin)
    {
        var link = Resolve(Str(entry, "url") is { Length: > 0 } url ? url : Str(entry, "external_url"), origin);
        var html = Str(entry, "content_html") is { Length: > 0 } markup
            ? markup
            : WebUtility.HtmlEncode(Str(entry, "content_text"));
        var title = Plain(Str(entry, "title"));
        var published = FeedDates.Parse(Str(entry, "date_published"));
        var updated = FeedDates.Parse(Str(entry, "date_modified"));

        var attachments = entry["attachments"] is JsonArray files
            ? files.OfType<JsonObject>()
                .Select(f => new FeedEnclosure(
                    Resolve(Str(f, "url"), origin),
                    Str(f, "mime_type"),
                    f["size_in_bytes"]?.GetValue<long?>() ?? 0,
                    Str(f, "title")))
                .Where(f => f.Url.Length > 0)
                .ToList()
            : [];

        return new FeedItem(
            Str(entry, "id") is { Length: > 0 } id ? id : link.Length > 0 ? link : FeedItem.Fingerprint(title, link, html, updated ?? published),
            title,
            JsonAuthor(entry),
            published,
            link,
            html)
        {
            Updated = updated,
            Summary = Str(entry, "summary") is { Length: > 0 } summary ? summary : PlainText(html),
            Categories = entry["tags"] is JsonArray tags
                ? [.. tags.Select(t => t?.GetValue<string>() ?? string.Empty).Where(t => t.Length > 0)]
                : [],
            Enclosures = attachments,
            ImageUrl = Resolve(
                Str(entry, "image") is { Length: > 0 } image ? image : Str(entry, "banner_image"),
                origin) is { Length: > 0 } picture
                ? picture
                : attachments.FirstOrDefault(a => a.IsImage)?.Url ?? Resolve(FirstImageIn(html), origin),
        };
    }

    /// <summary>1.1 gives an entry a list of authors; 1.0 gave it one. Both are read.</summary>
    private static string JsonAuthor(JsonObject entry)
    {
        if (entry["authors"] is JsonArray authors
            && authors.OfType<JsonObject>().Select(a => Str(a, "name")).FirstOrDefault(n => n.Length > 0) is { Length: > 0 } named)
        {
            return named;
        }

        return entry["author"] is JsonObject author ? Str(author, "name") : string.Empty;
    }

    private static string Str(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;

    // ---- Text -----------------------------------------------------------------------------------

    /// <summary>
    /// Markup with its tags taken off and its whitespace collapsed: the snippet a list shows
    /// under an article's title.
    /// </summary>
    /// <remarks>
    /// Not a sanitizer and not a renderer — what reaches the reading pane goes through the real
    /// one, as every other message's markup does. This is for the one line of text under a title,
    /// where a tag would show as a tag.
    /// </remarks>
    public static string PlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var text = new StringBuilder(html.Length);
        var depth = 0;
        var skipping = false;

        for (var at = 0; at < html.Length; at++)
        {
            var c = html[at];

            if (c == '<')
            {
                // A script or a style holds text nobody wants to read; the rest of a tag is
                // structure. Both come out, and what is between the tags stays.
                skipping = Opens(html, at, "script") || Opens(html, at, "style");
                depth++;
                continue;
            }

            if (c == '>')
            {
                if (depth > 0) depth--;
                if (depth == 0 && text.Length > 0 && text[^1] != ' ') text.Append(' ');
                continue;
            }

            if (depth == 0 && !skipping) text.Append(c);
            if (skipping && c == '>') skipping = false;
        }

        return string.Join(' ', WebUtility.HtmlDecode(text.ToString())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool Opens(string html, int at, string tag)
        => at + tag.Length + 1 < html.Length
           && string.Compare(html, at + 1, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0;
}
