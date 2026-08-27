using System.Security.Cryptography;
using System.Text;

namespace Mailbox.Core.Feeds;

/// <summary>
/// A file an entry carries: a podcast episode, a video, an image, a PDF.
/// </summary>
/// <param name="Url">Where the file is. Absolute by the time a parse has finished with it.</param>
/// <param name="MediaType">What the feed says it is, which is not always what it is.</param>
/// <param name="Length">The size in bytes the feed claims, or 0 when it does not say.</param>
public sealed record FeedEnclosure(string Url, string MediaType = "", long Length = 0, string Title = "")
{
    /// <summary>True for a type this would show rather than file: the thumbnail candidates.</summary>
    public bool IsImage => MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

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
    string Html)
{
    /// <summary>When the entry was last revised, when the feed distinguishes that from publication.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>The entry's own short form — Atom's summary, RSS's description beside a fuller content.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The tags the publisher filed it under.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>The files it carries.</summary>
    public IReadOnlyList<FeedEnclosure> Enclosures { get; init; } = [];

    /// <summary>The picture to show beside it in a list, absolute, or empty for none.</summary>
    public string ImageUrl { get; init; } = string.Empty;

    /// <summary>
    /// A fingerprint of what this entry says, so a revision of an entry already delivered is
    /// noticed and a re-download of an unchanged one is not.
    /// </summary>
    /// <remarks>
    /// Over the parts a reader would see changing — the title, the address, the markup and the
    /// revision stamp — rather than over the whole entry, because feeds routinely rewrite parts
    /// nobody reads (an analytics parameter, a re-ordered attribute) on every request, and
    /// hashing those would report every entry as revised on every poll.
    /// </remarks>
    public string Revision => Fingerprint(Title, Link, Html, Updated ?? Published);

    /// <summary>The fingerprint of an entry's visible parts. Stable across runs and machines.</summary>
    public static string Fingerprint(string title, string link, string html, DateTimeOffset? stamp)
    {
        var text = new StringBuilder()
            .Append(title).Append('\u001f')
            .Append(link).Append('\u001f')
            .Append(html).Append('\u001f')
            .Append(stamp?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
            .ToString();

        // Sixteen hex characters: a fingerprint, not a signature. Nothing here is defending
        // against a publisher who wants a collision, only against noticing a rewrite.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }
}

/// <summary>A feed: what it is called, where it points, and what is in it.</summary>
public sealed record FeedChannel(string Title, string Link, IReadOnlyList<FeedItem> Items)
{
    /// <summary>The publisher's own description of the feed, for the subscription's tooltip.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The feed's picture — Atom's icon, RSS's image, JSON Feed's favicon.</summary>
    public string IconUrl { get; init; } = string.Empty;

    /// <summary>Where the feed says it lives, from <c>link rel="self"</c>. Used to follow a move.</summary>
    public string SelfUrl { get; init; } = string.Empty;

    /// <summary>
    /// How often the publisher asks not to be asked again — RSS's <c>ttl</c>, or the syndication
    /// module's update period and frequency. Null when the feed says nothing.
    /// </summary>
    /// <remarks>
    /// This is the reference's "Update Limit", and it is a request rather than a rule: a reader
    /// that ignores it is the reason publishers block readers.
    /// </remarks>
    public TimeSpan? UpdateLimit { get; init; }

    /// <summary>The language the entries are written in, when the feed says.</summary>
    public string Language { get; init; } = string.Empty;
}
