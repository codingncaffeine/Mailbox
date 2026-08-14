using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// The zoom control in the status bar.
/// </summary>
/// <remarks>
/// Drawn rather than templated. The reference draws a hairline: a one-pixel track with a
/// one-pixel tick riding it, five pixels tall — no capsule, no knob, no fill either side of the
/// thumb. Every stock slider template has a chunky thumb and a track with thickness, and
/// re-templating one to disappear this far is more work, and more fragile, than two rectangles.
/// </remarks>
public sealed class ZoomSlider : Control
{
    /// <summary>Track and tick are both one pixel; the whole control is a hairline.</summary>
    private const double LineThickness = 1;

    /// <summary>Measured height of the tick marking the current value.</summary>
    private const double ThumbHeight = 5;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ZoomSlider, double>(nameof(Value), 100,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ZoomSlider, double>(nameof(Minimum), 50);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ZoomSlider, double>(nameof(Maximum), 200);

    /// <summary>Colour of both the track and the tick — they are never drawn apart.</summary>
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<ZoomSlider, IBrush?>(nameof(LineBrush));

    static ZoomSlider()
    {
        AffectsRender<ZoomSlider>(ValueProperty, MinimumProperty, MaximumProperty,
            LineBrushProperty);
    }

    public ZoomSlider()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        // Without this the control centres on a half-pixel in the status bar and the hairline
        // renders as two rows at half strength — the exact softness it exists to avoid.
        UseLayoutRounding = true;
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (LineBrush is not { } brush || Bounds.Width <= 0) return;

        // Snap against the window, not against ourselves. A hairline landing on a half-pixel
        // renders as two grey rows at half strength — the exact softness this control exists
        // to avoid — and the offset that causes it comes from an ancestor's centring, so
        // rounding within our own bounds cannot see it, and UseLayoutRounding does not fix it.
        var origin = TopLevel.GetTopLevel(this) is { } root
            ? this.TranslatePoint(default, root) ?? default
            : default;
        var snap = Math.Round(origin.Y) - origin.Y;
        var centre = Math.Round((Bounds.Height - LineThickness) / 2) + snap;

        context.FillRectangle(brush, new Rect(0, centre, Bounds.Width, LineThickness));
        context.FillRectangle(brush, new Rect(
            Math.Round(Fraction * (Bounds.Width - LineThickness)),
            centre - Math.Round((ThumbHeight - LineThickness) / 2),
            LineThickness,
            ThumbHeight));
    }

    /// <summary>
    /// An even height, so centring inside an even-height status bar lands on a whole pixel.
    /// The tick is drawn centred within it and is unaffected by the extra row.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, ThumbHeight + 1);

    private double Fraction
    {
        get
        {
            var span = Maximum - Minimum;
            return span <= 0 ? 0 : Math.Clamp((Value - Minimum) / span, 0, 1);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        SetFromPointer(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Equals(e.Pointer.Captured, this)) SetFromPointer(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void SetFromPointer(double x)
    {
        if (Bounds.Width <= 0) return;
        var fraction = Math.Clamp(x / Bounds.Width, 0, 1);
        Value = Math.Round(Minimum + (fraction * (Maximum - Minimum)));
    }
}
