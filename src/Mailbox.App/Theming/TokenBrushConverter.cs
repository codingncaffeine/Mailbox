using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Mailbox.App.Theming;

/// <summary>
/// Resolves a token name to the brush the current theme gives it.
/// </summary>
/// <remarks>
/// For the places where the token is data rather than markup — a category's colour is stored
/// per category, so the row cannot name it in XAML. Everything else should use
/// <c>{DynamicResource}</c>; this exists for values that arrive from the store.
/// <para>
/// It resolves once per binding rather than tracking the theme, so a live theme change needs
/// the rows rebuilt. That is what happens anyway, and the alternative is a subscription per
/// swatch.
/// </para>
/// </remarks>
public sealed class TokenBrushConverter : IValueConverter
{
    public static readonly TokenBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string token || token.Length == 0) return Brushes.Transparent;

        var application = Application.Current;
        if (application is null) return Brushes.Transparent;

        return application.Resources.TryGetResource(
            token + ".brush", application.ActualThemeVariant, out var brush)
            ? brush
            : Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("A brush cannot be turned back into a token name.");
}
