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
        "/feed", "/rss", "/feed.xml", "/rss.xml", "/atom.xml", "/index.xml",
        "/feeds/posts/default", "/feed/", "/blog/feed", "/feed.json", "/rss/index.xml",
        "/?feed=rss2",
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
