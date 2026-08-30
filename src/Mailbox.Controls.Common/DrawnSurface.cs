using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Mailbox.Theming.Fonts;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Common;

/// <summary>
/// A view that draws itself: the UI face, the text measuring and wrapping, the hairlines, and a
/// brush cache so that a render pass allocates none.
/// </summary>
/// <remarks>
/// Drawn rather than composed — a month grid is a thousand rectangles
/// and seven hundred pieces of text, and a control per cell would be a control per cell. Drawing
/// also puts every hairline on a whole device pixel, which is the difference between a grid and
/// a smear.
/// <para>
/// A theme change rewrites the resource dictionary rather than the tree, so anything a view
/// resolved from it has to be thrown away: <see cref="OnPaletteChanged"/> is where a view does
/// that, and the face and the widths are dropped here.
/// </para>
/// </remarks>
public abstract class DrawnSurface : Control
{
    private readonly Dictionary<Color, ImmutableSolidColorBrush> _brushes = [];
    private readonly Dictionary<(string Text, double Size, int Weight), double> _widths = [];
    private readonly Dictionary<string, Color> _colours = [];
    private readonly Dictionary<string, double> _numbers = [];
    private Typeface? _typeface;
    private Typeface? _bold;
    private Typeface? _semibold;

    protected DrawnSurface()
    {
        ClipToBounds = true;
        ResourcesChanged += (_, _) =>
        {
            _typeface = null;
            _bold = null;
            _semibold = null;
            _widths.Clear();
            _colours.Clear();
            _numbers.Clear();
            OnPaletteChanged();
            InvalidateVisual();
        };
    }

    /// <summary>Called when the theme has moved and anything resolved from it is stale.</summary>
    protected virtual void OnPaletteChanged()
    {
    }

    protected Typeface Face => _typeface ??= new Typeface(UiFamily());
    protected Typeface BoldFace => _bold ??= new Typeface(UiFamily(), FontStyle.Normal, FontWeight.Bold);
    protected Typeface SemiBoldFace => _semibold ??= new Typeface(UiFamily(), FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>The culture dates, times and names are written in.</summary>
    protected static CultureInfo Culture => CultureInfo.CurrentCulture;

    private FontFamily UiFamily()
    {
        // Through the collection key the bridge publishes: a bundled family asked for by its
        // bare name is not found, and the view would silently draw in the fallback face.
        if (this.TryFindResource("ui.fontfamily", out var found) && found is FontFamily family) return family;
        return BundledFonts.FamilyFor("Segoe UI");
    }

    // ---- The palette -------------------------------------------------------------------------

    /// <summary>
    /// A token's colour, read from the same resource dictionary <c>{DynamicResource}</c> reads and
    /// cached until the theme moves.
    /// </summary>
    /// <remarks>
    /// Here rather than in each view because every drawn view needs it and three had grown their
    /// own copy. A token a theme has not defined draws magenta on purpose: a view that silently
    /// fell back to a sensible colour would hide the gap the coverage gate exists to catch.
    /// </remarks>
    protected Color Colour(string key)
    {
        if (_colours.TryGetValue(key, out var cached)) return cached;
        var colour = this.TryFindResource(key + ".color", out var found) && found is Color resolved ? resolved : Colors.Magenta;
        _colours[key] = colour;
        return colour;
    }

    /// <summary>A token that is a number rather than a colour — how far a mix goes, how tall a row is.</summary>
    protected double Number(string key, double fallback)
    {
        if (_numbers.TryGetValue(key, out var cached)) return cached;
        var value = this.TryFindResource(key + ".value", out var found) && found is double resolved ? resolved : fallback;
        _numbers[key] = value;
        return value;
    }

    /// <summary>Mixes a colour toward a ground: 0 is the colour itself, 1 the ground.</summary>
    protected static Color Mix(Color colour, Color ground, double amount)
        => Blend.Toward(colour, ground, amount);

    /// <summary>A cached brush for a colour, so a render pass allocates none.</summary>
    protected IBrush Brush(Color colour)
    {
        if (_brushes.TryGetValue(colour, out var brush)) return brush;
        brush = new ImmutableSolidColorBrush(colour);
        _brushes[colour] = brush;
        return brush;
    }

    // ---- Text ------------------------------------------------------------------------------

    protected FormattedText Ink(string text, double size, Color colour, Typeface? face = null)
        => new(text, Culture, FlowDirection.LeftToRight, face ?? Face, size, Brush(colour));

    /// <summary>
    /// Draws a run so its baseline lands on <paramref name="baseline"/>.
    /// </summary>
    /// <remarks>
    /// Baselines rather than tops, because a top is a different distance from the ink in every
    /// face and size, and the reference's own measurements are of ink.
    /// </remarks>
    protected static void DrawAt(DrawingContext context, FormattedText text, double left, double baseline)
        => context.DrawText(text, new Point(left, baseline - text.Baseline));

    /// <summary>
    /// Breaks a line to a width the way the reference does: on spaces where it can, inside a word
    /// where it must — a long URL is split mid-token rather than overflowing — and the last line
    /// it is allowed ends in an ellipsis when there is more.
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

    /// <summary>One line of it, ending in an ellipsis when it does not fit.</summary>
    protected string Ellipsize(string text, double width, double size, Typeface? face = null)
        => Wrap(text, width, 1, size, face) is [var line] ? line : string.Empty;

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

    protected double Measure(string text, double size, Typeface? face = null)
    {
        var typeface = face ?? Face;
        var key = (text, size, (int)typeface.Weight);
        if (_widths.TryGetValue(key, out var cached)) return cached;
        var width = new FormattedText(text, Culture, FlowDirection.LeftToRight, typeface, size, null).Width;
        // Wrapping measures a prefix per binary-search step, so the cache is what keeps a full
        // view's text to one pass; it is bounded because a long line's text is unbounded.
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
    /// at an integer lands half in each of two pixels and renders as two grey ones.
    /// </remarks>
    protected void Line(DrawingContext context, double x, double y, double width, double height, Color colour)
        => context.FillRectangle(Brush(colour), new Rect(Math.Round(x), Math.Round(y), Math.Max(1, Math.Round(width)), Math.Max(1, Math.Round(height))));

    protected void Fill(DrawingContext context, Rect rect, Color colour)
        => context.FillRectangle(Brush(colour), rect);
}
