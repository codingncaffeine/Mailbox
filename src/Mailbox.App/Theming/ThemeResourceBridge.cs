using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Mailbox.Theming;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Theming;

/// <summary>
/// Publishes the active <see cref="ResolvedTokens"/> into Avalonia's resource dictionary so
/// XAML can bind them with <c>{DynamicResource surface.ground}</c>.
/// </summary>
/// <remarks>
/// Every token becomes three entries: the raw string, a <see cref="Color"/> when it parses as
/// one, and a matching <see cref="IBrush"/> under the suffix <c>.brush</c>. Numeric tokens also
/// get a <see cref="double"/>, and the elevation family a <see cref="BoxShadows"/>. Publishing
/// all forms up front means XAML never needs a converter, and a theme swap is one dictionary
/// update rather than a rebind of the visual tree.
/// <para>
/// This is the single seam between theme data and the UI. Nothing else in the application is
/// permitted to name a colour — <c>AuditPaintSweepTests</c> sweeps <c>src/</c> and fails if
/// anything does. That claim stood here for a long time with nothing behind it.
/// </para>
/// </remarks>
public sealed class ThemeResourceBridge
{
    private readonly IResourceDictionary _target;
    private readonly ThemeService _themes;

    public ThemeResourceBridge(IResourceDictionary target, ThemeService themes)
    {
        _target = target;
        _themes = themes;
        _themes.Changed += (_, e) => Publish(e.Tokens);
        Publish(_themes.Tokens);
    }

    /// <summary>Suffix under which a colour token's brush is published.</summary>
    public const string BrushSuffix = ".brush";

    /// <summary>Suffix under which an elevation token's <see cref="BoxShadows"/> is published.</summary>
    public const string ShadowSuffix = ".boxshadow";

    /// <summary>The one family published as a shadow rather than as a colour or a number.</summary>
    private const string ElevationPrefix = "elevation.";

    /// <summary>
    /// A shadow token, or a theme error naming the token — never a silently dropped shadow,
    /// which would leave the ribbon flat with nothing to say why.
    /// </summary>
    private static BoxShadows ParseShadow(string key, string raw)
    {
        try
        {
            return BoxShadows.Parse(raw);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            throw new ThemeResolutionException(
                $"Token '{key}' is '{raw}', which is not a shadow: {e.Message}");
        }
    }

    private void Publish(ResolvedTokens tokens)
    {
        foreach (var (key, raw) in tokens.AsPairs())
        {
            _target[key] = raw;

            // A drop shadow is neither a colour nor a number: Border.BoxShadow takes a
            // BoxShadows, so the elevation family is published in that form. Only that family
            // is offered to the parser — it throws rather than failing quietly, and every other
            // token would reach it.
            if (key.StartsWith(ElevationPrefix, StringComparison.Ordinal))
            {
                _target[key + ShadowSuffix] = ParseShadow(key, raw);
            }
            else if (Color.TryParse(raw, out var color))
            {
                _target[key + ".color"] = color;
                _target[key + BrushSuffix] = new ImmutableSolidColorBrush(color);
            }
            else if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                _target[key + ".value"] = number;
                _target[key + ".thickness"] = new Thickness(number);
                _target[key + ".gridlength"] = new GridLength(number);

                // A one-sided margin. Offsets measured from a single edge are common in the
                // chrome, and the uniform .thickness above would inset all four sides.
                _target[key + ".leftmargin"] = new Thickness(number, 0, 0, 0);
                _target[key + ".rightmargin"] = new Thickness(0, 0, number, 0);
            }
        }

        // Convenience aliases so common XAML reads naturally.
        // Through BundledFonts.FamilyFor, because a family Mailbox bundles is found only through
        // its collection: asked for by its bare name, Selawik was never drawn.
        if (tokens.TryGetString(TokenKeys.Typography.UiFamily, out var uiFamily))
        {
            _target["ui.fontfamily"] = Mailbox.Theming.Fonts.BundledFonts.FamilyFor(uiFamily);
        }

        if (tokens.TryGetString(TokenKeys.Typography.ContentFamily, out var contentFamily))
        {
            _target["content.fontfamily"] = Mailbox.Theming.Fonts.BundledFonts.FamilyFor(contentFamily);
        }

        if (tokens.TryGetString(TokenKeys.Typography.MonoFamily, out var monoFamily))
        {
            _target["mono.fontfamily"] = Mailbox.Theming.Fonts.BundledFonts.FamilyFor(monoFamily);
        }

        PublishControlThemePalette(tokens);
    }

    /// <summary>
    /// Publishes the control theme's own palette from our tokens. The mapping itself lives in
    /// <see cref="ControlThemePalette"/>, where it can be tested without a UI thread.
    /// </summary>
    private void PublishControlThemePalette(ResolvedTokens tokens)
    {
        foreach (var (key, value) in ControlThemePalette.Resolve(tokens))
        {
            if (!Color.TryParse(value, out var color)) continue;

            _target[key] = new ImmutableSolidColorBrush(color);
            _target[key.Replace("Brush", "Color")] = color;
        }
    }
}
