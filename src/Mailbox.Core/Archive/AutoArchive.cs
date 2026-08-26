using System.Text.Json;
using System.Text.Json.Serialization;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Archive;

/// <summary>What "older than N" counts in.</summary>
public enum ArchiveUnit
{
    Days,
    Weeks,
    Months,
}

/// <summary>What happens to an old item: moved to the archive, or deleted for good.</summary>
public enum ArchiveAction
{
    Move,
    Delete,
}

/// <summary>A folder's own AutoArchive choice — the reference's folder Properties › AutoArchive tab.</summary>
public enum FolderArchiveMode
{
    /// <summary>"Archive items in this folder using the default settings".</summary>
    Default,

    /// <summary>"Do not archive items in this folder".</summary>
    Off,

    /// <summary>"Archive this folder using these settings".</summary>
    Custom,
}

/// <summary>A folder's AutoArchive document, kept on the folder; null on the folder means <see cref="FolderArchiveMode.Default"/>.</summary>
public sealed record FolderArchivePolicy
{
    public FolderArchiveMode Mode { get; init; } = FolderArchiveMode.Default;
    public int OlderThan { get; init; } = 6;
    public ArchiveUnit Unit { get; init; } = ArchiveUnit.Months;
    public ArchiveAction Action { get; init; } = ArchiveAction.Move;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FolderArchivePolicy FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FolderArchivePolicy();
        try
        {
            return JsonSerializer.Deserialize<FolderArchivePolicy>(json, Json) ?? new FolderArchivePolicy();
        }
        catch (JsonException)
        {
            return new FolderArchivePolicy();
        }
    }
}

/// <summary>
/// The AutoArchive settings — Options › Advanced › AutoArchive Settings… — read from and written
/// to the settings file, one typed accessor per row.
/// </summary>
/// <remarks>
/// The reference archives into a data file of its own; here old mail moves into the account's
/// Archive folder, under a subfolder named for where it came from, so a message archived out of
/// Sent Items is still recognisably sent mail. Every switch is as the reference names it.
/// <para>
/// <b>Off until it is asked for.</b> A stated divergence, and the owner's call: the reference
/// ships this on and asks a fortnight in, and what the question means is "may I move some of
/// your mail". A reader who has not gone looking for AutoArchive has no way to know what it
/// would move or where it would put it, and a prompt at that moment is not consent — it is a
/// dialog in the way of the mail. The switches below still default to what the reference asks
/// for, so turning the one switch on gives the reference's own behaviour, prompt and all.
/// </para>
/// </remarks>
public sealed class AutoArchiveOptions(SettingsStore settings)
{
    private readonly SettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public const string EnabledKey = "autoarchive.enabled";
    public const string EveryDaysKey = "autoarchive.everydays";
    public const string PromptKey = "autoarchive.prompt";
    public const string DeleteExpiredKey = "autoarchive.deleteexpired";
    public const string ArchiveOldKey = "autoarchive.archiveold";
    public const string OlderThanKey = "autoarchive.olderthan";
    public const string UnitKey = "autoarchive.unit";
    public const string ActionKey = "autoarchive.action";
    public const string LastRunKey = "autoarchive.lastrun";

    /// <summary>"Run AutoArchive every N days" — the switch, off until somebody turns it on.</summary>
    public bool Enabled { get => _settings.GetBool(EnabledKey, false); set => _settings.Set(EnabledKey, value); }

    public int EveryDays { get => Math.Clamp((int)_settings.GetNumber(EveryDaysKey, 14), 1, 60); set => _settings.Set(EveryDaysKey, Math.Clamp(value, 1, 60)); }

    /// <summary>"Prompt before AutoArchive runs".</summary>
    public bool Prompt { get => _settings.GetBool(PromptKey, true); set => _settings.Set(PromptKey, value); }

    /// <summary>"Delete expired items (email folders only)".</summary>
    public bool DeleteExpired { get => _settings.GetBool(DeleteExpiredKey, true); set => _settings.Set(DeleteExpiredKey, value); }

    /// <summary>"Archive or delete old items".</summary>
    public bool ArchiveOld { get => _settings.GetBool(ArchiveOldKey, true); set => _settings.Set(ArchiveOldKey, value); }

    /// <summary>"Clean out items older than N".</summary>
    public int OlderThan { get => Math.Clamp((int)_settings.GetNumber(OlderThanKey, 6), 1, 999); set => _settings.Set(OlderThanKey, Math.Clamp(value, 1, 999)); }

    public ArchiveUnit Unit
    {
        get => Enum.TryParse<ArchiveUnit>(_settings.GetString(UnitKey), ignoreCase: true, out var unit) ? unit : ArchiveUnit.Months;
        set => _settings.Set(UnitKey, value.ToString());
    }

    /// <summary>"Move old items to" the archive, or "Permanently delete old items".</summary>
    public ArchiveAction Action
    {
        get => Enum.TryParse<ArchiveAction>(_settings.GetString(ActionKey), ignoreCase: true, out var action) ? action : ArchiveAction.Move;
        set => _settings.Set(ActionKey, value.ToString());
    }

    /// <summary>When AutoArchive last ran, or null for never.</summary>
    public DateTimeOffset? LastRun
    {
        get => _settings.Has(LastRunKey) && (long)_settings.GetNumber(LastRunKey) is var seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        set { if (value is { } when) _settings.Set(LastRunKey, when.ToUnixTimeSeconds()); }
    }

    /// <summary>The default policy, as a folder that follows the default sees it.</summary>
    public FolderArchivePolicy DefaultPolicy => new()
    {
        Mode = FolderArchiveMode.Default,
        OlderThan = OlderThan,
        Unit = Unit,
        Action = Action,
    };
}

/// <summary>The pure half of AutoArchive: when it is due, and where the line falls.</summary>
public static class AutoArchive
{
    /// <summary>True when the interval has passed since the last run — or there was none.</summary>
    public static bool IsDue(DateTimeOffset? lastRun, int everyDays, DateTimeOffset now)
        => lastRun is not { } last || now - last >= TimeSpan.FromDays(Math.Max(1, everyDays));

    /// <summary>The moment before which an item is old, for "clean out items older than N units".</summary>
    public static DateTimeOffset Cutoff(int olderThan, ArchiveUnit unit, DateTimeOffset now)
    {
        var count = Math.Max(1, olderThan);
        return unit switch
        {
            ArchiveUnit.Days => now.AddDays(-count),
            ArchiveUnit.Weeks => now.AddDays(-7 * count),
            _ => now.AddMonths(-count),
        };
    }

    /// <summary>
    /// The policy in force for a folder: its own when it has one and it says so, the default
    /// when it follows the default, nothing when it says off. Null means leave the folder alone.
    /// </summary>
    public static FolderArchivePolicy? Effective(FolderArchivePolicy? folder, FolderArchivePolicy defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return folder?.Mode switch
        {
            FolderArchiveMode.Off => null,
            FolderArchiveMode.Custom => folder,
            _ => defaults,
        };
    }

    /// <summary>The unit's word, singular or plural.</summary>
    public static string UnitWord(ArchiveUnit unit, int count) => (unit, count) switch
    {
        (ArchiveUnit.Days, 1) => "day",
        (ArchiveUnit.Days, _) => "days",
        (ArchiveUnit.Weeks, 1) => "week",
        (ArchiveUnit.Weeks, _) => "weeks",
        (_, 1) => "month",
        _ => "months",
    };
}
