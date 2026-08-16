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

    /// <summary>
    /// A dialog's caption is shorter and carries only the close button, as the reference's
    /// dialogs do — there is nothing useful about minimizing a modal.
    /// </summary>
    private const double DialogButtonWidth = 46;
    private const double DialogButtonHeight = 33;

    /// <summary>
    /// A system dialog's caption is the desktop's own: 30px, measured off the Account Settings
    /// capture, with the cross standing 15px in from the window's right edge — the desktop's
    /// button reaches out under an invisible resize border the capture does not show.
    /// </summary>
    private const double SystemButtonHeight = 30;
    private const double SystemGlyphInset = 8;

    private const double GlyphBox = 10;
    private const double StrokeWidth = 1;

    private readonly Window _window;
    private readonly Button? _maximize;

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

    /// <param name="dialog">
    /// True for a dialog: a shorter caption with a close button only, painted from the dialog
    /// tokens rather than the title bar's.
    /// </param>
    /// <param name="system">
    /// True for a system dialog — Account Settings and its children — whose caption is the
    /// desktop's light one in every theme: shorter again, and painted from the system dialog
    /// tokens. Implies <paramref name="dialog"/>.
    /// </param>
    public CaptionButtons(Window window, bool dialog = false, bool system = false)
    {
        _window = window;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Top;
        dialog |= system;

        if (!dialog)
        {
            Children.Add(Build(MinimizeGlyph(), "Minimize", isClose: false, dialog, system,
                () => _window.WindowState = WindowState.Minimized));

            _maximize = Build(MaximizeGlyph(), "Maximize", isClose: false, dialog, system, ToggleMaximize);
            Children.Add(_maximize);

            _window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty) SyncMaximizeGlyph();
            };
        }

        Children.Add(Build(CloseGlyph(), "Close", isClose: true, dialog, system, () => _window.Close()));
    }

    private void ToggleMaximize()
        => _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void SyncMaximizeGlyph()
    {
        if (_maximize is null) return;

        var maximized = _window.WindowState == WindowState.Maximized;
        _maximize.Content = maximized ? RestoreGlyph() : MaximizeGlyph();
        ToolTip.SetTip(_maximize, maximized ? "Restore Down" : "Maximize");
    }

    private static Button Build(
        Control glyph, string tip, bool isClose, bool dialog, bool system, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            Width = dialog ? DialogButtonWidth : ButtonWidth,
            Height = system ? SystemButtonHeight : dialog ? DialogButtonHeight : ButtonHeight,
            Padding = default,
            BorderThickness = default,
            CornerRadius = default,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { isClose ? "caption-close" : "caption" },
        };

        // A dialog's caption sits on the dialog's ground, not the title bar's, and in two of
        // the four themes those are opposite ends of the ramp.
        if (dialog) button.Classes.Add("on-dialog");

        // A system dialog's cross is further right than centred, as the desktop draws it.
        if (system)
        {
            button.Classes.Add("on-system");
            button.HorizontalContentAlignment = HorizontalAlignment.Right;
            button.Padding = new Thickness(0, 0, SystemGlyphInset, 1);
        }

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
