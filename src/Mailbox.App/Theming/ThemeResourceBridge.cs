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
/// get a <see cref="double"/>. Publishing all forms up front means XAML never needs a converter,
/// and a theme swap is one dictionary update rather than a rebind of the visual tree.
/// <para>
/// This is the single seam between theme data and the UI. Nothing else in the application is
/// permitted to name a colour — the coverage audit enforces that.
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

    private void Publish(ResolvedTokens tokens)
    {
        foreach (var (key, raw) in tokens.AsPairs())
        {
            _target[key] = raw;

            if (Color.TryParse(raw, out var color))
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
            }
        }

        // Convenience aliases so common XAML reads naturally.
        if (tokens.TryGetString(TokenKeys.Typography.UiFamily, out var uiFamily))
        {
            _target["ui.fontfamily"] = new FontFamily(uiFamily);
        }

        if (tokens.TryGetString(TokenKeys.Typography.ContentFamily, out var contentFamily))
        {
            _target["content.fontfamily"] = new FontFamily(contentFamily);
        }

        if (tokens.TryGetString(TokenKeys.Typography.MonoFamily, out var monoFamily))
        {
            _target["mono.fontfamily"] = new FontFamily(monoFamily);
        }
    }
}
