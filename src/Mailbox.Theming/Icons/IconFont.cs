using Avalonia.Media;

namespace Mailbox.Theming.Icons;

/// <summary>
/// The bundled icon typefaces. Referenced through <see cref="Family"/>, which follows the
/// active icon set — a control that asks at build time draws whichever set the theme chose.
/// </summary>
public static class IconFont
{
    /// <summary>Family names as embedded in the TTFs.</summary>
    public const string FamilyName = "FluentSystemIcons-Regular";
    public const string FilledFamilyName = "FluentSystemIcons-Filled";

    private const string RegularUri =
        "avares://Mailbox.Theming/Assets/Fonts/FluentSystemIcons-Regular.ttf#" + FamilyName;

    private const string FilledUri =
        "avares://Mailbox.Theming/Assets/Fonts/FluentSystemIcons-Filled.ttf#" + FilledFamilyName;

    private static FontFamily? _regular;
    private static FontFamily? _filled;

    /// <summary>The active set's family. The artwork difference lives in the font itself — the two largely share codepoints — but not everywhere, so the family and the glyph map still travel together.</summary>
    public static FontFamily Family => IconSets.Active == IconSets.Filled
        ? _filled ??= new FontFamily(FilledUri)
        : _regular ??= new FontFamily(RegularUri);
}
