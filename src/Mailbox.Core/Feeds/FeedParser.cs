using System.Globalization;
using System.Xml.Linq;

namespace Mailbox.Core.Feeds;

/// <summary>One entry of a feed, in the terms a message is written in.</summary>
/// <param name="Id">
/// What the feed calls this entry — a GUID, an Atom id, or the link. It is what tells a second
/// download of the same feed that this one has already been delivered.
/// </param>
/// <param name="Published">When the entry says it was published, or null when it does not say.</param>
/// <param name="Html">The entry's own markup, which is what the reading pane renders.</param>
public sealed record FeedItem(
    string Id,
    string Title,
    string Author,
    DateTimeOffset? Published,
    string Link,
    string Html);

/// <summary>A feed: what it is called, where it points, and what is in it.</summary>
public sealed record FeedChannel(string Title, string Link, IReadOnlyList<FeedItem> Items);

/// <summary>
/// Reads a feed, whichever of the three shapes it is written in.
/// </summary>
/// <remarks>
/// RSS 2.0, RSS 1.0 (RDF) and Atom, because a reader that handles one of them handles about half
/// the web. They differ in their element names and in nothing else that matters here: a feed has a
/// title and a list of entries, and an entry has an identity, a title, a date, a link and some
/// markup.
/// <para>
/// Namespace-agnostic on purpose — matched by local name — because feeds in the wild put their
/// elements in namespaces the specification does not mention, and a reader that insists on the
/// right namespace reads nothing at all. What is <em>not</em> relaxed is the parse itself: text
/// that is not XML is refused rather than guessed at, as the calendar and vCard codecs refuse
/// theirs.
/// </para>
/// </remarks>
public static class FeedParser
{
    /// <summary>The feed the text holds.</summary>
    /// <exception cref="FormatException">The text is not a feed this can read.</exception>
    public static FeedChannel Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.None);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new FormatException("The text is not a feed.", ex);
        }

        var root = document.Root ?? throw new FormatException("The text is not a feed.");

        // Atom keeps its entries at the root; RSS and RDF wrap theirs in a channel.
        var channel = Child(root, "channel") ?? root;
        var entries = Descendants(root, "item").Concat(Descendants(root, "entry")).ToList();

        if (entries.Count == 0 && Name(root) is not ("rss" or "feed" or "rdf"))
        {
            throw new FormatException("The text is not a feed.");
        }

        return new FeedChannel(
            Text(Child(channel, "title")) is { Length: > 0 } title ? title : "Feed",
            LinkOf(channel),
            [.. entries.Select(Item)]);
    }

    private static FeedItem Item(XElement entry)
    {
        var link = LinkOf(entry);
        var id = Text(Child(entry, "guid")) is { Length: > 0 } guid ? guid
            : Text(Child(entry, "id")) is { Length: > 0 } atom ? atom
            : link;

        // Atom's content, then its summary, then RSS's encoded content, then its description —
        // the order of preference every reader uses, richest first.
        var html = Text(Child(entry, "encoded")) is { Length: > 0 } encoded ? encoded
            : Text(Child(entry, "content")) is { Length: > 0 } content ? content
            : Text(Child(entry, "description")) is { Length: > 0 } description ? description
            : Text(Child(entry, "summary"));

        return new FeedItem(
            id.Length > 0 ? id : Text(Child(entry, "title")),
            Text(Child(entry, "title")),
            AuthorOf(entry),
            DateOf(entry),
            link,
            html);
    }

    /// <summary>Who wrote it: RSS says so plainly, Atom wraps a name in an author element.</summary>
    private static string AuthorOf(XElement entry)
    {
        if (Text(Child(entry, "creator")) is { Length: > 0 } creator) return creator;
        if (Child(entry, "author") is not { } author) return string.Empty;
        return Text(Child(author, "name")) is { Length: > 0 } named ? named : Text(author);
    }

    /// <summary>
    /// The entry's own address. RSS puts it in the element's text and Atom in an href attribute,
    /// and Atom may offer several — the one that says it is the entry itself wins.
    /// </summary>
    private static string LinkOf(XElement element)
    {
        var links = Children(element, "link").ToList();
        if (links.Count == 0) return string.Empty;

        var alternate = links.FirstOrDefault(l => Attribute(l, "rel") is "alternate" or "") ?? links[0];
        return Attribute(alternate, "href") is { Length: > 0 } href ? href : Text(alternate);
    }

    /// <summary>
    /// When it was published. RFC 822 in RSS, RFC 3339 in Atom, and neither reliably.
    /// </summary>
    private static DateTimeOffset? DateOf(XElement entry)
    {
        foreach (var name in new[] { "pubDate", "published", "updated", "date" })
        {
            if (Text(Child(entry, name)) is not { Length: > 0 } value) continue;

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    // ---- Namespace-agnostic XML ------------------------------------------------------------------

    private static string Name(XElement element) => element.Name.LocalName.ToLowerInvariant();

    private static XElement? Child(XElement? element, string name)
        => Children(element, name).FirstOrDefault();

    private static IEnumerable<XElement> Children(XElement? element, string name)
        => element?.Elements().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
           ?? [];

    private static IEnumerable<XElement> Descendants(XElement element, string name)
        => element.Descendants().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static string Attribute(XElement element, string name)
        => element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value
           ?? string.Empty;

    private static string Text(XElement? element) => element?.Value.Trim() ?? string.Empty;
}
