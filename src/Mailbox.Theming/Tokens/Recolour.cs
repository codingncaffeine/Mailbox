using System.Globalization;

namespace Mailbox.Theming.Tokens;

/// <summary>One ink the repair moved: which token, from what to what, against which ground, and both ratios.</summary>
public sealed record RepairedInk(string Token, string From, string To, string Ground, double Before, double After);

/// <summary>
/// The shared recolouring policies: how a new ground gets a readable ink, how hover and
/// pressed derive from what they sit on, which washes a caption takes, and how an unreadable
/// pair is repaired. One implementation, because the palette picker and the theme importer
/// both need exactly these rules and two copies would disagree.
/// </summary>
/// <remarks>
/// Every rule here either selects from values a base theme already carries or moves a colour
/// the caller supplied — nothing invents a colour of its own. That is what keeps an automated
/// door inside the owner's rules: the base's measured relationships stay the authority.
/// </remarks>
public static class Recolour
{
    /// <summary>
    /// The neutral washes a caption button wears, the same four constants the built-ins carry:
    /// white over a dark caption, black over a light one, at about an eighth and a fifth.
    /// Selected, never computed — the one alpha an automated writer is allowed to emit.
    /// </summary>
    public const string WashOverDark = "#22FFFFFF";
    public const string WashOverDarkPressed = "#33FFFFFF";
    public const string WashOverLight = "#14000000";
    public const string WashOverLightPressed = "#26000000";

    /// <summary>
    /// The luminance of mid grey — what "reads dark" means when nothing better is stated.
    /// Below this, white washes and light inks; above, the dark pair.
    /// </summary>
    public const double MidGreyLuminance = 0.2158;

    /// <summary>How far a solid hover fill moves from its ground's lightness, and a pressed one.</summary>
    private const double HoverStep = 0.06;
    private const double PressedStep = 0.10;

    /// <summary>Whether a ground is dark enough that light ink and white washes read on it.</summary>
    public static bool ReadsDark(string groundHex)
        => ContrastAudit.Luminance(groundHex) is { } l && l < MidGreyLuminance;

    /// <summary>The hover and pressed washes for a caption standing on this ground.</summary>
    public static (string Hover, string Pressed) WashesFor(string groundHex)
        => ReadsDark(groundHex)
            ? (WashOverDark, WashOverDarkPressed)
            : (WashOverLight, WashOverLightPressed);

    /// <summary>
    /// Policy: ink follows its ground, never its old value. Chooses between the base's own two
    /// ink extremes — its <c>palette.neutral.white</c> and <c>palette.neutral.primary</c> —
    /// whichever reads better against the new ground, and returns both the reference (what a
    /// theme file should carry, so it stays the base's) and the literal (for arithmetic).
    /// </summary>
    public static (string Reference, string Hex) InkFor(string groundHex, TokenSet baseTokens)
    {
        ArgumentNullException.ThrowIfNull(baseTokens);
        var white = baseTokens.TryGetRaw("palette.neutral.white", out var w) ? w : "#FFFFFF";
        var dark = baseTokens.TryGetRaw("palette.neutral.primary", out var d) ? d : "#323130";

        var whiteRatio = ContrastAudit.Ratio(white, groundHex) ?? 0;
        var darkRatio = ContrastAudit.Ratio(dark, groundHex) ?? 0;

        return whiteRatio >= darkRatio
            ? ("{palette.neutral.white}", white)
            : ("{palette.neutral.primary}", dark);
    }

    /// <summary>
    /// Policy: a solid hover fill derives from its ground in OKLCH — lightness moved away from
    /// the ground's own end of the range, hue and chroma kept — so a light chrome darkens under
    /// the pointer and a dark one lightens, as the built-ins measure.
    /// </summary>
    public static string Hover(string groundHex) => Step(groundHex, HoverStep);

    /// <summary>The pressed fill, a longer move of the same kind.</summary>
    public static string Pressed(string groundHex) => Step(groundHex, PressedStep);

    private static string Step(string groundHex, double step)
    {
        if (Oklch.Parse(groundHex) is not { } ground) return groundHex;
        var lightness = ground.L >= 0.5 ? ground.L - step : ground.L + step;
        return ground.WithLightness(lightness).ToHex();
    }

    /// <summary>
    /// Policy: alpha flattens before storage. A translucent colour is composited over the
    /// ground it was declared against and stored opaque, because the contrast arithmetic
    /// strips alpha and would over-report it. Fully transparent means <em>absent</em> — "let
    /// what is behind show through" — and returns null rather than black.
    /// </summary>
    public static string? Flatten(string hex, string groundHex)
    {
        if (ParseArgb(hex) is not { } colour) return hex;
        if (colour.A == 255) return $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";
        if (colour.A == 0) return null;
        if (ParseArgb(groundHex) is not { } ground) return $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

        var a = colour.A / 255.0;
        int Mix(int fg, int bg) => (int)Math.Round((fg * a) + (bg * (1 - a)));
        return $"#{Mix(colour.R, ground.R):X2}{Mix(colour.G, ground.G):X2}{Mix(colour.B, ground.B):X2}";
    }

    private static (int A, int R, int G, int B)? ParseArgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#') return null;
        var text = hex[1..];
        if (text.Length == 6) text = "FF" + text;
        if (text.Length != 8 || !long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return null;
        return ((int)(argb >> 24) & 0xFF, (int)(argb >> 16) & 0xFF, (int)(argb >> 8) & 0xFF, (int)argb & 0xFF);
    }

    /// <summary>
    /// A colour moved until it reads on a ground: OKLCH lightness walked away from the
    /// ground's side in 0.04 steps, hue and chroma kept, capped at ten. The original comes
    /// back when the cap cannot clear the ratio — the caller decides what that means.
    /// </summary>
    public static string ReadableOn(string inkHex, string groundHex, double minimum = ContrastAudit.MinimumRatio)
    {
        if ((ContrastAudit.Ratio(inkHex, groundHex) ?? 0) >= minimum) return inkHex;
        if (Oklch.Parse(inkHex) is not { } ink || ContrastAudit.Luminance(groundHex) is not { } groundLuminance) return inkHex;

        var direction = groundLuminance < MidGreyLuminance ? 1 : -1;
        for (var step = 1; step <= 10; step++)
        {
            var moved = ink.WithLightness(ink.L + (direction * 0.04 * step));
            if ((ContrastAudit.Ratio(moved.ToHex(), groundHex) ?? 0) >= minimum) return moved.ToHex();
        }

        return inkHex;
    }

    /// <summary>
    /// The contrast repair: for each audit finding whose ink the caller itself wrote, walk the
    /// ink's OKLCH lightness away from the ground in 0.04 steps — hue and chroma kept — until
    /// the ratio clears, capped at ten steps. Findings on tokens the base owns are never
    /// touched, and a pair the cap cannot clear is left as the author stated it: repaired
    /// where the writer caused the problem, reported everywhere.
    /// </summary>
    /// <param name="full">The complete candidate — base and overlay together — used to resolve and measure.</param>
    /// <param name="overlay">What the caller is writing; repaired values land here (and in <paramref name="full"/>).</param>
    /// <param name="written">The ink tokens the caller takes responsibility for; the overlay's own keys when null.</param>
    /// <returns>What moved. Residual findings are whatever <see cref="ContrastAudit.Check"/> still reports afterwards.</returns>
    public static IReadOnlyList<RepairedInk> RepairContrast(
        TokenSet full, TokenSet overlay, IReadOnlySet<string>? written = null, double minimum = ContrastAudit.MinimumRatio)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(overlay);
        var responsible = written ?? new HashSet<string>(overlay.Keys, StringComparer.OrdinalIgnoreCase);

        var repaired = new List<RepairedInk>();
        foreach (var finding in ContrastAudit.Check(full.Resolve(), minimum))
        {
            if (!responsible.Contains(finding.Ink)) continue;
            if (Oklch.Parse(finding.InkColour) is not { } ink) continue;
            if (ContrastAudit.Luminance(finding.GroundColour) is not { } groundLuminance) continue;

            // Away from the ground: a dark ground pushes the ink lighter, a light one darker.
            var direction = groundLuminance < MidGreyLuminance ? 1 : -1;
            var moved = ink;
            var best = finding.Ratio;
            for (var step = 1; step <= 10 && best < minimum; step++)
            {
                moved = ink.WithLightness(ink.L + (direction * 0.04 * step));
                best = ContrastAudit.Ratio(moved.ToHex(), finding.GroundColour) ?? best;
            }

            if (best < minimum) continue; // The cap could not clear it; report-only territory.

            var hex = moved.ToHex();
            full.Set(finding.Ink, hex);
            overlay.Set(finding.Ink, hex);
            repaired.Add(new RepairedInk(finding.Ink, finding.InkColour, hex, finding.Ground, finding.Ratio, best));
        }

        return repaired;
    }
}
