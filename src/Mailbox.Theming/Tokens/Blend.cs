using Avalonia.Media;

namespace Mailbox.Theming.Tokens;

/// <summary>
/// Mixing a colour toward a ground, which is how everything that draws in an item's own colour
/// makes a face out of it.
/// </summary>
/// <remarks>
/// One implementation because four things do it — a calendar chip, a note's square, a journal
/// entry's box and the note window itself — and a second copy would drift. The mix is straight
/// linear interpolation per channel rather than anything perceptual, because the values it is
/// asked for were measured off the reference, which evidently did the same.
/// </remarks>
public static class Blend
{
    /// <summary>0 is the colour itself, 1 the ground.</summary>
    public static Color Toward(Color colour, Color ground, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(colour.R + ((ground.R - colour.R) * t)),
            (byte)Math.Round(colour.G + ((ground.G - colour.G) * t)),
            (byte)Math.Round(colour.B + ((ground.B - colour.B) * t)));
    }
}
