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
        var due = _feeds.All.Where(f => !f.IsNewsletter() && (force || IsDue(f, now))).ToList();
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

            if (result.Answer.MovedTo is { Length: > 0 } moved && _feeds.Moved(result.Feed.Url, moved))
            {
                Log.Info($"Feeds: “{result.Feed.Name}” has moved to {moved}.");
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

            var (added, changed, muted) = Deliver(account, result.Feed, channel, now, result.Arrival, result.Downloads, Mutes);
            delivered += added;
            revised += changed;
            silenced += muted;

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

    /// <summary>Whether this feed may be asked for yet.</summary>
    private static bool IsDue(FeedSubscription feed, DateTimeOffset now)
        => feed.NextDueUtc is not { } due || due <= now;

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

        var downloads = feed.DownloadEnclosures || feed.DownloadFullArticle
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

            if (feed.DownloadFullArticle && item.Link.Length > 0) wanted.Add(item.Link);
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

            var summary = MessageMapper.ToSummary(message, item.Id, raw.Length, item.Published ?? now);

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

        // Which feed this came from. Nothing else in the message says: every feed on a host
        // sends as rss@<host>, so a rule matching the sender would catch a site's whole set.
        message.Headers.Add("X-Mailbox-Feed", feed.Url);
        if (item.Link is { Length: > 0 } link) message.Headers.Add("X-Mailbox-Feed-Link", link);
        if (item.ImageUrl is { Length: > 0 } picture) message.Headers.Add("X-Mailbox-Feed-Image", picture);
        if (feed.Category is { Length: > 0 } category) message.Headers.Add("X-Mailbox-Feed-Category", category);

        // The publisher's own tags, where a mail client keeps its own: a rule can act on them and
        // the reading pane can show them without anything having to know what a feed is.
        foreach (var tag in item.Categories.Take(16)) message.Headers.Add("Keywords", tag);

        message.From.Add(new MailboxAddress(who, $"rss@{host}"));
        message.To.Add(new MailboxAddress(channel.Title is { Length: > 0 } named ? named : feed.Name, $"subscriber@{host}"));

        var body = new BodyBuilder
        {
            HtmlBody = item.Html.Length > 0
                ? item.Html + Footer(item)
                : $"<p>{System.Net.WebUtility.HtmlEncode(item.Title)}</p>{Footer(item)}",
            TextBody = Text(item),
        };

        if (downloads is { Count: > 0 })
        {
            Attach(body, item, feed, downloads);
        }

        message.Body = body.ToMessageBody();
        return message;
    }

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
    /// The entry as plain text, which is what the list's snippet line is built from.
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
