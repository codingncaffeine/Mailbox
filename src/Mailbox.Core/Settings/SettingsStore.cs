using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Core.Settings;

/// <summary>
/// Where preferences live: one JSON file under <c>$XDG_CONFIG_HOME/mailbox</c>.
/// </summary>
/// <remarks>
/// Deliberately untyped at the storage layer. The Options pages are a data description of some
/// hundred and forty settings, and giving each one a property would mean editing three files to
/// add a checkbox — the description, a settings class and a mapping between them. A key and a
/// default keeps the description the single place a setting is declared.
/// <para>
/// Unknown keys in the file are preserved rather than dropped, so a settings file written by a
/// newer build survives being opened by an older one.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    private readonly string? _path;
    private JsonObject _values;

    /// <summary>Raised after any value changes, with the key that changed.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Opens the store at the given path, or the default location when null.</summary>
    public SettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        _values = Load(_path);
    }

    /// <summary>An in-memory store that never touches disk. For tests and previews.</summary>
    public static SettingsStore Transient() => new(path: null, values: new JsonObject());

    private SettingsStore(string? path, JsonObject values)
    {
        _path = path;
        _values = values;
    }

    /// <summary>
    /// A store over a copy of the real file, in a temporary place, for a run that must leave
    /// the real one alone.
    /// </summary>
    /// <remarks>
    /// The fidelity harness poses states that persist — the reading pane's visibility, the
    /// nav's, the zoom — and a photograph must not change what the person sees when they next
    /// open the application. The copy carries everything in and nothing out.
    /// </remarks>
    public static SettingsStore ScratchCopy()
    {
        var scratch = Path.Combine(
            Path.GetTempPath(), $"mailbox-settings-{Environment.ProcessId}.json");

        try
        {
            var real = DefaultPath();
            if (File.Exists(real)) File.Copy(real, scratch, overwrite: true);
        }
        catch (Exception)
        {
            // No real file to copy, or one we may not read: the scratch starts empty, which is
            // what a first run looks like anyway.
        }

        return new SettingsStore(scratch);
    }

    public static string DefaultPath()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "mailbox", "settings.json");
    }

    public bool GetBool(string key, bool fallback = false)
        => TryGet(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out var b)
            ? b
            : fallback;

    public string GetString(string key, string fallback = "")
        => TryGet(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : fallback;

    public double GetNumber(string key, double fallback = 0)
        => TryGet(key, out var node) && node is JsonValue v && v.TryGetValue<double>(out var d)
            ? d
            : fallback;

    public void Set(string key, bool value) => Write(key, JsonValue.Create(value));

    public void Set(string key, string value) => Write(key, JsonValue.Create(value));

    public void Set(string key, double value) => Write(key, JsonValue.Create(value));

    /// <summary>True when the key has been written; false means the caller's default applies.</summary>
    public bool Has(string key) => TryGet(key, out _);

    /// <summary>Forgets a key, so the caller's default applies again. Nothing happens for a key never written.</summary>
    public void Remove(string key)
    {
        if (!_values.Remove(key)) return;
        Save();
        Changed?.Invoke(this, key);
    }

    private bool TryGet(string key, [NotNullWhen(true)] out JsonNode? node)
    {
        node = _values.TryGetPropertyValue(key, out var found) ? found : null;
        return node is not null;
    }

    private void Write(string key, JsonNode? value)
    {
        var existing = _values.TryGetPropertyValue(key, out var found) ? found : null;
        if (existing?.ToJsonString() == value?.ToJsonString()) return;

        _values[key] = value;
        Save();
        Changed?.Invoke(this, key);
    }

    private static JsonObject Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new JsonObject();

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            // A corrupt settings file must not stop the application starting. Keep the bad file
            // rather than overwriting it, so it can be looked at.
            Log.Warn($"Could not read {path}; starting from defaults.", ex);
            return new JsonObject();
        }
    }

    private void Save()
    {
        if (_path is null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write beside the target and move into place, so an interrupted write cannot
            // leave a half-file where the settings used to be.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary,
                _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write {_path}.", ex);
        }
    }

    /// <summary>Discards everything. Used by "Reset" and by tests.</summary>
    public void Clear()
    {
        _values = new JsonObject();
        Save();
        Changed?.Invoke(this, string.Empty);
    }

    /// <summary>Parses a stored string as a number, for spinner rows that keep text.</summary>
    public static bool TryNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
