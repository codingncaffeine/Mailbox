using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;

namespace Mailbox.Protocols;

/// <summary>What looking for a feed at an address turned up.</summary>
/// <param name="Feeds">What was found, best first. Empty when nothing was.</param>
/// <param name="Error">Why nothing was found, or empty.</param>
public sealed record FeedSearch(IReadOnlyList<DiscoveredFeed> Feeds, string Error = "")
{
    public bool Found => Feeds.Count > 0;

    /// <summary>How many addresses were tried, for the log.</summary>
    public int Tried { get; init; }

    /// <summary>
    /// The documents already fetched and parsed to confirm each feed, by address — so whatever
    /// draws a card for one has no reason to fetch the same document again seconds later.
    /// </summary>
    public IReadOnlyDictionary<string, FeedChannel>? Channels { get; init; }

    /// <summary>The parsed feed behind an offered address, when the search still holds it.</summary>
    public FeedChannel? ChannelFor(string url)
        => Channels is { } held && held.TryGetValue(url.TrimEnd('/'), out var channel) ? channel : null;
}

/// <summary>
/// Finding the feeds behind an address somebody actually has.
/// </summary>
/// <remarks>
/// "Enter the location of the RSS Feed" asks a question most people cannot answer. They have the
/// address of a site, because that is what is in the browser's bar; a feed's address is a thing
/// you have to already know to look for. A reader pastes <c>theverge.com</c> and either gets
/// their feed or decides the application is broken.
/// <para>
/// <b>Where this can beat a hosted reader, and where it cannot.</b> A hosted reader answers from
/// an index built by crawling what its users have subscribed to — which is why typing a
/// publication's <em>name</em> works there and cannot work here. But for an <em>address</em> that
/// index is a liability as much as an asset: it answers with what was crawled, which may be a
/// feed the publisher moved a year ago. This reads the live page every time, and can afford to
/// try far harder than a service running the same query for millions of people:
/// </para>
/// <list type="bullet">
/// <item>the platforms whose feed address is a fixed rule are worked out with no request at all;</item>
/// <item>the page's own advertisement is read, and so are the links in its body, which is where
/// the feed lives on sites that never learnt to write a link element;</item>
/// <item>the twenty-odd paths publishing software uses are all probed <em>at once</em>, so
/// twenty candidates cost about what one costs;</item>
/// <item>every candidate is parsed before it is offered, so nothing is suggested that is not
/// actually a feed;</item>
/// <item>and everything found is returned rather than the first hit, because a site usually has
/// several — the articles, the comments, one per section — and which is wanted is the reader's
/// business, not ours.</item>
/// </list>
/// </remarks>
public sealed class FeedFinder(FeedFetch fetch)
{
    private readonly FeedFetch _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));

    /// <summary>
    /// How many candidates are asked for at once.
    /// </summary>
    /// <remarks>
    /// Low on purpose. Every candidate in a round is the same host, so this is how hard one
    /// publisher is hit — and eight was enough to earn a 429 from LWN.
    /// </remarks>
    private const int AtOnce = 4;

    /// <summary>How many candidate addresses are probed in total. Generous; each is one request.</summary>
    private const int MostCandidates = 24;

    /// <summary>The feeds at or behind an address.</summary>
    public async Task<FeedSearch> FindAsync(string address, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return new FeedSearch([], "Enter an address or a subject.");

        // Somebody who typed a subject rather than a place gets a standing search, which is a
        // real feed rather than a pretence at the index we do not have.
        if (FeedPlatforms.LooksLikeTopic(address))
        {
            var standing = await ConfirmAsync(FeedPlatforms.ForTopic(address), cancellation).ConfigureAwait(false);
            return standing.Count > 0
                ? Assembled(standing, 0)
                : new FeedSearch([], $"“{address.Trim()}” is not an address, and no news search could be reached.");
        }

        var url = Normalize(address);
        if (url is null) return new FeedSearch([], "That is not an address.");

        // Asked for in rounds, cheapest and most likely first, stopping at the first round that
        // finds anything.
        //
        // <b>This is not an optimisation, it is politeness.</b> Probing every candidate at once
        // is quicker and it is how this was first written — and it earned a 429 from LWN within
        // four runs, which is precisely the behaviour the polling layer was built to avoid. A
        // site that advertises its feed properly now costs one page and one feed; the twenty-odd
        // guesses are only spent on a site that has told us nothing.
        var page = await _fetch.GetAsync(url, cancellation: cancellation).ConfigureAwait(false);
        var final = page.FinalUrl is { Length: > 0 } ended ? ended : url;
        var tried = 1;

        if (page.Ok)
        {
            // The address may be the feed, in which case there is nothing to look for.
            if (FeedLinks.LooksLikeFeed(page.Text) && TryParse(page.Text, final) is { } itself)
            {
                return Assembled([new Confirmed(new DiscoveredFeed(final, itself.Title), itself)], tried);
            }
        }

        // Round 1: what needs no guessing — the platforms whose address is a rule, and what the
        // page itself says. Nearly every site ends here.
        var round = new List<DiscoveredFeed>();
        Add(round, FeedPlatforms.For(url));

        if (page.Ok)
        {
            Add(round, FeedLinks.In(page.Text, final));
            Add(round, FeedLinks.LinkedFrom(page.Text, final));
        }

        var confirmed = await ConfirmAsync(round, cancellation).ConfigureAwait(false);
        tried += round.Count;

        // Round 2: the places publishing software puts them, for a site that advertises nothing.
        if (confirmed.Count == 0)
        {
            var guesses = new List<DiscoveredFeed>();
            Add(guesses, FeedLinks.Guessed(final).Select(g => new DiscoveredFeed(g)));
            guesses.RemoveAll(g => round.Any(r => Same(r.Url, g.Url)));

            var take = guesses.Take(MostCandidates).ToList();
            confirmed = await ConfirmAsync(take, cancellation).ConfigureAwait(false);
            tried += take.Count;
        }

        // Round 3: the section page. A newspaper's front page is a shop window with no feed on
        // it, and the section underneath advertises one — often on a different host, which is the
        // one case no amount of guessing at the typed address can reach.
        if (confirmed.Count == 0 && page.Ok)
        {
            var deeper = await SectionsAsync(final, cancellation).ConfigureAwait(false);
            if (deeper.Count > 0)
            {
                tried += deeper.Count;
                confirmed = await ConfirmAsync(deeper, cancellation).ConfigureAwait(false);
            }
        }

        if (confirmed.Count > 0)
        {
            Log.Info($"Feeds: {final} — {confirmed.Count} feed(s) confirmed of {tried} tried.");
            return Assembled(confirmed, tried);
        }

        return new FeedSearch(
            [],
            page.Ok
                ? $"That site does not appear to publish a feed. {tried} addresses were tried."
                : page.Error.Length > 0 ? page.Error : "That address could not be reached.")
        {
            Tried = tried,
        };
    }

    /// <summary>Adds what is not already there, matching on the address.</summary>
    private static void Add(List<DiscoveredFeed> into, IEnumerable<DiscoveredFeed> found)
    {
        foreach (var one in found)
        {
            if (one.Url.Length == 0 || into.Any(c => Same(c.Url, one.Url))) continue;
            into.Add(one);
        }
    }

    /// <summary>
    /// The pages a publication keeps its articles on, read for the feed its front page did not
    /// mention.
    /// </summary>
    /// <remarks>
    /// One hop, and only when the front page yielded nothing, so the ordinary case still costs a
    /// single round of requests. It is worth the second round because the sites it rescues are
    /// the large ones: a newspaper's front page is a shop window with no feed on it, and the
    /// section page underneath is where the feed is advertised — often on a different host, which
    /// is the one case no amount of guessing at the typed address can reach.
    /// </remarks>
    private async Task<IReadOnlyList<DiscoveredFeed>> SectionsAsync(string url, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var site)) return [];

        var root = new Uri(site.GetLeftPart(UriPartial.Authority));
        var found = new List<DiscoveredFeed>();
        using var gate = new SemaphoreSlim(AtOnce);

        var sections = new[] { "news", "blog", "articles", "posts", "latest", "stories" }
            .Select(name => Uri.TryCreate(root, name, out var at) ? at.AbsoluteUri : null)
            .OfType<string>()
            .ToArray();

        var reads = sections.Select(async section =>
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var answer = await _fetch.GetAsync(section, cancellation: cancellation).ConfigureAwait(false);
                if (!answer.Ok) return new List<DiscoveredFeed>();

                var at = answer.FinalUrl is { Length: > 0 } ended ? ended : section;
                return FeedLinks.In(answer.Text, at).Concat(FeedLinks.LinkedFrom(answer.Text, at)).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or UriFormatException)
            {
                return new List<DiscoveredFeed>();
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var one in (await Task.WhenAll(reads).ConfigureAwait(false)).SelectMany(f => f))
        {
            if (one.Url.Length == 0 || found.Any(f => Same(f.Url, one.Url))) continue;
            found.Add(one);
        }

        return found;
    }

    /// <summary>
    /// Fetches every candidate at once and keeps the ones that really are feeds, named by what
    /// they call themselves.
    /// </summary>
    /// <remarks>
    /// All at once rather than one after another, stopping at the first: twenty requests made
    /// together cost about what one costs, and the difference between "the first thing that
    /// answered" and "everything this site publishes" is the difference between guessing for the
    /// reader and letting them choose.
    /// <para>
    /// Two feeds are the same feed when they agree on their title and their first entry, however
    /// different their addresses look — <c>/feed</c>, <c>/feed/</c> and <c>/?feed=rss2</c> are
    /// routinely all the same document.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Confirmed>> ConfirmAsync(
        IEnumerable<DiscoveredFeed> candidates, CancellationToken cancellation)
    {
        var ordered = candidates.ToList();
        if (ordered.Count == 0) return [];

        using var gate = new SemaphoreSlim(AtOnce);

        var checks = ordered.Select(async candidate =>
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var answer = await _fetch.GetAsync(candidate.Url, cancellation: cancellation).ConfigureAwait(false);
                if (!answer.Ok || answer.Text.Length == 0) return null;

                var at = answer.FinalUrl is { Length: > 0 } ended ? ended : candidate.Url;
                if (TryParse(answer.Text, at) is not { } channel) return null;

                return new Confirmed(
                    candidate with
                    {
                        Url = at,
                        Title = channel.Title is { Length: > 0 } named ? named : candidate.Title,
                    },
                    channel);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or UriFormatException)
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        var found = (await Task.WhenAll(checks).ConfigureAwait(false)).OfType<Confirmed>().ToList();

        var kept = new List<Confirmed>();

        foreach (var one in found.OrderByDescending(Rank))
        {
            if (kept.Any(k => Same(k.Feed.Url, one.Feed.Url))) continue;
            if (kept.Any(k => SameContent(k.Channel, one.Channel))) continue;

            kept.Add(one);
        }

        return kept;
    }

    private sealed record Confirmed(DiscoveredFeed Feed, FeedChannel Channel);

    /// <summary>The search result, carrying the documents already parsed so nothing fetches twice.</summary>
    private static FeedSearch Assembled(IReadOnlyList<Confirmed> confirmed, int tried)
    {
        var channels = new Dictionary<string, FeedChannel>(StringComparer.OrdinalIgnoreCase);
        foreach (var one in confirmed) channels[one.Feed.Url.TrimEnd('/')] = one.Channel;
        return new FeedSearch([.. confirmed.Select(c => c.Feed)]) { Tried = tried, Channels = channels };
    }

    /// <summary>
    /// Which of a site's feeds to offer first: the one with the most in it, and never the
    /// comments feed ahead of the articles.
    /// </summary>
    private static int Rank(Confirmed one)
    {
        var score = Math.Min(one.Channel.Items.Count, 50);

        // "Comments on:" is what every comments feed calls itself, and it is almost never what
        // somebody meant to subscribe to.
        if (one.Feed.Url.Contains("comment", StringComparison.OrdinalIgnoreCase)
            || one.Channel.Title.StartsWith("Comments", StringComparison.OrdinalIgnoreCase))
        {
            score -= 100;
        }

        return score;
    }

    /// <summary>
    /// Whether two documents are the same feed, judged on the articles they carry.
    /// </summary>
    /// <remarks>
    /// On the entries' <em>addresses</em> and on nothing else, because everything else about the
    /// same feed differs between its formats. Ars Technica publishes the identical article in its
    /// RSS and its Atom under two different headlines and two different ids — so a reader who
    /// asked for arstechnica.com was offered the same publication three times and invited to
    /// choose between them. The article addresses are the one thing that does not change.
    /// <para>
    /// Half the entries in common is enough: two fetches seconds apart can differ at the edges
    /// as a publisher pushes something new.
    /// </para>
    /// </remarks>
    private static bool SameContent(FeedChannel a, FeedChannel b)
    {
        var first = Links(a);
        var second = Links(b);
        if (first.Count == 0 || second.Count == 0) return false;

        var shared = first.Count(second.Contains);
        return shared * 2 >= Math.Min(first.Count, second.Count);
    }

    private static HashSet<string> Links(FeedChannel channel)
        => [.. channel.Items.Take(12).Select(i => i.Link.TrimEnd('/')).Where(l => l.Length > 0)];

    /// <summary>Two addresses that differ only in a trailing slash are one address.</summary>
    private static bool Same(string a, string b)
        => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a feed at a known address, for showing what is in it before anything subscribes.
    /// </summary>
    public async Task<FeedChannel?> PeekAsync(string url, CancellationToken cancellation = default)
    {
        var answer = await _fetch.GetAsync(url, cancellation: cancellation).ConfigureAwait(false);
        return answer.Ok ? TryParse(answer.Text, answer.FinalUrl is { Length: > 0 } final ? final : url) : null;
    }

    private static FeedChannel? TryParse(string text, string url)
    {
        try
        {
            var channel = FeedParser.Parse(text, url);

            // A document that parses but carries nothing is not a feed worth offering: an HTML
            // page occasionally reduces to one, and a stub with a title and no articles gives a
            // reader nothing to judge and nothing to read.
            return channel.Items.Count > 0 ? channel : null;
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
