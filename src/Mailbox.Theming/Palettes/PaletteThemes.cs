using Mailbox.Theming.Files;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Theming.Palettes;

/// <summary>What applying a palette produced, for the read-back.</summary>
public sealed record PaletteResult(
    ThemeFile File,
    string BaseId,
    bool ReadsDark,
    IReadOnlyList<string> TokensWritten,
    IReadOnlyList<RepairedInk> Repaired,
    IReadOnlyList<ContrastFinding> Residual);

/// <summary>
/// A colour scheme onto Mailbox's chrome: the raised slot paints the caption strip, the ground
/// slot the left chrome, the ink slot their text where it reads, and the most usable accent
/// slot becomes <c>palette.brand.primary</c> — one key that buys the whole brand ramp, because
/// accent derivation shifts every entry of the base's by the same measured offsets. Content is
/// never touched: a palette recolours what frames the mail, not the mail.
/// </summary>
/// <remarks>
/// The same shape as the importer's mapping deliberately — both are writers into the same
/// engine, and both lean on <see cref="Recolour"/> for every judgement: ink follows ground
/// from the base's own extremes when the scheme's ink cannot be read, washes are the
/// built-ins' constants, and whatever the mapper itself made unreadable is repaired in bounded
/// steps before the file is written.
/// </remarks>
public static class PaletteThemes
{
    /// <summary>The id a scheme's theme file carries: <c>palette-&lt;scheme&gt;</c>.</summary>
    public static string ThemeId(ColourScheme scheme) => $"palette-{scheme.Id}";

    public static PaletteResult Map(ColourScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        var baseId = scheme.Dark ? OfficeThemes.DarkGray : OfficeThemes.White;
        var baseTokens = OfficeThemes.Build(baseId);
        var overlay = new TokenSet();

        var ground = scheme.Slot("base00");
        var raised = scheme.Palette.ContainsKey("base01") ? scheme.Slot("base01") : ground;
        var stated = scheme.Palette.TryGetValue("base05", out var schemeInk) ? schemeInk : null;

        string InkOn(string surface)
            => stated is not null && (ContrastAudit.Ratio(stated, surface) ?? 0) >= ContrastAudit.MinimumRatio
                ? stated
                : Recolour.InkFor(surface, baseTokens).Reference;

        // The caption strip takes the raised slot — base16 draws its status bars there — and
        // the left chrome the ground, so the two read as layers the way the built-ins do.
        overlay.Set(TokenKeys.TitleBar.Background, raised);
        overlay.Set(TokenKeys.Ribbon.TabStripBackground, raised);
        overlay.Set(TokenKeys.StatusBar.Background, raised);
        overlay.Set(TokenKeys.TitleBar.Foreground, InkOn(raised));
        overlay.Set(TokenKeys.Ribbon.TabText, InkOn(raised));
        overlay.Set(TokenKeys.StatusBar.Foreground, InkOn(raised));

        overlay.Set(TokenKeys.Nav.Background, ground);
        overlay.Set(TokenKeys.Rail.Background, ground);
        overlay.Set(TokenKeys.List.Background, ground);
        overlay.Set(TokenKeys.Nav.ItemText, InkOn(ground));
        overlay.Set(TokenKeys.Rail.ItemText, InkOn(ground));
        overlay.Set(TokenKeys.List.HeaderText, InkOn(ground));

        var (hover, pressed) = Recolour.WashesFor(raised);
        overlay.Set(TokenKeys.TitleBar.CaptionHover, hover);
        overlay.Set(TokenKeys.TitleBar.CaptionPressed, pressed);
        var (railHover, railPressed) = Recolour.WashesFor(ground);
        overlay.Set(TokenKeys.Rail.ItemHover, railHover);
        overlay.Set(TokenKeys.Rail.ItemPressed, railPressed);

        // The accent: the first slot with real chroma, in the order base16 reserves them —
        // blue, violet, green, red. One write; the ramp derives. The accent reaches content
        // through references — unread text on the light rows above all — so it is walked to
        // readability against the base's own row ground before it is written: the hue is the
        // scheme's, the legibility is non-negotiable.
        var rowGround = baseTokens.Resolve().GetString(TokenKeys.List.RowBackground);
        foreach (var slot in (string[])["base0D", "base0E", "base0B", "base08"])
        {
            if (scheme.Palette.TryGetValue(slot, out var candidate)
                && Oklch.Parse(candidate) is { C: > 0.04 })
            {
                overlay.Set(AccentDerivation.Primary, Recolour.ReadableOn(candidate, rowGround));
                break;
            }
        }

        var written = overlay.Keys.ToList();

        // Every choice above is the mapper's, so every failing ink it wrote is its to repair.
        var full = baseTokens.OverlaidWith(overlay);
        var repaired = Recolour.RepairContrast(full, overlay);
        var residual = ContrastAudit.Check(full.Resolve());

        overlay.Set("palette.origin", "colour-scheme");
        overlay.Set("palette.scheme", scheme.Id);
        overlay.Set("palette.author", scheme.Author.Replace("{", "").Replace("}", ""));

        var file = new ThemeFile(ThemeId(scheme), scheme.Name, baseId, IsDark: null, overlay);
        return new PaletteResult(file, baseId, scheme.Dark, written, repaired, residual);
    }

    /// <summary>Maps and writes the scheme's theme file into the themes directory, replacing its earlier self.</summary>
    public static (PaletteResult Result, string Path) Write(ColourScheme scheme, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var result = Map(scheme);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, result.File.Id + ThemeFileFormat.Extension);
        File.WriteAllText(path, ThemeFileFormat.Write(result.File));
        return (result, path);
    }
}
