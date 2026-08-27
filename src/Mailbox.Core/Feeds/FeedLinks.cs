namespace Mailbox.Core.Feeds;

/// <summary>A feed a page advertises, or a place one was found.</summary>
/// <param name="Url">Where the feed is, absolute.</param>
/// <param name="Title">What the page calls it, which is often the only way to tell two apart.</param>
/// <param name="MediaType">What the page says it is, empty when it does not say.</param>
public sealed record DiscoveredFeed(string Url, string Title = "", string MediaType = "")
{
    /// <summary>What to show when there is nothing better: the address without its scheme.</summary>
    public string Label => Title is { Length: > 0 } named
        ? named
        : Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? Url[8..]
        : Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Url[7..]
        : Url;
}

/// <summary>
/// Finding the feed behind an address a reader actually has.
/// </summary>
/// <remarks>
/// Nobody knows the address of a feed. They know the address of a site, because that is what is
/// in the browser's bar and on the back of the magazine — so "paste the location of the RSS Feed"
/// is a question most people cannot answer, and the reader that answers it for them is the one
/// they keep. This is the parsing half: what a page advertises, and where to look when it
/// advertises nothing. Fetching is <c>FeedFinder</c>'s, in the protocols layer.
/// <para>
/// The scan is deliberately a scan rather than an HTML parse. It is looking for one element with
/// three attributes in a document it will never render, the failure mode is finding no feed
/// rather than finding a wrong one, and an HTML parser is a large dependency to take on for that.
/// </para>
/// </remarks>
public static class FeedLinks
{
    /// <summary>The media types a page uses to advertise a feed.</summary>
    private static readonly string[] FeedTypes =
    [
        "application/rss+xml",
        "application/atom+xml",
        "application/feed+json",
        "application/json",
        "application/rdf+xml",
        "text/xml",
        "application/xml",
    ];

    /// <summary>
    /// The paths worth trying on a site that advertises nothing, in the order they are worth
    /// trying — which is roughly the order the publishing software people use puts them at.
    /// </summary>
    private static readonly string[] Guesses =
    [
        // The four that cover most of the web, first.
        "/feed", "/rss", "/feed.xml", "/rss.xml",

        // The static-site generators.
        "/index.xml", "/atom.xml", "/feed.atom", "/rss.json", "/feed.json",

        // The blogging platforms and the engines behind most publications.
        "/feeds/posts/default", "/?feed=rss2", "/rss/index.xml", "/feeds/all.atom.xml",
        "/atom", "/rss/all.xml", "/feeds/rss",

        // Where a publication that has more than one puts the main one.
        "/blog/feed", "/blog/rss", "/news/feed", "/articles/feed", "/posts/feed",
        "/en/feed", "/index.rss",
    ];

    /// <summary>Every feed the page advertises, in the order it advertises them.</summary>
    /// <param name="html">The page's markup.</param>
    /// <param name="baseUrl">Where the page was fetched from, for resolving relative addresses.</param>
    public static IReadOnlyList<DiscoveredFeed> In(string html, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        var origin = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) ? parsed : null;
        var found = new List<DiscoveredFeed>();

        foreach (var tag in Tags(html, "link"))
        {
            var attributes = Attributes(tag);

            if (!attributes.TryGetValue("href", out var href) || href.Length == 0) continue;
            if (!attributes.TryGetValue("rel", out var rel)) continue;
            if (!rel.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(r => r.Equals("alternate", StringComparison.OrdinalIgnoreCase))) continue;

            attributes.TryGetValue("type", out var type);
            type ??= string.Empty;
            if (!FeedTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) continue;

            // application/json is only a feed when it says so or looks like one: it is also what
            // a page uses to advertise its structured data, and subscribing to that finds nothing.
            if (type.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                && !href.Contains("feed", StringComparison.OrdinalIgnoreCase)) continue;

            var url = Absolute(href, origin);
            if (url.Length == 0 || found.Any(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;

            attributes.TryGetValue("title", out var title);
            found.Add(new DiscoveredFeed(url, title ?? string.Empty, type));
        }

        return found;
    }

    /// <summary>Where to look on a site that advertises nothing, absolute.</summary>
    public static IReadOnlyList<string> Guessed(string siteUrl)
    {
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var site)) return [];

        var root = new Uri(site.GetLeftPart(UriPartial.Authority));
        var paths = new List<string>();

        // The directory the address itself is in comes first: a reader who pasted the address of
        // one blog on a shared host means that blog's feed, not the host's.
        if (site.AbsolutePath.Trim('/').Length > 0)
        {
            foreach (var guess in (string[])["feed", "rss", "feed.xml", "index.xml", "atom.xml"])
            {
                if (Uri.TryCreate(site, guess, out var nearby)) paths.Add(nearby.AbsoluteUri);
            }
        }

        foreach (var guess in Guesses)
        {
            if (Uri.TryCreate(root, guess, out var absolute) && !paths.Contains(absolute.AbsoluteUri))
            {
                paths.Add(absolute.AbsoluteUri);
            }
        }

        return paths;
    }

    /// <summary>
    /// The feeds a page links to in its body, for the sites that never learnt to write a link
    /// element.
    /// </summary>
    /// <remarks>
    /// A great many sites — especially older ones and hand-written ones — advertise nothing in
    /// their head and simply put "RSS" in the footer. That link is the feed, and it is the one
    /// thing a reader can see and the application could not, which is exactly the kind of gap
    /// that makes software feel stupid.
    /// <para>
    /// Deliberately narrow about what counts: an address that ends in a feed-shaped path, or an
    /// anchor whose own text says it is a feed. Following every link on a page would be a
    /// crawler, which this is not.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DiscoveredFeed> LinkedFrom(string html, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        var origin = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) ? parsed : null;
        var found = new List<DiscoveredFeed>();

        foreach (var (tag, text) in Anchors(html))
        {
            var attributes = Attributes(tag);
            if (!attributes.TryGetValue("href", out var href) || href.Length == 0) continue;

            if (!FeedShaped(href) && !SaysFeed(text) && !SaysFeed(attributes.GetValueOrDefault("title", string.Empty)))
            {
                continue;
            }

            var url = Absolute(href, origin);
            if (url.Length == 0 || found.Any(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;

            found.Add(new DiscoveredFeed(url, text.Trim()));
            if (found.Count >= 12) break;
        }

        return found;
    }

    /// <summary>An address that is shaped like a feed's.</summary>
    private static bool FeedShaped(string href)
    {
        var path = href.Split('?', '#')[0].TrimEnd('/');

        foreach (var ending in (string[])["/feed", "/rss", ".rss", ".atom", "/atom", "feed.xml", "rss.xml", "atom.xml", "index.xml", "feed.json"])
        {
            if (path.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return href.Contains("feed=rss", StringComparison.OrdinalIgnoreCase)
               || href.Contains("/feeds/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Link text that says the link is a feed. "Subscribe" alone is not enough.</summary>
    private static bool SaysFeed(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > 40) return false;

        foreach (var word in (string[])["rss", "atom", "rss feed", "subscribe via rss", "feed"])
        {
            if (trimmed.Equals(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return trimmed.Contains("rss", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every anchor in the markup, as its tag and the text between it and its close.</summary>
    private static IEnumerable<(string Tag, string Text)> Anchors(string html)
    {
        var at = 0;

        while (at < html.Length)
        {
            var open = html.IndexOf("<a", at, StringComparison.OrdinalIgnoreCase);
            if (open < 0 || open + 2 >= html.Length) yield break;

            // "<a" is also the start of "<address" and "<article".
            if (char.IsAsciiLetterOrDigit(html[open + 2]))
            {
                at = open + 2;
                continue;
            }

            var close = html.IndexOf('>', open);
            if (close < 0) yield break;

            var end = html.IndexOf("</a", close, StringComparison.OrdinalIgnoreCase);
            var text = end > close && end - close < 400 ? Strip(html[(close + 1)..end]) : string.Empty;

            yield return (html[(open + 1)..close], text);
            at = close + 1;
        }
    }

    /// <summary>Anchor text with any markup inside it taken off.</summary>
    private static string Strip(string html)
    {
        if (!html.Contains('<')) return System.Net.WebUtility.HtmlDecode(html).Trim();

        var text = new System.Text.StringBuilder(html.Length);
        var inside = false;

        foreach (var c in html)
        {
            if (c == '<') inside = true;
            else if (c == '>') inside = false;
            else if (!inside) text.Append(c);
        }

        return System.Net.WebUtility.HtmlDecode(text.ToString()).Trim();
    }

    /// <summary>True when the text looks like a feed rather than a page, without parsing it.</summary>
    /// <remarks>
    /// Used to decide whether an address a reader pasted is the feed itself or the site in front
    /// of it. Cheap on purpose: this runs before the parse, on a document that may be a megabyte
    /// of HTML, and the parse is what actually decides.
    /// </remarks>
    public static bool LooksLikeFeed(string text)
    {
        var head = text.AsSpan(0, Math.Min(text.Length, 1024));

        foreach (var marker in (string[])["<rss", "<feed", "<rdf:RDF", "<channel"])
        {
            if (head.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // A JSON feed announces itself in its first object, whatever order the keys are written in.
        return head.Contains("jsonfeed.org/version", StringComparison.OrdinalIgnoreCase);
    }

    // ---- A very small scanner ------------------------------------------------------------------

    /// <summary>Every tag of the given name in the markup, as the text between angle brackets.</summary>
    private static IEnumerable<string> Tags(string html, string name)
    {
        var at = 0;

        while (at < html.Length)
        {
            var open = html.IndexOf('<', at);
            if (open < 0 || open + 1 >= html.Length) yield break;

            // The head is where these live, and a page's body can be a megabyte of anything.
            // Stopping at the body is what keeps this from scanning an article for link tags.
            if (Matches(html, open + 1, "/head") || Matches(html, open + 1, "body")) yield break;

            var close = html.IndexOf('>', open);
            if (close < 0) yield break;

            if (Matches(html, open + 1, name)) yield return html[(open + 1)..close];
            at = close + 1;
        }
    }

    private static bool Matches(string html, int at, string name)
        => at + name.Length <= html.Length
           && string.Compare(html, at, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
           && (at + name.Length == html.Length || !char.IsAsciiLetterOrDigit(html[at + name.Length]));

    /// <summary>The attributes of one tag, lower-cased by name, with entities decoded.</summary>
    private static Dictionary<string, string> Attributes(string tag)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var at = 0;

        while (at < tag.Length)
        {
            while (at < tag.Length && !char.IsAsciiLetter(tag[at])) at++;

            var start = at;
            while (at < tag.Length && (char.IsAsciiLetterOrDigit(tag[at]) || tag[at] is '-' or '_' or ':')) at++;
            if (at == start) break;

            var name = tag[start..at];
            while (at < tag.Length && char.IsWhiteSpace(tag[at])) at++;

            if (at >= tag.Length || tag[at] != '=')
            {
                found.TryAdd(name, string.Empty);
                continue;
            }

            at++;
            while (at < tag.Length && char.IsWhiteSpace(tag[at])) at++;
            if (at >= tag.Length) break;

            string value;
            if (tag[at] is '"' or '\'')
            {
                var quote = tag[at++];
                var end = tag.IndexOf(quote, at);
                if (end < 0) break;
                value = tag[at..end];
                at = end + 1;
            }
            else
            {
                var end = at;
                while (end < tag.Length && !char.IsWhiteSpace(tag[end]) && tag[end] != '/') end++;
                value = tag[at..end];
                at = end;
            }

            found.TryAdd(name, System.Net.WebUtility.HtmlDecode(value).Trim());
        }

        return found;
    }

    private static string Absolute(string href, Uri? origin)
    {
        var trimmed = href.Trim();
        if (trimmed.Length == 0) return string.Empty;

        if (Uri.TryCreate(origin, trimmed, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute.AbsoluteUri;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var alone) && alone.Scheme is "http" or "https"
            ? alone.AbsoluteUri
            : string.Empty;
    }
}
