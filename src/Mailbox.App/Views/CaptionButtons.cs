using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// Windows-style minimize, maximize/restore and close buttons for a client-side-decorated
/// window.
/// </summary>
/// <remarks>
/// Drawn as vector geometry rather than glyphs because the Windows originals live in Segoe
/// MDL2 Assets, which is not redistributable. The shapes are simple enough that reproducing
/// them exactly costs less than finding a substitute font, and it means they stay crisp at any
/// scale and take their colour from theme tokens.
/// <para>
/// Metrics are measured off the reference: 48x44 hit targets, a 10px glyph box, and the close
/// button turning red on hover with white glyph — the one caption button that does not use the
/// neutral hover.
/// </para>
/// </remarks>
public sealed class CaptionButtons : StackPanel
{
    private const double ButtonWidth = 48;
    private const double ButtonHeight = 44;
    private const double GlyphBox = 10;
    private const double StrokeWidth = 1;

    private readonly Window _window;
    private readonly Button _maximize;

    /// <summary>
    /// Forces a caption button into its hover state so the fidelity harness can photograph it.
    /// A screenshot cannot move the pointer, and the close button's red is the one caption
    /// colour that only exists on hover — it went unverified, and wrong, for two sessions.
    /// </summary>
    public void ForceHover(string which)
    {
        var cls = which == "close" ? "caption-close" : "caption";
        foreach (var button in Children.OfType<Button>().Where(b => b.Classes.Contains(cls)))
        {
            ((IPseudoClasses)button.Classes).Add(":pointerover");
            if (which != "close") break;
        }
    }

    public CaptionButtons(Window window)
    {
        _window = window;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Top;

        Children.Add(Build(MinimizeGlyph(), "Minimize", isClose: false,
            () => _window.WindowState = WindowState.Minimized));

        _maximize = Build(MaximizeGlyph(), "Maximize", isClose: false, ToggleMaximize);
        Children.Add(_maximize);

        Children.Add(Build(CloseGlyph(), "Close", isClose: true, () => _window.Close()));

        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) SyncMaximizeGlyph();
        };
    }

    private void ToggleMaximize()
        => _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void SyncMaximizeGlyph()
    {
        var maximized = _window.WindowState == WindowState.Maximized;
        _maximize.Content = maximized ? RestoreGlyph() : MaximizeGlyph();
        ToolTip.SetTip(_maximize, maximized ? "Restore Down" : "Maximize");
    }

    private static Button Build(Control glyph, string tip, bool isClose, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            Width = ButtonWidth,
            Height = ButtonHeight,
            Padding = default,
            BorderThickness = default,
            CornerRadius = default,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { isClose ? "caption-close" : "caption" },
        };

        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---- Glyphs ------------------------------------------------------------------------
    // Each sits in a 10x10 box so the three read as one set.

    private static Control MinimizeGlyph()
    {
        var line = new Rectangle
        {
            Width = GlyphBox,
            Height = StrokeWidth,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        BindFill(line);
        return Box(line);
    }

    private static Control MaximizeGlyph()
    {
        var square = new Rectangle
        {
            Width = GlyphBox,
            Height = GlyphBox,
            StrokeThickness = StrokeWidth,
            Fill = Brushes.Transparent,
        };
        BindStroke(square);
        return Box(square);
    }

    private static Control RestoreGlyph()
    {
        // Two offset squares: the back one clipped by the front, as Windows draws it.
        var back = new Rectangle
        {
            Width = GlyphBox - 2,
            Height = GlyphBox - 2,
            StrokeThickness = StrokeWidth,
            Fill = Brushes.Transparent,
            Margin = new Thickness(2, 0, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        BindStroke(back);

        var front = new Rectangle
        {
            Width = GlyphBox - 2,
            Height = GlyphBox - 2,
            StrokeThickness = StrokeWidth,
            Fill = Brushes.Transparent,
            Margin = new Thickness(0, 2, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        BindStroke(front);

        var stack = new Panel();
        stack.Children.Add(back);
        stack.Children.Add(front);
        return Box(stack);
    }

    private static Control CloseGlyph()
    {
        var canvas = new Panel();

        foreach (var angle in (double[])[45, -45])
        {
            var bar = new Rectangle
            {
                Width = GlyphBox * 1.35,
                Height = StrokeWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(angle),
            };
            BindFill(bar);
            canvas.Children.Add(bar);
        }

        return Box(canvas);
    }

    private static Control Box(Control content) => new Panel
    {
        Width = GlyphBox + 4,
        Height = GlyphBox + 4,
        Children = { content },
    };

    /// <summary>
    /// Glyphs follow their button's <see cref="TemplatedControl.Foreground"/> rather than
    /// binding a brush of their own. A brush set on the shape is a local value, and a local
    /// value cannot be overridden by a style — which is what silently defeated the close
    /// button's white-on-red hover.
    /// </summary>
    private static Binding FromButton() => new Binding(nameof(Button.Foreground))
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor)
        {
            AncestorType = typeof(Button),
        },
    };

    private static void BindFill(Shape shape) => shape[!Shape.FillProperty] = FromButton();

    private static void BindStroke(Shape shape) => shape[!Shape.StrokeProperty] = FromButton();
}
