using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>One region the overlay can name: what the label says, and which token areas a click opens.</summary>
internal sealed record InspectRegion(string Name, IReadOnlyList<string> AreaIds, Control Control);

/// <summary>
/// The area picker: a layer over the whole shell that dims everything, lifts the region under
/// the pointer with its name, and hands a click to whoever opened it. Regions are the shell's
/// own named controls — geometric and honest, because "pick an area" means a place on screen,
/// not a token's ancestry. Escape leaves without choosing.
/// </summary>
/// <remarks>
/// Draws only from tokens and the theming project's wash constants — the sweep holds here as
/// everywhere. The overlay owns the pointer completely while it is up; ending it restores the
/// shell untouched, which the drag pose proves.
/// </remarks>
internal sealed class InspectOverlay : Control
{
    private readonly IReadOnlyList<InspectRegion> _regions;
    private readonly Action<InspectRegion?> _done;
    private InspectRegion? _hovered;

    public InspectOverlay(IReadOnlyList<InspectRegion> regions, Action<InspectRegion?> done)
    {
        _regions = regions;
        _done = done;
        IsHitTestVisible = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>Poses a hover without a pointer, for the harness's photographs.</summary>
    internal void Pose(string regionName)
    {
        _hovered = _regions.FirstOrDefault(r =>
            r.Name.Contains(regionName, StringComparison.OrdinalIgnoreCase)
            || r.AreaIds.Any(a => string.Equals(a, regionName, StringComparison.OrdinalIgnoreCase)));
        InvalidateVisual();
    }

    internal string Describe()
        => string.Join("; ", _regions.Select(r =>
        {
            var bounds = BoundsOf(r);
            return $"{r.Name} [{string.Join(",", r.AreaIds)}] at {bounds.X:0},{bounds.Y:0} {bounds.Width:0}×{bounds.Height:0}";
        }));

    internal InspectRegion? Find(string name)
        => _regions.FirstOrDefault(r =>
            r.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
            || r.AreaIds.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The harness's click: the same completion a pointer press runs.</summary>
    internal void Choose(InspectRegion region) => _done(region);

    private Rect BoundsOf(InspectRegion region)
    {
        var topLeft = region.Control.TranslatePoint(default, this) ?? default;
        return new Rect(topLeft, region.Control.Bounds.Size);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        // The dim is a selected wash, not an invented colour; the lifted region stays clear.
        var dim = new SolidColorBrush(Color.Parse(Recolour.WashOverLightPressed));
        if (_hovered is null)
        {
            context.FillRectangle(dim, bounds);
            return;
        }

        var lifted = BoundsOf(_hovered);
        context.FillRectangle(dim, new Rect(bounds.Left, bounds.Top, bounds.Width, Math.Max(0, lifted.Top - bounds.Top)));
        context.FillRectangle(dim, new Rect(bounds.Left, lifted.Bottom, bounds.Width, Math.Max(0, bounds.Bottom - lifted.Bottom)));
        context.FillRectangle(dim, new Rect(bounds.Left, lifted.Top, Math.Max(0, lifted.Left - bounds.Left), lifted.Height));
        context.FillRectangle(dim, new Rect(lifted.Right, lifted.Top, Math.Max(0, bounds.Right - lifted.Right), lifted.Height));

        var accent = App.Themes.Tokens.GetBrush(TokenKeys.Accent.Rest);
        context.DrawRectangle(null, new Pen(accent, 2), lifted.Deflate(1));

        // The name, on a small accent tag inside the region's top-left corner.
        var text = new FormattedText(_hovered.Name, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(App.Themes.Tokens.GetString(TokenKeys.Typography.UiFamily)),
            App.Themes.Tokens.GetDouble(TokenKeys.Typography.UiSize),
            App.Themes.Tokens.GetBrush(TokenKeys.Text.OnAccent));
        var pad = new Size(text.Width + 12, text.Height + 6);
        var at = new Point(
            Math.Clamp(lifted.Left + 4, bounds.Left, Math.Max(bounds.Left, bounds.Right - pad.Width)),
            Math.Clamp(lifted.Top + 4, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - pad.Height)));
        context.FillRectangle(accent, new Rect(at, pad), 3);
        context.DrawText(text, at + new Vector(6, 3));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        var over = _regions.LastOrDefault(r => r.Control.IsVisible && BoundsOf(r).Contains(position));
        if (!ReferenceEquals(over, _hovered))
        {
            _hovered = over;
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Handled = true;
        _done(_hovered);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _done(null);
        }
    }
}
