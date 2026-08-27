using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;

namespace Mailbox.Protocols;

/// <summary>What looking for a feed at an address turned up.</summary>
/// <param name="Feeds">What was found, best first. Empty when nothing was.</param>
/// <param name="Error">Why nothing was found, or empty.</param>
public sealed record FeedSearch(IReadOnlyList<DiscoveredFeed> Feeds, string Error = "")
{
    public bool Found => Feeds.Count > 0;
}

/// <summary>
/// Finding the feed behind an address somebody actually has.
/// </summary>
/// <remarks>
/// "Enter the location of the RSS Feed" asks a question most people cannot answer. They have the
/// address of the site, because that is what is in the browser's bar; the feed's address is a
/// thing you have to know to look for. So a reader pastes <c>theverge.com</c> and either gets
/// their feed or decides the application is broken.
/// <para>
/// Three steps, cheapest first: what was pasted may be the feed itself, which is one request; the
/// page it points at may advertise one, which is the same request read differently; and failing
/// both, the handful of paths publishing software puts feeds at are tried. The last step is the
/// only one that costs extra requests, and it stops at the first that answers.
/// </para>
/// </remarks>
public sealed class FeedFinder(FeedFetch fetch)
{
    private readonly FeedFetch _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));

    /// <summary>How many guessed addresses are tried before giving up. Each is one request.</summary>
    private const int MostGuesses = 8;

    /// <summary>The feeds at or behind an address.</summary>
    public async Task<FeedSearch> FindAsync(string address, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return new FeedSearch([], "Enter an address.");

        var url = Normalize(address);
        if (url is null) return new FeedSearch([], "That is not an address.");

        var answer = await _fetch.GetAsync(url, cancellation: cancellation).ConfigureAwait(false);
        if (!answer.Ok)
        {
            // A site that refuses its own front page may still serve a feed at one of the usual
            // places — a WordPress behind a firewall that blocks unknown agents on HTML only.
            var guessed = await GuessAsync(url, cancellation).ConfigureAwait(false);
            return guessed.Found ? guessed : new FeedSearch([], answer.Error);
        }

        var final = answer.FinalUrl is { Length: > 0 } ended ? ended : url;

        // The address was the feed.
        if (FeedLinks.LooksLikeFeed(answer.Text) && TryParse(answer.Text, final) is { } channel)
        {
            return new FeedSearch([new DiscoveredFeed(final, channel.Title)]);
        }

        // The address was a page that advertises one.
        if (FeedLinks.In(answer.Text, final) is { Count: > 0 } advertised)
        {
            Log.Info($"Feeds: {final} advertises {advertised.Count} feed(s).");
            return new FeedSearch(advertised);
        }

        var found = await GuessAsync(final, cancellation).ConfigureAwait(false);
        return found.Found
            ? found
            : new FeedSearch([], "No feed was found at that address.");
    }

    /// <summary>
    /// Reads a feed at a known address, for showing what is in it before anything subscribes.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="FindAsync"/> because the question is different: this one already
    /// knows where the feed is and wants its contents, where finding is about not knowing.
    /// </remarks>
    public async Task<FeedChannel?> PeekAsync(string url, CancellationToken cancellation = default)
    {
        var answer = await _fetch.GetAsync(url, cancellation: cancellation).ConfigureAwait(false);
        return answer.Ok ? TryParse(answer.Text, answer.FinalUrl is { Length: > 0 } final ? final : url) : null;
    }

    /// <summary>The usual places, tried in order, stopping at the first that answers.</summary>
    private async Task<FeedSearch> GuessAsync(string url, CancellationToken cancellation)
    {
        foreach (var guess in FeedLinks.Guessed(url).Take(MostGuesses))
        {
            cancellation.ThrowIfCancellationRequested();

            var answer = await _fetch.GetAsync(guess, cancellation: cancellation).ConfigureAwait(false);
            if (!answer.Ok || !FeedLinks.LooksLikeFeed(answer.Text)) continue;

            var final = answer.FinalUrl is { Length: > 0 } ended ? ended : guess;
            if (TryParse(answer.Text, final) is not { } channel) continue;

            Log.Info($"Feeds: found “{channel.Title}” at {final} by looking.");
            return new FeedSearch([new DiscoveredFeed(final, channel.Title)]);
        }

        return new FeedSearch([]);
    }

    private static FeedChannel? TryParse(string text, string url)
    {
        try
        {
            return FeedParser.Parse(text, url);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// What somebody typed, as an address.
    /// </summary>
    /// <remarks>
    /// People type "theverge.com", paste "feed://example.com/rss" out of an old link, and copy
    /// addresses with a space on the end. All three are the address they meant.
    /// </remarks>
    public static string? Normalize(string address)
    {
        var text = address.Trim();
        if (text.Length == 0) return null;

        // The feed scheme is a browser convention for "subscribe to this", and it is http.
        if (text.StartsWith("feed://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text[7..];
        else if (text.StartsWith("feed:", StringComparison.OrdinalIgnoreCase)) text = text[5..].TrimStart('/');

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url.AbsoluteUri
            : null;
    }
}
