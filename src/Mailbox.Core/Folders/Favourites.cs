using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Folders;

/// <summary>A folder in the Favourites section: whose account, and where in its tree.</summary>
/// <param name="Address">The account's address — never a row id, which means nothing once a store is copied.</param>
/// <param name="Path">The folder's names from the top, joined by "/" — "Inbox", "Projects/Mailbox".</param>
public sealed record FavouriteFolder(string Address, string Path)
{
    public bool Is(string address, string path)
        => string.Equals(Address, address, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Path, path, StringComparison.Ordinal);
}

/// <summary>
/// The Favourites section at the top of the folder pane, as the reference keeps it: a short list
/// of folders from any account, in the order they were added, that a reader puts there with
/// Show in Favorites and takes out with Remove from Favorites.
/// </summary>
/// <remarks>
/// Kept in the settings file under one key as readable JSON, keyed by address and folder path
/// rather than by ids, so it survives a store being restored or an account being set up again.
/// A folder that no longer exists is simply not shown; it is not removed, because it may be a
/// folder that has not synced yet. The reference starts a fresh profile with the default
/// account's Inbox, Sent Items and Deleted Items, and so does this — once, on the first run:
/// the key's presence is what says the reader has since had their say, so an emptied section
/// stays empty rather than refilling itself.
/// </remarks>
public sealed class Favourites
{
    public const string Key = "folders.favourites";

    private readonly SettingsStore _settings;
    private readonly List<FavouriteFolder> _entries = [];

    public Favourites(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        if (settings.Has(Key)) _entries.AddRange(Parse(settings.GetString(Key)));
    }

    /// <summary>Raised after the list changes, so the pane can rebuild.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<FavouriteFolder> All => _entries;

    /// <summary>Whether the reader has ever had a Favourites section written down — false only on a fresh profile.</summary>
    public bool IsSeeded => _settings.Has(Key);

    /// <summary>
    /// The reference's starting set — Inbox, Sent Items and Deleted Items of the default account —
    /// written once, on a profile that has none. Returns whether it wrote anything.
    /// </summary>
    public bool SeedIfFresh(string defaultAddress, IEnumerable<string> paths)
    {
        if (IsSeeded) return false;
        _entries.Clear();
        foreach (var path in paths) _entries.Add(new FavouriteFolder(defaultAddress, path));
        Save();
        return true;
    }

    public bool Contains(string address, string path) => _entries.Any(e => e.Is(address, path));

    /// <summary>Show in Favorites: on the end of the list, once.</summary>
    public void Add(string address, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Contains(address, path)) return;
        _entries.Add(new FavouriteFolder(address, path));
        Save();
    }

    /// <summary>Remove from Favorites.</summary>
    /// <summary>Where a favourite stands in the list, or -1: what greys the move entries.</summary>
    public int IndexOf(string address, string path) => _entries.FindIndex(e => e.Is(address, path));

    public bool Remove(string address, string path)
    {
        var removed = _entries.RemoveAll(e => e.Is(address, path)) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>Move Up / Move Down within the section.</summary>
    public bool Move(string address, string path, int delta)
    {
        var from = _entries.FindIndex(e => e.Is(address, path));
        if (from < 0 || delta == 0) return false;
        var to = Math.Clamp(from + delta, 0, _entries.Count - 1);
        if (to == from) return false;
        var entry = _entries[from];
        _entries.RemoveAt(from);
        _entries.Insert(to, entry);
        Save();
        return true;
    }

    /// <summary>A renamed or moved folder keeps its place: every entry under the old path follows it to the new one.</summary>
    public void Repath(string address, string oldPath, string newPath)
    {
        var changed = false;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (!string.Equals(entry.Address, address, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Path == oldPath)
            {
                _entries[i] = entry with { Path = newPath };
                changed = true;
            }
            else if (entry.Path.StartsWith(oldPath + "/", StringComparison.Ordinal))
            {
                _entries[i] = entry with { Path = newPath + entry.Path[oldPath.Length..] };
                changed = true;
            }
        }

        if (changed) Save();
    }

    private void Save()
    {
        var array = new JsonArray();
        foreach (var entry in _entries)
        {
            array.Add(new JsonObject { ["account"] = entry.Address, ["path"] = entry.Path });
        }

        _settings.Set(Key, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads what is stored, dropping any entry a hand edit left without an account or a path.</summary>
    private static IEnumerable<FavouriteFolder> Parse(string stored)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(stored);
        }
        catch (JsonException)
        {
            Log.Warn($"{Key} is not JSON; the Favourites section starts empty.");
            yield break;
        }

        if (node is not JsonArray array) yield break;

        foreach (var item in array)
        {
            var address = item?["account"]?.GetValue<string>();
            var path = item?["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(path))
            {
                Log.Warn($"{Key} has an entry without an account or a path; skipped.");
                continue;
            }

            yield return new FavouriteFolder(address, path);
        }
    }
}
