using System.Globalization;

namespace Mailbox.Theming.Tokens;

/// <summary>
/// A colour in OKLCH — lightness, chroma, hue — the space in which "a little darker" and "the
/// pale version of this" mean what a person means by them, which sRGB arithmetic does not.
/// </summary>
/// <remarks>
/// Björn Ottosson's OKLab, in polar form. Only what accent derivation needs: to and from sRGB
/// hex, and a way to move lightness while keeping the hue. Chroma is scaled down as lightness
/// heads for white or black so a derived shade stays inside sRGB instead of clipping to a
/// different hue.
/// </remarks>
public readonly record struct Oklch(double L, double C, double H)
{
    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c> (alpha ignored); null for anything else.</summary>
    public static Oklch? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var text = hex.Trim();
        if (text.Length == 0 || text[0] != '#') return null;
        text = text[1..];
        if (text.Length == 8) text = text[2..];
        if (text.Length != 6) return null;
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;

        return FromRgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    public static Oklch FromRgb(int r, int g, int b)
    {
        var lr = Linear(r / 255.0);
        var lg = Linear(g / 255.0);
        var lb = Linear(b / 255.0);

        var l = Math.Cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
        var m = Math.Cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
        var s = Math.Cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);

        var lab = (
            L: 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            A: 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            B: 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);

        var c = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
        var h = c < 1e-6 ? 0 : (Math.Atan2(lab.B, lab.A) * 180 / Math.PI + 360) % 360;
        return new Oklch(lab.L, c, h);
    }

    /// <summary><c>#RRGGBB</c>, each channel clipped to sRGB.</summary>
    public string ToHex()
    {
        var (r, g, b) = ToRgb();
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public (int R, int G, int B) ToRgb()
    {
        var hr = H * Math.PI / 180;
        var a = C * Math.Cos(hr);
        var bb = C * Math.Sin(hr);

        var l = L + 0.3963377774 * a + 0.2158037573 * bb;
        var m = L - 0.1055613458 * a - 0.0638541728 * bb;
        var s = L - 0.0894841775 * a - 1.2914855480 * bb;
        l *= l * l;
        m *= m * m;
        s *= s * s;

        var lr = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var lg = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var lb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        return (Channel(lr), Channel(lg), Channel(lb));
    }

    /// <summary>
    /// The same hue at another lightness, chroma eased towards zero as lightness heads for the
    /// ends of the range — a pale tint of a saturated blue is a pale blue, not a clipped one.
    /// </summary>
    public Oklch WithLightness(double lightness)
    {
        var l = Math.Clamp(lightness, 0, 1);
        // Chroma at the extremes cannot be shown; scale it by how far from the ends we are,
        // relative to where the source sits, and never up.
        var room = Math.Min(l, 1 - l) * 2;
        var sourceRoom = Math.Min(L, 1 - L) * 2;
        var scale = sourceRoom < 1e-6 ? 0 : Math.Min(1, room / sourceRoom);
        return new Oklch(l, C * scale, H);
    }

    private static double Linear(double channel)
        => channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static int Channel(double linear)
    {
        var clipped = Math.Clamp(linear, 0, 1);
        var srgb = clipped <= 0.0031308 ? clipped * 12.92 : 1.055 * Math.Pow(clipped, 1 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(srgb, 0, 1) * 255);
    }
}

/// <summary>
/// The rest of an accent from its one colour: the darker shades a hover and a press use, and
/// the pale tint a selection sits on — so a theme that says <c>palette.brand.primary</c> and
/// nothing more is a complete accent, instead of a new colour with the base's blue still
/// under the pointer.
/// </summary>
public static class AccentDerivation
{
    /// <summary>The primary and what derives from it, in the token names the built-ins use.</summary>
    public const string Primary = "palette.brand.primary";
    public const string Dark = "palette.brand.dark";
    public const string Darker = "palette.brand.darker";
    public const string Light = "palette.brand.light";

    /// <summary>
    /// The lightness steps, read off the built-ins: Colorful's #0F6CBD sits at L≈0.53, its hover
    /// #0C5595 at 0.45 and its pressed #0A4A82 at 0.41; the light tint is #EFF6FC on a light
    /// ground (L≈0.97) and #B3D3EC where the surface is a mid grey (L≈0.85).
    /// </summary>
    private const double HoverStep = -0.08;
    private const double PressedStep = -0.12;
    private const double LightOnLight = 0.97;
    private const double LightOnDark = 0.85;

    /// <summary>The three shades from a primary, as hex.</summary>
    public static (string Dark, string Darker, string Light)? From(string primaryHex, bool darkTheme)
    {
        if (Oklch.Parse(primaryHex) is not { } primary) return null;
        return (
            primary.WithLightness(primary.L + HoverStep).ToHex(),
            primary.WithLightness(primary.L + PressedStep).ToHex(),
            primary.WithLightness(darkTheme ? LightOnDark : LightOnLight).ToHex());
    }

    /// <summary>The prefix every brand entry shares — the primary and everything that follows it.</summary>
    public const string BrandPrefix = "palette.brand.";

    /// <summary>
    /// Fills in every <c>palette.brand.*</c> entry a theme did not set, when it set the primary
    /// — the primary being what <paramref name="setByTheme"/> says the theme itself declared, as
    /// opposed to what its base carried. Each entry follows the new primary by the offset the
    /// base kept between its own primary and that entry, in OKLCH: as much darker, as much
    /// paler, as much less saturated, the same turn of hue. So a base's measured relationships —
    /// the title bar a touch brighter than the accent, the selection tint pale — become the
    /// rules for a theme that says only its colour. Without a base primary to measure against,
    /// the three shades every theme has are derived by fixed steps.
    /// </summary>
    /// <returns>How many tokens were derived.</returns>
    public static int Complete(TokenSet tokens, TokenSet? baseTokens, ISet<string> setByTheme, bool darkTheme)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(setByTheme);
        if (!setByTheme.Contains(Primary) || !tokens.TryGetRaw(Primary, out var primaryRaw)) return 0;
        if (Oklch.Parse(primaryRaw) is not { } primary) return 0;

        var count = 0;
        var basePrimary = baseTokens is not null && baseTokens.TryGetRaw(Primary, out var basePrimaryRaw)
            ? Oklch.Parse(basePrimaryRaw)
            : null;

        if (baseTokens is not null && basePrimary is { } from)
        {
            foreach (var key in baseTokens.Keys.Where(k => k.StartsWith(BrandPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (string.Equals(key, Primary, StringComparison.OrdinalIgnoreCase) || setByTheme.Contains(key)) continue;
                if (!baseTokens.TryGetRaw(key, out var baseRaw) || Oklch.Parse(baseRaw) is not { } baseValue) continue;

                tokens.Set(key, Shift(primary, from, baseValue).ToHex());
                count++;
            }

            return count;
        }

        if (From(primaryRaw, darkTheme) is not { } derived) return 0;
        foreach (var (key, value) in new[] { (Dark, derived.Dark), (Darker, derived.Darker), (Light, derived.Light) })
        {
            if (setByTheme.Contains(key)) continue;
            tokens.Set(key, value);
            count++;
        }

        return count;
    }

    /// <summary>
    /// <paramref name="target"/> as it stands to <paramref name="from"/>, applied to
    /// <paramref name="primary"/>: the same lightness offset, the same chroma ratio, the same
    /// turn of hue.
    /// </summary>
    internal static Oklch Shift(Oklch primary, Oklch from, Oklch target)
    {
        var lightness = Math.Clamp(primary.L + (target.L - from.L), 0, 1);
        var chroma = from.C < 1e-6 ? target.C : primary.C * (target.C / from.C);
        var hue = (primary.H + (target.H - from.H) + 360) % 360;
        // Ease chroma towards the ends of the lightness range, as WithLightness does, so a
        // pale tint of a strong new colour stays a pale tint.
        var room = Math.Min(lightness, 1 - lightness) * 2;
        var sourceRoom = Math.Min(target.L, 1 - target.L) * 2;
        if (sourceRoom > 1e-6 && room < sourceRoom) chroma *= room / sourceRoom;
        return new Oklch(lightness, chroma, hue);
    }
}
