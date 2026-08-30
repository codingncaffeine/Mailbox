using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mailbox.App.Options;

/// <summary>
/// The coloured pictograms the reference stands to the left of an Options group — the green
/// abc-check, the blue pane, the bell on the envelope — drawn, because they are two-colour
/// artwork the icon font cannot carry.
/// </summary>
/// <remarks>
/// The artwork's colours come in as brushes bound from the <c>pictogram.*</c> tokens — the
/// reference's document blue, spelling green and alert amber, brightened by the Black theme —
/// and the neutral strokes take the dialog's own ink, so the pictogram reads on a dark dialog
/// without its identity changing shade.
/// </remarks>
internal sealed class OptionsPictogram : Control
{
    public static readonly StyledProperty<string> GlyphProperty =
        AvaloniaProperty.Register<OptionsPictogram, string>(nameof(Glyph), string.Empty);

    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<OptionsPictogram, IBrush?>(nameof(Ink));

    public static readonly StyledProperty<IBrush?> BlueProperty =
        AvaloniaProperty.Register<OptionsPictogram, IBrush?>(nameof(BlueInk));

    public static readonly StyledProperty<IBrush?> GreenProperty =
        AvaloniaProperty.Register<OptionsPictogram, IBrush?>(nameof(GreenInk));

    public static readonly StyledProperty<IBrush?> AmberProperty =
        AvaloniaProperty.Register<OptionsPictogram, IBrush?>(nameof(AmberInk));

    static OptionsPictogram()
    {
        AffectsRender<OptionsPictogram>(GlyphProperty, InkProperty, BlueProperty, GreenProperty, AmberProperty);
    }

    public IBrush? BlueInk { get => GetValue(BlueProperty); set => SetValue(BlueProperty, value); }
    public IBrush? GreenInk { get => GetValue(GreenProperty); set => SetValue(GreenProperty, value); }
    public IBrush? AmberInk { get => GetValue(AmberProperty); set => SetValue(AmberProperty, value); }

    private IBrush Blue => BlueInk ?? Brushes.Magenta;
    private IBrush Green => GreenInk ?? Brushes.Magenta;
    private IBrush Amber => AmberInk ?? Brushes.Magenta;

    public OptionsPictogram()
    {
        Width = 26;
        Height = 26;
    }

    public string Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>The neutral stroke — the page's outline, the letters — from the dialog's own ink.</summary>
    public IBrush? Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    /// <summary>Whether this kit draws the name at all, so a caller can fall back to the font.</summary>
    public static bool Draws(string glyph)
        => glyph is "source" or "reader" or "categorize" or "reading-pane" or "reminder" or "layout" or "archive";

    public override void Render(DrawingContext context)
    {
        if (Ink is not { } ink) return;
        var pen = new Pen(ink, 1.2);

        switch (Glyph)
        {
            case "source":
                DrawPage(context, pen);
                DrawPencil(context);
                break;

            case "reader":
                DrawAbcCheck(context, ink);
                break;

            case "categorize":
                DrawStationery(context, ink);
                break;

            case "reading-pane":
                DrawPane(context);
                break;

            case "reminder":
                DrawEnvelope(context, pen);
                DrawBell(context);
                break;

            case "layout":
                DrawPage(context, pen);
                context.FillRectangle(Blue, new Rect(6.5, 6.5, 4, 13));
                break;

            case "archive":
                DrawArchive(context, pen);
                break;
        }
    }

    /// <summary>A page with a turned corner, in the neutral stroke.</summary>
    private static void DrawPage(DrawingContext context, Pen pen)
    {
        var page = new StreamGeometry();
        using (var draw = page.Open())
        {
            draw.BeginFigure(new Point(5.5, 3.5), isFilled: false);
            draw.LineTo(new Point(13.5, 3.5));
            draw.LineTo(new Point(17.5, 7.5));
            draw.LineTo(new Point(17.5, 20.5));
            draw.LineTo(new Point(5.5, 20.5));
            draw.EndFigure(true);
        }

        context.DrawGeometry(null, pen, page);
        context.DrawLine(pen, new Point(13.5, 3.5), new Point(13.5, 7.5));
        context.DrawLine(pen, new Point(13.5, 7.5), new Point(17.5, 7.5));
    }

    /// <summary>The blue pencil across the page's corner.</summary>
    private void DrawPencil(DrawingContext context)
    {
        var blue = Blue;
        var body = new StreamGeometry();
        using (var draw = body.Open())
        {
            draw.BeginFigure(new Point(14, 12), isFilled: true);
            draw.LineTo(new Point(22, 20));
            draw.LineTo(new Point(20, 22));
            draw.LineTo(new Point(12, 14));
            draw.EndFigure(true);
        }

        context.DrawGeometry(blue, null, body);

        // The tip.
        var tip = new StreamGeometry();
        using (var draw = tip.Open())
        {
            draw.BeginFigure(new Point(12, 14), isFilled: true);
            draw.LineTo(new Point(14, 12));
            draw.LineTo(new Point(10.6, 10.6));
            draw.EndFigure(true);
        }

        context.DrawGeometry(blue, null, tip);
    }

    /// <summary>"abc" over the green check, as the spelling group's pictogram is.</summary>
    private void DrawAbcCheck(DrawingContext context, IBrush ink)
    {
        var text = new FormattedText(
            "abc", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 11, ink);
        context.DrawText(text, new Point(4, 2));

        var pen = new Pen(Green, 2.4, lineCap: PenLineCap.Round);
        context.DrawLine(pen, new Point(6, 18), new Point(10, 22));
        context.DrawLine(pen, new Point(10, 22), new Point(20, 12));
    }

    /// <summary>The stationery pair: a large letter in the ink, a small one in the blue.</summary>
    private void DrawStationery(DrawingContext context, IBrush ink)
    {
        var big = new FormattedText(
            "A", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 17, ink);
        context.DrawText(big, new Point(4, 3));

        var small = new FormattedText(
            "a", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 12, Blue);
        context.DrawText(small, new Point(15, 6));
    }

    /// <summary>The blue reading pane: the window with its left band filled.</summary>
    private void DrawPane(DrawingContext context)
    {
        var blue = Blue;
        var pen = new Pen(blue, 1.4);
        context.DrawRectangle(null, pen, new Rect(4.5, 5.5, 17, 14));
        context.FillRectangle(blue, new Rect(5.5, 6.5, 5, 12));
        context.FillRectangle(blue, new Rect(12, 8.5, 8.5, 1.4));
        context.FillRectangle(blue, new Rect(12, 12, 8.5, 1.4));
        context.FillRectangle(blue, new Rect(12, 15.5, 6, 1.4));
    }

    /// <summary>The envelope in the neutral stroke.</summary>
    private static void DrawEnvelope(DrawingContext context, Pen pen)
    {
        context.DrawRectangle(null, pen, new Rect(3.5, 6.5, 15, 11));
        context.DrawLine(pen, new Point(3.5, 6.5), new Point(11, 13));
        context.DrawLine(pen, new Point(11, 13), new Point(18.5, 6.5));
    }

    /// <summary>The amber bell over its corner.</summary>
    private void DrawBell(DrawingContext context)
    {
        var amber = Amber;
        var bell = new StreamGeometry();
        using (var draw = bell.Open())
        {
            draw.BeginFigure(new Point(15, 20), isFilled: true);
            draw.LineTo(new Point(16, 16));
            draw.ArcTo(new Point(21, 14.6), new Size(3.4, 3.6), 0, false, SweepDirection.Clockwise);
            draw.LineTo(new Point(23, 18));
            draw.EndFigure(true);
        }

        context.DrawGeometry(amber, null, bell);
        context.DrawEllipse(amber, null, new Point(19, 21.4), 1.3, 1.3);
    }

    /// <summary>The archive box: the lid in the blue, the box in the neutral stroke.</summary>
    private void DrawArchive(DrawingContext context, Pen pen)
    {
        context.FillRectangle(Blue, new Rect(4, 5, 18, 4));
        context.DrawRectangle(null, pen, new Rect(5.5, 10.5, 15, 10));
        context.DrawLine(pen, new Point(9.5, 13.5), new Point(16.5, 13.5));
    }
}
