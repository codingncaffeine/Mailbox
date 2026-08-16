using Mailbox.Theming.Fonts;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Theming;

/// <summary>Spacing density. Orthogonal to colour, so it composes with any theme.</summary>
public enum Density
{
    Compact,
    Cozy,
    Comfortable,
}

public sealed class ThemeChangedEventArgs(ResolvedTokens tokens, string themeId, Density density)
    : EventArgs
{
    public ResolvedTokens Tokens { get; } = tokens;
    public string ThemeId { get; } = themeId;
    public Density Density { get; } = density;
}

/// <summary>
/// Owns the active theme and hands the UI a resolved token set. A theme swap is an atomic
/// replacement — one event, one relayout — rather than a storm of per-property notifications.
/// </summary>
public sealed class ThemeService
{
    private readonly FontResolver _fonts;

    /// <summary>Overrides the startup theme. Used by the fidelity harness to capture all four.</summary>
    public const string ThemeVariable = "MAILBOX_THEME";

    /// <summary>Overrides the startup density: compact, cozy or comfortable.</summary>
    public const string DensityVariable = "MAILBOX_DENSITY";

    public ThemeService(FontResolver fonts, Mailbox.Theming.Files.ThemeLibrary? library = null)
    {
        _fonts = fonts;
        Library = library ?? Mailbox.Theming.Files.ThemeLibrary.BuiltIns;
        ThemeId = ResolveStartupTheme(Library);
        Density = ResolveStartupDensity();
        Tokens = Compose(ThemeId, Density, null);
    }

    /// <summary>The themes that can be applied: the built-ins and the reader's theme files.</summary>
    public Mailbox.Theming.Files.ThemeLibrary Library { get; private set; }

    /// <summary>
    /// Replaces the library — the themes directory has changed — and re-applies the current
    /// theme from it when the theme is one of the files, so an edit shows without a restart. A
    /// current theme the new library no longer has falls back to Colorful.
    /// </summary>
    public void ReplaceLibrary(Mailbox.Theming.Files.ThemeLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        Library = library;
        if (Mailbox.Theming.Files.ThemeLibrary.IsBuiltIn(ThemeId)) return;

        try
        {
            Apply(library.Contains(ThemeId) ? ThemeId : OfficeThemes.Colorful);
        }
        catch (ThemeResolutionException ex)
        {
            Mailbox.Core.Diagnostics.Log.Warn($"Theme \"{ThemeId}\" could not be re-applied after its file changed: {ex.Message}");
            Apply(OfficeThemes.Colorful);
        }
    }

    private static string ResolveStartupTheme(Mailbox.Theming.Files.ThemeLibrary library)
    {
        var requested = Environment.GetEnvironmentVariable(ThemeVariable);
        if (string.IsNullOrWhiteSpace(requested)) return OfficeThemes.Colorful;

        return library.Canonical(requested.Trim()) ?? OfficeThemes.Colorful;
    }

    private static Density ResolveStartupDensity()
        => Environment.GetEnvironmentVariable(DensityVariable)?.ToLowerInvariant() switch
        {
            "compact" => Density.Compact,
            "comfortable" => Density.Comfortable,
            _ => Density.Cozy,
        };

    public string ThemeId { get; private set; }

    public Density Density { get; private set; } = Density.Cozy;

    public ResolvedTokens Tokens { get; private set; }

    /// <summary>User overrides layered on top of the built-in. Null for an unmodified theme.</summary>
    public TokenSet? UserOverrides { get; private set; }

    public bool IsDark => Library.IsDark(ThemeId);

    /// <summary>What the theme picker shows for a theme, built-in or file.</summary>
    public string DisplayName(string id) => Library.DisplayName(id);

    public event EventHandler<ThemeChangedEventArgs>? Changed;

    public void Apply(string themeId, Density? density = null, TokenSet? overrides = null)
    {
        ThemeId = themeId;
        Density = density ?? Density;
        UserOverrides = overrides ?? UserOverrides;
        Tokens = Compose(ThemeId, Density, UserOverrides);
        Changed?.Invoke(this, new ThemeChangedEventArgs(Tokens, ThemeId, Density));
    }

    public void SetDensity(Density density) => Apply(ThemeId, density);

    public void ClearOverrides()
    {
        UserOverrides = null;
        Apply(ThemeId, Density);
    }

    private ResolvedTokens Compose(string themeId, Density density, TokenSet? overrides)
    {
        var tokens = Library.Build(themeId);
        ApplyDensity(tokens, density);
        ApplyFontResolution(tokens);

        if (overrides is not null) tokens = tokens.OverlaidWith(overrides);

        var resolved = tokens.Resolve();
        AssertCoverage(resolved, themeId);
        return resolved;
    }

    /// <summary>
    /// Density touches spacing and row heights only, never colour. Values are multiples of the
    /// cozy baseline measured from reference captures.
    /// </summary>
    private static void ApplyDensity(TokenSet tokens, Density density)
    {
        if (density == Density.Cozy) return;

        var (row, compact, group, nav) = density switch
        {
            Density.Compact => ("36", "18", "22", "208"),
            Density.Comfortable => ("54", "26", "30", "256"),
            _ => ("44", "22", "26", "232"),
        };

        tokens.Set(TokenKeys.List.RowHeight, row);
        tokens.Set(TokenKeys.List.RowHeightCompact, compact);
        tokens.Set(TokenKeys.List.GroupHeaderHeight, group);
        tokens.Set(TokenKeys.Nav.Width, nav);
    }

    /// <summary>
    /// Rewrites logical family tokens to families this machine can actually draw. The theme
    /// says "Segoe UI"; on a box without it, the UI renders Selawik at identical metrics.
    /// </summary>
    private void ApplyFontResolution(TokenSet tokens)
    {
        foreach (var key in (string[])
                 [TokenKeys.Typography.UiFamily,
                  TokenKeys.Typography.ContentFamily,
                  TokenKeys.Typography.MonoFamily])
        {
            if (tokens.TryGetRaw(key, out var requested))
            {
                tokens.Set(key, _fonts.Resolve(requested).Rendered);
            }
        }
    }

    /// <summary>
    /// The coverage gate. A theme missing a required token is rejected at load rather than
    /// producing an unstyled surface somewhere deep in the application — the exact failure
    /// mode that leaves Thunderbird's compose window unthemeable.
    /// </summary>
    private static void AssertCoverage(ResolvedTokens tokens, string themeId)
    {
        var missing = TokenKeys.Required.Where(k => !tokens.Contains(k)).ToList();
        if (missing.Count == 0) return;

        throw new ThemeResolutionException(
            $"Theme '{themeId}' does not define {missing.Count} required token(s): " +
            string.Join(", ", missing.Take(10)) +
            (missing.Count > 10 ? $", and {missing.Count - 10} more." : "."));
    }
}
