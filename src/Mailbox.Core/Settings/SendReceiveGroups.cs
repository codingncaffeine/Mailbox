using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Core.Settings;

/// <summary>
/// A named set of accounts, and when they are checked.
/// </summary>
/// <remarks>
/// The unit F9 acts on. A laptop with a work account behind a VPN and a personal account that is
/// always reachable wants the second checked every ten minutes and the first only on request,
/// and a group is how that is said.
/// </remarks>
public sealed record SendReceiveGroup
{
    public required string Name { get; init; }

    /// <summary>Whether pressing F9 includes this group. The reference's own wording.</summary>
    public bool IncludeInSendReceiveAll { get; init; } = true;

    /// <summary>Check this group on a timer as well as on request.</summary>
    public bool ScheduleEnabled { get; init; }

    public int ScheduleMinutes { get; init; } = 30;

    /// <summary>
    /// The accounts in the group, by address.
    /// </summary>
    /// <remarks>
    /// Empty means every account, which is what makes the shipped group keep working when a
    /// second account is added. A group listing accounts by name would quietly stop covering
    /// the new one.
    /// </remarks>
    public IReadOnlyList<string> Accounts { get; init; } = [];

    public bool Includes(string address)
        => Accounts.Count == 0
           || Accounts.Contains(address, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The defined groups, persisted with the rest of the preferences.
/// </summary>
/// <remarks>
/// Stored as one JSON string under a single key, as the toolbar's command list is: the settings
/// file stays something a person can read, and a malformed entry costs a group rather than the
/// application starting.
/// </remarks>
public sealed class SendReceiveGroups
{
    public const string Key = "sendreceive.groups";

    /// <summary>What ships: everything, on request, as the reference's own default group does.</summary>
    public static SendReceiveGroup AllAccounts { get; } = new() { Name = "All Accounts" };

    private readonly SettingsStore _settings;
    private readonly List<SendReceiveGroup> _groups;

    public SendReceiveGroups(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        _groups = settings.Has(Key) ? Parse(settings.GetString(Key)) : [AllAccounts];

        // A file edited down to nothing still needs a group to press F9 on.
        if (_groups.Count == 0) _groups.Add(AllAccounts);
    }

    public IReadOnlyList<SendReceiveGroup> All => _groups;

    public SendReceiveGroup? Find(string name)
        => _groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The accounts F9 checks: everything in a group that asked to be included.
    /// </summary>
    /// <remarks>
    /// A union rather than a concatenation. An account in two groups is one account, and
    /// checking it twice in a run would download nothing the second time and report two tasks
    /// for one mailbox.
    /// </remarks>
    public IReadOnlyList<string> AccountsForSendReceiveAll(IEnumerable<string> everyAccount)
    {
        ArgumentNullException.ThrowIfNull(everyAccount);
        var accounts = everyAccount.ToList();

        var included = _groups.Where(g => g.IncludeInSendReceiveAll).ToList();
        if (included.Count == 0) return [];

        return [.. accounts.Where(a => included.Any(g => g.Includes(a)))];
    }

    /// <summary>The accounts one group covers, out of those that exist.</summary>
    public IReadOnlyList<string> AccountsIn(SendReceiveGroup group, IEnumerable<string> everyAccount)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(everyAccount);

        return [.. everyAccount.Where(group.Includes)];
    }

    public void Replace(IEnumerable<SendReceiveGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        _groups.Clear();
        _groups.AddRange(groups);
        if (_groups.Count == 0) _groups.Add(AllAccounts);

        Save();
    }

    /// <summary>Back to the shipped group.</summary>
    public void Reset() => Replace([AllAccounts]);

    /// <summary>A name no group is using, for one the user has just made.</summary>
    public string NextName()
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? "New Group" : $"New Group {n}";
            if (Find(candidate) is null) return candidate;
        }
    }

    private void Save()
    {
        var array = new JsonArray();

        foreach (var group in _groups)
        {
            array.Add(new JsonObject
            {
                ["name"] = group.Name,
                ["includeInAll"] = group.IncludeInSendReceiveAll,
                ["schedule"] = group.ScheduleEnabled,
                ["minutes"] = group.ScheduleMinutes,
                ["accounts"] = new JsonArray([.. group.Accounts.Select(a => JsonValue.Create(a))]),
            });
        }

        _settings.Set(Key, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    /// <summary>
    /// Reads the stored groups, dropping anything malformed.
    /// </summary>
    /// <remarks>
    /// The settings file is meant to be editable by hand, so it is allowed to be wrong. A group
    /// that will not parse is a note in the log and one missing group, never a reason send and
    /// receive stops working.
    /// </remarks>
    private static List<SendReceiveGroup> Parse(string stored)
    {
        var groups = new List<SendReceiveGroup>();

        try
        {
            foreach (var node in JsonNode.Parse(stored) as JsonArray ?? [])
            {
                if (node is not JsonObject entry) continue;
                if (Text(entry, "name") is not { Length: > 0 } name) continue;

                groups.Add(new SendReceiveGroup
                {
                    Name = name,
                    IncludeInSendReceiveAll = Flag(entry, "includeInAll", fallback: true),
                    ScheduleEnabled = Flag(entry, "schedule", fallback: false),
                    ScheduleMinutes = Math.Clamp(Number(entry, "minutes", 30), 1, 1440),
                    Accounts =
                    [
                        .. (entry["accounts"] as JsonArray ?? [])
                            .OfType<JsonValue>()
                            .Select(v => v.TryGetValue<string>(out var a) ? a : null)
                            .Where(a => !string.IsNullOrWhiteSpace(a))
                            .Select(a => a!),
                    ],
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {Key}; starting from the shipped group.", ex);
            return [];
        }

        return groups;
    }

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonObject node, string key, bool fallback)
        => node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static int Number(JsonObject node, string key, int fallback)
        => node[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
}
