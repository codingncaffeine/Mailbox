using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Notifications;

/// <summary>
/// The tray icon with the unread count drawn on it — §10's "notification area icon with unread
/// badge".
/// </summary>
/// <remarks>
/// Drawn rather than pre-rendered, because the count changes and the colours are the theme's:
/// the disc is the danger status token and the digits the on-accent ink, resolved from the
/// running theme, so a hard-coded red never reaches the tray. Rendered at the icon's own pixel
/// size; a tray scales it however it likes and the badge scales with it.
/// </remarks>
internal static class TrayBadge
{
    /// <summary>The plain icon, or the icon wearing the count when there is one.</summary>
    public static WindowIcon For(Bitmap icon, int unread)
    {
        ArgumentNullException.ThrowIfNull(icon);
        return unread <= 0 ? new WindowIcon(icon) : new WindowIcon(Render(icon, unread));
    }

    /// <summary>The icon with the count drawn on it, as a bitmap of the icon's own size.</summary>
    public static RenderTargetBitmap Render(Bitmap icon, int unread)
    {
        ArgumentNullException.ThrowIfNull(icon);

        var size = icon.PixelSize;
        var target = new RenderTargetBitmap(size, new Vector(96, 96));

        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(icon, new Rect(0, 0, size.Width, size.Height));
            if (unread <= 0) return target;

            // Grey antialiasing for the digits: subpixel rendering — the application's default,
            // and right on a screen — puts colour fringes on glyphs, and on a bitmap that a tray
            // scales and composites over an unknown ground the fringes read as dirt.
            using var _ = context.PushTextOptions(new TextOptions
            {
                TextRenderingMode = TextRenderingMode.Antialias,
            });

            var label = unread > 99 ? "99+" : unread.ToString(CultureInfo.InvariantCulture);
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(App.Fonts.Resolve("Segoe UI").Rendered, weight: FontWeight.SemiBold),
                label.Length > 2 ? size.Height * 0.30 : size.Height * 0.38,
                Brush(TokenKeys.Text.OnAccent));

            // A disc for one or two digits, a pill for more, sat in the bottom-right corner
            // where every desktop's badges go.
            var height = Math.Round(size.Height * 0.55);
            var width = Math.Max(height, Math.Round(text.Width + height * 0.45));
            var badge = new Rect(size.Width - width, size.Height - height, width, height);

            context.DrawRectangle(Brush(TokenKeys.Status.Danger), null, badge, height / 2, height / 2);
            context.DrawText(text, new Point(
                badge.X + (badge.Width - text.Width) / 2,
                badge.Y + (badge.Height - text.Height) / 2));
        }

        return target;
    }

    /// <summary>The current theme's brush for a token, resolved from the application's resources.</summary>
    private static IBrush Brush(string token)
    {
        var application = Application.Current;
        return application is not null
               && application.Resources.TryGetResource(token + ".brush", application.ActualThemeVariant, out var found)
               && found is IBrush brush
            ? brush
            : Brushes.Transparent;
    }
}
