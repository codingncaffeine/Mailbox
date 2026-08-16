using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mailbox.Theming.Fonts;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// What every calendar view shares: the resolved palette, the UI face, and the text and chip
/// drawing the reference's views are made of.
/// </summary>
/// <remarks>
/// These views are drawn rather than composed, for the reason §7.4 gives — a month grid is a
/// thousand rectangles and seven hundred pieces of text, and a control per cell would be a
/// control per cell. Drawing also puts every hairline on a whole device pixel, which is the
/// difference between a grid and a smear.
/// </remarks>
public abstract class CalendarSurface : Control
{
    private CalendarPalette? _palette;
    private Typeface? _typeface;
    private Typeface? _bold;
    private Typeface? _semibold;

    protected CalendarSurface()
    {
        ClipToBounds = true;
        // A theme change rewrites the resource dictionary rather than the tree, so the cached
        // palette is the one thing that has to be thrown away for the view to follow it.
        ResourcesChanged += (_, _) =>
        {
            _palette = null;
            _typeface = null;
            _bold = null;
            _semibold = null;
            InvalidateVisual();
        };
    }

    /// <summary>The calendar tokens, resolved once and kept until the theme moves.</summary>
    protected CalendarPalette Palette => _palette ??= CalendarPalette.From(this);

    protected Typeface Face => _typeface ??= new Typeface(UiFamily());
    protected Typeface BoldFace => _bold ??= new Typeface(UiFamily(), FontStyle.Normal, FontWeight.Bold);
    protected Typeface SemiBoldFace => _semibold ??= new Typeface(UiFamily(), FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>The culture dates and times are written in.</summary>
    protected static CultureInfo Culture => CultureInfo.CurrentCulture;

    private FontFamily UiFamily()
    {
        // Through the collection key the bridge publishes: a bundled family asked for by its
        // bare name is not found, and the view would silently draw in the fallback face.
        if (this.TryFindResource("ui.fontfamily", out var found) && found is FontFamily family) return family;
        return BundledFonts.FamilyFor("Segoe UI");
    }

    // ---- Text ------------------------------------------------------------------------------

    protected FormattedText Ink(string text, double size, Color colour, Typeface? face = null)
        => new(text, Culture, FlowDirection.LeftToRight, face ?? Face, size, Palette.Brush(colour));

    /// <summary>
    /// Draws a run so its baseline lands on <paramref name="baseline"/>.
    /// </summary>
    /// <remarks>
    /// Baselines rather than tops, because a top is a different distance from the ink in every
    /// face and size, and the reference's own measurements are of ink. Every number in these
    /// views that positions text is a baseline read off a capture.
    /// </remarks>
    protected static void DrawAt(DrawingContext context, FormattedText text, double left, double baseline)
        => context.DrawText(text, new Point(left, baseline - text.Baseline));

    /// <summary>
    /// Breaks a line to a width the way the reference's chips do: on spaces where it can, inside
    /// a word where it must — a long URL is split mid-token rather than overflowing — and the
    /// last line it is allowed ends in an ellipsis when there is more.
    /// </summary>
    protected IReadOnlyList<string> Wrap(string text, double width, int maxLines, double size, Typeface? face = null)
    {
        if (string.IsNullOrEmpty(text) || maxLines <= 0 || width <= 0) return [];

        var lines = new List<string>();
        var rest = text.Trim();

        while (rest.Length > 0 && lines.Count < maxLines)
        {
            var last = lines.Count == maxLines - 1;
            if (Measure(rest, size, face) <= width)
            {
                lines.Add(rest);
                break;
            }

            var take = LongestFitting(rest, width, size, face, last);
            if (take <= 0) take = 1;
            var line = rest[..take].TrimEnd();
            if (last)
            {
                // Room for the ellipsis is taken out of the line, not added to it.
                while (line.Length > 0 && Measure(line + "…", size, face) > width) line = line[..^1].TrimEnd();
                lines.Add(line + "…");
                break;
            }

            lines.Add(line);
            rest = rest[take..].TrimStart();
        }

        return lines;
    }

    /// <summary>How many characters of the text fit, preferring the last space inside them.</summary>
    private int LongestFitting(string text, double width, double size, Typeface? face, bool allowMidWord)
    {
        var lo = 1;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Measure(text[..mid], size, face) <= width) lo = mid;
            else hi = mid - 1;
        }

        if (allowMidWord) return lo;

        // Back up to the last space, unless the first word is itself longer than the line —
        // then the break has to fall inside it, which is what the reference does to a URL.
        var space = text.LastIndexOf(' ', Math.Min(lo, text.Length - 1));
        return space > 0 ? space : lo;
    }

    private readonly Dictionary<(string Text, double Size, int Weight), double> _widths = [];

    protected double Measure(string text, double size, Typeface? face = null)
    {
        var typeface = face ?? Face;
        var key = (text, size, (int)typeface.Weight);
        if (_widths.TryGetValue(key, out var cached)) return cached;
        var width = new FormattedText(text, Culture, FlowDirection.LeftToRight, typeface, size, null).Width;
        // Wrapping measures a prefix per binary-search step, so the cache is what keeps a full
        // month's chips to one pass; it is bounded because a long day's text is unbounded.
        if (_widths.Count > 8192) _widths.Clear();
        _widths[key] = width;
        return width;
    }

    // ---- Rules -----------------------------------------------------------------------------

    /// <summary>
    /// A hairline on a whole device pixel.
    /// </summary>
    /// <remarks>
    /// Filled rather than stroked. A stroke straddles the coordinate it is given, so a 1px line
    /// at an integer lands half in each of two pixels and renders as two grey ones; the grid's
    /// crispness is the whole reason these views are drawn.
    /// </remarks>
    protected void Line(DrawingContext context, double x, double y, double width, double height, Color colour)
        => context.FillRectangle(Palette.Brush(colour), new Rect(Math.Round(x), Math.Round(y), Math.Max(1, Math.Round(width)), Math.Max(1, Math.Round(height))));

    protected void Fill(DrawingContext context, Rect rect, Color colour)
        => context.FillRectangle(Palette.Brush(colour), rect);

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
