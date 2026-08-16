using System.Text.Json;
using Mailbox.Core.Commands;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Keyboard;

/// <summary>Modifier keys, as a chord names them.</summary>
[Flags]
public enum ChordModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8,
}

/// <summary>
/// One keyboard shortcut: modifiers and a key, written "Ctrl+Shift+R", "Delete", "F9" — the
/// key by the name the windowing layer's Key enum gives it, so the two agree without a table.
/// </summary>
public sealed record Chord(ChordModifiers Modifiers, string Key)
{
    /// <summary>"Ctrl+Shift+R", the modifiers always in this order.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ChordModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ChordModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ChordModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ChordModifiers.Meta)) parts.Add("Meta");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>The chord as a person reads it: "Ctrl+Shift+R", "Delete", "Backspace", "Ctrl+1".</summary>
    public string Display => Modifiers == ChordModifiers.None ? Pretty(Key) : ToString()[..^Key.Length] + Pretty(Key);

    /// <summary>Reads "Ctrl+Shift+R", any order of modifiers, any case; null for nothing readable.</summary>
    public static Chord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var modifiers = ChordModifiers.None;
        string? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ChordModifiers.Control; break;
                case "alt": modifiers |= ChordModifiers.Alt; break;
                case "shift": modifiers |= ChordModifiers.Shift; break;
                case "meta": case "super": case "win": modifiers |= ChordModifiers.Meta; break;
                default: key = Canonical(raw); break;
            }
        }

        return key is null ? null : new Chord(modifiers, key);
    }

    /// <summary>A key's name as the Key enum spells it: "Delete", "Back", "F9", "D1", "OemPlus".</summary>
    private static string Canonical(string raw) => raw.ToLowerInvariant() switch
    {
        "del" or "delete" => "Delete",
        "backspace" or "back" => "Back",
        "esc" or "escape" => "Escape",
        "enter" or "return" => "Enter",
        "space" => "Space",
        "tab" => "Tab",
        "ins" or "insert" => "Insert",
        "home" => "Home",
        "end" => "End",
        "pgup" or "pageup" => "PageUp",
        "pgdn" or "pagedown" => "PageDown",
        "up" => "Up",
        "down" => "Down",
        "left" => "Left",
        "right" => "Right",
        var digit when digit.Length == 1 && char.IsDigit(digit[0]) => "D" + digit,
        var letter when letter.Length == 1 && char.IsLetter(letter[0]) => letter.ToUpperInvariant(),
        var f when f.Length is 2 or 3 && f[0] == 'f' && int.TryParse(f[1..], out _) => f.ToUpperInvariant(),
        _ => raw,
    };

    private static string Pretty(string key) => key switch
    {
        "Back" => "Backspace",
        "D0" or "D1" or "D2" or "D3" or "D4" or "D5" or "D6" or "D7" or "D8" or "D9" => key[1..],
        "OemPlus" => "=",
        "OemMinus" => "-",
        "OemComma" => ",",
        "OemPeriod" => ".",
        _ => key,
    };
}

/// <summary>
/// Which key runs which command: every command's own default, with the reader's changes over
/// it — a chord of their choosing, or none — kept in the settings file. One chord runs one
/// command; assigning it to another takes it away from the first, which is what the editor's
/// "Currently assigned to" warns of.
/// </summary>
/// <remarks>
/// The map is the only place a key is turned into a command: the window asks it for every
/// keystroke that is not the ribbon's own, and runs what it says. So a shortcut changed here is
/// changed everywhere, and one added to a command that never had one starts working.
/// </remarks>
public sealed class KeyMap
{
    public const string OverridesKey = "keyboard.overrides";

    private readonly SettingsStore _settings;
    private readonly CommandCatalog _catalog;

    /// <summary>The reader's changes: a command id to its chord's text, or "" for none.</summary>
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    public KeyMap(SettingsStore settings, CommandCatalog catalog)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Load();
    }

    /// <summary>Raised when an assignment changes, so the ribbon's tooltips can follow.</summary>
    public event EventHandler? Changed;

    /// <summary>The chord that runs a command — the reader's, else the command's own — or null.</summary>
    public Chord? GestureFor(CommandId id)
    {
        if (_overrides.TryGetValue(id.Value, out var text)) return Chord.Parse(text);
        return _catalog.TryGet(id, out var command) ? Chord.Parse(command.DefaultGesture) : null;
    }

    /// <summary>The command a chord runs, or null when it runs none.</summary>
    public CommandId? CommandFor(Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        foreach (var command in _catalog.All)
        {
            if (GestureFor(command.Id) is { } gesture && gesture == chord) return command.Id;
        }

        return null;
    }

    /// <summary>True when the command's shortcut is not the one it shipped with.</summary>
    public bool IsCustomised(CommandId id) => _overrides.ContainsKey(id.Value);

    /// <summary>
    /// Gives a command a chord, and takes it from whichever command had it. Returns the command
    /// that lost it, if any.
    /// </summary>
    public CommandId? Assign(CommandId id, Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var previous = CommandFor(chord);
        if (previous is { } other && other != id) SetOverride(other, string.Empty);
        SetOverride(id, chord.ToString());
        Save();
        return previous is { } lost && lost != id ? lost : null;
    }

    /// <summary>Takes a command's shortcut away.</summary>
    public void Remove(CommandId id)
    {
        SetOverride(id, string.Empty);
        Save();
    }

    /// <summary>Puts one command back to the shortcut it shipped with.</summary>
    public void Reset(CommandId id)
    {
        _overrides.Remove(id.Value);
        Save();
    }

    /// <summary>Puts every command back to what it shipped with.</summary>
    public void ResetAll()
    {
        _overrides.Clear();
        Save();
    }

    private void SetOverride(CommandId id, string chordText)
    {
        // An override that equals the default is no override.
        var shipped = _catalog.TryGet(id, out var command) ? Chord.Parse(command.DefaultGesture)?.ToString() ?? string.Empty : string.Empty;
        if (chordText == shipped) _overrides.Remove(id.Value);
        else _overrides[id.Value] = chordText;
    }

    private void Load()
    {
        _overrides.Clear();
        var json = _settings.GetString(OverridesKey);
        if (json.Length == 0) return;
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (read is null) return;
            foreach (var (key, value) in read) _overrides[key] = value ?? string.Empty;
        }
        catch (JsonException)
        {
            // Unreadable overrides are no overrides; the defaults still work.
        }
    }

    private void Save()
    {
        _settings.Set(OverridesKey, JsonSerializer.Serialize(_overrides));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
