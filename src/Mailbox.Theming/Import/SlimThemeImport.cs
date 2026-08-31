using Mailbox.Theming.Files;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Theming.Import;

/// <summary>What one import produced, for the summary dialog and the harness read-back alike.</summary>
public sealed record ImportResult(
    ThemeFile File,
    string BaseId,
    bool ReadsDark,
    string DarkSignal,
    IReadOnlyList<string> TokensWritten,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<RepairedInk> Repaired,
    IReadOnlyList<ContrastFinding> Residual,
    string Origin = "browser");

/// <summary>
/// The slim mapper: a browser theme's caption strip and left chrome onto Mailbox, and nothing
/// else. <c>frame</c> and its ink paint the header band — title bar, tab strip, status bar —
/// <c>sidebar</c> and its ink the left chrome — folder pane, rail, the pane behind the list,
/// never the rows — and the header image becomes the caption backdrop. A ~10-token overlay on
/// a built-in base; every content surface is the base's, correct by construction, which is the
/// light-content rule enforced by omission rather than by care.
/// </summary>
/// <remarks>
/// The wider mapping — popups, toolbar fields, the accent chain — stays in the plan as the
/// later, additive version; the themes the owner actually liked "only really showed up at the
/// title bar", and this is that cut. All the judgement lives in <see cref="Recolour"/>: inks
/// follow their grounds from the base's own extremes, washes are the built-ins' constants,
/// alpha flattens, and a pair the mapper itself made unreadable is repaired before the file is
/// written — then written anyway, because a theme that is hard to read is its owner's to fix
/// and still theirs to use.
/// </remarks>
public static class SlimThemeImport
{
    /// <summary>The colour keys the slim cut consumes; everything else a theme says is "seen, unmapped".</summary>
    private static readonly string[] Consumed = ["frame", "tab_background_text", "sidebar", "sidebar_text"];

    public static ImportResult Map(BrowserTheme theme, string id, string name, string? backdropPath)
    {
        ArgumentNullException.ThrowIfNull(theme);

        // One import per package: dark_theme overlays when the theme reads dark (or is all
        // there is), so the file carries the half a dark-leaning theme meant.
        var colours = new Dictionary<string, string>(theme.Colours, StringComparer.OrdinalIgnoreCase);
        var (dark, signal) = ReadsDark(theme, colours);
        if (theme.DarkColours.Count > 0 && (dark || colours.Count == 0))
        {
            foreach (var (key, value) in theme.DarkColours) colours[key] = value;
            (dark, signal) = ReadsDark(theme, colours);
        }

        var baseId = dark ? OfficeThemes.DarkGray : OfficeThemes.White;
        var baseTokens = OfficeThemes.Build(baseId);
        var overlay = new TokenSet();

        // The caption strip. frame is the format's one mandatory colour, so an images-only
        // theme still lands a readable band.
        if (Flat(colours, "frame", "#FFFFFF") is { } frame)
        {
            overlay.Set(TokenKeys.TitleBar.Background, frame);
            overlay.Set(TokenKeys.Ribbon.TabStripBackground, frame);
            overlay.Set(TokenKeys.StatusBar.Background, frame);

            var ink = Flat(colours, "tab_background_text", frame) ?? Recolour.InkFor(frame, baseTokens).Reference;
            overlay.Set(TokenKeys.TitleBar.Foreground, ink);
            overlay.Set(TokenKeys.Ribbon.TabText, ink);
            overlay.Set(TokenKeys.StatusBar.Foreground, ink);

            var (hover, pressed) = Recolour.WashesFor(frame);
            overlay.Set(TokenKeys.TitleBar.CaptionHover, hover);
            overlay.Set(TokenKeys.TitleBar.CaptionPressed, pressed);
        }

        // The left chrome — and only the chrome: the rows and the reading pane are the
        // base's, whatever this theme thinks a document looks like.
        if (Flat(colours, "sidebar", "#FFFFFF") is { } sidebar)
        {
            overlay.Set(TokenKeys.Nav.Background, sidebar);
            overlay.Set(TokenKeys.Rail.Background, sidebar);
            overlay.Set(TokenKeys.List.Background, sidebar);

            var ink = Flat(colours, "sidebar_text", sidebar) ?? Recolour.InkFor(sidebar, baseTokens).Reference;
            overlay.Set(TokenKeys.Nav.ItemText, ink);
            overlay.Set(TokenKeys.Rail.ItemText, ink);
            overlay.Set(TokenKeys.List.HeaderText, ink);

            var (hover, pressed) = Recolour.WashesFor(sidebar);
            overlay.Set(TokenKeys.Rail.ItemHover, hover);
            overlay.Set(TokenKeys.Rail.ItemPressed, pressed);
        }

        // The header image rides the caption backdrop, placed as the manifest placed it.
        if (backdropPath is not null)
        {
            overlay.Set(TokenKeys.TitleBar.Backdrop, backdropPath);
            overlay.Set(TokenKeys.TitleBar.BackdropAlignment, theme.Alignment);
            overlay.Set(TokenKeys.TitleBar.BackdropTiling, theme.Tiling);
            overlay.Set(TokenKeys.TitleBar.BackdropSize, "auto");
            overlay.Set(TokenKeys.TitleBar.BackdropOpacity, "1");
        }

        var written = overlay.Keys.ToList();

        // Repair what the mapper itself made unreadable — the base's own tokens are never
        // touched, and an ink the theme stated against a ground it also stated is the
        // author's twice over: reported, not repaired.
        var statedInks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (colours.ContainsKey("frame") && colours.ContainsKey("tab_background_text"))
        {
            statedInks.Add(TokenKeys.TitleBar.Foreground);
            statedInks.Add(TokenKeys.Ribbon.TabText);
            statedInks.Add(TokenKeys.StatusBar.Foreground);
        }

        if (colours.ContainsKey("sidebar") && colours.ContainsKey("sidebar_text"))
        {
            statedInks.Add(TokenKeys.Nav.ItemText);
            statedInks.Add(TokenKeys.Rail.ItemText);
            statedInks.Add(TokenKeys.List.HeaderText);
        }

        var repairable = new HashSet<string>(
            written.Where(k => !statedInks.Contains(k) && !k.Contains(".backdrop", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        var full = baseTokens.OverlaidWith(overlay);
        var repaired = Recolour.RepairContrast(full, overlay, repairable);
        var residual = ContrastAudit.Check(full.Resolve());

        // Provenance, inert in token values, so a re-import can find its earlier self.
        overlay.Set("import.origin", "firefox-static-theme");
        overlay.Set("import.source", theme.SourceId);
        overlay.Set("import.name", theme.Name);
        overlay.Set("import.version", theme.Version);

        var unmapped = colours.Keys
            .Where(k => !Consumed.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // "dark" is deliberately omitted from the file: the base's answer is inherited, as a
        // theme the editor saves does.
        var file = new ThemeFile(id, name, baseId, IsDark: null, overlay);
        return new ImportResult(file, baseId, dark, signal, written, unmapped, theme.Skipped, repaired, residual);
    }

    /// <summary>A stated colour, flattened over the ground it is drawn on; null when absent or fully transparent.</summary>
    private static string? Flat(Dictionary<string, string> colours, string key, string ground)
        => colours.TryGetValue(key, out var value) ? Recolour.Flatten(value, ground) : null;

    /// <summary>
    /// "Reads dark", first answer wins: the manifest's own <c>color_scheme</c>; the ink
    /// brighter than its ground — the browser's own logic, the reliable signal; the frame
    /// against mid grey.
    /// </summary>
    private static (bool Dark, string Signal) ReadsDark(BrowserTheme theme, Dictionary<string, string> colours)
    {
        if (theme.ColorScheme is "dark") return (true, "color_scheme says dark");
        if (theme.ColorScheme is "light") return (false, "color_scheme says light");

        colours.TryGetValue("frame", out var frame);
        colours.TryGetValue("tab_background_text", out var ink);
        if (frame is not null && ink is not null
            && ContrastAudit.Luminance(Recolour.Flatten(ink, frame) ?? ink) is { } inkLum
            && ContrastAudit.Luminance(Recolour.Flatten(frame, "#FFFFFF") ?? frame) is { } frameLum)
        {
            return (inkLum > frameLum, inkLum > frameLum
                ? "the caption's ink is brighter than its ground"
                : "the caption's ink is darker than its ground");
        }

        var dark = frame is not null && Recolour.ReadsDark(Recolour.Flatten(frame, "#FFFFFF") ?? frame);
        return (dark, dark ? "the frame is darker than mid grey" : "the frame is lighter than mid grey (or absent)");
    }
}
