using Avalonia;
using Avalonia.Media;
using Mailbox.Controls.Common;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// What every calendar view shares on top of a drawn surface: the resolved calendar palette and
/// the chip drawing the reference's views are made of.
/// </summary>
public abstract class CalendarSurface : DrawnSurface
{
    private CalendarPalette? _palette;

    /// <summary>The calendar tokens, resolved once and kept until the theme moves.</summary>
    protected CalendarPalette Palette => _palette ??= CalendarPalette.From(this);

    /// <summary>
    /// A theme change rewrites the resource dictionary rather than the tree, so the cached
    /// palette is what has to be thrown away for the view to follow it.
    /// </summary>
    protected override void OnPaletteChanged() => _palette = null;

    /// <summary>
    /// An appointment dragged somewhere else, or an edge of one dragged to a new length.
    /// </summary>
    /// <remarks>
    /// Declared here rather than on each view because a drag means the same thing in all of them
    /// — these are the times the appointment should now keep — and the workspace wires one
    /// handler per view either way.
    /// </remarks>
    public event EventHandler<EntryMove>? EntryMoved;

    protected void RaiseMoved(EntryMove move) => EntryMoved?.Invoke(this, move);

    // ---- Chips -----------------------------------------------------------------------------

    /// <summary>The bar down a chip's left edge, including its own outline: 7px, measured.</summary>
    public const double ChipBarWidth = 7;

    /// <summary>Where a chip's text starts, from the chip's left edge: measured.</summary>
    public const double ChipTextInset = 10;

    /// <summary>The distance between the baselines of a chip's lines: measured.</summary>
    public const double ChipLineHeight = 13;

    /// <summary>The first line's baseline, from the chip's top: measured.</summary>
    public const double ChipFirstBaseline = 12;

    /// <summary>What a chip adds to <c>lines × 13</c>: measured.</summary>
    public const double ChipPadding = 5;

    protected const double ChipTextSize = 12;

    /// <summary>The height a chip of this many lines is drawn at.</summary>
    public static double ChipHeight(int lines) => (Math.Max(1, lines) * ChipLineHeight) + ChipPadding;

    /// <summary>
    /// Draws one appointment: the outline, the body, the bar, and as many lines as it was given
    /// room for.
    /// </summary>
    protected void DrawChip(DrawingContext context, Rect box, ChipPaint paint, IReadOnlyList<string> lines, bool selected, bool boldFirstLine = false)
    {
        var rect = new Rect(Math.Round(box.X), Math.Round(box.Y), Math.Round(box.Width), Math.Round(box.Height));
        if (rect.Width < 3 || rect.Height < 3) return;

        Fill(context, rect, paint.Body);

        // The bar, inside the outline. Tentative draws it as diagonals over the hatch ground,
        // which is what tells "pencilled in" from "booked" at a glance.
        var bar = new Rect(rect.X + 1, rect.Y + 1, ChipBarWidth - 2, rect.Height - 2);
        if (bar.Width > 0 && bar.Height > 0)
        {
            if (paint.Hatched)
            {
                Fill(context, bar, Palette.Colour(TokenKeys.Calendar.ChipHatch));
                DrawHatch(context, bar, paint.Bar);
            }
            else
            {
                Fill(context, bar, paint.Bar);
            }
        }

        Outline(context, rect, paint.Edge, paint.Dashed);

        if (selected)
        {
            // A selected chip keeps its own colours and gains a second line inside the first,
            // so selection reads without repainting what the appointment says about itself.
            Outline(context, rect.Deflate(1), Palette.Colour(TokenKeys.Calendar.ChipText), dashed: false);
        }

        var text = Palette.Colour(TokenKeys.Calendar.ChipText);
        var left = rect.X + ChipTextInset;
        var baseline = rect.Y + ChipFirstBaseline;
        using var clip = context.PushClip(rect.Deflate(1));
        for (var i = 0; i < lines.Count; i++)
        {
            var face = boldFirstLine && i == 0 ? SemiBoldFace : Face;
            DrawAt(context, Ink(lines[i], ChipTextSize, text, face), left, baseline);
            baseline += ChipLineHeight;
        }
    }

    /// <summary>A 1px line round a rectangle, whole or dashed 3-on 3-off as Tentative draws it.</summary>
    private void Outline(DrawingContext context, Rect rect, Color colour, bool dashed)
    {
        if (!dashed)
        {
            Fill(context, new Rect(rect.X, rect.Y, rect.Width, 1), colour);
            Fill(context, new Rect(rect.X, rect.Bottom - 1, rect.Width, 1), colour);
            Fill(context, new Rect(rect.X, rect.Y, 1, rect.Height), colour);
            Fill(context, new Rect(rect.Right - 1, rect.Y, 1, rect.Height), colour);
            return;
        }

        const double On = 3;
        const double Period = 6;
        for (var x = rect.X; x < rect.Right; x += Period)
        {
            var w = Math.Min(On, rect.Right - x);
            Fill(context, new Rect(x, rect.Y, w, 1), colour);
            Fill(context, new Rect(x, rect.Bottom - 1, w, 1), colour);
        }

        for (var y = rect.Y; y < rect.Bottom; y += Period)
        {
            var h = Math.Min(On, rect.Bottom - y);
            Fill(context, new Rect(rect.X, y, 1, h), colour);
            Fill(context, new Rect(rect.Right - 1, y, 1, h), colour);
        }
    }

    /// <summary>The Tentative stripe: 3px diagonals on an 8px pitch, measured off the capture.</summary>
    private void DrawHatch(DrawingContext context, Rect bar, Color colour)
    {
        using var _ = context.PushClip(bar);
        var pen = new Pen(Palette.Brush(colour), 3);
        for (var offset = -bar.Height; offset < bar.Width + bar.Height; offset += 8)
        {
            context.DrawLine(
                pen,
                new Point(bar.X + offset, bar.Bottom),
                new Point(bar.X + offset + bar.Height, bar.Y));
        }
    }
}
