using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>
/// A miniature Mailbox shell drawn from a resolved token set: caption band with its backdrop,
/// tab strip, ribbon, rail, folder pane, message rows, reading pane, status bar. The theme
/// browser's preview paints with this — the tokens it draws are the ones an install would
/// produce, because the same mapper made them, so "what it would look like" is a fact rather
/// than an artist's impression.
/// </summary>
/// <remarks>
/// Every colour comes from the token set it is handed; the geometry is this control's own.
/// Reused wherever a theme needs showing before it is applied.
/// </remarks>
internal sealed class ThemePreview : Control
{
    private ResolvedTokens? _tokens;
    private Bitmap? _backdrop;

    /// <summary>Shows a theme; the backdrop is the caption image when the theme brings one.</summary>
    public void Show(ResolvedTokens tokens, Bitmap? backdrop)
    {
        _tokens = tokens;
        _backdrop?.Dispose();
        _backdrop = backdrop;
        InvalidateVisual();
    }

    public void Clear()
    {
        _tokens = null;
        _backdrop?.Dispose();
        _backdrop = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_tokens is null || Bounds.Width < 40 || Bounds.Height < 30) return;
        var t = _tokens;

        // The drawing is laid out in 320×200 units and scaled to fit, letterboxed.
        var scale = Math.Min(Bounds.Width / 320, Bounds.Height / 200);
        var origin = new Point((Bounds.Width - (320 * scale)) / 2, (Bounds.Height - (200 * scale)) / 2);
        Rect R(double x, double y, double w, double h)
            => new(origin.X + (x * scale), origin.Y + (y * scale), w * scale, h * scale);

        IBrush B(string key) => new SolidColorBrush(t.GetColor(key));

        void Dash(string ink, double x, double y, double w, double opacity = 0.7)
        {
            using var o = context.PushOpacity(opacity);
            context.FillRectangle(B(ink), R(x, y, w, 3), (float)(1.5 * scale));
        }

        var window = R(0, 0, 320, 200);
        context.FillRectangle(B(TokenKeys.Ribbon.TabStripBackground), window, (float)(6 * scale));
        using (context.PushClip(new RoundedRect(window, 6 * scale)))
        {
            // The caption band, its backdrop, the search pill and the caption glyph dots.
            context.FillRectangle(B(TokenKeys.TitleBar.Background), R(0, 0, 320, 22));
            if (_backdrop is not null)
            {
                // The same arithmetic the real band uses — cover, panned by the theme's own
                // alignment — so the preview's crop is the installed crop, miniaturised.
                var band = R(0, 0, 320, 22);
                var alignment = CaptionBackdrop.ParseAlignment(
                    t.TryGetString(TokenKeys.TitleBar.BackdropAlignment, out var stated) ? stated : "right center");
                var cover = Math.Max(band.Width / _backdrop.PixelSize.Width, band.Height / _backdrop.PixelSize.Height);
                var w = _backdrop.PixelSize.Width * cover;
                var h = _backdrop.PixelSize.Height * cover;
                using var bandClip = context.PushClip(band);
                context.DrawImage(_backdrop, new Rect(_backdrop.Size),
                    new Rect(band.Left + ((band.Width - w) * alignment.X), band.Top + ((band.Height - h) * alignment.Y), w, h));
            }

            context.FillRectangle(B(TokenKeys.TitleBar.Search), R(110, 5, 100, 12), (float)(6 * scale));
            for (var i = 0; i < 3; i++) Dash(TokenKeys.TitleBar.Foreground, 282 + (i * 11), 9, 6);

            // Tab strip with its words and the underline that says which is open.
            for (var i = 0; i < 4; i++) Dash(TokenKeys.Ribbon.TabText, 10 + (i * 26), 28, 16);
            context.FillRectangle(B(TokenKeys.Ribbon.TabUnderline), R(36, 33, 16, 2));

            // The ribbon panel and a hint of its controls.
            context.FillRectangle(B(TokenKeys.Ribbon.Background), R(6, 38, 308, 26), (float)(3 * scale));
            for (var i = 0; i < 6; i++) Dash(TokenKeys.Ribbon.GroupLabel, 14 + (i * 34), 49, 20, 0.5);

            // Rail, folder pane, the list's rows, the reading pane.
            context.FillRectangle(B(TokenKeys.Rail.Background), R(0, 68, 16, 118));
            for (var i = 0; i < 4; i++) Dash(TokenKeys.Rail.ItemText, 5, 76 + (i * 16), 7, 0.6);

            context.FillRectangle(B(TokenKeys.Nav.Background), R(16, 68, 56, 118));
            for (var i = 0; i < 5; i++) Dash(TokenKeys.Nav.ItemText, 22, 78 + (i * 14), 40, 0.6);

            context.FillRectangle(B(TokenKeys.List.Background), R(72, 68, 100, 118));
            for (var i = 0; i < 4; i++)
            {
                var row = R(76, 74 + (i * 27), 92, 23);
                context.FillRectangle(B(TokenKeys.List.RowBackground), row, (float)(2 * scale));
                if (i < 2) context.FillRectangle(B(TokenKeys.List.UnreadBar), R(76, 74 + (i * 27), 2, 23));
                Dash(i < 2 ? TokenKeys.List.UnreadText : TokenKeys.List.ReadText, 82, 79 + (i * 27), 60);
                Dash(TokenKeys.List.PreviewText, 82, 87 + (i * 27), 75, 0.45);
            }

            context.FillRectangle(B(TokenKeys.Reading.Background), R(172, 68, 148, 118));
            Dash(TokenKeys.Text.Primary, 180, 78, 90);
            for (var i = 0; i < 5; i++) Dash(TokenKeys.Text.Secondary, 180, 92 + (i * 12), 128, 0.5);

            // The status bar closes the window.
            context.FillRectangle(B(TokenKeys.StatusBar.Background), R(0, 186, 320, 14));
            Dash(TokenKeys.StatusBar.Foreground, 8, 191, 40, 0.7);
        }

        context.DrawRectangle(null, new Pen(B(TokenKeys.WindowShape.Border), 1),
            new RoundedRect(window, 6 * scale));
    }
}
