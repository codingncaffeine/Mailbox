using System.Globalization;

namespace Mailbox.Theming.Import;

/// <summary>
/// The colour grammar a browser theme speaks: hex in all four widths, <c>rgb()</c>/<c>rgba()</c>,
/// <c>hsl()</c>/<c>hsla()</c>, the CSS named colours, and the manifest's integer arrays. Emits
/// the engine's own <c>#RRGGBB</c> / <c>#AARRGGBB</c>; anything it cannot read is null, and the
/// caller says so in the read-back rather than guessing.
/// </summary>
public static class CssColour
{
    /// <summary>A CSS colour string to engine hex, or null.</summary>
    public static string? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        if (text.StartsWith('#')) return ParseHex(text);
        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return ParseRgbFunction(text);
        if (text.StartsWith("hsl", StringComparison.OrdinalIgnoreCase)) return ParseHslFunction(text);

        // The named colours, through the toolkit's own table — "transparent" included.
        if (Avalonia.Media.Color.TryParse(text, out var named)) return Emit(named.A, named.R, named.G, named.B);

        return null;
    }

    /// <summary>The manifest's array form: <c>[r, g, b]</c> or <c>[r, g, b, a]</c>, alpha 0–1.</summary>
    public static string? Parse(IReadOnlyList<double> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count is not (3 or 4)) return null;

        int Channel(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
        var alpha = channels.Count == 4 ? (int)Math.Clamp(Math.Round(channels[3] * 255), 0, 255) : 255;
        return Emit(alpha, Channel(channels[0]), Channel(channels[1]), Channel(channels[2]));
    }

    private static string? ParseHex(string text)
    {
        var digits = text[1..];
        if (!digits.All(Uri.IsHexDigit)) return null;

        return digits.Length switch
        {
            3 => Emit(255, Wide(digits[0]), Wide(digits[1]), Wide(digits[2])),
            4 => Emit(Wide(digits[3]), Wide(digits[0]), Wide(digits[1]), Wide(digits[2])),
            6 => Emit(255, Byte(digits, 0), Byte(digits, 2), Byte(digits, 4)),
            8 => Emit(Byte(digits, 6), Byte(digits, 0), Byte(digits, 2), Byte(digits, 4)),
            _ => null,
        };

        static int Wide(char c) => Convert.ToInt32($"{c}{c}", 16);
        static int Byte(string s, int at) => Convert.ToInt32(s.Substring(at, 2), 16);
    }

    private static string? ParseRgbFunction(string text)
    {
        if (Arguments(text) is not { } parts || parts.Length is not (3 or 4)) return null;

        int? Channel(string part)
        {
            part = part.Trim();
            if (part.EndsWith('%'))
            {
                return double.TryParse(part[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                    ? (int)Math.Clamp(Math.Round(percent * 2.55), 0, 255)
                    : null;
            }

            return double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? (int)Math.Clamp(Math.Round(v), 0, 255)
                : null;
        }

        if (Channel(parts[0]) is not { } r || Channel(parts[1]) is not { } g || Channel(parts[2]) is not { } b) return null;
        var alpha = parts.Length == 4 ? Alpha(parts[3]) : 255;
        return alpha is { } a ? Emit(a, r, g, b) : null;
    }

    private static string? ParseHslFunction(string text)
    {
        if (Arguments(text) is not { } parts || parts.Length is not (3 or 4)) return null;

        var hueText = parts[0].Trim().Replace("deg", "", StringComparison.OrdinalIgnoreCase);
        if (!double.TryParse(hueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return null;
        if (!Percent(parts[1], out var s) || !Percent(parts[2], out var l)) return null;
        var alpha = parts.Length == 4 ? Alpha(parts[3]) : 255;
        if (alpha is not { } a) return null;

        h = ((h % 360) + 360) % 360;
        var c = (1 - Math.Abs((2 * l) - 1)) * s;
        var x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        var m = l - (c / 2);
        var (r1, g1, b1) = ((int)(h / 60)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        int Channel(double v) => (int)Math.Clamp(Math.Round((v + m) * 255), 0, 255);
        return Emit(a, Channel(r1), Channel(g1), Channel(b1));

        static bool Percent(string part, out double value)
        {
            value = 0;
            part = part.Trim();
            if (!part.EndsWith('%')) return false;
            if (!double.TryParse(part[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) return false;
            value = Math.Clamp(percent / 100, 0, 1);
            return true;
        }
    }

    private static int? Alpha(string part)
    {
        part = part.Trim();
        if (part.EndsWith('%'))
        {
            return double.TryParse(part[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                ? (int)Math.Clamp(Math.Round(percent * 2.55), 0, 255)
                : null;
        }

        return double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Clamp(Math.Round(v * 255), 0, 255)
            : null;
    }

    private static string[]? Arguments(string text)
    {
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        var inner = text[(open + 1)..close];

        // Both the comma form and the modern space form, "/" introducing alpha in the latter.
        return inner.Contains(',')
            ? inner.Split(',')
            : inner.Replace("/", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Emit(int a, int r, int g, int b)
        => a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{a:X2}{r:X2}{g:X2}{b:X2}";
}
