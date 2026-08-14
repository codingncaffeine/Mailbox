using Avalonia.Media;

namespace Mailbox.Theming.Icons;

/// <summary>
/// The bundled icon typeface. Referenced through a token so a theme can substitute a
/// different icon set without any command definition changing.
/// </summary>
public static class IconFont
{
    /// <summary>Family name as embedded in the TTF.</summary>
    public const string FamilyName = "FluentSystemIcons-Regular";

    private const string ResourceUri =
        "avares://Mailbox.Theming/Assets/Fonts/FluentSystemIcons-Regular.ttf#" + FamilyName;

    private static FontFamily? _family;

    public static FontFamily Family => _family ??= new FontFamily(ResourceUri);
}
