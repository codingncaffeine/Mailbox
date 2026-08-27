using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Feeds;

/// <summary>
/// One subscription: where the feed is, what its folder is called, and everything a polite and
/// cheap poll of it needs to remember.
/// </summary>
/// <param name="Url">The feed's address, which is also what tells two subscriptions apart.</param>
/// <param name="Name">The folder it delivers into — the feed's own title unless it is renamed.</param>
public sealed record FeedSubscription(string Url, string Name, DateTimeOffset? LastChecked = null)
{
    /// <summary>
    /// The heading it is filed under, which is the folder its own folder sits inside.
    /// </summary>
    /// <remarks>
    /// Empty for a feed at the top level. This is the only structure a reader with fifty
    /// subscriptions has: an unread count against "Technology" is worth more than fifty counts
    /// against fifty sites, and it is the first thing anybody rebuilds after moving reader.
    /// </remarks>
    public string Category { get; init; } = string.Empty;

    /// <summary>What the feed calls itself, as opposed to what the reader has renamed it to.</summary>
    public string ChannelTitle { get; init; } = string.Empty;

    /// <summary>The site behind the feed, for the reader who wants to visit it.</summary>
    public string SiteUrl { get; init; } = string.Empty;

    /// <summary>The feed's own picture, shown beside it in the list.</summary>
    public string IconUrl { get; init; } = string.Empty;

    /// <summary>The publisher's description of the feed.</summary>
    public string Description { get; init; } = string.Empty;

    // ---- What a cheap poll remembers ---------------------------------------------------------

    /// <summary>The ETag the last answer carried, sent back so an unchanged feed costs one 304.</summary>
    public string Etag { get; init; } = string.Empty;

    /// <summary>The Last-Modified the last answer carried, for a server that offers no ETag.</summary>
    public string LastModified { get; init; } = string.Empty;

    /// <summary>What went wrong last time, or empty when the last poll worked.</summary>
    public string LastError { get; init; } = string.Empty;

    /// <summary>How many polls in a row have failed, which is what the backing-off is measured in.</summary>
    public int Failures { get; init; }

    /// <summary>
    /// The earliest this should be asked for again — from the publisher's own limit, or from
    /// backing off after a failure. Null to poll it on the next pass.
    /// </summary>
    public DateTimeOffset? NextDueUtc { get; init; }

    /// <summary>What the publisher last asked for, in minutes, or null when it asked for nothing.</summary>
    public int? ProviderLimitMinutes { get; init; }

    /// <summary>Whether that request is honoured. The reference's "Update Limit" tick, and on.</summary>
    public bool UseProviderLimit { get; init; } = true;

    // ---- What the reader asked for ------------------------------------------------------------

    /// <summary>Fetch the files entries carry and attach them. The reference's own option.</summary>
    public bool DownloadEnclosures { get; init; }

    /// <summary>
    /// Fetch the article itself and attach it, for a feed that publishes only a teaser. The
    /// reference's other download option, and the one Feedly charges for.
    /// </summary>
    public bool DownloadFullArticle { get; init; }

    /// <summary>
    /// Read the publisher's page for the article itself, when the feed sends only a teaser.
    /// </summary>
    /// <remarks>
    /// On by default, and it is the difference between a usable reader and a list of headlines.
    /// A great many feeds — TechCrunch's carries a hundred and thirty characters an entry — send
    /// a sentence and a link, and without this a reader who subscribes gets a list of things
    /// they cannot read. The page also carries the picture such a feed never sends, so the same
    /// one request fills in both.
    /// <para>
    /// Distinct from <see cref="DownloadFullArticle"/>, which keeps the whole page as an
    /// attachment: this puts the article in the message where the reading pane will show it and
    /// the index will find it.
    /// </para>
    /// </remarks>
    public bool ReadFullArticle { get; init; } = true;

    /// <summary>When the reader last read anything from this feed, for sorting by liveliness.</summary>
    public DateTimeOffset? LastItemUtc { get; init; }

    /// <summary>
    /// Stopped, without being unsubscribed from.
    /// </summary>
    /// <remarks>
    /// The difference between "I am not reading this at the moment" and "I no longer want this",
    /// and the reason people put up with a feed they have stopped caring about: unsubscribing
    /// throws away the subscription, and a reader who might come back in a month would rather
    /// not have to find the address again.
    /// </remarks>
    public bool Paused { get; init; }

    /// <summary>
    /// How often to ask this publisher, in minutes, or 0 to follow the schedule everything else
    /// follows.
    /// </summary>
    /// <remarks>
    /// Per feed because feeds differ by orders of magnitude: a wire service publishes fifty times
    /// an hour and a personal blog four times a year, and one interval for both is either
    /// wasteful or late. The publisher's own limit still wins over a shorter one — asking more
    /// often than they asked for is how a reader gets themselves blocked.
    /// </remarks>
    public int RefreshMinutes { get; init; }

    /// <summary>
    /// How many articles to keep from this feed, or 0 for all of them.
    /// </summary>
    /// <remarks>
    /// Nothing is trimmed by default, and that is deliberate: keeping everything is the thing a
    /// local reader can do that a hosted one cannot, and it is why a search here reaches back
    /// further than Feedly's does. But a feed publishing fifty a day is a reasonable thing to
    /// want a lid on, and the reader is the one who knows which theirs is.
    /// </remarks>
    public int KeepMost { get; init; }

    /// <summary>Where the reader has put this feed in the pane. Ties break on the name.</summary>
    public int Ordinal { get; init; }

    /// <summary>True when the last poll failed, which is what the list marks.</summary>
    public bool IsFailing => LastError.Length > 0;

    /// <summary>Where this delivers, as a path under the feeds root: "Technology/The Verge".</summary>
    public string FolderPath => Category.Length > 0 ? $"{Category}/{Name}" : Name;
}

/// <summary>
/// The RSS feeds this reader is subscribed to.
/// </summary>
/// <remarks>
/// Kept where the folder pane's Favourites and the favourite contacts are — the settings file,
/// under one key, as readable JSON — because a subscription is a preference of this reader's and
/// not something a mail server has an opinion about. What arrives from a feed <em>is</em> stored:
/// the items are messages in a folder, which is what §15 asks for and what makes the list, the
/// reading pane, Delete, Categorize and search work on a feed without knowing it is one.
/// </remarks>
public sealed class FeedSubscriptions
{
    public const string Key = "rss.feeds";

    private readonly SettingsStore _settings;
    private readonly List<FeedSubscription> _feeds = [];
    private int _deferred;
    private bool _dirty;

    public FeedSubscriptions(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        if (settings.Has(Key)) _feeds.AddRange(Parse(settings.GetString(Key)));

        if (settings.Has(HeadingsKey))
        {
            _headings.AddRange(settings.GetString(HeadingsKey)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    /// <summary>Raised after the list changes, so a pane or a dialog can rebuild.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<FeedSubscription> All => _feeds;

    /// <summary>
    /// The feeds in the order the reader has put them, alphabetically where they have not.
    /// </summary>
    /// <remarks>
    /// Ordinal first so a reader who has arranged their pane keeps that arrangement, and the name
    /// as the tie-break so one who has never dragged anything gets a sorted list rather than the
    /// order they happened to subscribe in.
    /// </remarks>
    public IReadOnlyList<FeedSubscription> InOrder =>
        [.. _feeds.OrderBy(f => f.Ordinal).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Puts one feed immediately after another in the pane, or at the front when after is null.
    /// </summary>
    /// <remarks>
    /// The whole list is renumbered rather than the moved one being given a fractional place:
    /// there are tens of these, renumbering is nothing, and integers that stay integers are what
    /// makes the file readable to somebody opening it.
    /// </remarks>
    public bool Move(string url, string? afterUrl)
    {
        if (Find(url) is not { } moving) return false;
        if (string.Equals(url, afterUrl, StringComparison.OrdinalIgnoreCase)) return false;

        var order = InOrder.Where(f => !string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)).ToList();

        var at = afterUrl is null
            ? 0
            : order.FindIndex(f => string.Equals(f.Url, afterUrl, StringComparison.OrdinalIgnoreCase)) + 1;

        if (at < 0) at = order.Count;
        order.Insert(Math.Clamp(at, 0, order.Count), moving);

        using (Batch())
        {
            for (var n = 0; n < order.Count; n++)
            {
                var place = n + 1;
                Update(order[n].Url, f => f with { Ordinal = place });
            }
        }

        return true;
    }

    /// <summary>Stops or restarts a feed without unsubscribing from it.</summary>
    public bool Pause(string url, bool paused)
        => Update(url, feed => feed with { Paused = paused, NextDueUtc = paused ? feed.NextDueUtc : null });

    /// <summary>
    /// The headings, in the order a reader would expect them.
    /// </summary>
    /// <remarks>
    /// The ones feeds are filed under, and the ones the reader has made and not filled yet. An
    /// empty heading has to be able to exist: making the folder and then dragging things into it
    /// is the order people work in, and a heading that only came into being once something was
    /// already in it would mean there was no way to make the first one.
    /// </remarks>
    public IReadOnlyList<string> Categories =>
        [.. _feeds.OrderBy(f => f.Ordinal).Select(f => f.Category).Where(c => c.Length > 0).Concat(_headings)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Headings the reader has made, whether or not anything is filed under them yet.</summary>
    private readonly List<string> _headings = [];

    /// <summary>The key the made-but-empty headings are kept under.</summary>
    public const string HeadingsKey = "rss.headings";

    /// <summary>Makes a heading. False when there is already one of that name.</summary>
    public bool AddCategory(string name)
    {
        var wanted = name.Trim();
        if (wanted.Length == 0) return false;
        if (Categories.Any(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase))) return false;

        _headings.Add(wanted);
        SaveHeadings();
        return true;
    }

    private void SaveHeadings()
    {
        // Only the ones nothing is filed under: a heading that has feeds in it is already
        // described by them, and writing it twice is a second place for the two to disagree.
        var loose = _headings
            .Where(h => !_feeds.Any(f => string.Equals(f.Category, h, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _settings.Set(HeadingsKey, string.Join('\n', loose));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Contains(string url)
        => _feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));

    public FeedSubscription? Find(string url)
        => _feeds.FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Holds the file still until the work is done.
    /// </summary>
    /// <remarks>
    /// A poll touches every subscription — the stamp, the caching headers, the next due time —
    /// and every one of those is a write of the whole settings file. With fifty feeds that is
    /// fifty rewrites per poll of a file holding every preference in the application. One write
    /// at the end is the same result for a fiftieth of the work.
    /// </remarks>
    public IDisposable Batch()
    {
        _deferred++;
        return new Deferral(this);
    }

    private sealed class Deferral(FeedSubscriptions owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;

            owner._deferred--;
            if (owner._deferred == 0 && owner._dirty) owner.Save();
        }
    }

    /// <summary>Subscribes, once per address.</summary>
    public FeedSubscription Add(string url, string name, string category = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var trimmed = url.Trim();
        if (Find(trimmed) is { } already) return already;

        var feed = new FeedSubscription(trimmed, Unique(name.Trim() is { Length: > 0 } given ? given : trimmed, category))
        {
            Category = category.Trim(),
        };

        _feeds.Add(feed);
        Save();
        return feed;
    }

    /// <summary>
    /// A folder name nothing else under the same heading is already using.
    /// </summary>
    /// <remarks>
    /// Two feeds called "Blog" under one heading would otherwise deliver into one folder and
    /// read as a single feed whose articles come from two places.
    /// </remarks>
    private string Unique(string name, string category)
    {
        if (!Taken(name, category)) return name;

        for (var n = 2; n < 100; n++)
        {
            var candidate = $"{name} ({n})";
            if (!Taken(candidate, category)) return candidate;
        }

        return name;
    }

    private bool Taken(string name, string category)
        => _feeds.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(f.Category, category.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool Remove(string url)
    {
        var removed = _feeds.RemoveAll(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>Renames a feed's folder, which is what the reference's Change… does.</summary>
    public bool Rename(string url, string name)
    {
        if (name.Trim().Length == 0) return false;

        return Update(url, feed => feed with { Name = name.Trim() });
    }

    /// <summary>Files a feed under a heading, or under none when it is empty.</summary>
    public bool Recategorize(string url, string category)
        => Update(url, feed => feed with { Category = category.Trim() });

    /// <summary>Every feed filed under a heading.</summary>
    public IReadOnlyList<FeedSubscription> Under(string category)
        => [.. _feeds.Where(f => string.Equals(f.Category, category.Trim(), StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Renames a heading, taking every feed under it with it.
    /// </summary>
    /// <remarks>
    /// The heading is not a record of its own — it is a field on each subscription, which is what
    /// makes it free to have and free to leave empty. So renaming one is a pass over the feeds
    /// that carry it, in a single write of the file.
    /// </remarks>
    /// <returns>How many feeds moved, or 0 when the name is taken or nothing is under it.</returns>
    public int RenameCategory(string from, string to)
    {
        var wanted = to.Trim();
        var current = from.Trim();

        if (wanted.Length == 0 || current.Length == 0) return 0;
        if (string.Equals(wanted, current, StringComparison.Ordinal)) return 0;

        // A heading that already exists is a merge, not a rename, and the reader did not ask for
        // one: two feeds of the same name under one heading would deliver into one folder.
        if (!string.Equals(wanted, current, StringComparison.OrdinalIgnoreCase)
            && Categories.Any(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        var moved = 0;
        using (Batch())
        {
            foreach (var feed in Under(current))
            {
                if (Update(feed.Url, f => f with { Category = wanted })) moved++;
            }
        }

        // The declaration moves too, so renaming one nothing is filed under yet works as well as
        // renaming one that is full.
        for (var at = 0; at < _headings.Count; at++)
        {
            if (string.Equals(_headings[at], current, StringComparison.OrdinalIgnoreCase)) _headings[at] = wanted;
        }

        SaveHeadings();
        return moved;
    }

    /// <summary>
    /// Removes a heading. Its feeds go to the top level rather than away with it.
    /// </summary>
    /// <returns>How many feeds came out of it.</returns>
    public int RemoveCategory(string category)
    {
        var moved = 0;
        using (Batch())
        {
            foreach (var feed in Under(category))
            {
                if (Update(feed.Url, f => f with { Category = string.Empty })) moved++;
            }
        }

        _headings.RemoveAll(h => string.Equals(h, category.Trim(), StringComparison.OrdinalIgnoreCase));
        SaveHeadings();
        return moved;
    }

    /// <summary>Replaces a subscription wholesale, matched on its address.</summary>
    public bool Replace(FeedSubscription feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return Update(feed.Url, _ => feed);
    }

    /// <summary>Applies a change to one subscription and saves, if it is still here.</summary>
    public bool Update(string url, Func<FeedSubscription, FeedSubscription> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var at = _feeds.FindIndex(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return false;

        var updated = change(_feeds[at]);

        // The address is the identity. A change that moves one is a different subscription, and
        // letting it through here would leave two rows the store cannot tell apart.
        _feeds[at] = updated.Url.Equals(_feeds[at].Url, StringComparison.OrdinalIgnoreCase)
            ? updated
            : updated with { Url = _feeds[at].Url };

        Save();
        return true;
    }

    /// <summary>
    /// Follows a permanent redirect: the same subscription, at the address the server moved it to.
    /// </summary>
    /// <remarks>
    /// A 301 that is not followed in the stored address is re-followed on every poll for the rest
    /// of the subscription's life, which is exactly the behaviour publishers move a feed to stop.
    /// </remarks>
    public bool Moved(string from, string to)
    {
        if (!Uri.TryCreate(to, UriKind.Absolute, out _)) return false;

        var at = _feeds.FindIndex(f => string.Equals(f.Url, from, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return false;

        // Unless something is already there, in which case the two have converged and this one
        // is now a duplicate of it.
        if (Find(to) is not null)
        {
            _feeds.RemoveAt(at);
            Save();
            return true;
        }

        _feeds[at] = _feeds[at] with { Url = to, Etag = string.Empty, LastModified = string.Empty };
        Save();
        return true;
    }

    /// <summary>Records that a feed has just been read, which is what the dialog's column shows.</summary>
    public void Checked(string url, DateTimeOffset when)
        => Update(url, feed => feed with { LastChecked = when });

    private void Save()
    {
        if (_deferred > 0)
        {
            _dirty = true;
            return;
        }

        _dirty = false;

        var array = new JsonArray();
        foreach (var feed in _feeds) array.Add(Write(feed));

        _settings.Set(Key, array.ToJsonString());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One subscription as JSON.
    /// </summary>
    /// <remarks>
    /// Everything that is at its default is left out, so the common case — a feed somebody
    /// subscribed to and never configured — stays the three keys it was before this grew, and a
    /// reader who opens the settings file can still see what is in it.
    /// </remarks>
    private static JsonObject Write(FeedSubscription feed)
    {
        var entry = new JsonObject
        {
            ["url"] = feed.Url,
            ["name"] = feed.Name,
        };

        void Text(string key, string value)
        {
            if (value.Length > 0) entry[key] = value;
        }

        if (feed.LastChecked is { } checkedAt) entry["checked"] = checkedAt.ToUnixTimeSeconds();
        Text("category", feed.Category);
        Text("title", feed.ChannelTitle);
        Text("site", feed.SiteUrl);
        Text("icon", feed.IconUrl);
        Text("description", feed.Description);
        Text("etag", feed.Etag);
        Text("modified", feed.LastModified);
        Text("error", feed.LastError);
        if (feed.Failures > 0) entry["failures"] = feed.Failures;
        if (feed.NextDueUtc is { } due) entry["due"] = due.ToUnixTimeSeconds();
        if (feed.LastItemUtc is { } latest) entry["latest"] = latest.ToUnixTimeSeconds();
        if (feed.ProviderLimitMinutes is { } limit) entry["limit"] = limit;
        if (!feed.UseProviderLimit) entry["uselimit"] = false;
        if (feed.DownloadEnclosures) entry["enclosures"] = true;
        if (feed.DownloadFullArticle) entry["article"] = true;
        if (!feed.ReadFullArticle) entry["fulltext"] = false;
        if (feed.Paused) entry["paused"] = true;
        if (feed.RefreshMinutes > 0) entry["every"] = feed.RefreshMinutes;
        if (feed.KeepMost > 0) entry["keep"] = feed.KeepMost;
        if (feed.Ordinal != 0) entry["ordinal"] = feed.Ordinal;

        return entry;
    }

    private static IEnumerable<FeedSubscription> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            // A hand-edited settings file should not cost somebody their subscriptions silently.
            Log.Warn($"The feed subscriptions could not be read: {ex.Message}");
        }

        if (node is not JsonArray array) yield break;

        foreach (var entry in array.OfType<JsonObject>())
        {
            if (Text(entry, "url") is not { Length: > 0 } url) continue;

            yield return new FeedSubscription(url, Text(entry, "name") is { Length: > 0 } name ? name : url, Stamp(entry, "checked"))
            {
                Category = Text(entry, "category"),
                ChannelTitle = Text(entry, "title"),
                SiteUrl = Text(entry, "site"),
                IconUrl = Text(entry, "icon"),
                Description = Text(entry, "description"),
                Etag = Text(entry, "etag"),
                LastModified = Text(entry, "modified"),
                LastError = Text(entry, "error"),
                Failures = Number(entry, "failures") is { } failures ? (int)failures : 0,
                NextDueUtc = Stamp(entry, "due"),
                LastItemUtc = Stamp(entry, "latest"),
                ProviderLimitMinutes = Number(entry, "limit") is { } limit ? (int)limit : null,
                UseProviderLimit = Flag(entry, "uselimit") ?? true,
                DownloadEnclosures = Flag(entry, "enclosures") ?? false,
                DownloadFullArticle = Flag(entry, "article") ?? false,
                ReadFullArticle = Flag(entry, "fulltext") ?? true,
                Paused = Flag(entry, "paused") ?? false,
                RefreshMinutes = Number(entry, "every") is { } every ? (int)every : 0,
                KeepMost = Number(entry, "keep") is { } keep ? (int)keep : 0,
                Ordinal = Number(entry, "ordinal") is { } ordinal ? (int)ordinal : 0,
            };
        }
    }

    /// <summary>
    /// A value that may have been written as any JSON type. A settings file gets hand-edited and
    /// gets written by older builds, and neither should cost a reader their subscription list.
    /// </summary>
    private static string Text(JsonObject entry, string key)
    {
        try
        {
            return entry[key] switch
            {
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                { } other => other.ToString(),
                _ => string.Empty,
            };
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static long? Number(JsonObject entry, string key)
        => entry[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static bool? Flag(JsonObject entry, string key)
        => entry[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static DateTimeOffset? Stamp(JsonObject entry, string key)
    {
        if (Number(entry, key) is { } seconds) return DateTimeOffset.FromUnixTimeSeconds(seconds);

        // Written as text by a build that used the round-trip format, or by hand.
        return DateTimeOffset.TryParse(Text(entry, key), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}
