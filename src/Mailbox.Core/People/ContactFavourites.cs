using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Core.People;

/// <summary>
/// The favourite contacts: the short list the To-Do Bar's People section shows, in the order they
/// were added.
/// </summary>
/// <remarks>
/// The same shape the folder pane's <c>Favourites</c> has, and for the same reasons. Kept in the
/// settings file under one key as readable JSON, and keyed by the <b>card's UID</b> rather than by
/// a row id: a contact pulled down again after a store is restored is the same person with the
/// same UID and a new id, and a list of ids would quietly point at strangers.
/// <para>
/// It is a preference rather than a property of the card. A vCard has no standard way to say
/// "this person is a favorite of mine", and inventing an X- property would write one reader's
/// short list into everybody else's address book. This is the same call the folder pane's
/// Favourites section makes, and it is why neither travels.
/// </para>
/// <para>
/// A UID that no longer names anybody is not removed: a card may simply not have synced yet, and
/// forgetting it on a bad morning would be worse than showing nothing for it.
/// </para>
/// </remarks>
public sealed class ContactFavourites
{
    public const string Key = "people.favourites";

    private readonly SettingsStore _settings;
    private readonly List<string> _uids = [];

    public ContactFavourites(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        if (settings.Has(Key)) _uids.AddRange(Parse(settings.GetString(Key)));
    }

    /// <summary>Raised after the list changes, so the pane can rebuild.</summary>
    public event EventHandler? Changed;

    /// <summary>The favourites, in the order they were added.</summary>
    public IReadOnlyList<string> All => _uids;

    public bool Contains(string uid)
        => uid is { Length: > 0 } && _uids.Contains(uid, StringComparer.OrdinalIgnoreCase);

    /// <summary>Add to Favourites: on the end of the list, once.</summary>
    public void Add(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        if (Contains(uid)) return;
        _uids.Add(uid);
        Save();
    }

    /// <summary>Remove from Favourites.</summary>
    public bool Remove(string uid)
    {
        var removed = _uids.RemoveAll(u => string.Equals(u, uid, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>The one gesture the ribbon has: in if it is out, out if it is in.</summary>
    /// <returns>Whether the contact is a favourite afterwards.</returns>
    public bool Toggle(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        if (Contains(uid))
        {
            Remove(uid);
            return false;
        }

        Add(uid);
        return true;
    }

    private void Save()
    {
        var array = new JsonArray();
        foreach (var uid in _uids) array.Add(uid);
        _settings.Set(Key, array.ToJsonString());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<string> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            // A hand-edited settings file should not cost somebody their address book.
            Log.Warn($"The favourite contacts could not be read: {ex.Message}");
        }

        if (node is not JsonArray array) yield break;

        foreach (var entry in array)
        {
            if (entry?.GetValue<string>() is { Length: > 0 } uid) yield return uid;
        }
    }
}
