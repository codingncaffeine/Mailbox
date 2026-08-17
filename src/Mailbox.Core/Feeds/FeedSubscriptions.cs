using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Feeds;

/// <summary>One subscription: where the feed is, what its folder is called, and when it was read.</summary>
/// <param name="Url">The feed's address, which is also what tells two subscriptions apart.</param>
/// <param name="Name">The folder it delivers into — the feed's own title unless it is renamed.</param>
public sealed record FeedSubscription(string Url, string Name, DateTimeOffset? LastChecked = null);

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

    public FeedSubscriptions(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        if (settings.Has(Key)) _feeds.AddRange(Parse(settings.GetString(Key)));
    }

    /// <summary>Raised after the list changes, so a pane or a dialog can rebuild.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<FeedSubscription> All => _feeds;

    public bool Contains(string url)
        => _feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>Subscribes, once per address.</summary>
    public FeedSubscription Add(string url, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var trimmed = url.Trim();
        if (_feeds.FirstOrDefault(f => string.Equals(f.Url, trimmed, StringComparison.OrdinalIgnoreCase)) is { } already)
        {
            return already;
        }

        var feed = new FeedSubscription(trimmed, name.Trim() is { Length: > 0 } given ? given : trimmed);
        _feeds.Add(feed);
        Save();
        return feed;
    }

    public bool Remove(string url)
    {
        var removed = _feeds.RemoveAll(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>Renames a feed's folder, which is what the reference's Change… does.</summary>
    public bool Rename(string url, string name)
    {
        var at = _feeds.FindIndex(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || name.Trim().Length == 0) return false;

        _feeds[at] = _feeds[at] with { Name = name.Trim() };
        Save();
        return true;
    }

    /// <summary>Records that a feed has just been read, which is what the dialog's column shows.</summary>
    public void Checked(string url, DateTimeOffset when)
    {
        var at = _feeds.FindIndex(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;

        _feeds[at] = _feeds[at] with { LastChecked = when };
        Save();
    }

    private void Save()
    {
        var array = new JsonArray();
        foreach (var feed in _feeds)
        {
            array.Add(new JsonObject
            {
                ["url"] = feed.Url,
                ["name"] = feed.Name,
                ["checked"] = feed.LastChecked?.ToUnixTimeSeconds(),
            });
        }

        _settings.Set(Key, array.ToJsonString());
        Changed?.Invoke(this, EventArgs.Empty);
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
            if (entry["url"]?.GetValue<string>() is not { Length: > 0 } url) continue;

            var name = entry["name"]?.GetValue<string>() ?? url;
            var when = entry["checked"]?.GetValue<long?>();
            yield return new FeedSubscription(url, name, when is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
        }
    }
}
