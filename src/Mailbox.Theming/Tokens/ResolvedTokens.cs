using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Mailbox.Theming.Tokens;

/// <summary>
/// Tokens after reference expansion — every value is literal. This is what the UI binds
/// against. Immutable, so a theme swap is an atomic replacement rather than a cascade of
/// property change notifications.
/// </summary>
public sealed class ResolvedTokens
{
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, Color> _colorCache = new(StringComparer.OrdinalIgnoreCase);

    internal ResolvedTokens(Dictionary<string, string> values) => _values = values;

    public int Count => _values.Count;

    public IReadOnlyCollection<string> Keys => _values.Keys;

    public bool Contains(string key) => _values.ContainsKey(key);

    public bool TryGetString(string key, [NotNullWhen(true)] out string? value)
        => _values.TryGetValue(key, out value);

    public string GetString(string key) =>
        _values.TryGetValue(key, out var v)
            ? v
            : throw new ThemeResolutionException($"Token '{key}' is not defined in this theme.");

    public Color GetColor(string key)
    {
        if (_colorCache.TryGetValue(key, out var cached)) return cached;

        var raw = GetString(key);
        if (!Color.TryParse(raw, out var color))
        {
            throw new ThemeResolutionException(
                $"Token '{key}' has value '{raw}', which is not a colour. Expected #RRGGBB or #AARRGGBB.");
        }

        _colorCache[key] = color;
        return color;
    }

    public IBrush GetBrush(string key) => new SolidColorBrush(GetColor(key));

    public double GetDouble(string key)
    {
        var raw = GetString(key);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ThemeResolutionException(
                $"Token '{key}' has value '{raw}', which is not a number.");
    }

    /// <summary>Parses "8" as uniform, "8,4" as horizontal/vertical, "1,2,3,4" as LTRB.</summary>
    public Thickness GetThickness(string key)
    {
        var raw = GetString(key);
        try
        {
            return Thickness.Parse(raw);
        }
        catch (FormatException)
        {
            throw new ThemeResolutionException(
                $"Token '{key}' has value '{raw}', which is not a thickness.");
        }
    }

    public CornerRadius GetCornerRadius(string key)
        => new(GetDouble(key));

    public IEnumerable<KeyValuePair<string, string>> AsPairs() => _values;
}
