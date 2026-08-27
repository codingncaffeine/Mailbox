namespace Mailbox.Core.Feeds;

/// <summary>
/// What a web page says about itself: the headline, the summary, the picture and whose site it
/// is. What a saved link is drawn from.
/// </summary>
/// <param name="Url">The address that was saved, which is the identity of the saved link.</param>
public sealed record PageCard(string Url)
{
    /// <summary>The page's headline, or the address when the page would not say.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The page's own summary of itself.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The picture the page offers, absolute.</summary>
    public string ImageUrl { get; init; } = string.Empty;

    /// <summary>Whose site it is — the publication's name, not the domain.</summary>
    public string SiteName { get; init; } = string.Empty;

    /// <summary>What the headline should be, falling back to something rather than nothing.</summary>
    public string Headline => Title is { Length: > 0 } written ? written : Readable(Url);

    /// <summary>Who published it, falling back to the host.</summary>
    public string Publisher => SiteName is { Length: > 0 } named
        ? named
        : Uri.TryCreate(Url, UriKind.Absolute, out var address) && address.Host is { Length: > 0 } host
            ? host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host
            : "Saved link";

    /// <summary>An address without its scheme, for showing where there is no title.</summary>
    private static string Readable(string url)
    {
        var trimmed = url.Trim();

        foreach (var scheme in (string[])["https://", "http://"])
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return trimmed[scheme.Length..];
        }

        return trimmed;
    }
}

/// <summary>
/// Reads a page's own description of itself out of its markup.
/// </summary>
/// <remarks>
/// This is what makes "save any address to a board" more than a bookmark with a URL in it: a
/// saved link arrives with a headline, a summary and a picture, and sits in the article list
/// beside the things that came from a feed looking like one of them.
/// <para>
/// Open Graph first, because it is what publishers actually fill in and what every other reader
/// on the web reads; Twitter's card next, because a site that has one usually has no Open Graph;
/// then the ordinary <c>&lt;title&gt;</c> and <c>&lt;meta name="description"&gt;</c>, which is
/// what is left on a page nobody prepared for sharing.
/// </para>
/// <para>
/// Deliberately not a parser. The scanner underneath reads the head and stops at the body, so a
/// megabyte of article is never walked, and a page that is malformed — which most are — costs a
/// missing field rather than an exception. Nothing here is trusted: the picture is only taken if
/// it resolves to an http address, because a saved link's picture is fetched later and a
/// <c>javascript:</c> or <c>file:</c> URL in that column is a bug waiting for somewhere to
/// happen.
/// </para>
/// </remarks>
public static class PageCards
{
    /// <summary>How much of a page's summary is worth keeping.</summary>
    private const int LongestSummary = 500;

    /// <summary>Reads what the page says about itself. Never throws; a page that says nothing gives a card that says nothing.</summary>
    public static PageCard Read(string html, string url)
    {
        var meta = Meta(html ?? string.Empty);

        return new PageCard(url)
        {
            Title = Trim(First(meta, "og:title", "twitter:title") is { Length: > 0 } tagged ? tagged : Title(html ?? string.Empty)),
            Summary = Clip(Trim(First(meta, "og:description", "twitter:description", "description"))),
            ImageUrl = Absolute(First(meta, "og:image", "og:image:url", "twitter:image", "twitter:image:src"), url),
            SiteName = Trim(First(meta, "og:site_name", "application-name")),
        };
    }

    /// <summary>
    /// Every meta value on the page, by the name it was given.
    /// </summary>
    /// <remarks>
    /// Open Graph writes its key in <c>property</c> and everything else writes it in
    /// <c>name</c>, and enough pages get that the wrong way round that both are read for both.
    /// First one wins: a page that repeats <c>og:image</c> is offering its best first.
    /// </remarks>
    private static Dictionary<string, string> Meta(string html)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in FeedLinks.Tags(html, "meta"))
        {
            var attributes = FeedLinks.Attributes(tag);

            var key = attributes.GetValueOrDefault("property") is { Length: > 0 } property
                ? property
                : attributes.GetValueOrDefault("name") ?? string.Empty;

            if (key.Length == 0) continue;
            if (attributes.GetValueOrDefault("content") is not { Length: > 0 } content) continue;

            found.TryAdd(key, content);
        }

        return found;
    }

    private static string First(Dictionary<string, string> meta, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (meta.GetValueOrDefault(key) is { Length: > 0 } value) return value;
        }

        return string.Empty;
    }

    /// <summary>The text between the title tags, which the tag scanner cannot give.</summary>
    private static string Title(string html)
    {
        var open = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return string.Empty;

        var start = html.IndexOf('>', open);
        if (start < 0) return string.Empty;

        var close = html.IndexOf("</title", start, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return string.Empty;

        return System.Net.WebUtility.HtmlDecode(html[(start + 1)..close]);
    }

    /// <summary>
    /// A picture address as something that can actually be fetched.
    /// </summary>
    /// <remarks>
    /// Resolved against the page, because a great many sites write <c>/img/card.png</c>; and
    /// refused unless it comes out as http or https, because the column this lands in is handed
    /// to a fetcher and to an image control, and neither should ever be given a scheme somebody
    /// chose.
    /// </remarks>
    private static string Absolute(string candidate, string pageUrl)
    {
        if (candidate.Trim() is not { Length: > 0 } trimmed) return string.Empty;

        var resolved = Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
                       && Uri.TryCreate(page, trimmed, out var absolute)
            ? absolute
            : Uri.TryCreate(trimmed, UriKind.Absolute, out var alone) ? alone : null;

        return resolved is { Scheme: "http" or "https" } ? resolved.AbsoluteUri : string.Empty;
    }

    /// <summary>Collapses the whitespace a title carries when it is written across three lines.</summary>
    private static string Trim(string text)
    {
        if (text.Length == 0) return string.Empty;

        var collapsed = new System.Text.StringBuilder(text.Length);
        var space = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && collapsed.Length > 0) collapsed.Append(' ');
            space = false;
            collapsed.Append(c);
        }

        return collapsed.ToString();
    }

    private static string Clip(string text)
        => text.Length <= LongestSummary ? text : text[..LongestSummary].TrimEnd() + "…";
}
