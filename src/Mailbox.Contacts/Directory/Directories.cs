using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Contacts.Directory;

/// <summary>
/// The directories this application looks people up in.
/// </summary>
/// <remarks>
/// One JSON string under a single key, as the identities and the signatures are: the settings
/// file stays something a person can read, and a malformed entry costs one directory rather than
/// the application starting.
/// <para>
/// A setting rather than a collection in the store, for the reason <see cref="LdapDirectory"/>
/// gives: nothing of a directory's is kept, so there is nothing for it to be a collection of. The
/// password is not here — it goes to the desktop keyring like every other one, under its own
/// purpose, so a settings file that is copied or backed up carries no credential.
/// </para>
/// </remarks>
public sealed class Directories
{
    public const string Key = "contacts.directories";

    /// <summary>What the keyring files these passwords under.</summary>
    public const string PasswordPurpose = "ldap";

    private readonly SettingsStore _settings;
    private readonly List<LdapDirectory> _directories;

    public Directories(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _directories = settings.Has(Key) ? Parse(settings.GetString(Key)) : [];
    }

    /// <summary>All of them, in the order they were given.</summary>
    public IReadOnlyList<LdapDirectory> All() => _directories;

    /// <summary>The ones a search should actually ask, which is the enabled and usable ones.</summary>
    public IReadOnlyList<LdapDirectory> Searchable()
        => [.. _directories.Where(d => d.IsEnabled && d.IsUsable)];

    /// <summary>The one called this, or null. Names are how every picker refers to them.</summary>
    public LdapDirectory? Find(string name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _directories.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds one, or replaces the one that was called <paramref name="replacing"/>.
    /// </summary>
    /// <remarks>
    /// Replacing by the old name rather than by position, because renaming a directory is one of
    /// the things the Change… dialog can do and the row it came from is not identity enough.
    /// </remarks>
    public void Save(LdapDirectory directory, string? replacing = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (directory.Name.Trim().Length == 0) return;

        var at = replacing is { Length: > 0 }
            ? _directories.FindIndex(d => string.Equals(d.Name, replacing, StringComparison.OrdinalIgnoreCase))
            : -1;

        if (at >= 0) _directories[at] = directory;
        else _directories.Add(directory);

        Write();
    }

    /// <summary>Forgets one. The password it left in the keyring is the caller's to clear.</summary>
    public bool Remove(string name)
    {
        if (_directories.RemoveAll(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        Write();
        return true;
    }

    /// <summary>Whether a name is already taken — two by one name are indistinguishable in a picker.</summary>
    public bool IsTaken(string name, string? except = null)
        => _directories.Any(
            d => string.Equals(d.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(d.Name, except, StringComparison.OrdinalIgnoreCase));

    private void Write()
    {
        var array = new JsonArray();
        foreach (var directory in _directories)
        {
            array.Add(new JsonObject
            {
                ["name"] = directory.Name,
                ["host"] = directory.Host,
                ["port"] = directory.Port,
                ["tls"] = directory.UseTls,
                ["baseDn"] = directory.BaseDn,
                ["bindDn"] = directory.BindDn,
                ["scope"] = directory.Scope == DirectoryScope.OneLevel ? "one" : "subtree",
                ["max"] = directory.MaxResults,
                ["timeout"] = directory.TimeoutSeconds,
                ["enabled"] = directory.IsEnabled,
            });
        }

        _settings.Set(Key, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    /// <summary>
    /// Reads the stored list, keeping what parses.
    /// </summary>
    /// <remarks>
    /// One bad entry costs that directory rather than all of them, and rather than the
    /// application starting — the settings file is one a person may edit by hand.
    /// </remarks>
    private static List<LdapDirectory> Parse(string json)
    {
        var directories = new List<LdapDirectory>();

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return directories;

            foreach (var entry in array.OfType<JsonObject>())
            {
                if (entry["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;

                directories.Add(new LdapDirectory
                {
                    Name = name,
                    Host = entry["host"]?.GetValue<string>() ?? string.Empty,
                    Port = entry["port"]?.GetValue<int>() ?? 389,
                    UseTls = entry["tls"]?.GetValue<bool>() ?? true,
                    BaseDn = entry["baseDn"]?.GetValue<string>() ?? string.Empty,
                    BindDn = entry["bindDn"]?.GetValue<string>() ?? string.Empty,
                    Scope = string.Equals(entry["scope"]?.GetValue<string>(), "one", StringComparison.Ordinal)
                        ? DirectoryScope.OneLevel
                        : DirectoryScope.Subtree,
                    MaxResults = entry["max"]?.GetValue<int>() ?? 100,
                    TimeoutSeconds = entry["timeout"]?.GetValue<int>() ?? 8,
                    IsEnabled = entry["enabled"]?.GetValue<bool>() ?? true,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The directories could not be read; none will be searched.", ex);
        }

        return directories;
    }
}
