namespace Mailbox.Theming.Tokens;

/// <summary>
/// Maps the control theme's own palette onto ours.
/// </summary>
/// <remarks>
/// The control theme supplies templates, and those templates reach for <em>its</em> palette
/// rather than the token set. Anywhere a template paints a surface that has not been explicitly
/// restyled, its scheme shows through: menu flyouts arrived on a dark presenter in the light
/// themes, with our dark text on top, readable only where a hover lit the row behind it.
/// <para>
/// Repointing the palette at the root fixes every such surface at once, and goes on fixing them
/// for controls nobody has styled yet. Chasing each presenter as it is noticed is the same bug
/// discovered repeatedly.
/// </para>
/// </remarks>
public static class ControlThemePalette
{
    /// <summary>Control-theme brush key paired with the token that should drive it.</summary>
    public static readonly IReadOnlyList<(string Key, string Token)> Map =
    [
        ("ThemeBackgroundBrush", TokenKeys.Surface.Ground),
        ("ThemeForegroundBrush", TokenKeys.Text.Primary),
        ("ThemeForegroundLowBrush", TokenKeys.Text.Secondary),
        ("ThemeBorderLowBrush", TokenKeys.Border.Subtle),
        ("ThemeBorderMidBrush", TokenKeys.Border.Subtle),
        ("ThemeBorderHighBrush", TokenKeys.Border.Strong),
        ("ThemeControlLowBrush", TokenKeys.Surface.Sunken),
        ("ThemeControlMidBrush", TokenKeys.Surface.Raised),
        ("ThemeControlMidHighBrush", TokenKeys.State.Hover),
        ("ThemeControlHighBrush", TokenKeys.State.Pressed),
        ("ThemeAccentBrush", TokenKeys.Accent.Rest),
        ("ThemeAccentBrush2", TokenKeys.Accent.Hover),
        ("HighlightBrush", TokenKeys.Accent.Rest),
        ("HighlightForegroundBrush", TokenKeys.Text.OnAccent),
        ("ErrorBrush", TokenKeys.Status.Danger),
        ("ErrorLowBrush", TokenKeys.Status.Danger),
    ];

    /// <summary>Resolves the map against a theme, skipping anything it does not define.</summary>
    public static IEnumerable<(string Key, string Value)> Resolve(ResolvedTokens tokens)
    {
        foreach (var (key, token) in Map)
        {
            if (tokens.TryGetString(token, out var value)) yield return (key, value);
        }
    }
}
