using Avalonia;
using Avalonia.Media;

namespace Mailbox.App.Theming;

/// <summary>
/// Controls glyph rasterization, and the harness for the Phase 0 text-rendering investigation.
/// </summary>
/// <remarks>
/// This is the highest fidelity risk in the whole project. Windows draws the reference's UI with
/// ClearType subpixel antialiasing. Skia disables LCD subpixel rendering and gamma correction
/// on X11, and Avalonia's <see cref="TextRenderingMode.SubpixelAntialias"/> is reported to have
/// no effect on several Linux distributions. Grayscale antialiasing renders thinner and softer
/// than Windows, and that single difference is most of what makes a Linux clone feel wrong
/// before you can articulate why.
/// <para>
/// Avalonia 12.1 exposes these through <see cref="TextOptions"/> Get/Set methods rather than
/// public attached-property fields, so they cannot be set from XAML — hence this class.
/// </para>
/// <para>
/// Override at runtime to compare modes side by side:
/// <c>MAILBOX_TEXT_MODE=subpixel|antialias|alias|unspecified</c> and
/// <c>MAILBOX_TEXT_HINTING=none|light|strong|unspecified</c>.
/// </para>
/// </remarks>
public static class TextRendering
{
    public const string ModeVariable = "MAILBOX_TEXT_MODE";
    public const string HintingVariable = "MAILBOX_TEXT_HINTING";

    /// <summary>
    /// What Mailbox asks for by default. Subpixel matches Windows where the platform honours
    /// it, and degrades to grayscale where it does not — so asking costs nothing.
    /// </summary>
    public static TextRenderingMode DefaultMode => TextRenderingMode.SubpixelAntialias;

    /// <summary>
    /// Full hinting snaps stems to the pixel grid, which is what makes Windows UI text look
    /// crisp at 12px. On Linux this is also the setting most likely to fight fontconfig.
    /// </summary>
    public static TextHintingMode DefaultHinting => TextHintingMode.Strong;

    public static TextRenderingMode ResolveMode()
        => Environment.GetEnvironmentVariable(ModeVariable)?.ToLowerInvariant() switch
        {
            "subpixel" => TextRenderingMode.SubpixelAntialias,
            "antialias" or "grayscale" => TextRenderingMode.Antialias,
            "alias" or "none" => TextRenderingMode.Alias,
            "unspecified" => TextRenderingMode.Unspecified,
            _ => DefaultMode,
        };

    public static TextHintingMode ResolveHinting()
        => Environment.GetEnvironmentVariable(HintingVariable)?.ToLowerInvariant() switch
        {
            "none" => TextHintingMode.None,
            "light" => TextHintingMode.Light,
            "strong" or "full" => TextHintingMode.Strong,
            "unspecified" => TextHintingMode.Unspecified,
            _ => DefaultHinting,
        };

    /// <summary>Applies the resolved modes to a visual and everything beneath it.</summary>
    public static void Apply(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);

        TextOptions.SetTextRenderingMode(root, ResolveMode());
        TextOptions.SetTextHintingMode(root, ResolveHinting());
    }

    /// <summary>One line for the status bar and the investigation log.</summary>
    public static string Describe()
        => $"text: {ResolveMode()} / hinting {ResolveHinting()}";
}
