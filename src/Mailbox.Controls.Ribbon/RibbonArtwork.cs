using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.Controls.Ribbon;

/// <summary>
/// A ribbon icon that is drawn rather than set in the icon font.
/// </summary>
/// <remarks>
/// Some of the reference's ribbon icons say what they mean with colour, and a monochrome font
/// cannot carry any of them. <b>Categorize</b> is four coloured swatches and <b>Follow Up</b> is
/// a red flag on a grey pole; <b>New Email</b>, <b>Archive</b> and <b>Move</b> are outlined
/// shapes with a light fill and a coloured badge — a green cross, a green lid, a blue arrow.
/// All five are drawn here and every colour comes from a <c>ribbon.icon.*</c> token, so a theme
/// retints them with everything else.
/// <para>
/// Composed of whole-pixel rectangles rather than paths: at 1× these are hairline grids, and a
/// path with a stroke lands them between pixels. Rounding to the device pixel is the difference
/// between four crisp swatches and four grey smudges. The three two-tone icons go further and
/// carry the reference's own pixels as a grid per size (see <see cref="Figure"/>), because the
/// reference ships a drawing per size rather than one scaled: its 32px cross is 1px thick and
/// 15 long, its 18px cross 2px thick and 10 long, and scaling either into the other's box loses
/// exactly the crispness this class exists to keep.
/// </para>
/// </remarks>
public sealed class RibbonArtwork : Control
{
    /// <summary>The swatch and flag figures' own size, measured: 18 square, centred in the box.</summary>
    private const double Swatches = 18;

    /// <summary>Which drawing: <c>categorize</c>, <c>followup</c>, <c>mail-new</c>, <c>archive</c> or <c>move</c>.</summary>
    public static readonly StyledProperty<string> DrawingProperty =
        AvaloniaProperty.Register<RibbonArtwork, string>(nameof(Drawing), "categorize");

    public static readonly StyledProperty<IBrush?> OutlineProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Outline));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> PlusProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Plus));
    public static readonly StyledProperty<IBrush?> ArrowProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Arrow));
    public static readonly StyledProperty<IBrush?> BlueProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Blue));
    public static readonly StyledProperty<IBrush?> BlueOutlineProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(BlueOutline));
    public static readonly StyledProperty<IBrush?> GreyProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Grey));
    public static readonly StyledProperty<IBrush?> GreyOutlineProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(GreyOutline));
    public static readonly StyledProperty<IBrush?> GoldProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Gold));
    public static readonly StyledProperty<IBrush?> GoldOutlineProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(GoldOutline));
    public static readonly StyledProperty<IBrush?> GreenProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Green));
    public static readonly StyledProperty<IBrush?> GreenOutlineProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(GreenOutline));
    public static readonly StyledProperty<IBrush?> FlagProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(Flag));
    public static readonly StyledProperty<IBrush?> FlagOutlineProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(FlagOutline));
    public static readonly StyledProperty<IBrush?> FlagPoleProperty =
        AvaloniaProperty.Register<RibbonArtwork, IBrush?>(nameof(FlagPole));

    static RibbonArtwork()
    {
        AffectsRender<RibbonArtwork>(
            DrawingProperty, OutlineProperty, FillProperty, PlusProperty, ArrowProperty,
            BlueProperty, BlueOutlineProperty, GreyProperty, GreyOutlineProperty,
            GoldProperty, GoldOutlineProperty, GreenProperty, GreenOutlineProperty,
            FlagProperty, FlagOutlineProperty, FlagPoleProperty);
    }

    public RibbonArtwork(string drawing, double box)
    {
        Drawing = drawing;
        Width = box;
        Height = box;

        Bind(OutlineProperty, "ribbon.icon.outline.brush");
        Bind(FillProperty, "ribbon.icon.fill.brush");
        Bind(PlusProperty, "ribbon.icon.plus.brush");
        Bind(ArrowProperty, "ribbon.icon.blue.brush");
        Bind(BlueProperty, "ribbon.icon.swatch.blue.brush");
        Bind(BlueOutlineProperty, "ribbon.icon.swatch.blue.outline.brush");
        Bind(GreyProperty, "ribbon.icon.swatch.grey.brush");
        Bind(GreyOutlineProperty, "ribbon.icon.swatch.grey.outline.brush");
        Bind(GoldProperty, "ribbon.icon.swatch.gold.brush");
        Bind(GoldOutlineProperty, "ribbon.icon.swatch.gold.outline.brush");
        Bind(GreenProperty, "ribbon.icon.swatch.green.brush");
        Bind(GreenOutlineProperty, "ribbon.icon.swatch.green.outline.brush");
        Bind(FlagProperty, "ribbon.icon.flag.brush");
        Bind(FlagOutlineProperty, "ribbon.icon.flag.outline.brush");
        Bind(FlagPoleProperty, "ribbon.icon.flag.pole.brush");
    }

    public string Drawing
    {
        get => GetValue(DrawingProperty);
        set => SetValue(DrawingProperty, value);
    }

    public IBrush? Outline => GetValue(OutlineProperty);
    public IBrush? Fill => GetValue(FillProperty);
    public IBrush? Plus => GetValue(PlusProperty);
    public IBrush? Arrow => GetValue(ArrowProperty);
    public IBrush? Blue => GetValue(BlueProperty);
    public IBrush? BlueOutline => GetValue(BlueOutlineProperty);
    public IBrush? Grey => GetValue(GreyProperty);
    public IBrush? GreyOutline => GetValue(GreyOutlineProperty);
    public IBrush? Gold => GetValue(GoldProperty);
    public IBrush? GoldOutline => GetValue(GoldOutlineProperty);
    public IBrush? Green => GetValue(GreenProperty);
    public IBrush? GreenOutline => GetValue(GreenOutlineProperty);
    public IBrush? Flag => GetValue(FlagProperty);
    public IBrush? FlagOutline => GetValue(FlagOutlineProperty);
    public IBrush? FlagPole => GetValue(FlagPoleProperty);

    private void Bind(AvaloniaProperty property, string key)
        => this[!property] = new DynamicResourceExtension(key);

    public override void Render(DrawingContext context)
    {
        // By name, and nothing for a name there is no drawing for — the icon map's own rule. A
        // fall-through to the swatches is how People's Follow Up button spent a stretch drawing
        // Categorize: its command asked for "follow-up", which is not what this one is called.
        if (Figures.TryGetValue(Drawing, out var sizes))
        {
            var figure = Nearest(sizes, Math.Min(Bounds.Width, Bounds.Height));
            using var _ = Centre(context, figure.Width, figure.Height);
            Paint(context, figure);
            return;
        }

        using var __ = Centre(context, Swatches, Swatches);
        switch (Drawing)
        {
            case "followup": DrawFollowUp(context); break;
            case "categorize": DrawCategorize(context); break;
            default: break;
        }
    }

    /// <summary>Centred on a whole pixel, so the grid stays crisp in a box of any size.</summary>
    private DrawingContext.PushedState Centre(DrawingContext context, double width, double height)
        => context.PushTransform(Matrix.CreateTranslation(
            Math.Round((Bounds.Width - width) / 2), Math.Round((Bounds.Height - height) / 2)));

    // ---- The two-tone icons ---------------------------------------------------------------

    /// <summary>
    /// One drawing at one size: the reference's own pixels, a character to a pixel.
    /// </summary>
    /// <remarks>
    /// <c>#</c> is the outline, <c>o</c> the light fill, <c>G</c> the badge, <c>g</c> the badge's
    /// own outline, and <c>.</c> nothing at all. Sampled out of the captures at the size each
    /// figure is drawn at, so the rectilinear ink is exact rather than rounded.
    /// <para>
    /// <see cref="Strokes"/> holds the one thing a grid cannot: a diagonal. The envelope's flap
    /// crosses 1.5 pixels a row and the arrow's head 1, and either stepped into whole pixels
    /// comes out as a staircase of separate dots — which is what the reference antialiases
    /// rather than steps. Both are drawn as lines instead, through their cells' own centres, at
    /// the 1.1 width a hairline needs to survive Skia.
    /// </para>
    /// </remarks>
    internal sealed record Figure(int Width, int Height, string[] Rows, Stroke[]? Strokes = null);

    /// <summary>A polyline through cell centres, in one of the grid's own roles.</summary>
    internal sealed record Stroke(char Role, Point[] Points);

    /// <summary>
    /// The badge stands off what it is drawn over: a pixel of outline or fill touching it is
    /// left unpainted, so the surface behind shows through and the badge reads as being in
    /// front. Measured — the reference punches its ribbon's own grey through the tray where the
    /// arrow crosses it, and through the envelope's bottom edge where the cross does.
    /// </summary>
    /// <remarks>
    /// Only <c>G</c> knocks a hole, never <c>g</c>. Archive's lid is a fill inside an outline of
    /// its own, so its two colours touch everywhere and a rule that cut between them would erase
    /// the lid.
    /// </remarks>
    internal static bool Knocks(char role) => role is '#' or 'o';

    private IBrush? BrushFor(char role) => role switch
    {
        '#' => Outline,
        'o' => Fill,
        // Archive's lid is exactly the green swatch pair, in all four themes and inverted in
        // Black with everything else — so it takes those rather than a colour of its own.
        'G' => Drawing switch { "archive" => Green, "move" => Arrow, _ => Plus },
        'g' => Drawing == "archive" ? GreenOutline : null,
        _ => null,
    };

    /// <summary>The figure whose own size is nearest the box, the reference having one per size.</summary>
    internal static Figure Nearest(Figure[] sizes, double box)
    {
        var best = sizes[0];
        foreach (var figure in sizes)
        {
            if (Math.Abs(figure.Width - box) < Math.Abs(best.Width - box)) best = figure;
        }
        return best;
    }

    private void Paint(DrawingContext context, Figure figure)
    {
        var rows = figure.Rows;

        // A row at a time, merging equal neighbours into one rectangle: a 28-wide edge is one
        // fill rather than 28, and the seams between them cannot show.
        for (var y = 0; y < rows.Length; y++)
        {
            var row = rows[y];
            var x = 0;
            while (x < row.Length)
            {
                var role = Role(rows, x, y);
                if (role == '.') { x++; continue; }

                var run = 1;
                while (x + run < row.Length && Role(rows, x + run, y) == role) run++;

                var brush = BrushFor(role);
                if (brush is not null) context.FillRectangle(brush, new Rect(x, y, run, 1));
                x += run;
            }
        }

        foreach (var stroke in figure.Strokes ?? [])
        {
            // 1.0 is an unantialiased hairline in Skia and 1.1 is what the reference's own
            // diagonals weigh.
            if (BrushFor(stroke.Role) is not { } ink) continue;
            var pen = new Pen(ink, 1.1);
            var points = stroke.Points;
            for (var i = 1; i < points.Length; i++) context.DrawLine(pen, points[i - 1], points[i]);
        }
    }

    /// <summary>The character at a place, or <c>.</c> where a badge has knocked it out.</summary>
    internal static char Role(string[] rows, int x, int y)
    {
        var role = rows[y][x];
        if (!Knocks(role)) return role;

        if (x > 0 && rows[y][x - 1] == 'G') return '.';
        if (x + 1 < rows[y].Length && rows[y][x + 1] == 'G') return '.';
        if (y > 0 && rows[y - 1][x] == 'G') return '.';
        if (y + 1 < rows.Length && rows[y + 1][x] == 'G') return '.';
        return role;
    }

    /// <summary>
    /// The three the reference draws two-tone, at the sizes it draws them.
    /// </summary>
    /// <remarks>
    /// Every grid but one is sampled out of a capture: New Email and Archive at 32 off the
    /// classic ribbon and at 18 off the Simplified bar, Move at 16 off the classic ribbon's Move
    /// group and at 18 off the Simplified bar. <b>Move at 32 is authored</b> — no capture shows
    /// the reference's own, its Move being a small button everywhere one was taken — so it is the
    /// 16px figure's proportions at the size the other two large icons measure.
    /// </remarks>
    internal static readonly Dictionary<string, Figure[]> Figures = new(StringComparer.Ordinal)
    {
        ["mail-new"] =
        [
            // A 28×19 envelope with a 1px cross 15 long over its bottom-right corner. The flap
            // meets 9.9 rows down, on the envelope's own centre line.
            new(30, 26,
                [
                    "############################..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooooooo#..",
                    "#oooooooooooooooooooooGoooo#..",
                    "#oooooooooooooooooooooGoooo#..",
                    "#oooooooooooooooooooooGoooo#..",
                    "#oooooooooooooooooooooGoooo#..",
                    "#oooooooooooooooooooooGoooo#..",
                    "#oooooooooooooooooooooGoooo#..",
                    "#oooooooooooooooooooooGoooo#..",
                    "###############GGGGGGGGGGGGGGG",
                    "......................G.......",
                    "......................G.......",
                    "......................G.......",
                    "......................G.......",
                    "......................G.......",
                    "......................G.......",
                    "......................G.......",
                ],
            [new Stroke('#', [new Point(1, 1), new Point(14, 9.9), new Point(27, 1)])]),

            // An 18×13 envelope and a 2px cross 10 long. The reference thickens the badge here
            // rather than shortening it: 1px of green would disappear at this size.
            new(19, 17,
                [
                    "##################.",
                    "#oooooooooooooooo#.",
                    "#oooooooooooooooo#.",
                    "#oooooooooooooooo#.",
                    "#oooooooooooooooo#.",
                    "#oooooooooooooooo#.",
                    "#oooooooooooooooo#.",
                    "#ooooooooooooGGoo#.",
                    "#ooooooooooooGGoo#.",
                    "#ooooooooooooGGoo#.",
                    "#ooooooooooooGGoo#.",
                    "#ooooooooGGGGGGGGGG",
                    "#########GGGGGGGGGG",
                    ".............GG....",
                    ".............GG....",
                    ".............GG....",
                    ".............GG....",
                ],
            [new Stroke('#', [new Point(1, 1), new Point(9, 6), new Point(17, 1)])]),
        ],

        ["archive"] =
        [
            // A 28×6 lid over a 26×19 box, the lid's own bottom line serving as the box's top.
            new(28, 24,
                [
                    "gggggggggggggggggggggggggggg",
                    "gGGGGGGGGGGGGGGGGGGGGGGGGGGg",
                    "gGGGGGGGGGGGGGGGGGGGGGGGGGGg",
                    "gGGGGGGGGGGGGGGGGGGGGGGGGGGg",
                    "gGGGGGGGGGGGGGGGGGGGGGGGGGGg",
                    "gggggggggggggggggggggggggggg",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooo############oooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".#oooooooooooooooooooooooo#.",
                    ".##########################.",
                ]),

            new(18, 14,
                [
                    "gggggggggggggggggg",
                    "gGGGGGGGGGGGGGGGGg",
                    "gGGGGGGGGGGGGGGGGg",
                    "gggggggggggggggggg",
                    ".#oooooooooooooo#.",
                    ".#oooooooooooooo#.",
                    ".#oooooooooooooo#.",
                    ".#ooo########ooo#.",
                    ".#oooooooooooooo#.",
                    ".#oooooooooooooo#.",
                    ".#oooooooooooooo#.",
                    ".#oooooooooooooo#.",
                    ".#oooooooooooooo#.",
                    ".################.",
                ]),
        ],

        ["move"] =
        [
            // Authored — see the remark above. The 16px figure's proportions at 28 wide, which
            // is where New Email and Archive measure. The head is 45° at every size the
            // reference does draw, so it stays 45° here.
            new(28, 25,
                [
                    ".................G..........",
                    ".................G..........",
                    "############.....G..........",
                    "#oooooooooo#.....G..........",
                    "#oooooooooo######G##########",
                    "############oooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#ooooooooooooooooGooooooooo#",
                    "#oooooooooooooooooooooooooo#",
                    "#oooooooooooooooooooooooooo#",
                    "#oooooooooooooooooooooooooo#",
                    "#oooooooooooooooooooooooooo#",
                    "#oooooooooooooooooooooooooo#",
                    "#oooooooooooooooooooooooooo#",
                    "#oooooooooooooooooooooooooo#",
                    "############################",
                ],
            [new Stroke('G', [new Point(11.5, 10.5), new Point(17.5, 16.5), new Point(23.5, 10.5)])]),

            new(18, 18,
                [
                    "...........G......",
                    "...........G......",
                    "########...G......",
                    "#oooooo#...G......",
                    "#oooooo####G######",
                    "########oooGooooo#",
                    "#ooooooooooGooooo#",
                    "#ooooooooooGooooo#",
                    "#ooooooooooGooooo#",
                    "#ooooooooooGooooo#",
                    "#ooooooooooGooooo#",
                    "#ooooooooooGooooo#",
                    "#ooooooooooGooooo#",
                    "#oooooooooooooooo#",
                    "#oooooooooooooooo#",
                    "#oooooooooooooooo#",
                    "#oooooooooooooooo#",
                    "##################",
                ],
            [new Stroke('G', [new Point(7.5, 8.5), new Point(11.5, 12.5), new Point(15.5, 8.5)])]),

            new(16, 15,
                [
                    "..........G.....",
                    "#######...G.....",
                    "#ooooo####G#####",
                    "#######oooGoooo#",
                    "#oooooooooGoooo#",
                    "#oooooooooGoooo#",
                    "#oooooooooGoooo#",
                    "#oooooooooGoooo#",
                    "#oooooooooGoooo#",
                    "#oooooooooGoooo#",
                    "#oooooooooGoooo#",
                    "#oooooooooooooo#",
                    "#oooooooooooooo#",
                    "#oooooooooooooo#",
                    "################",
                ],
            [new Stroke('G', [new Point(7.5, 7.5), new Point(10.5, 10.5), new Point(13.5, 7.5)])]),
        ],
    };

    // ---- Categorize and Follow Up -----------------------------------------------------------

    /// <summary>
    /// Four 8×8 swatches in a 2×2 grid with 2px gutters: a 1px outline round a 6×6 fill. Blue
    /// and grey on top, gold and green below, as the reference orders them.
    /// </summary>
    private void DrawCategorize(DrawingContext context)
    {
        Swatch(context, 0, 0, Blue, BlueOutline);
        Swatch(context, 10, 0, Grey, GreyOutline);
        Swatch(context, 0, 10, Gold, GoldOutline);
        Swatch(context, 10, 10, Green, GreenOutline);
    }

    private static void Swatch(DrawingContext context, double x, double y, IBrush? fill, IBrush? outline)
    {
        // Bound to ribbon.icon.swatch.*; magenta is what a theme missing one gets, rather than
        // a plausible grey that would leave the gap looking like a drawing.
        context.FillRectangle(outline ?? Brushes.Magenta, new Rect(x, y, 8, 8));
        context.FillRectangle(fill ?? Brushes.Magenta, new Rect(x + 1, y + 1, 6, 6));
    }

    /// <summary>
    /// Two overlapping flags on a pole, the second dropped three pixels and offset six: a 1px
    /// outline round a 5px cloth, over a 1px pole running to the figure's foot.
    /// </summary>
    private void DrawFollowUp(DrawingContext context)
    {
        var pole = FlagPole ?? Brushes.Magenta;
        var cloth = Flag ?? Brushes.Magenta;
        var edge = FlagOutline ?? Brushes.Magenta;

        // The pole starts under the near flag and runs to the bottom of the figure.
        context.FillRectangle(pole, new Rect(0, 9, 1, 9));

        FlagPanel(context, 0, 0, cloth, edge);
        FlagPanel(context, 6, 3, cloth, edge);
    }

    private static void FlagPanel(DrawingContext context, double x, double y, IBrush cloth, IBrush edge)
    {
        context.FillRectangle(edge, new Rect(x, y, 7, 9));
        context.FillRectangle(cloth, new Rect(x + 1, y + 1, 5, 7));
    }
}
