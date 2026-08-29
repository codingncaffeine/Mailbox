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

        // The punctuation keys as they are written down, so "Ctrl+." and "Alt+-" read back as
        // the names the windowing layer gives them.
        "?" => "OemQuestion",
        "." => "OemPeriod",
        "," => "OemComma",
        "-" => "OemMinus",
        "=" => "OemPlus",
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
        "OemQuestion" => "?",
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
    /// <remarks>
    /// Every command's own shortcut — the reader's or the shipped one — is looked at first, and
    /// only then the shipped "also" chords (<see cref="MailboxCommand.AlsoGestures"/>), so a chord
    /// the reader has given to some command is that command's whatever else shipped with it.
    /// </remarks>
    public CommandId? CommandFor(Chord chord) => CommandFor(chord, null);

    /// <summary>
    /// The command a chord runs with a module open — the open module's own before the ones every
    /// module shares, and never another module's.
    /// </summary>
    /// <remarks>
    /// One key means different things in different modules: Delete throws away a message in Mail
    /// and an appointment in Calendar, Ctrl+N writes a message in one and books an appointment in
    /// the other. Rather than a table of exceptions, each of those is an ordinary command scoped
    /// to its module, and this picks between them: a command scoped to the open module first, then
    /// one scoped to every module, and one belonging to a different module not at all — pressing
    /// Ctrl+R in the calendar should do nothing, not reach for a message that is not there.
    /// <para>
    /// Passing null asks the question without a module, which is what the shortcut editor wants:
    /// every chord in the catalogue, whichever module owns it.
    /// </para>
    /// </remarks>
    public CommandId? CommandFor(Chord chord, MailboxModule? module)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var scope = module?.AsScope() ?? ModuleScope.None;

        // Asking with a module is the shell asking, and the shell cannot run the compose or
        // appointment window's commands: Ctrl+U marks a message unread here whatever it does in
        // an editor.
        //
        // Asked on whether a module was named, not on whether that module has a scope. Folders
        // and Shortcuts have none — AsScope answers None for both — and reading that as "nobody
        // named a module" let every command in, whichever window it belongs to: in those two
        // modules Ctrl+C reached the compose window's Copy, was treated as handled, and so never
        // reached the control the reader was actually in. Twelve chords did that, the clipboard's
        // four among them.
        bool Here(MailboxCommand c) => module is null || c.Surface == CommandSurface.Shell;

        // Two passes — own shortcuts, then the shipped "also" chords — each asked twice: the
        // module's own commands, then the ones every module shares.
        foreach (var also in (bool[])[false, true])
        {
            if (Match(chord, also, c => Here(c) && scope != ModuleScope.None && c.Scope != ModuleScope.Any && c.Scope.HasFlag(scope)) is { } own) return own;

            // A command that names this module as its chord's home, ahead of the ones every
            // module shares. Six commands put a New Items entry in every module and are scoped
            // Any for it, and all six also carry Ctrl+N; scope alone therefore could not say
            // which of them Ctrl+N means in the calendar, and the shared pass below answered
            // with whichever the frozen catalogue happened to enumerate first — a different one
            // between runs. GestureHome is the module where the answer is not a guess.
            if (Match(chord, also, c => Here(c) && scope != ModuleScope.None && c.GestureHome == scope) is { } home) return home;

            // Asked on `module is null` for the same reason Here is: a module with no scope of
            // its own — Folders and Shortcuts — is still a module, and "no scope" must mean
            // "only what every module shares", not "anything at all". Reading it the other way
            // let Delete reach the journal's delete and Enter the calendar's open, each the
            // first of several module-scoped owners in catalogue order. Null really is nobody
            // asking from a module: that is the shortcut editor, which wants the whole map.
            if (Match(chord, also, c => Here(c) && (module is null || c.Scope == ModuleScope.Any)) is { } shared) return shared;
        }

        return null;
    }

    /// <summary>
    /// The command a chord runs in a window of its own — a compose or an appointment window.
    /// </summary>
    /// <remarks>
    /// Those windows have no module and no list; what they have is their own set of commands, and
    /// their keys are the shell's to know nothing about. Asking this way is what lets Ctrl+U
    /// underline in one window and mark a message unread in the other.
    /// </remarks>
    public CommandId? CommandFor(Chord chord, CommandSurface surface)
    {
        ArgumentNullException.ThrowIfNull(chord);

        foreach (var also in (bool[])[false, true])
        {
            if (Match(chord, also, c => c.Surface == surface) is { } found) return found;
        }

        return null;
    }

    private CommandId? Match(Chord chord, bool also, Func<MailboxCommand, bool> wanted)
    {
        foreach (var command in _catalog.All)
        {
            if (!wanted(command)) continue;

            if (!also)
            {
                if (GestureFor(command.Id) is { } gesture && gesture == chord) return command.Id;
                continue;
            }

            foreach (var text in command.AlsoGestures)
            {
                if (Chord.Parse(text) is { } gesture && gesture == chord) return command.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// The shipped chords that also run a command, beyond <see cref="GestureFor"/> — those of
    /// them the reader has not since given to something else.
    /// </summary>
    public IReadOnlyList<Chord> AlsoGesturesFor(CommandId id)
    {
        if (!_catalog.TryGet(id, out var command)) return [];
        var chords = new List<Chord>();
        foreach (var also in command.AlsoGestures)
        {
            if (Chord.Parse(also) is { } chord && CommandFor(chord) == id) chords.Add(chord);
        }

        return chords;
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

        // Taken from the previous owner only when the chord was its own shortcut. One held as a
        // shipped "also" chord needs nothing done — the new owner's own shortcut outranks it —
        // and stripping the owner would take its real shortcut away instead.
        if (previous is { } other && other != id && GestureFor(other) == chord) SetOverride(other, string.Empty);
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
        // No overrides is no key, not an empty document — the same rule the ribbon's
        // customization follows by deleting its file when nothing differs from shipped.
        if (_overrides.Count == 0) _settings.Remove(OverridesKey);
        else _settings.Set(OverridesKey, JsonSerializer.Serialize(_overrides));

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
