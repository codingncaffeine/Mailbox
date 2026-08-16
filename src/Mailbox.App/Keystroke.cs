using Avalonia.Input;
using Mailbox.Core.Keyboard;

namespace Mailbox.App;

/// <summary>
/// Turns a keystroke into a chord and back, so every window asks the key map the same question.
/// </summary>
/// <remarks>
/// <see cref="Chord"/> lives in the core, which knows nothing of a windowing layer; this is the
/// one place the two names for a key meet. Kept out of any one window because three of them ask —
/// the shell, the compose window and the appointment window — and a shortcut that worked in one
/// and not the others would be a bug nobody could see the shape of.
/// </remarks>
internal static class Keystroke
{
    /// <summary>The keystroke as the key map names it — null for a modifier pressed alone.</summary>
    public static Chord? Of(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
        {
            return null;
        }

        var modifiers = ChordModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= ChordModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= ChordModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= ChordModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= ChordModifiers.Meta;
        return new Chord(modifiers, e.Key.ToString());
    }

    /// <summary>A chord's modifiers as the windowing layer names them, for the harness.</summary>
    public static KeyModifiers Modifiers(ChordModifiers modifiers)
    {
        var pressed = KeyModifiers.None;
        if (modifiers.HasFlag(ChordModifiers.Control)) pressed |= KeyModifiers.Control;
        if (modifiers.HasFlag(ChordModifiers.Alt)) pressed |= KeyModifiers.Alt;
        if (modifiers.HasFlag(ChordModifiers.Shift)) pressed |= KeyModifiers.Shift;
        if (modifiers.HasFlag(ChordModifiers.Meta)) pressed |= KeyModifiers.Meta;
        return pressed;
    }

    /// <summary>
    /// Whether a chord is the reader typing rather than reaching for a command.
    /// </summary>
    /// <remarks>
    /// A window whose content is text — a message being written, an appointment's subject —
    /// keeps the plain keys for itself. Only a chord holding Ctrl or Alt, or a function key,
    /// which types nothing, is a command's.
    /// </remarks>
    public static bool IsTyping(Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (chord.Modifiers is not (ChordModifiers.None or ChordModifiers.Shift)) return false;
        return !(chord.Key.Length > 1 && chord.Key[0] == 'F' && int.TryParse(chord.Key[1..], out _));
    }
}
