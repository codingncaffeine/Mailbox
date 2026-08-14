using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Mailbox.Theming.Tokens;

/// <summary>
/// A flat map of token key to raw value, before reference resolution. Values may reference
/// other tokens with <c>{other.token}</c>, which is how a five-line theme can restyle the
/// whole application: override <c>accent.rest</c> and everything pointing at it follows.
/// </summary>
public sealed partial class TokenSet
{
    private readonly Dictionary<string, string> _values;

    public TokenSet() => _values = new(StringComparer.OrdinalIgnoreCase);

    public TokenSet(IEnumerable<KeyValuePair<string, string>> values)
        => _values = new(values, StringComparer.OrdinalIgnoreCase);

    public int Count => _values.Count;

    public IReadOnlyCollection<string> Keys => _values.Keys;

    public string? this[string key]
    {
        get => _values.GetValueOrDefault(key);
        set
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    public bool TryGetRaw(string key, [NotNullWhen(true)] out string? value)
        => _values.TryGetValue(key, out value);

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
    }

    /// <summary>Layers another set on top of this one, returning a new set. Later wins.</summary>
    public TokenSet OverlaidWith(TokenSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var merged = new TokenSet(_values);
        foreach (var (key, value) in other._values) merged._values[key] = value;
        return merged;
    }

    public IEnumerable<string> KeysInLayer(TokenLayer layer)
        => _values.Keys.Where(k => TokenLayerExtensions.InferLayer(k) == layer);

    /// <summary>
    /// Expands every <c>{reference}</c> until values are literal. Throws on a cycle rather
    /// than looping — a theme with a reference cycle is a bug the author needs told about.
    /// </summary>
    public ResolvedTokens Resolve()
    {
        var resolved = new Dictionary<string, string>(_values.Count, StringComparer.OrdinalIgnoreCase);
        var resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _values.Keys)
        {
            resolved[key] = ResolveOne(key, resolving, resolved);
        }

        return new ResolvedTokens(resolved);
    }

    private string ResolveOne(
        string key,
        HashSet<string> resolving,
        Dictionary<string, string> memo)
    {
        if (memo.TryGetValue(key, out var already)) return already;

        if (!_values.TryGetValue(key, out var raw))
        {
            throw new ThemeResolutionException($"Token '{key}' is referenced but never defined.");
        }

        if (!resolving.Add(key))
        {
            throw new ThemeResolutionException(
                $"Token '{key}' takes part in a reference cycle: " +
                string.Join(" -> ", resolving) + $" -> {key}");
        }

        var expanded = ReferencePattern().Replace(raw, match =>
        {
            var target = match.Groups["name"].Value;
            return ResolveOne(target, resolving, memo);
        });

        resolving.Remove(key);
        memo[key] = expanded;
        return expanded;
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9_.\-]+)\}", RegexOptions.Compiled)]
    private static partial Regex ReferencePattern();
}

public sealed class ThemeResolutionException(string message) : Exception(message);
