using System.Text.RegularExpressions;

namespace Mailbox.Core.Feeds;

/// <summary>
/// The addresses that are a feed without anybody having to look.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> Nobody knows a feed's address, and the reason a hosted reader feels
/// like magic is that it never asks: paste a YouTube channel, a subreddit, a GitHub repository,
/// and it just works. Most of that is not magic at all — it is a table. These platforms publish
/// feeds at addresses derived from the page address by a fixed rule, so the feed can be worked
/// out here, exactly, with no request at all and no index of anybody's subscriptions.
/// <para>
/// What is <em>not</em> reproducible is the other half: a hosted reader also has an index built
/// from what its users have subscribed to, which is how typing a publication's name finds it.
/// That is other people's data and there is no local equivalent, so the honest substitute is
/// this table plus a real search over the page itself.
/// </para>
/// <para>
/// Each rule is a guess with high confidence rather than a certainty — a repository may publish
/// no releases — so what comes back is candidates, in the order worth trying, and the caller
/// still checks that one of them parses.
/// </para>
/// </remarks>
public static partial class FeedPlatforms
{
    /// <summary>
    /// The feeds an address implies, best first, or empty when it implies none.
    /// </summary>
    public static IReadOnlyList<DiscoveredFeed> For(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return [];
        }

        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
        var path = url.AbsolutePath.Trim('/');
        var parts = path.Length == 0 ? [] : path.Split('/');

        return host switch
        {
            "youtube.com" or "m.youtube.com" => YouTube(url, parts),
            "reddit.com" or "old.reddit.com" or "np.reddit.com" => Reddit(parts),
            "github.com" => GitHub(parts),
            "medium.com" => Medium(parts),
            "stackoverflow.com" or "serverfault.com" or "superuser.com" or "askubuntu.com" => StackExchange(host, parts),
            _ => ByShape(url, host, path, parts),
        };
    }

    /// <summary>
    /// A channel's uploads, which YouTube publishes as Atom keyed on the channel's own id.
    /// </summary>
    /// <remarks>
    /// A handle — <c>/@someone</c> — is not the id and cannot be turned into one without asking,
    /// so it is left to the ordinary scan: YouTube's own pages carry the feed in a link element,
    /// which is what that scan is for.
    /// </remarks>
    private static IReadOnlyList<DiscoveredFeed> YouTube(Uri url, string[] parts)
    {
        const string feeds = "https://www.youtube.com/feeds/videos.xml";

        if (parts is ["channel", { Length: > 0 } id, ..])
        {
            return [new DiscoveredFeed($"{feeds}?channel_id={Uri.EscapeDataString(id)}", "YouTube channel")];
        }

        if (parts is ["playlist", ..] || url.Query.Contains("list=", StringComparison.OrdinalIgnoreCase))
        {
            var list = Query(url, "list");
            if (list.Length > 0) return [new DiscoveredFeed($"{feeds}?playlist_id={Uri.EscapeDataString(list)}", "YouTube playlist")];
        }

        if (parts is ["user", { Length: > 0 } user, ..])
        {
            return [new DiscoveredFeed($"{feeds}?user={Uri.EscapeDataString(user)}", "YouTube channel")];
        }

        return [];
    }

    private static IReadOnlyList<DiscoveredFeed> Reddit(string[] parts)
    {
        if (parts is ["r", { Length: > 0 } sub, ..])
        {
            return [new DiscoveredFeed($"https://www.reddit.com/r/{Uri.EscapeDataString(sub)}/.rss", $"r/{sub}")];
        }

        // Both spellings of a person's page reach the same feed.
        if (parts is ["user" or "u", { Length: > 0 } user, ..])
        {
            return [new DiscoveredFeed($"https://www.reddit.com/user/{Uri.EscapeDataString(user)}/.rss", $"u/{user}")];
        }

        return [];
    }

    /// <summary>
    /// A repository's releases, its tags and its commits, and a person's activity — all four of
    /// which GitHub publishes as Atom at a fixed suffix.
    /// </summary>
    private static IReadOnlyList<DiscoveredFeed> GitHub(string[] parts) => parts switch
    {
        [{ Length: > 0 } owner, { Length: > 0 } repo, ..] =>
        [
            new DiscoveredFeed($"https://github.com/{owner}/{repo}/releases.atom", $"{owner}/{repo} releases"),
            new DiscoveredFeed($"https://github.com/{owner}/{repo}/tags.atom", $"{owner}/{repo} tags"),
            new DiscoveredFeed($"https://github.com/{owner}/{repo}/commits.atom", $"{owner}/{repo} commits"),
        ],
        [{ Length: > 0 } owner] =>
            [new DiscoveredFeed($"https://github.com/{owner}.atom", $"{owner} on GitHub")],
        _ => [],
    };

    private static IReadOnlyList<DiscoveredFeed> Medium(string[] parts) => parts switch
    {
        [{ Length: > 1 } handle, ..] when handle[0] == '@' =>
            [new DiscoveredFeed($"https://medium.com/feed/{handle}", handle)],
        ["tag", { Length: > 0 } tag, ..] =>
            [new DiscoveredFeed($"https://medium.com/feed/tag/{tag}", $"#{tag} on Medium")],
        [{ Length: > 0 } publication, ..] =>
            [new DiscoveredFeed($"https://medium.com/feed/{publication}", publication)],
        _ => [],
    };

    private static IReadOnlyList<DiscoveredFeed> StackExchange(string host, string[] parts) => parts switch
    {
        ["questions", "tagged", { Length: > 0 } tag, ..] =>
            [new DiscoveredFeed($"https://{host}/feeds/tag/{Uri.EscapeDataString(tag)}", $"{tag} on {host}")],
        ["questions", { Length: > 0 } id, ..] when id.All(char.IsAsciiDigit) =>
            [new DiscoveredFeed($"https://{host}/feeds/question/{id}", $"A question on {host}")],
        _ => [new DiscoveredFeed($"https://{host}/feeds", host)],
    };

    /// <summary>
    /// The hosts whose feed is decided by the shape of the address rather than by the host
    /// itself: anything on Substack, Tumblr, Blogger, or a Mastodon server.
    /// </summary>
    private static IReadOnlyList<DiscoveredFeed> ByShape(Uri url, string host, string path, string[] parts)
    {
        var root = $"{url.Scheme}://{url.Host}";

        // A Mastodon account is /@user on whatever server it lives on, and the feed is the same
        // address with .rss on the end. Matched on the shape because there is no list of servers.
        if (parts is [{ Length: > 1 } handle] && handle[0] == '@')
        {
            return [new DiscoveredFeed($"{root}/{handle}.rss", handle)];
        }

        if (host.EndsWith(".substack.com", StringComparison.OrdinalIgnoreCase))
        {
            return [new DiscoveredFeed($"{root}/feed", host[..^13])];
        }

        if (host.EndsWith(".tumblr.com", StringComparison.OrdinalIgnoreCase))
        {
            return [new DiscoveredFeed($"{root}/rss", host[..^11])];
        }

        if (host.EndsWith(".blogspot.com", StringComparison.OrdinalIgnoreCase))
        {
            return [new DiscoveredFeed($"{root}/feeds/posts/default", host[..^13])];
        }

        // A tag or a category on the two engines most of the web is published with.
        if (parts is ["tag" or "category" or "topic", { Length: > 0 }, ..] && path.Length > 0)
        {
            return [new DiscoveredFeed($"{root}/{path}/feed", path)];
        }

        return [];
    }

    /// <summary>
    /// A standing search, for somebody who typed a subject rather than an address.
    /// </summary>
    /// <remarks>
    /// The nearest honest thing to a hosted reader's topic search, which needs an index of the
    /// open web that a local application cannot have. This is not that: it is a real feed
    /// address at a news aggregator, subscribed to like any other, with no account and no key —
    /// and it is offered as what it is rather than dressed up as a publication.
    /// </remarks>
    public static IReadOnlyList<DiscoveredFeed> ForTopic(string topic)
    {
        var trimmed = topic.Trim();
        if (trimmed.Length == 0 || trimmed.Contains("://", StringComparison.Ordinal)) return [];

        var query = Uri.EscapeDataString(trimmed);

        return
        [
            new DiscoveredFeed(
                $"https://news.google.com/rss/search?q={query}",
                $"News about “{trimmed}”"),
        ];
    }

    /// <summary>True when the text is a subject rather than an address.</summary>
    /// <remarks>
    /// A host has a dot in it and no spaces; anything else somebody typed into the box is a
    /// thing they want to read about, not a place.
    /// </remarks>
    public static bool LooksLikeTopic(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.Contains("://", StringComparison.Ordinal)) return false;

        return trimmed.Contains(' ') || !HostShaped().IsMatch(trimmed);
    }

    private static string Query(Uri url, string name)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split <= 0) continue;
            if (!pair[..split].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            return Uri.UnescapeDataString(pair[(split + 1)..]);
        }

        return string.Empty;
    }

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HostShaped();
}
