using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.Controls.Ribbon;

/// <summary>
/// A ribbon icon that is drawn rather than set in the icon font.
/// </summary>
/// <remarks>
/// Two of the reference's ribbon icons say what they mean with colour, and a monochrome font
/// cannot carry either: <b>Categorize</b> is four coloured swatches, and <b>Follow Up</b> is a
/// red flag on a grey pole. Both are drawn here, pixel for pixel off the captures — an 18×18
/// figure inside the ribbon's 20px icon box — and every colour comes from a
/// <c>ribbon.icon.*</c> token, so a theme retints them with everything else.
/// <para>
/// Composed of whole-pixel rectangles rather than paths: at 1× these are hairline grids, and a
/// path with a stroke lands them between pixels. Rounding to the device pixel is the difference
/// between four crisp swatches and four grey smudges.
/// </para>
/// </remarks>
public sealed class RibbonArtwork : Control
{
    /// <summary>The figure's own size, measured: 18 square, centred in whatever box it is given.</summary>
    private const double Figure = 18;

    /// <summary>Which drawing: <c>categorize</c> or <c>followup</c>.</summary>
    public static readonly StyledProperty<string> DrawingProperty =
        AvaloniaProperty.Register<RibbonArtwork, string>(nameof(Drawing), "categorize");

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
            DrawingProperty, BlueProperty, BlueOutlineProperty, GreyProperty, GreyOutlineProperty,
            GoldProperty, GoldOutlineProperty, GreenProperty, GreenOutlineProperty,
            FlagProperty, FlagOutlineProperty, FlagPoleProperty);
    }

    public RibbonArtwork(string drawing, double box)
    {
        Drawing = drawing;
        Width = box;
        Height = box;

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
        // Centred on a whole pixel, so the grid stays crisp in a box of any size.
        var left = Math.Round((Bounds.Width - Figure) / 2);
        var top = Math.Round((Bounds.Height - Figure) / 2);
        using var _ = context.PushTransform(Matrix.CreateTranslation(left, top));

        // By name, and nothing for a name there is no drawing for — the icon map's own rule. A
        // fall-through to the swatches is how People's Follow Up button spent a phase drawing
        // Categorize: its command asked for "follow-up", which is not what this one is called.
        switch (Drawing)
        {
            case "followup": DrawFollowUp(context); break;
            case "categorize": DrawCategorize(context); break;
            default: break;
        }
    }

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
        context.FillRectangle(outline ?? Brushes.Gray, new Rect(x, y, 8, 8));
        context.FillRectangle(fill ?? Brushes.White, new Rect(x + 1, y + 1, 6, 6));
    }

    /// <summary>
    /// Two overlapping flags on a pole, the second dropped three pixels and offset six: a 1px
    /// outline round a 5px cloth, over a 1px pole running to the figure's foot.
    /// </summary>
    private void DrawFollowUp(DrawingContext context)
    {
        var pole = FlagPole ?? Brushes.Gray;
        var cloth = Flag ?? Brushes.IndianRed;
        var edge = FlagOutline ?? Brushes.DarkRed;

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
