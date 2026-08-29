using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>What one pass over the subscriptions did.</summary>
/// <param name="Delivered">New items filed as messages.</param>
/// <param name="Failed">Feeds that could not be read, with what went wrong.</param>
public sealed record FeedReport(int Delivered, IReadOnlyList<(string Url, string Error)> Failed)
{
    public static readonly FeedReport Nothing = new(0, []);

    /// <summary>Articles the publisher had revised since they were delivered.</summary>
    public int Revised { get; init; }

    /// <summary>Feeds actually asked for, as against skipped because they were not due.</summary>
    public int Polled { get; init; }

    /// <summary>Feeds that answered "nothing has changed", which is the cheap and common case.</summary>
    public int Unchanged { get; init; }

    /// <summary>Articles a mute filter kept out, which were never filed.</summary>
    public int Muted { get; init; }

    public bool DidAnything => Delivered + Revised > 0;

    public string Summary
    {
        get
        {
            if (Delivered == 0 && Revised == 0 && Muted == 0 && Failed.Count == 0)
            {
                return Polled == 0 ? "nothing due" : "nothing new";
            }

            var parts = new List<string>();
            if (Delivered > 0) parts.Add($"{Delivered} new item{(Delivered == 1 ? string.Empty : "s")}");
            if (Revised > 0) parts.Add($"{Revised} updated");
            if (Muted > 0) parts.Add($"{Muted} muted");
            if (Failed.Count > 0) parts.Add($"{Failed.Count} feed{(Failed.Count == 1 ? string.Empty : "s")} failed");

            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// The RSS reader (§15): feeds delivered into mail folders as messages.
/// </summary>
/// <remarks>
/// <b>A feed item is a message.</b> It is written into a folder as MIME, with the entry's own id
/// as its server id, so everything the mail module already does works on it without being told it
/// is a feed: the list draws it, the reading pane renders it through the same sanitizer, Delete
/// deletes it, Categorize tags it, search finds it, AutoArchive files it. That is what §15 means
/// by reusing the list and the reading pane wholesale.
/// <para>
/// The entry's id is what stops a second download delivering it twice — the same job a POP3
/// UIDL does, filed in the same column. What tells a <em>revised</em> article from one already
/// delivered is the Message-ID, written from a fingerprint of what the entry said: same id and a
/// different fingerprint means the publisher changed it, and the message is replaced in place so
/// the reader keeps their flag, their category and their place in the folder.
/// </para>
/// <para>
/// <b>Network work is parallel; store work is not.</b> Fifty subscriptions polled one at a time
/// is fifty round trips end to end, and the store is one SQLite file per account which nothing
/// should be writing to from two threads. So every feed's fetching — the feed, its enclosures,
/// its articles — happens together under a bound, and what comes back is filed one feed at a
/// time on the caller's thread.
/// </para>
/// </remarks>
public sealed class FeedReceiver : IDisposable
{
    /// <summary>The folder the reference keeps feeds under, and this does too.</summary>
    public const string RootFolder = "RSS Feeds";

    /// <summary>How many publishers are asked at once. Enough to be quick, few enough to be polite.</summary>
    private const int AtOnce = 6;

    /// <summary>The largest file that will be attached to an entry.</summary>
    private const long LargestEnclosure = 32 * 1024 * 1024;

    /// <summary>The largest article that will be fetched and attached.</summary>
    private const long LargestArticle = 4 * 1024 * 1024;

    /// <summary>The most files one entry can bring with it.</summary>
    private const int MostEnclosures = 10;

    /// <summary>
    /// Below this many characters, what a feed sent is a teaser rather than the article.
    /// </summary>
    /// <remarks>
    /// Measured against real feeds rather than chosen: TechCrunch sends about 130 characters an
    /// entry, the BBC about 200, and a feed that publishes its articles in full sends thousands.
    /// There is a wide gap between the two and this sits in it, so a short post from a full-text
    /// feed costs one unnecessary request and nothing else.
    /// </remarks>
    private const int TeaserLength = 1000;

    /// <summary>
    /// How many publisher pages one poll of one feed may read.
    /// </summary>
    /// <remarks>
    /// A first poll of a busy feed would otherwise be fifty requests to one site in a row, which
    /// is how a reader gets themselves blocked. What is skipped is said in the log rather than
    /// passed over: a cap nobody is told about reads as "this feed has no article text".
    /// </remarks>
    private const int MostPagesPerPoll = 25;

    /// <summary>
    /// How long to wait after a failure, doubling each time, and how long is too long.
    /// </summary>
    /// <remarks>
    /// A feed that is down stays down for hours, and a reader with a dead subscription should not
    /// be asking for it every fifteen minutes for the rest of the year. Doubling reaches the
    /// ceiling after about seven failures, which is most of a day of being wrong.
    /// </remarks>
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromHours(12);

    private readonly FeedSubscriptions _feeds;
    private readonly FeedFetch _fetch;
    private readonly bool _ownsFetch;

    public FeedReceiver(FeedSubscriptions feeds, HttpMessageHandler? handler = null)
        : this(feeds, new FeedFetch(handler), ownsFetch: true)
    {
    }

    public FeedReceiver(FeedSubscriptions feeds, FeedFetch fetch, bool ownsFetch = false)
    {
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _ownsFetch = ownsFetch;
    }

    /// <summary>Finds the feed behind an address, for the subscribe box.</summary>
    public FeedFinder Finder => new(_fetch);

    /// <summary>
    /// The client every page in this module is read through, for saving a link.
    /// </summary>
    /// <remarks>
    /// The same one rather than a second: it is the one with the size cap, the timeout, the
    /// redirect limit and no cookies on it, and a page saved to a board is fetched on exactly
    /// the terms a feed is.
    /// </remarks>
    public FeedFetch Fetch => _fetch;

    /// <summary>Reads every subscription that is due and files what is new into <paramref name="account"/>.</summary>
    /// <param name="force">
    /// Ask every feed regardless of when it is next due — what an explicit "update this feed"
    /// means, as against the scheduled pass that Send/Receive runs.
    /// </param>
    public async Task<FeedReport> PollAsync(
        OpenAccount account,
        DateTimeOffset now,
        CancellationToken cancellation = default,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (_feeds.All.Count == 0) return FeedReport.Nothing;

        // A newsletter's issues arrive as mail, so there is nothing here to ask for. Skipped by
        // its address rather than by a flag: the fetch would refuse a "newsletter:" scheme
        // anyway, and this keeps the report honest about how many feeds were polled.
        // Force means "never mind whose schedule says what", not "never mind that I paused this":
        // a reader who paused a feed and then pressed Update Feeds did not ask for it back. The
        // way to read a paused one deliberately is Update This Feed, which goes the other way in.
        var due = _feeds.All
            .Where(f => !f.IsNewsletter() && !f.Paused && (force || IsDue(f, now)))
            .ToList();
        if (due.Count == 0) return FeedReport.Nothing;

        // What each feed has already delivered, read before anything goes parallel: the store is
        // one file and the fetching that follows runs on six threads.
        var known = due.ToDictionary(
            f => f.Url,
            f => Folder(account, f, create: false) is { } folder ? account.Mail.ServerUidIndex(folder.Id) : [],
            StringComparer.OrdinalIgnoreCase);

        var prepared = await FetchAllAsync(due, known, now, cancellation).ConfigureAwait(false);

        // One write of the subscription file for the whole pass rather than one per feed.
        using (_feeds.Batch())
        {
            return File(account, prepared, now, cancellation) with { Polled = due.Count };
        }
    }

    /// <summary>
    /// Files what came back: the articles into the store, and what was learnt into the
    /// subscriptions.
    /// </summary>
    /// <remarks>
    /// The one place both passes meet — the scheduled one over everything due, and the reader
    /// pressing Update This Feed — so the caching headers, the backoff, the move-following and
    /// the revision handling cannot drift apart between them.
    /// </remarks>
    private FeedReport File(
        OpenAccount account, IReadOnlyList<Prepared> prepared, DateTimeOffset now, CancellationToken cancellation)
    {
        var delivered = 0;
        var revised = 0;
        var unchanged = 0;
        var silenced = 0;
        var failed = new List<(string Url, string Error)>();

        foreach (var result in prepared)
        {
            cancellation.ThrowIfCancellationRequested();

            // Whether the address it has moved to is one the reader is already subscribed to,
            // asked before the move is followed because following it is what makes the two one.
            var converged = result.Answer.MovedTo is { Length: > 0 } target
                && !string.Equals(result.Feed.Url, target, StringComparison.OrdinalIgnoreCase)
                && _feeds.Find(target) is not null;

            if (result.Answer.MovedTo is { Length: > 0 } moved && _feeds.Moved(result.Feed.Url, moved))
            {
                Log.Info($"Feeds: “{result.Feed.Name}” has moved to {moved}.");
            }

            // Two subscriptions that have become one. The surviving one owns these entries and
            // files them in its own folder; delivering them here as well would put a second copy
            // of the whole feed in a folder whose subscription no longer exists, and announce
            // twice as many new articles as arrived.
            if (converged)
            {
                Log.Info($"Feeds: “{result.Feed.Name}” now points at a feed already subscribed to; "
                    + "its articles are filed under that one.");
                continue;
            }

            var url = result.Answer.MovedTo is { Length: > 0 } to ? to : result.Feed.Url;

            if (result.Answer.NotModified)
            {
                unchanged++;
                _feeds.Update(url, f => Succeeded(f, result.Answer, f.ProviderLimitMinutes, now));
                continue;
            }

            if (result.Error is { Length: > 0 } error)
            {
                failed.Add((result.Feed.Url, error));
                _feeds.Update(url, f => Failed(f, error, result.Answer.RetryAfter, now));
                Log.Warn($"The feed at {result.Feed.Url} could not be read: {error}");
                continue;
            }

            if (result.Channel is not { } channel) continue;

            // A feed subscribed to by address and never named — an OPML file with no title, a
            // pasted URL — takes the publisher's own title the first time it answers. Only
            // before it has delivered anything: the name is also the folder name, and renaming
            // one that already holds articles would leave them behind in the old folder and
            // split the feed in two.
            var named = Named(account, result.Feed, channel);
            if (!ReferenceEquals(named, result.Feed))
            {
                _feeds.Update(url, _ => named);
                Log.Info($"Feeds: {result.Feed.Url} is called “{named.Name}”, which is what it says it is.");
            }

            var (added, changed, muted) = Deliver(account, named, channel, now, result.Arrival, result.Downloads, Mutes);
            delivered += added;
            revised += changed;
            silenced += muted;

            if (added > 0) Trim(account, named);

            _feeds.Update(url, f => Succeeded(f with
            {
                ChannelTitle = channel.Title,
                SiteUrl = channel.Link.Length > 0 ? channel.Link : f.SiteUrl,
                IconUrl = channel.IconUrl.Length > 0 ? channel.IconUrl : f.IconUrl,
                Description = channel.Description.Length > 0 ? channel.Description : f.Description,
                LastItemUtc = channel.Items.Select(i => i.Published).Where(p => p is not null).Max() ?? f.LastItemUtc,
            }, result.Answer, Minutes(channel.UpdateLimit), now));
        }

        return new FeedReport(delivered, failed)
        {
            Revised = revised,
            Unchanged = unchanged,
            Muted = silenced,
            Polled = prepared.Count,
        };
    }

    /// <summary>
    /// The subscription under the publisher's own title, when it has never had one of its own.
    /// </summary>
    /// <remarks>
    /// Hands back the same object when there is nothing to change, so the caller can tell.
    /// </remarks>
    private static FeedSubscription Named(OpenAccount account, FeedSubscription feed, FeedChannel channel)
    {
        if (channel.Title is not { Length: > 0 } title) return feed;
        if (!string.Equals(feed.Name, feed.Url, StringComparison.OrdinalIgnoreCase)) return feed;
        if (Folder(account, feed, create: false) is not null) return feed;

        return feed with { Name = title.Trim() };
    }

    /// <summary>
    /// Reads one subscription now, whether or not it is due.
    /// </summary>
    /// <remarks>
    /// What "Update This Feed" means, and what a reader presses the moment after they subscribe:
    /// a new subscription that shows nothing until the next scheduled pass looks like it did not
    /// work. Goes through the same path a full pass does, so the caching headers, the backoff and
    /// the revision handling are the same ones — this is a narrower pass, not a second
    /// implementation.
    /// </remarks>
    public async Task<FeedReport> PollOneAsync(OpenAccount account, FeedSubscription feed, DateTimeOffset now,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(feed);

        if (_feeds.Find(feed.Url) is not { } current) return FeedReport.Nothing;

        var known = Folder(account, current, create: false) is { } folder
            ? account.Mail.ServerUidIndex(folder.Id)
            : [];

        var prepared = await PrepareAsync(current, known, cancellation).ConfigureAwait(false);
        using (_feeds.Batch())
        {
            return File(account, [prepared], now, cancellation) with { Polled = 1 };
        }
    }

    /// <summary>
    /// How often to ask a feed that has no interval of its own, or null to follow Send/Receive.
    /// </summary>
    /// <remarks>
    /// The reader's own answer to "how often is this checked", which was previously not a
    /// question anything could be asked: feeds rode whatever schedule Send/Receive was on and
    /// there was nothing anywhere that said so or let it be changed.
    /// </remarks>
    public TimeSpan? DefaultRefresh { get; set; }

    /// <summary>What the reader asked to have done to each arriving item, or null for nothing.</summary>
    public IArrivalHandler? Arrival { get; set; }

    /// <summary>
    /// The articles the reader has asked not to see, or null for none.
    /// </summary>
    /// <remarks>
    /// Consulted at delivery, so a muted article is never filed: it costs no space and turns up
    /// in no count and no search. The consequence is that a filter added today does not clear
    /// out yesterday's articles, which the dialog says plainly rather than leaving a reader to
    /// discover.
    /// </remarks>
    public MuteFilters? Mutes { get; set; }

    // ---- Scheduling ------------------------------------------------------------------------------

    /// <summary>
    /// Whether this feed may be asked for yet.
    /// </summary>
    /// <remarks>
    /// Three things can hold it back, and they compose: the reader has paused it; the publisher
    /// asked to be left alone until a time that has not arrived; and the reader's own interval
    /// for this feed has not elapsed. The publisher's request wins over a shorter interval of the
    /// reader's — asking more often than a publisher asked for is how a reader gets blocked, and
    /// it is not a thing a settings box should be able to do.
    /// </remarks>
    private bool IsDue(FeedSubscription feed, DateTimeOffset now)
    {
        if (feed.Paused) return false;
        if (feed.NextDueUtc is { } due && due > now) return false;

        // The feed's own interval, or the reader's default for everything that has none.
        var wanted = feed.RefreshMinutes > 0
            ? TimeSpan.FromMinutes(feed.RefreshMinutes)
            : DefaultRefresh;

        return wanted is not { } every
               || feed.LastChecked is not { } last
               || last + every <= now;
    }

    private static int? Minutes(TimeSpan? limit)
        => limit is { } span && span > TimeSpan.Zero ? (int)Math.Ceiling(span.TotalMinutes) : null;

    /// <summary>The subscription after a poll that worked.</summary>
    private static FeedSubscription Succeeded(FeedSubscription feed, FeedFetchResult answer, int? limit, DateTimeOffset now)
        => feed with
        {
            LastChecked = now,
            LastError = string.Empty,
            Failures = 0,
            Etag = answer.Etag.Length > 0 ? answer.Etag : feed.Etag,
            LastModified = answer.LastModified.Length > 0 ? answer.LastModified : feed.LastModified,
            ProviderLimitMinutes = limit ?? feed.ProviderLimitMinutes,

            // The publisher's own request, honoured unless the reader turned it off. A feed that
            // asks for nothing is polled on whatever schedule Send/Receive runs on.
            NextDueUtc = feed.UseProviderLimit && (limit ?? feed.ProviderLimitMinutes) is { } minutes && minutes > 0
                ? now.AddMinutes(minutes)
                : null,
        };

    /// <summary>The subscription after a poll that did not.</summary>
    private static FeedSubscription Failed(FeedSubscription feed, string error, TimeSpan? retryAfter, DateTimeOffset now)
    {
        var failures = feed.Failures + 1;
        var wait = TimeSpan.FromTicks(Math.Min(
            FirstBackoff.Ticks * (long)Math.Pow(2, Math.Min(failures - 1, 10)),
            LongestBackoff.Ticks));

        // A server that said how long to wait is believed over our own arithmetic, whichever is
        // longer: it knows why it is refusing and we are guessing.
        if (retryAfter is { } asked && asked > wait && asked < TimeSpan.FromDays(1)) wait = asked;

        return feed with
        {
            LastChecked = now,
            LastError = error,
            Failures = failures,
            NextDueUtc = now + wait,
        };
    }

    // ---- Fetching --------------------------------------------------------------------------------

    /// <summary>One feed, fetched and parsed, with whatever its new entries needed downloading.</summary>
    private sealed record Prepared(FeedSubscription Feed, FeedFetchResult Answer)
    {
        public FeedChannel? Channel { get; init; }
        public string Error { get; init; } = string.Empty;
        public IReadOnlyDictionary<string, byte[]> Downloads { get; init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        public IArrivalHandler? Arrival { get; init; }
    }

    private async Task<List<Prepared>> FetchAllAsync(
        List<FeedSubscription> due,
        Dictionary<string, Dictionary<string, (long Id, string MessageId)>> known,
        DateTimeOffset now,
        CancellationToken cancellation)
    {
        using var gate = new SemaphoreSlim(AtOnce);

        var work = due.Select(async feed =>
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await PrepareAsync(feed, known[feed.Url], cancellation).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        var prepared = await Task.WhenAll(work).ConfigureAwait(false);

        // Back into the order the reader has them in, which is the order the log reads in.
        return [.. prepared];
    }

    private async Task<Prepared> PrepareAsync(
        FeedSubscription feed,
        Dictionary<string, (long Id, string MessageId)> known,
        CancellationToken cancellation)
    {
        var answer = await _fetch
            .GetAsync(feed.Url, feed.Etag, feed.LastModified, cancellation)
            .ConfigureAwait(false);

        if (answer.NotModified) return new Prepared(feed, answer);
        if (!answer.Ok) return new Prepared(feed, answer) { Error = answer.Error };

        FeedChannel channel;
        try
        {
            channel = FeedParser.Parse(answer.Text, answer.FinalUrl is { Length: > 0 } url ? url : feed.Url);
        }
        catch (FormatException ex)
        {
            return new Prepared(feed, answer) { Error = ex.Message };
        }

        var downloads = feed.DownloadEnclosures || feed.DownloadFullArticle || feed.ReadFullArticle
            ? await ExtrasAsync(feed, channel, known, cancellation).ConfigureAwait(false)
            : new Dictionary<string, byte[]>(StringComparer.Ordinal);

        return new Prepared(feed, answer)
        {
            Channel = channel,
            Downloads = downloads,
            Arrival = Arrival,
        };
    }

    /// <summary>
    /// The files the reader asked to have brought down with the new entries.
    /// </summary>
    /// <remarks>
    /// Only for entries that are actually new or revised: fetching an article again on every poll
    /// for the life of the subscription is the behaviour that gets a reader blocked, and it is
    /// what an implementation that did not consult the store first would do.
    /// </remarks>
    private async Task<Dictionary<string, byte[]>> ExtrasAsync(
        FeedSubscription feed,
        FeedChannel channel,
        Dictionary<string, (long Id, string MessageId)> known,
        CancellationToken cancellation)
    {
        var wanted = new List<string>();
        var pages = 0;
        var skipped = 0;

        foreach (var item in channel.Items)
        {
            if (known.TryGetValue(item.Id, out var stored) && stored.MessageId == MessageIdFor(item)) continue;

            if (feed.DownloadEnclosures)
            {
                wanted.AddRange(item.Enclosures
                    .Where(e => e.Length is 0 or <= LargestEnclosure)
                    .Take(MostEnclosures)
                    .Select(e => e.Url));
            }

            if (item.Link.Length == 0) continue;

            // The page is worth reading when the reader asked for it as an attachment; when this
            // entry is a teaser and would otherwise arrive unreadable; and when it brought no
            // picture, because nearly every article published has one and a feed that does not
            // send it still has it on the page. One request answers all three.
            var wantsPage = feed.DownloadFullArticle
                            || (feed.ReadFullArticle && (IsTeaser(item) || item.ImageUrl.Length == 0));
            if (!wantsPage) continue;

            if (pages >= MostPagesPerPoll)
            {
                skipped++;
                continue;
            }

            pages++;
            wanted.Add(item.Link);
        }

        if (skipped > 0)
        {
            // Not "on the next pass": by then they have been delivered and say what the feed said,
            // so the very test at the top of this loop passes over them. Opening one is what
            // reads it, which is what the reader does with the ones they care about anyway.
            Log.Info($"Feeds: “{feed.Name}” had {skipped} more article(s) to read than one pass will "
                + "fetch; they keep what the feed itself sent, and are read from the publisher's "
                + "page when one is opened.");
        }

        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (wanted.Count == 0) return found;

        foreach (var url in wanted.Distinct(StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();

            var answer = await _fetch.GetAsync(url, cancellation: cancellation).ConfigureAwait(false);
            if (!answer.Ok || answer.Text.Length == 0) continue;

            var bytes = System.Text.Encoding.UTF8.GetBytes(answer.Text);
            if (bytes.LongLength > LargestArticle && !feed.DownloadEnclosures) continue;

            found[url] = bytes;
        }

        return found;
    }

    // ---- Delivery --------------------------------------------------------------------------------

    /// <summary>
    /// Files a feed's new entries, and replaces the ones the publisher has revised.
    /// </summary>
    /// <remarks>
    /// Public and static so the harness can pose a feed without a network: what has to be
    /// provable is that an entry becomes a message in the right folder exactly once, not that
    /// HTTP works.
    /// </remarks>
    /// <param name="arrival">
    /// What to run over each item as it is filed, or null to file it and nothing more. Null is
    /// the default and the reference's: its "Enable rules on all messages downloaded from RSS
    /// Feeds" is off out of the box, and a folder-per-feed is already a kind of filing.
    /// </param>
    public static int Deliver(OpenAccount account, FeedSubscription feed, FeedChannel channel, DateTimeOffset now,
        IArrivalHandler? arrival = null, MuteFilters? mutes = null)
        => Deliver(account, feed, channel, now, arrival, null, mutes).Delivered;

    private static (int Delivered, int Revised, int Muted) Deliver(
        OpenAccount account,
        FeedSubscription feed,
        FeedChannel channel,
        DateTimeOffset now,
        IArrivalHandler? arrival,
        IReadOnlyDictionary<string, byte[]>? downloads,
        MuteFilters? mutes)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(channel);

        var folder = Folder(account, feed, create: true)!;
        var already = account.Mail.ServerUidIndex(folder.Id);

        var delivered = 0;
        var revised = 0;
        var muted = 0;

        foreach (var item in channel.Items)
        {
            if (item.Id.Length == 0) continue;

            var identity = MessageIdFor(item);
            var stored = already.TryGetValue(item.Id, out var found) ? found : default((long Id, string MessageId)?);

            // Delivered already, and saying the same thing it said then.
            if (stored is { } previous && previous.MessageId == identity) continue;

            // Muted, and not already here. An article the reader has since decided to mute stays
            // where it is — a filter is not a licence to delete what has already arrived.
            if (stored is null && mutes?.Matching(item.Title, Text(item), feed.Category, feed.Url, now) is { } silencing)
            {
                mutes.Counted(silencing);
                muted++;
                continue;
            }

            var message = Compose(channel, item, feed, identity, downloads);
            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();

            // The snippet is the entry's own words, said here rather than left to be sliced off
            // the body: the body ends with the article's address so a plain-text reader can reach
            // it, and a list whose every row trails "https://…" is a list nobody can skim.
            var summary = MessageMapper.ToSummary(message, item.Id, raw.Length, item.Published ?? now)
                with { Preview = Snippet(item, channel) };

            if (stored is { } outdated)
            {
                // The publisher revised it. The row stays, so a flag or a category put on it
                // survives, and the reader is not shown the same article twice.
                if (account.Mail.ReplaceMessage(outdated.Id, summary, raw)) revised++;
                continue;
            }

            var id = account.Mail.AddMessage(folder.Id, summary, raw);
            delivered++;

            // Rules over feed items are off unless asked for, and a rule that moves one has to
            // see it where it was filed — so the pipeline runs after the message exists, not on
            // the way in. A null id is a message this store already had, which is nothing new to
            // run anything over.
            if (arrival is not null && id is { } newlyStored) arrival.Handle(account.Mail, folder, newlyStored, message);
        }

        return (delivered, revised, muted);
    }

    /// <summary>
    /// The Message-ID a given version of an entry gets.
    /// </summary>
    /// <remarks>
    /// Written from the fingerprint of what the entry says rather than left to MimeKit, which
    /// would invent a random one. Two things follow from that: the same entry composed twice
    /// produces the same message, so nothing depends on when it was downloaded; and the header
    /// already in the store says which version is filed there, which is what makes noticing a
    /// revision one query rather than a re-download of every article in the folder.
    /// </remarks>
    /// <remarks>
    /// Written without its angle brackets, because that is the form MimeKit reads a Message-ID
    /// back in and therefore the form the store holds. Comparing the bracketed form against the
    /// stored one never matches, which would report every article in every feed as revised on
    /// every poll — and rewrite the whole folder each time.
    /// </remarks>
    private static string MessageIdFor(FeedItem item) => $"{item.Revision}.feed@mailbox.invalid";

    /// <summary>
    /// The feed's folder, under the RSS Feeds heading and under its own heading when it has one.
    /// </summary>
    /// <remarks>
    /// The heading is a folder because that is what makes the unread count against "Technology"
    /// work: the folder pane already totals a subtree, so filing a feed one level deeper gives a
    /// reader the count they actually want for free.
    /// </remarks>
    /// <summary>Where a feed delivers, or null when it has never delivered anything.</summary>
    /// <remarks>
    /// Public because renaming a feed has to move its folder with it, and the rule for where a
    /// feed's folder is belongs here rather than being written a second time in the shell.
    /// </remarks>
    public static Folder? Folder(OpenAccount account, FeedSubscription feed)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(feed);
        return Folder(account, feed, create: false);
    }

    private static Folder? Folder(OpenAccount account, FeedSubscription feed, bool create)
    {
        var folders = account.Mail.Folders(account.Account.Id);

        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == RootFolder);
        if (root is null)
        {
            if (!create) return null;
            root = account.Mail.AddFolder(account.Account.Id, RootFolder);
            folders = account.Mail.Folders(account.Account.Id);
        }

        var parent = root;

        if (feed.Category is { Length: > 0 } category)
        {
            var heading = folders.FirstOrDefault(f => f.ParentId == root.Id && f.Name == category);
            if (heading is null)
            {
                if (!create) return null;
                heading = account.Mail.AddFolder(account.Account.Id, category, parentId: root.Id);
                folders = account.Mail.Folders(account.Account.Id);
            }

            parent = heading;
        }

        var name = feed.Name is { Length: > 0 } named ? named : feed.Url;
        var own = folders.FirstOrDefault(f => f.ParentId == parent.Id && f.Name == name);

        if (own is not null) return own;

        return create ? account.Mail.AddFolder(account.Account.Id, name, parentId: parent.Id) : null;
    }

    /// <summary>
    /// Keeps a feed's folder to the number of articles the reader asked for.
    /// </summary>
    /// <remarks>
    /// Nothing is trimmed unless the reader asked, and what is kept forever is the thing a local
    /// reader has over a hosted one. When they do ask, three kinds of article survive the cut
    /// whatever their age, because each of them is something the reader said they wanted: one
    /// kept for later, one saved onto a board, and one that has not been read yet. A retention
    /// setting is a way to stop a folder growing, not a licence to throw away the piece somebody
    /// put aside to come back to.
    /// </remarks>
    public static int Trim(OpenAccount account, FeedSubscription feed)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(feed);

        if (feed.KeepMost <= 0) return 0;
        if (Folder(account, feed, create: false) is not { } folder) return 0;

        var all = account.Mail.Messages(folder.Id, limit: 5000);
        if (all.Count <= feed.KeepMost) return 0;

        // One query for the whole folder rather than one per article: this runs after every poll
        // of a busy feed, and a question asked per row turns a trim into a thousand queries.
        var boarded = account.Mail.BoardsFor([.. all.Select(m => m.Id)]);

        var kept = 0;
        var doomed = new List<long>();

        // Newest first, which is the order the folder comes back in. The first KeepMost survive
        // because they are the newest; the ones after that survive only if the reader said so.
        foreach (var article in all)
        {
            var asked = article.IsFlagged || !article.IsRead || boarded.ContainsKey(article.Id);

            if (kept < feed.KeepMost || asked)
            {
                kept++;
                continue;
            }

            doomed.Add(article.Id);
        }

        if (doomed.Count == 0) return 0;

        account.Mail.DeleteMessages(doomed);
        Log.Info($"Feeds: “{feed.Name}” trimmed to {feed.KeepMost}; {doomed.Count} older article(s) removed.");
        return doomed.Count;
    }

    // ---- Moving the tree about ---------------------------------------------------------------

    /// <summary>
    /// Renames a heading's folder, so the articles under it follow the heading.
    /// </summary>
    /// <remarks>
    /// A heading is a folder here — that is what makes the unread count against it work — so a
    /// rename that touched only the subscriptions would leave every article behind in a folder
    /// nothing points at any more, and the feed would look as though it had lost its history.
    /// Called before the subscriptions are updated, because it finds the folder by the old path.
    /// </remarks>
    public static bool RenameHeading(OpenAccount account, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(account);

        var folders = account.Mail.Folders(account.Account.Id);
        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == RootFolder);
        if (root is null) return false;

        if (folders.FirstOrDefault(f => f.ParentId == root.Id && f.Name == from) is not { } heading) return false;

        // Something already there is a merge, which nothing here is asking for.
        if (folders.Any(f => f.ParentId == root.Id && f.Name == to)) return false;

        account.Mail.RenameFolder(heading.Id, to, null);
        Log.Info($"Feeds: the heading “{from}” is now “{to}”.");
        return true;
    }

    /// <summary>
    /// Moves a feed's folder under a different heading, making it if it is not there.
    /// </summary>
    /// <remarks>
    /// Same reasoning as the rename, and the same ordering: called with the subscription as it
    /// still is, because that is what says where its folder is now. An empty heading means the
    /// top of the feeds tree.
    /// </remarks>
    public static bool MoveToHeading(OpenAccount account, FeedSubscription feed, string category)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(feed);

        if (Folder(account, feed, create: false) is not { } own) return true;

        var folders = account.Mail.Folders(account.Account.Id);
        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == RootFolder);
        if (root is null) return false;

        var parent = root;
        if (category.Trim() is { Length: > 0 } wanted)
        {
            parent = folders.FirstOrDefault(f => f.ParentId == root.Id && f.Name == wanted)
                     ?? account.Mail.AddFolder(account.Account.Id, wanted, parentId: root.Id);
        }

        if (own.ParentId == parent.Id) return true;

        // A feed of the same name already filed there. Moving on top of it would give one folder
        // two feeds' articles, which reads as a single feed publishing twice as much.
        if (account.Mail.Folders(account.Account.Id).Any(f => f.ParentId == parent.Id && f.Name == own.Name))
        {
            Log.Warn($"Feeds: “{feed.Name}” cannot move to “{category}” — something of that name is already there.");
            return false;
        }

        var moved = account.Mail.MoveFolder(own.Id, parent.Id, null);
        if (moved) Log.Info($"Feeds: “{feed.Name}” moved to {(category.Length > 0 ? category : "the top level")}.");
        return moved;
    }

    /// <summary>
    /// Removes a heading's folder once its feeds have gone, so an empty one does not linger.
    /// </summary>
    /// <remarks>
    /// Only when it is empty of both. A heading folder still holding messages is one somebody's
    /// articles are in, and tidying the tree is not a reason to delete them.
    /// </remarks>
    public static bool RemoveEmptyHeading(OpenAccount account, string category)
    {
        ArgumentNullException.ThrowIfNull(account);

        var folders = account.Mail.Folders(account.Account.Id);
        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == RootFolder);
        if (root is null) return false;

        if (folders.FirstOrDefault(f => f.ParentId == root.Id && f.Name == category) is not { } heading) return false;
        if (folders.Any(f => f.ParentId == heading.Id)) return false;
        if (account.Mail.Messages(heading.Id, limit: 1).Count > 0) return false;

        account.Mail.RemoveFolder(heading.Id);
        return true;
    }

    /// <summary>
    /// One entry as a message.
    /// </summary>
    /// <remarks>
    /// The feed is the sender, because that is who a reader is hearing from; the entry's own link
    /// goes at the foot of the body, where the reference puts its "View article" line, so a reader
    /// can always reach the original. The address is invented from the feed's host and marked as
    /// such — nothing here should ever be replied to by accident.
    /// </remarks>
    private static MimeMessage Compose(
        FeedChannel channel,
        FeedItem item,
        FeedSubscription feed,
        string identity,
        IReadOnlyDictionary<string, byte[]>? downloads)
    {
        // An address needs a host, and a feed's address may not have one — a local path parses as
        // a file URI whose host is empty, and "rss@" is not an address MimeKit will take.
        var host = Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } hosted
            ? hosted
            : "feed.invalid";
        var who = item.Author is { Length: > 0 } author ? author : channel.Title;

        var message = new MimeMessage
        {
            Subject = item.Title is { Length: > 0 } title ? title : "(no subject)",
            Date = item.Published ?? DateTimeOffset.UtcNow,
            MessageId = identity,
        };

        // What the publisher's own page had to say, when it was read: the article for a feed
        // that sent a teaser, and the picture for a feed that sent none. Both come out of the
        // one page, which is why they are read together rather than in two places.
        var page = Page(item, feed, downloads);

        // Which feed this came from. Nothing else in the message says: every feed on a host
        // sends as rss@<host>, so a rule matching the sender would catch a site's whole set.
        message.Headers.Add("X-Mailbox-Feed", feed.Url);
        if (item.Link is { Length: > 0 } link) message.Headers.Add("X-Mailbox-Feed-Link", link);

        var picture = item.ImageUrl is { Length: > 0 } own ? own : page.Picture;
        if (picture.Length > 0) message.Headers.Add("X-Mailbox-Feed-Image", picture);

        if (page.Article.Found) message.Headers.Add("X-Mailbox-Feed-Fulltext", item.Link);

        // The file an entry carries, named on the message so it can be played without the
        // message being opened and without it having been downloaded. A podcast episode is the
        // case, and streaming it is both quicker and kinder than fetching a hundred megabytes to
        // a temporary file first.
        if (item.Enclosures.FirstOrDefault(e => e.IsPlayable) is { } media)
        {
            message.Headers.Add("X-Mailbox-Feed-Media", $"{media.MediaType} {media.Url}");
        }
        if (feed.Category is { Length: > 0 } category) message.Headers.Add("X-Mailbox-Feed-Category", category);

        // The publisher's own tags, where a mail client keeps its own: a rule can act on them and
        // the reading pane can show them without anything having to know what a feed is.
        foreach (var tag in item.Categories.Take(16)) message.Headers.Add("Keywords", tag);

        message.From.Add(new MailboxAddress(who, $"rss@{host}"));
        message.To.Add(new MailboxAddress(channel.Title is { Length: > 0 } named ? named : feed.Name, $"subscriber@{host}"));

        var body = new BodyBuilder
        {
            HtmlBody = page.Article.Found
                ? page.Article.Html + Footer(item)
                : item.Html.Length > 0
                    ? item.Html + Footer(item)
                    : $"<p>{System.Net.WebUtility.HtmlEncode(item.Title)}</p>{Footer(item)}",

            TextBody = page.Article.Found
                ? item.Link.Length > 0 ? $"{page.Article.Text}\n\n{item.Link}" : page.Article.Text
                : Text(item),
        };

        if (downloads is { Count: > 0 })
        {
            Attach(body, item, feed, downloads);
        }

        message.Body = body.ToMessageBody();
        return message;
    }

    /// <summary>What reading the publisher's page got, or nothing when it was not read.</summary>
    private readonly record struct FromPage(ArticleBody Article, string Picture)
    {
        public static readonly FromPage Nothing = new(ArticleBody.Nothing, string.Empty);
    }

    /// <summary>
    /// Reads the page fetched for this entry, if one was.
    /// </summary>
    /// <remarks>
    /// The extracted article is used only when it is meaningfully more than the feed already
    /// sent. A page whose text cannot be found gives back a handful of characters of navigation,
    /// and replacing a publisher's own summary with that would make the reader worse rather than
    /// better — so the bar is a real multiple, and failing it means keeping what the feed said.
    /// </remarks>
    private static FromPage Page(FeedItem item, FeedSubscription feed, IReadOnlyDictionary<string, byte[]>? downloads)
    {
        if (item.Link.Length == 0) return FromPage.Nothing;
        if (downloads is null || !downloads.TryGetValue(item.Link, out var bytes)) return FromPage.Nothing;

        string html;
        try
        {
            html = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return FromPage.Nothing;
        }

        // The picture is worth taking whether or not the article was: a feed that sends full text
        // and no picture is common, and this is where the picture is.
        var picture = item.ImageUrl.Length > 0 ? string.Empty : PageCards.Read(html, item.Link).ImageUrl;

        if (!feed.ReadFullArticle || !IsTeaser(item)) return new FromPage(ArticleBody.Nothing, picture);

        var written = (item.Html.Length > 0 ? FeedParser.PlainText(item.Html) : item.Summary).Trim().Length;
        var article = ArticleText.Extract(html, item.Link);

        var worthIt = article.Found && article.Length > Math.Max(WorthReplacing, written * 2);
        if (!worthIt && article.Found)
        {
            Log.Debug($"Feeds: the page behind “{item.Title}” gave {article.Length} characters, "
                + $"which is not enough more than the {written} the feed sent; keeping the feed's own.");
        }

        return new FromPage(worthIt ? article : ArticleBody.Nothing, picture);
    }

    /// <summary>The least an extracted article can be and still be worth showing instead.</summary>
    private const int WorthReplacing = 400;

    /// <summary>The files this entry brought with it, as parts of the message.</summary>
    private static void Attach(
        BodyBuilder body, FeedItem item, FeedSubscription feed, IReadOnlyDictionary<string, byte[]> downloads)
    {
        if (feed.DownloadEnclosures)
        {
            foreach (var enclosure in item.Enclosures.Take(MostEnclosures))
            {
                if (!downloads.TryGetValue(enclosure.Url, out var bytes)) continue;

                try
                {
                    body.Attachments.Add(FileName(enclosure), bytes,
                        enclosure.MediaType is { Length: > 0 } type && ContentType.TryParse(type, out var parsed)
                            ? parsed
                            : new ContentType("application", "octet-stream"));
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    Log.Warn($"Feeds: “{enclosure.Url}” could not be attached: {ex.Message}");
                }
            }
        }

        if (feed.DownloadFullArticle && item.Link.Length > 0 && downloads.TryGetValue(item.Link, out var article))
        {
            body.Attachments.Add("article.html", article, new ContentType("text", "html"));
        }
    }

    /// <summary>
    /// Whether what the feed sent for this entry is a teaser rather than the article.
    /// </summary>
    /// <remarks>
    /// Judged on the words rather than on the markup: a hundred characters of text wrapped in two
    /// kilobytes of tracking pixels and a "read more" button is still a teaser.
    /// </remarks>
    private static bool IsTeaser(FeedItem item)
    {
        var written = item.Html.Length > 0 ? FeedParser.PlainText(item.Html) : item.Summary;
        return written.Trim().Length < TeaserLength;
    }

    /// <summary>A file name for an enclosure, from its address, always with an extension.</summary>
    private static string FileName(FeedEnclosure enclosure)
    {
        var name = Uri.TryCreate(enclosure.Url, UriKind.Absolute, out var url)
            ? Path.GetFileName(url.LocalPath)
            : string.Empty;

        if (name.Length == 0) name = "enclosure";

        // A query string is not part of a file name, and neither is a path separator somebody
        // put in one.
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

        return name.Length > 120 ? name[^120..] : name;
    }

    /// <summary>
    /// The line under the headline in the article list: what the entry says, and nothing else.
    /// </summary>
    /// <remarks>
    /// The publisher's own summary first, because that is what they wrote for exactly this; the
    /// article's own opening when they wrote none. Never the address, never the boilerplate the
    /// footer adds.
    /// </remarks>
    private static string Snippet(FeedItem item, FeedChannel channel)
    {
        var written = item.Summary is { Length: > 0 } given ? given : FeedParser.PlainText(item.Html);

        if (written.Trim() is not { Length: > 0 } text) return channel.Title;

        // Whitespace collapsed: a summary written across six indented lines of XML draws as six
        // lines of gaps in a row that has room for two.
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 400 ? flat : flat[..400];
    }

    /// <summary>
    /// The entry as plain text, which is what the message body's text half is.
    /// </summary>
    private static string Text(FeedItem item)
    {
        var summary = item.Summary is { Length: > 0 } written ? written : FeedParser.PlainText(item.Html);

        return item.Link.Length > 0
            ? summary.Length > 0 ? $"{summary}\n\n{item.Link}" : $"{item.Title}\n\n{item.Link}"
            : summary.Length > 0 ? summary : item.Title;
    }

    private static string Footer(FeedItem item)
        => item.Link.Length > 0
            ? $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(item.Link)}\">View article</a></p>"
            : string.Empty;

    public void Dispose()
    {
        if (_ownsFetch) _fetch.Dispose();
    }
}
