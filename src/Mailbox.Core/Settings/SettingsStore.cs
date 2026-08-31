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

    /// <summary>Where this store lives on disk, or null for a transient one — the backup names it.</summary>
    public string? PathOnDisk => _path;
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
    /// <param name="at">
    /// Where the copy lives, for a caller that wants to look at it afterwards or hand it to the
    /// next run. Null puts it under the process id, which is what a photograph wants: nobody
    /// reads it and it never collides.
    /// </param>
    /// <remarks>
    /// <b>A named file is opened, not overwritten.</b> The scratch copy is per-process, so no
    /// claim about a setting <em>surviving a run</em> could be made through the harness at all —
    /// only about the settings layer inside one process, which is a different and much weaker
    /// statement. Naming the file makes the second run read what the first one wrote: press a row
    /// in Options, close the dialog, run again pointed at the same file, and the tick is where it
    /// was left or it is not.
    /// </remarks>
    public static SettingsStore ScratchCopy(string? at = null)
    {
        var scratch = string.IsNullOrWhiteSpace(at)
            ? Path.Combine(Path.GetTempPath(), $"mailbox-settings-{Environment.ProcessId}.json")
            : at;

        try
        {
            // A named file that already holds settings is the input: copying the real one over it
            // would throw away exactly what the caller kept it for. An unnamed scratch is always
            // rebuilt, since a stale one from a recycled process id would be somebody else's.
            var carryIn = string.IsNullOrWhiteSpace(at) || !File.Exists(scratch);

            var real = DefaultPath();
            if (carryIn && File.Exists(real)) File.Copy(real, scratch, overwrite: true);

            if (Path.GetDirectoryName(scratch) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }
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

    /// <summary>
    /// The value as it is stored, whatever its type — null for a key never written.
    /// </summary>
    /// <remarks>
    /// For saying what a key holds without knowing what kind of thing it holds, which is what a
    /// harness reading a press back needs: a tick writes a boolean, a radio a string and a spinner
    /// a number, and asking <see cref="GetString"/> for the first of those quietly answers with the
    /// caller's own fallback.
    /// </remarks>
    public string? Stored(string key) => TryGet(key, out var node) ? node.ToJsonString() : null;

    /// <summary>Forgets a key, so the caller's default applies again. Nothing happens for a key never written.</summary>
    public void Remove(string key)
    {
        if (!_values.Remove(key)) return;
        Save();
        Changed?.Invoke(this, key);
    }

    /// <summary>
    /// What every key holds at this moment, to be handed back to <see cref="Revert"/>.
    /// </summary>
    /// <remarks>
    /// For a dialog that writes as the reader changes each control — which the Options dialog
    /// does on purpose, so a page revisited shows what was chosen — and must still be able to
    /// answer Cancel. Take one before the dialog opens; hand it back if Cancel is pressed; drop
    /// it on OK.
    /// <para>
    /// Values are kept as their JSON text rather than as live nodes: a <see cref="JsonNode"/>
    /// belongs to one parent, so a snapshot holding the real nodes would be a second reference
    /// to the objects the store goes on mutating.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string?> Snapshot()
        => _values.ToDictionary(pair => pair.Key, pair => pair.Value?.ToJsonString(), StringComparer.Ordinal);

    /// <summary>
    /// Puts the store back the way <paramref name="snapshot"/> found it.
    /// </summary>
    /// <remarks>
    /// Saves once and then raises <see cref="Changed"/> for every key that actually moved, so
    /// anything that applied a setting live — the theme, the ribbon layout — is told to read it
    /// again. Keys written since the snapshot and absent from it are removed, which is what
    /// makes a Cancel after a first-ever write leave no trace.
    /// </remarks>
    public void Revert(IReadOnlyDictionary<string, string?> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var moved = new List<string>();

        foreach (var key in _values.Select(pair => pair.Key).ToArray())
        {
            if (snapshot.ContainsKey(key)) continue;
            _values.Remove(key);
            moved.Add(key);
        }

        foreach (var (key, json) in snapshot)
        {
            var current = _values.TryGetPropertyValue(key, out var found) ? found?.ToJsonString() : null;
            if (current == json) continue;

            _values[key] = json is null ? null : JsonNode.Parse(json);
            moved.Add(key);
        }

        if (moved.Count == 0) return;

        Save();
        foreach (var key in moved) Changed?.Invoke(this, key);
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
            // A corrupt settings file must not stop the application starting, and it must not be
            // thrown away either: the very next setting anything writes — and the shell writes
            // several while it starts — would otherwise overwrite it with defaults, taking the
            // ribbon customization, the Quick Steps, the view definitions and every Options
            // choice with it. So it is moved aside first, under a name that says what it is,
            // and the comment promising it was kept is now true.
            Log.Warn($"Could not read {path}; starting from defaults.", ex);
            Preserve(path, ex);
            return new JsonObject();
        }
    }

    /// <summary>
    /// Moves a file that would not parse out of the way, so the next write cannot destroy it.
    /// </summary>
    /// <remarks>
    /// One copy, not a series: the interesting file is the one from before anything went wrong,
    /// and a run that fails to parse it twice would otherwise overwrite the good copy with the
    /// second reading of the same bad one. Nothing is reported if this fails — the application is
    /// already starting from defaults, and a dialog about the recovery copy of a settings file is
    /// not the thing to open a session with.
    /// </remarks>
    private static void Preserve(string path, Exception cause)
    {
        var kept = path + ".corrupt";

        try
        {
            if (File.Exists(kept)) return;

            File.Move(path, kept);
            Log.Warn($"The unreadable settings file was kept as {kept}.", cause);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not keep the unreadable settings file aside as {kept}.", ex);
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
