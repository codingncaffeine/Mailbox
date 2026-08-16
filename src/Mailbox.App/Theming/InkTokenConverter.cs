using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Mailbox.App.Theming;

/// <summary>
/// A token name to the brush for a row's ink — and, for no token, nothing at all, so the
/// property falls back to whatever the styles say. What lets a conditional-formatting colour
/// sit over the unread style without replacing it when there is none.
/// </summary>
public sealed class InkTokenConverter : IValueConverter
{
    public static readonly InkTokenConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string token || token.Length == 0) return AvaloniaProperty.UnsetValue;

        var application = Application.Current;
        if (application is null) return AvaloniaProperty.UnsetValue;

        return application.Resources.TryGetResource(token + ".brush", application.ActualThemeVariant, out var brush) && brush is IBrush
            ? brush
            : AvaloniaProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("A brush cannot be turned back into a token name.");
}
