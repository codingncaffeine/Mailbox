using System.Globalization;
using System.Xml.Linq;
using static Mailbox.Core.Feeds.FeedXml;

namespace Mailbox.Core.Feeds;

/// <summary>One subscription in an outline: the feed, and the heading it was filed under.</summary>
/// <param name="Category">
/// The heading it sat under, or empty for one at the top level. Nested headings are joined with
/// a slash, so a three-deep outline survives a round trip through a two-level reader.
/// </param>
public sealed record OpmlEntry(string Title, string Url, string Category = "", string SiteUrl = "");

/// <summary>
/// OPML: the file every reader imports and exports, and the only way anybody moves between them.
/// </summary>
/// <remarks>
/// This is how somebody arrives from Feedly, Inoreader, NetNewsWire or Thunderbird with two
/// hundred subscriptions, and how they leave again — which matters as much: a reader that cannot
/// be left is a reader that has to be trusted, and §7.6a's argument about mail applies exactly
/// as well to a subscription list.
/// <para>
/// The format is loose in practice. The specification says <c>xmlUrl</c> and readers write
/// <c>xmlurl</c>, <c>XMLURL</c> and <c>url</c>; headings are sometimes <c>title</c> and sometimes
/// <c>text</c>; some exports nest three deep and some are flat. All of it is read, and what is
/// written is the strict form.
/// </para>
/// </remarks>
public static class Opml
{
    /// <summary>The subscriptions the outline holds, flattened, in the order they appear.</summary>
    /// <exception cref="FormatException">The text is not an outline.</exception>
    public static IReadOnlyList<OpmlEntry> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var root = FeedXml.Load(text).Root ?? throw new FormatException("The text is not an outline.");
        if (!Is(root, "opml") && !Is(root, "outline"))
        {
            throw new FormatException("The text is not an outline.");
        }

        var found = new List<OpmlEntry>();
        var body = Child(root, "body") ?? root;

        foreach (var outline in Children(body, "outline")) Walk(outline, string.Empty, found);

        return found;
    }

    private static void Walk(XElement outline, string category, List<OpmlEntry> found)
    {
        var url = FirstAttribute(outline, "xmlUrl", "xmlurl", "url");
        var title = FirstAttribute(outline, "title", "text");

        if (url.Length > 0)
        {
            if (!found.Any(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new OpmlEntry(
                    title.Length > 0 ? title : url,
                    url,
                    category,
                    FirstAttribute(outline, "htmlUrl", "htmlurl")));
            }

            // A feed outline with children is not a heading; a handful of exports hang the
            // feed's own categories off it, and treating those as subscriptions would file
            // every article twice.
            return;
        }

        var heading = title.Length > 0
            ? category.Length > 0 ? $"{category}/{title}" : title
            : category;

        foreach (var child in Children(outline, "outline")) Walk(child, heading, found);
    }

    private static string FirstAttribute(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (Attribute(element, name) is { Length: > 0 } value) return value;
        }

        return string.Empty;
    }

    /// <summary>The outline for a set of subscriptions, grouped under their headings.</summary>
    /// <param name="title">What the file calls itself.</param>
    /// <param name="now">
    /// The stamp in the head. Passed in rather than read from the clock so an export is
    /// reproducible, which is what lets a test compare two of them.
    /// </param>
    public static string Write(string title, IEnumerable<OpmlEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var body = new XElement("body");

        // Ungrouped first, then each heading in the order its first feed appears: an export that
        // reorders somebody's list every time it is written is one they cannot diff.
        var all = entries.ToList();

        foreach (var entry in all.Where(e => e.Category.Length == 0)) body.Add(Outline(entry));

        foreach (var group in all.Where(e => e.Category.Length > 0)
                     .GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase))
        {
            var heading = new XElement("outline",
                new XAttribute("text", group.Key),
                new XAttribute("title", group.Key));

            foreach (var entry in group) heading.Add(Outline(entry));
            body.Add(heading);
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("opml",
                new XAttribute("version", "2.0"),
                new XElement("head",
                    new XElement("title", title),
                    new XElement("dateCreated", now.ToString("r", CultureInfo.InvariantCulture))),
                body));

        return document.Declaration + Environment.NewLine + document.ToString(SaveOptions.None) + Environment.NewLine;
    }

    private static XElement Outline(OpmlEntry entry)
    {
        var outline = new XElement("outline",
            new XAttribute("type", "rss"),
            new XAttribute("text", entry.Title),
            new XAttribute("title", entry.Title),
            new XAttribute("xmlUrl", entry.Url));

        if (entry.SiteUrl.Length > 0) outline.Add(new XAttribute("htmlUrl", entry.SiteUrl));

        return outline;
    }
}
