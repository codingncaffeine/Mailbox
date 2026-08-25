using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Calendars;

/// <summary>One published calendar: which one, where it is put, and when it last went.</summary>
/// <param name="CollectionId">The calendar in the PIM store, which is what tells two entries apart.</param>
/// <param name="Url">Where the document is written — an HTTP address that takes a PUT.</param>
/// <param name="Name">What the calendar was called when it was published, for a list to draw.</param>
public sealed record PublishedCalendar(
    long CollectionId,
    string Url,
    string Name,
    DateTimeOffset? LastPublished = null);

/// <summary>
/// The calendars this reader publishes, and where each one goes.
/// </summary>
/// <remarks>
/// Kept in the settings file beside the feed subscriptions and the certificate pins, for the same
/// reason: where somebody chooses to put a copy of their calendar is a preference of theirs, not
/// a fact about the calendar, and a reader who wants to find it and take it back out should be
/// able to read the file and see it.
/// <para>
/// Publishing here is a document at a URL, which is the same thing a subscription reads — so what
/// this writes, <see cref="CalendarSubscription"/> can subscribe to. The reference publishes to
/// its own service as well; that is a tenant service and out of scope (§3), and a WebDAV address
/// is the part of its own dialog that survives the difference.
/// </para>
/// </remarks>
public sealed class PublishedCalendars
{
    public const string Key = "calendar.published";

    private readonly SettingsStore _settings;
    private readonly List<PublishedCalendar> _published = [];

    public PublishedCalendars(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        if (settings.Has(Key)) _published.AddRange(Parse(settings.GetString(Key)));
    }

    /// <summary>Raised after the list changes, so a pane or a dialog can rebuild.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<PublishedCalendar> All => _published;

    public PublishedCalendar? For(long collectionId)
        => _published.FirstOrDefault(p => p.CollectionId == collectionId);

    /// <summary>
    /// Publishes a calendar to an address, or moves an already-published one to a new address.
    /// </summary>
    /// <remarks>
    /// One entry per calendar rather than one per address: publishing the same calendar to a
    /// second place is a second subscription for whoever reads it, and Change… in the reference's
    /// own dialog changes where a calendar goes rather than adding somewhere else it also goes.
    /// </remarks>
    public PublishedCalendar Set(long collectionId, string url, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var entry = new PublishedCalendar(collectionId, url.Trim(), name.Trim(), For(collectionId)?.LastPublished);
        var at = _published.FindIndex(p => p.CollectionId == collectionId);
        if (at < 0) _published.Add(entry);
        else _published[at] = entry;

        Save();
        return entry;
    }

    /// <summary>
    /// Stops publishing. What was already written stays where it was put.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the dialog says so: a DELETE would take a calendar away from everybody
    /// subscribed to it on the strength of one press here, and "stop sending them updates" is
    /// what Remove means in a list of things this machine does.
    /// </remarks>
    public bool Remove(long collectionId)
    {
        var removed = _published.RemoveAll(p => p.CollectionId == collectionId) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>Records that a calendar has just gone up, which is what the dialog's column shows.</summary>
    public void Published(long collectionId, DateTimeOffset when)
    {
        var at = _published.FindIndex(p => p.CollectionId == collectionId);
        if (at < 0) return;

        _published[at] = _published[at] with { LastPublished = when };
        Save();
    }

    /// <summary>Keeps the listed name in step when the calendar itself is renamed.</summary>
    public void Renamed(long collectionId, string name)
    {
        var at = _published.FindIndex(p => p.CollectionId == collectionId);
        if (at < 0 || name.Trim().Length == 0 || _published[at].Name == name.Trim()) return;

        _published[at] = _published[at] with { Name = name.Trim() };
        Save();
    }

    private void Save()
    {
        var array = new JsonArray();
        foreach (var entry in _published)
        {
            array.Add(new JsonObject
            {
                ["collection"] = entry.CollectionId,
                ["url"] = entry.Url,
                ["name"] = entry.Name,
                ["published"] = entry.LastPublished?.ToUnixTimeSeconds(),
            });
        }

        _settings.Set(Key, array.ToJsonString());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<PublishedCalendar> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            // A hand-edited settings file should not cost somebody their published calendars
            // silently, as it should not cost them their feeds.
            Log.Warn($"The published calendars could not be read: {ex.Message}");
        }

        if (node is not JsonArray array) yield break;

        foreach (var entry in array.OfType<JsonObject>())
        {
            if (entry["url"]?.GetValue<string>() is not { Length: > 0 } url) continue;
            if (entry["collection"]?.GetValue<long?>() is not { } collection) continue;

            var name = entry["name"]?.GetValue<string>() ?? url;
            var when = entry["published"]?.GetValue<long?>();
            yield return new PublishedCalendar(
                collection, url, name, when is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
        }
    }
}
