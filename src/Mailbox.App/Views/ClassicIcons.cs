using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Mailbox.App.Views;

/// <summary>
/// The small coloured toolbar icons of the system dialogs, drawn at 16px.
/// </summary>
/// <remarks>
/// The reference's Account Settings toolbar carries the older generation of icons — a gold
/// envelope in a blue-grey sleeve for New, a hammer and wrench for Repair, a form with a
/// pencil across it for Change, a black disc with a white tick for Set as Default — rather than
/// the monochrome glyphs its ribbon uses. Those bitmaps are not ours to ship, so these are drawn
/// here to the same designs: the same subjects, sizes and colour language, from the token
/// palette rather than fixed paint. Disabled, an icon is its silhouette in the disabled ink,
/// which is how the reference greys them.
/// </remarks>
public sealed class ClassicIcon : Control
{
    /// <summary>Which drawing: new, add-file, repair, change, default, remove, up, down, folder, book.</summary>
    public static readonly StyledProperty<string> GlyphProperty =
        AvaloniaProperty.Register<ClassicIcon, string>(nameof(Glyph), "new");

    /// <summary>True to draw the silhouette in the disabled ink.</summary>
    public static readonly StyledProperty<bool> IsDisabledProperty =
        AvaloniaProperty.Register<ClassicIcon, bool>(nameof(IsDisabled));

    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(Ink));

    public static readonly StyledProperty<IBrush?> PaperProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(Paper));

    public static readonly StyledProperty<IBrush?> GoldProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(Gold));

    public static readonly StyledProperty<IBrush?> GoldDarkProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(GoldDark));

    public static readonly StyledProperty<IBrush?> SteelProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(Steel));

    public static readonly StyledProperty<IBrush?> SteelDarkProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(SteelDark));

    public static readonly StyledProperty<IBrush?> WoodProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(Wood));

    public static readonly StyledProperty<IBrush?> GreenProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(Green));

    public static readonly StyledProperty<IBrush?> BlueProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(Blue));

    public static readonly StyledProperty<IBrush?> BlueDarkProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(BlueDark));

    public static readonly StyledProperty<IBrush?> RedProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(Red));

    public static readonly StyledProperty<IBrush?> RedLightProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(RedLight));

    public static readonly StyledProperty<IBrush?> DisabledInkProperty =
        AvaloniaProperty.Register<ClassicIcon, IBrush?>(nameof(DisabledInk));

    static ClassicIcon()
    {
        AffectsRender<ClassicIcon>(
            GlyphProperty, IsDisabledProperty, InkProperty, PaperProperty, GoldProperty,
            GoldDarkProperty, SteelProperty, SteelDarkProperty, WoodProperty, GreenProperty,
            BlueProperty, BlueDarkProperty, DisabledInkProperty);
    }

    public ClassicIcon(string glyph)
    {
        Glyph = glyph;
        Width = 16;
        Height = 16;

        Bind(InkProperty, "systemdialog.icon.ink.brush");
        Bind(PaperProperty, "systemdialog.icon.paper.brush");
        Bind(GoldProperty, "systemdialog.icon.gold.brush");
        Bind(GoldDarkProperty, "systemdialog.icon.gold.dark.brush");
        Bind(SteelProperty, "systemdialog.icon.steel.brush");
        Bind(SteelDarkProperty, "systemdialog.icon.steel.dark.brush");
        Bind(WoodProperty, "systemdialog.icon.wood.brush");
        Bind(GreenProperty, "systemdialog.icon.green.brush");
        Bind(BlueProperty, "systemdialog.icon.blue.brush");
        Bind(BlueDarkProperty, "systemdialog.icon.blue.dark.brush");
        Bind(RedProperty, "systemdialog.icon.red.brush");
        Bind(RedLightProperty, "systemdialog.icon.red.light.brush");
        Bind(DisabledInkProperty, "systemdialog.foreground.disabled.brush");
    }

    public string Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool IsDisabled
    {
        get => GetValue(IsDisabledProperty);
        set => SetValue(IsDisabledProperty, value);
    }

    public IBrush? Ink { get => GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public IBrush? Paper { get => GetValue(PaperProperty); set => SetValue(PaperProperty, value); }
    public IBrush? Gold { get => GetValue(GoldProperty); set => SetValue(GoldProperty, value); }
    public IBrush? GoldDark { get => GetValue(GoldDarkProperty); set => SetValue(GoldDarkProperty, value); }
    public IBrush? Steel { get => GetValue(SteelProperty); set => SetValue(SteelProperty, value); }
    public IBrush? SteelDark { get => GetValue(SteelDarkProperty); set => SetValue(SteelDarkProperty, value); }
    public IBrush? Wood { get => GetValue(WoodProperty); set => SetValue(WoodProperty, value); }
    public IBrush? Green { get => GetValue(GreenProperty); set => SetValue(GreenProperty, value); }
    public IBrush? Blue { get => GetValue(BlueProperty); set => SetValue(BlueProperty, value); }
    public IBrush? BlueDark { get => GetValue(BlueDarkProperty); set => SetValue(BlueDarkProperty, value); }
    public IBrush? Red { get => GetValue(RedProperty); set => SetValue(RedProperty, value); }
    public IBrush? RedLight { get => GetValue(RedLightProperty); set => SetValue(RedLightProperty, value); }
    public IBrush? DisabledInk { get => GetValue(DisabledInkProperty); set => SetValue(DisabledInkProperty, value); }

    private void Bind(AvaloniaProperty property, string key)
        => this[!property] = new DynamicResourceExtension(key);

    public override void Render(DrawingContext context)
    {
        // Disabled: every colour becomes the disabled ink — the silhouette the reference draws
        // — while a hole cut through a shape stays a hole: the tick in the disc is the ground
        // showing through, and greying the disc leaves it showing through.
        // Every one of these is bound to a systemdialog.icon.* token above, so the fallback is
        // only ever reached when a theme has not defined one — and then it draws magenta on
        // purpose, the way DrawnSurface does. Falling back to a plausible colour instead is how
        // a missing token stays missing: the drawing still looks like a drawing.
        var grey = IsDisabled ? DisabledInk : null;
        var missing = Brushes.Magenta;
        var p = new Palette(
            grey ?? Ink ?? missing,
            grey ?? Paper ?? missing,
            Paper ?? missing,
            grey ?? Gold ?? missing,
            grey ?? GoldDark ?? missing,
            grey ?? Steel ?? missing,
            grey ?? SteelDark ?? missing,
            grey ?? Wood ?? missing,
            grey ?? Green ?? missing,
            grey ?? Blue ?? missing,
            grey ?? BlueDark ?? missing,
            grey ?? Red ?? missing,
            grey ?? RedLight ?? missing);

        Draw(context, Glyph, p);
    }

    /// <summary>
    /// The colours a drawing is made of. <see cref="Paper"/> is white that is painted — a page,
    /// a face of a box — and greys with the rest; <see cref="Hole"/> is white that is cut out
    /// of a shape, and stays.
    /// </summary>
    public readonly record struct Palette(
        IBrush Ink, IBrush Paper, IBrush Hole, IBrush Gold, IBrush GoldDark, IBrush Steel, IBrush SteelDark,
        IBrush Wood, IBrush Green, IBrush Blue, IBrush BlueDark, IBrush Red, IBrush RedLight)
    {
        /// <summary>A two-colour palette, for the marker a list draws itself: a disc in ink with a tick cut from it.</summary>
        public static Palette Mono(IBrush ink, IBrush ground) => new(ink, ink, ground, ink, ink, ink, ink, ink, ink, ink, ink, ink, ink);
    }

    /// <summary>Draws a glyph at the origin of <paramref name="context"/>, 16px square.</summary>
    public static void Draw(DrawingContext context, string glyph, Palette p)
    {
        switch (glyph)
        {
            case "new": DrawNew(context, p); break;
            case "add-file": DrawAddFile(context, p); break;
            case "repair": DrawRepair(context, p); break;
            case "change": DrawChange(context, p); break;
            case "default": DrawDefault(context, p); break;
            case "remove": DrawRemove(context, p); break;
            case "up": DrawArrow(context, p, up: true); break;
            case "down": DrawArrow(context, p, up: false); break;
            case "folder": DrawFolder(context, p); break;
            case "book": DrawBook(context, p); break;
            case "move-to-folder": DrawMoveToFolder(context, p); break;
            case "flag": DrawFlag(context, p); break;
            case "alert-star": DrawAlertStar(context, p); break;
            case "sound": DrawSound(context, p); break;
            case "envelope": DrawEnvelope(context, p); break;
            case "identities": DrawIdentities(context, p); break;
            case "send": DrawSend(context, p); break;
            case "tick": DrawTickBox(context, p, ticked: true); break;
            case "untick": DrawTickBox(context, p, ticked: false); break;
        }
    }

    // ---- The drawings ---------------------------------------------------------------------

    /// <summary>
    /// The list-view tick box the reference puts beside each rule: a 13px white square in a grey
    /// line, with a black tick when it is on.
    /// </summary>
    /// <remarks>
    /// Drawn rather than templated because it lives inside a list that draws itself. 13px is the
    /// desktop's own size and it sits one pixel down of centre in a 16px slot, which is where the
    /// capture puts it against a 17px row.
    /// </remarks>
    private static void DrawTickBox(DrawingContext c, Palette p, bool ticked)
    {
        c.DrawRectangle(p.Hole, new Pen(p.Ink, 1), new Rect(1.5, 1.5, 12, 12));
        if (!ticked) return;

        // Two strokes, the short one down-right and the long one up-right, as a tick is drawn.
        var pen = new Pen(p.Ink, 2);
        c.DrawLine(pen, new Point(4, 8), new Point(6.5, 10.5));
        c.DrawLine(pen, new Point(6.5, 10.5), new Point(11, 5));
    }

    // Each is composed on a 16x16 grid. Outlines are 1px and sit on pixel edges so they stay
    // crisp at 1x; the diagonals are the exception, and are meant to anti-alias.

    /// <summary>A gold envelope slipping out of a blue-grey sleeve, with a green spark: New.</summary>
    private static void DrawNew(DrawingContext c, Palette p)
    {
        // The sleeve: an open box seen from above, a white top face over a blue-grey side.
        c.DrawGeometry(p.Paper, new Pen(p.SteelDark, 1),
            Poly((1.5, 5.5), (9.5, 1.5), (13.5, 3.5), (5.5, 7.5)));
        c.DrawGeometry(p.Steel, new Pen(p.SteelDark, 1),
            Poly((1.5, 5.5), (5.5, 7.5), (5.5, 12.5), (1.5, 10.5)));

        // The envelope, with its flap.
        c.DrawRectangle(p.Gold, new Pen(p.GoldDark, 1), new Rect(6.5, 8.5, 9, 6));
        c.DrawGeometry(null, new Pen(p.GoldDark, 1), Poly((6.5, 8.5), (11, 12), (15.5, 8.5)));

        // The spark that says "new".
        c.DrawRectangle(p.Green, null, new Rect(13, 3, 3, 1));
        c.DrawRectangle(p.Green, null, new Rect(14, 2, 1, 3));
    }

    /// <summary>A filing cabinet with an envelope in front of it: Add, on Data Files.</summary>
    private static void DrawAddFile(DrawingContext c, Palette p)
    {
        c.DrawRectangle(p.Steel, new Pen(p.SteelDark, 1), new Rect(1.5, 0.5, 11, 14));
        c.DrawRectangle(p.Paper, null, new Rect(3, 2, 8, 5));
        c.DrawRectangle(p.Paper, null, new Rect(3, 8, 8, 5));
        c.DrawRectangle(p.SteelDark, null, new Rect(5, 4, 4, 1));
        c.DrawRectangle(p.SteelDark, null, new Rect(5, 10, 4, 1));

        c.DrawRectangle(p.Gold, new Pen(p.GoldDark, 1), new Rect(7.5, 8.5, 8, 6));
        c.DrawGeometry(null, new Pen(p.GoldDark, 1), Poly((7.5, 8.5), (11.5, 12), (15.5, 8.5)));
    }

    /// <summary>A hammer over a wrench: Repair.</summary>
    private static void DrawRepair(DrawingContext c, Palette p)
    {
        // The wrench, its open jaw at the top left and its shaft running down to the right.
        c.DrawLine(new Pen(p.SteelDark, 2.2), new Point(5, 5), new Point(13.5, 13.5));
        c.DrawGeometry(p.Steel, new Pen(p.SteelDark, 1),
            Poly((1, 3), (3, 1), (5, 2), (4.5, 3.5), (6.5, 5.5), (5, 7), (3, 5), (1.5, 5.5)));

        // The hammer over it: a wooden handle down to the left under a steel head at the top right.
        c.DrawLine(new Pen(p.Wood, 2.4), new Point(10, 6), new Point(2.5, 13.5));
        c.DrawGeometry(p.Steel, new Pen(p.SteelDark, 1),
            Poly((6.5, 4.5), (10.5, 1.5), (14.5, 3.5), (13.5, 6.5), (11.5, 6), (9.5, 7.5)));
    }

    /// <summary>A form with a pencil across its corner: Change, and Settings on Data Files.</summary>
    private static void DrawChange(DrawingContext c, Palette p)
    {
        c.DrawRectangle(p.Paper, new Pen(p.SteelDark, 1), new Rect(1.5, 4.5, 11, 10));
        c.DrawRectangle(p.Steel, null, new Rect(2, 5, 10, 2));
        c.DrawRectangle(p.SteelDark, null, new Rect(3, 9, 7, 1));
        c.DrawRectangle(p.SteelDark, null, new Rect(3, 11, 7, 1));

        // The pencil: gold body, green cap, dark point.
        c.DrawLine(new Pen(p.Gold, 3), new Point(8, 8.5), new Point(13.5, 3));
        c.DrawLine(new Pen(p.Green, 3), new Point(13, 3.5), new Point(15, 1.5));
        c.DrawGeometry(p.Ink, null, Poly((6, 10.5), (7, 7.5), (9, 9.5)));
    }

    /// <summary>A black disc with a white tick: the default account, and Set as Default.</summary>
    private static void DrawDefault(DrawingContext c, Palette p)
    {
        c.DrawEllipse(p.Ink, null, new Point(8, 8), 7, 7);
        c.DrawGeometry(null, new Pen(p.Hole, 2), Path((4.5, 8), (7, 10.5), (11.5, 5.5)));
    }

    /// <summary>A cross: Remove.</summary>
    private static void DrawRemove(DrawingContext c, Palette p)
    {
        var pen = new Pen(p.Ink, 1.6);
        c.DrawLine(pen, new Point(3, 3), new Point(13, 13));
        c.DrawLine(pen, new Point(13, 3), new Point(3, 13));
    }

    /// <summary>A stout blue arrow, 9 wide and 10 tall as the reference's: move up or down.</summary>
    private static void DrawArrow(DrawingContext c, Palette p, bool up)
    {
        IBrush fill = p.Blue is ISolidColorBrush light && p.BlueDark is ISolidColorBrush dark && !ReferenceEquals(light, dark)
            ? new ImmutableLinearGradientBrush(
                [new ImmutableGradientStop(0, light.Color), new ImmutableGradientStop(1, dark.Color)],
                startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(1, 1, RelativeUnit.Relative))
            : p.Blue;

        // Head 9 wide by 5 tall, shaft 5 wide by 4 tall, in the icon's middle.
        var geometry = up
            ? Poly((3.5, 8.5), (8, 3.5), (12.5, 8.5), (10.5, 8.5), (10.5, 12.5), (5.5, 12.5), (5.5, 8.5))
            : Poly((3.5, 7.5), (8, 12.5), (12.5, 7.5), (10.5, 7.5), (10.5, 3.5), (5.5, 3.5), (5.5, 7.5));

        c.DrawGeometry(fill, new Pen(p.BlueDark, 1), geometry);
    }

    /// <summary>A yellow folder: Open File Location.</summary>
    private static void DrawFolder(DrawingContext c, Palette p)
    {
        c.DrawGeometry(p.Gold, new Pen(p.GoldDark, 1),
            Poly((1.5, 3.5), (6.5, 3.5), (8, 5.5), (14.5, 5.5), (14.5, 13.5), (1.5, 13.5)));
        c.DrawGeometry(p.Paper, null, Poly((2, 6), (14, 6), (14, 7), (2, 7)));
        c.DrawGeometry(p.Gold, new Pen(p.GoldDark, 1),
            Poly((1.5, 7.5), (14.5, 7.5), (14.5, 13.5), (1.5, 13.5)));
    }

    /// <summary>An open address book with coloured index tabs: New, on Address Books.</summary>
    private static void DrawBook(DrawingContext c, Palette p)
    {
        // Two pages meeting at the spine, on a blue-grey cover.
        c.DrawGeometry(p.SteelDark, null, Poly((1, 3), (15, 3), (15, 14), (1, 14)));
        c.DrawGeometry(p.Paper, new Pen(p.Steel, 1), Poly((2.5, 4.5), (7.5, 4.5), (7.5, 12.5), (2.5, 12.5)));
        c.DrawGeometry(p.Paper, new Pen(p.Steel, 1), Poly((8.5, 4.5), (13.5, 4.5), (13.5, 12.5), (8.5, 12.5)));

        // Lines of entries.
        c.DrawRectangle(p.Blue, null, new Rect(3, 6, 4, 1));
        c.DrawRectangle(p.Blue, null, new Rect(3, 8, 4, 1));
        c.DrawRectangle(p.Blue, null, new Rect(9, 6, 4, 1));
        c.DrawRectangle(p.Blue, null, new Rect(9, 8, 4, 1));

        // The index tabs down the right edge.
        c.DrawRectangle(p.Gold, null, new Rect(14, 5, 2, 2));
        c.DrawRectangle(p.Green, null, new Rect(14, 8, 2, 2));
        c.DrawRectangle(p.Ink, null, new Rect(14, 11, 2, 2));
    }

    /// <summary>A page with a folded corner and a blue arrow moving it down into a folder:
    /// the wizard's move-messages templates.</summary>
    private static void DrawMoveToFolder(DrawingContext c, Palette p)
    {
        c.DrawGeometry(p.Paper, new Pen(p.Steel, 1),
            Poly((3.5, 1.5), (10.5, 1.5), (12.5, 3.5), (12.5, 14.5), (3.5, 14.5)));
        c.DrawGeometry(p.Hole, new Pen(p.Steel, 1), Poly((10.5, 1.5), (10.5, 3.5), (12.5, 3.5)));
        c.DrawRectangle(p.Blue, null, new Rect(7, 5, 2, 5));
        c.DrawGeometry(p.Blue, null, Poly((5, 10), (11, 10), (8, 13.5)));
    }

    /// <summary>The follow-up flag: a two-tone red pennant on a steel pole.</summary>
    private static void DrawFlag(DrawingContext c, Palette p)
    {
        c.DrawRectangle(p.SteelDark, null, new Rect(4, 2, 1, 12));
        c.DrawRectangle(p.RedLight, new Pen(p.Red, 1), new Rect(4.5, 2.5, 4, 5));
        c.DrawRectangle(p.Red, new Pen(p.Red, 1), new Rect(8.5, 1.5, 4, 5));
    }

    /// <summary>An envelope with a gold star on its corner: the New Item Alert Window.</summary>
    private static void DrawAlertStar(DrawingContext c, Palette p)
    {
        c.DrawGeometry(p.Paper, new Pen(p.Steel, 1), Poly((1.5, 3.5), (12.5, 3.5), (12.5, 11.5), (1.5, 11.5)));
        c.DrawGeometry(null, new Pen(p.Steel, 1), Build([(1.5, 3.5), (7, 8), (12.5, 3.5)], closed: false));
        c.DrawGeometry(p.Gold, new Pen(p.GoldDark, 1),
            Poly((11.5, 8), (12.6, 10.4), (15, 10.7), (13.2, 12.4), (13.7, 14.8), (11.5, 13.5),
                (9.3, 14.8), (9.8, 12.4), (8, 10.7), (10.4, 10.4)));
    }

    /// <summary>A speaker with its sound on the air: Play a sound.</summary>
    private static void DrawSound(DrawingContext c, Palette p)
    {
        c.DrawGeometry(p.Steel, new Pen(p.SteelDark, 1),
            Poly((1.5, 5.5), (4.5, 5.5), (8.5, 2), (8.5, 14), (4.5, 10.5), (1.5, 10.5)));
        c.DrawGeometry(null, new Pen(p.SteelDark, 1), Build([(10.5, 6), (11.3, 8), (10.5, 10)], closed: false));
        c.DrawGeometry(null, new Pen(p.SteelDark, 1), Build([(12.5, 4.5), (13.8, 8), (12.5, 11.5)], closed: false));
    }

    /// <summary>A plain envelope: Apply rule on messages I receive.</summary>
    private static void DrawEnvelope(DrawingContext c, Palette p)
    {
        c.DrawGeometry(p.Paper, new Pen(p.SteelDark, 1), Poly((1.5, 3.5), (14.5, 3.5), (14.5, 12.5), (1.5, 12.5)));
        c.DrawGeometry(null, new Pen(p.SteelDark, 1), Build([(1.5, 3.5), (8, 9), (14.5, 3.5)], closed: false));
    }

    /// <summary>
    /// Two envelopes, one behind the other: the addresses an account may send as.
    /// </summary>
    /// <remarks>
    /// The one in front is the envelope glyph moved down and right, so the two read as the same
    /// thing twice rather than as two different objects — which is exactly what an identity is.
    /// The one behind is drawn first and shows only its top and left edges.
    /// </remarks>
    private static void DrawIdentities(DrawingContext c, Palette p)
    {
        c.DrawGeometry(p.Paper, new Pen(p.SteelDark, 1), Poly((1.5, 2.5), (11.5, 2.5), (11.5, 9.5), (1.5, 9.5)));
        c.DrawGeometry(null, new Pen(p.SteelDark, 1), Build([(1.5, 2.5), (6.5, 6.5), (11.5, 2.5)], closed: false));

        c.DrawGeometry(p.Paper, new Pen(p.SteelDark, 1), Poly((4.5, 6.5), (14.5, 6.5), (14.5, 13.5), (4.5, 13.5)));
        c.DrawGeometry(null, new Pen(p.SteelDark, 1), Build([(4.5, 6.5), (9.5, 10.5), (14.5, 6.5)], closed: false));
    }

    /// <summary>A paper dart on its way: Apply rule on messages I send.</summary>
    private static void DrawSend(DrawingContext c, Palette p)
    {
        c.DrawGeometry(p.Paper, new Pen(p.SteelDark, 1), Poly((1.5, 8.5), (14.5, 2.5), (9.5, 13.5), (7.5, 9.5)));
        c.DrawGeometry(null, new Pen(p.SteelDark, 1), Build([(14.5, 2.5), (7.5, 9.5)], closed: false));
    }

    /// <summary>A closed, filled polygon.</summary>
    private static StreamGeometry Poly(params (double X, double Y)[] points) => Build(points, closed: true);

    /// <summary>An open polyline, for a stroke that is not a shape.</summary>
    private static StreamGeometry Path(params (double X, double Y)[] points) => Build(points, closed: false);

    private static StreamGeometry Build((double X, double Y)[] points, bool closed)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: closed);
        for (var i = 1; i < points.Length; i++) context.LineTo(new Point(points[i].X, points[i].Y));
        context.EndFigure(isClosed: closed);
        return geometry;
    }
}
