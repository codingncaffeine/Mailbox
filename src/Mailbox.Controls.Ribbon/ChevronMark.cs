using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mailbox.Controls.Ribbon;

/// <summary>
/// The Ribbon Display Options chevron, drawn to the reference's pixels: a "V" nine wide and five
/// tall whose two arms are crisp 45° strokes about a pixel wide, as dark as the labels.
/// </summary>
/// <remarks>
/// Drawn rather than set in the icon font. The font's chevron is this size at 14px, but its
/// strokes fall between pixels and render as a soft grey, and at 16px it is a size larger with
/// two-pixel strokes. A polyline through the centres of the pixels it inks, with a 1.1px
/// square-capped stroke, reproduces the reference's pixels — the same reason the zoom slider is
/// drawn.
/// <para>
/// It snaps against the window, not against itself: the ribbon panel sits at a fractional
/// offset that differs between the two layouts, and a stroke that is pixel-aligned within this
/// control's own bounds still lands on half-pixels on screen. Translating the origin to the
/// top-level and correcting by the fractional part is what puts each arm through pixel centres —
/// the same correction the zoom slider makes for its hairline.
/// </para>
/// </remarks>
public sealed class ChevronMark : Control
{
    /// <summary>The ink box: the arms span these pixels.</summary>
    public const double InkWidth = 9;
    public const double InkHeight = 5;

    private const double StrokeWidth = 1.1;

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<ChevronMark, IBrush?>(nameof(Stroke));

    static ChevronMark()
    {
        AffectsRender<ChevronMark>(StrokeProperty);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(InkWidth, InkHeight);

    public override void Render(DrawingContext context)
    {
        if (Stroke is not { } brush) return;

        var origin = TopLevel.GetTopLevel(this) is { } root
            ? this.TranslatePoint(default, root) ?? default
            : default;
        var dx = Math.Round(origin.X) - origin.X;
        var dy = Math.Round(origin.Y) - origin.Y;

        // Through the centres of the pixels each arm inks: one column per row, nine columns and
        // five rows, meeting at the bottom centre.
        var left = new Point(0.5 + dx, 0.5 + dy);
        var apex = new Point(4.5 + dx, 4.5 + dy);
        var right = new Point(8.5 + dx, 0.5 + dy);

        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Square, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(left, isFilled: false);
            ctx.LineTo(apex);
            ctx.LineTo(right);
            ctx.EndFigure(isClosed: false);
        }

        // The square caps fill the tip pixels, as the reference's are filled, but poke half a
        // pixel past them; the reference's arms end flat at the ink box's top and bottom, so
        // the box — snapped like the strokes — is the clip, a pixel wider each side to keep the
        // tips' outer shoulders, which the reference has. The round join at the apex spills
        // the same way and is cut the same way.
        using (context.PushClip(new Rect(dx - 1, dy, InkWidth + 2, InkHeight)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
