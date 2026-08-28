using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

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

    /// <summary>
    /// The full height of the 49px caption band, less the 1px the frame's own edge takes.
    /// </summary>
    /// <remarks>
    /// Measured off <c>close button.png</c>, where the hovered red runs y1..48 — the whole band.
    /// It was 44, which left the bottom five pixels bare title bar with <c>WindowFrame.Drags</c>
    /// on them: a press there began a window drag instead of pressing the button directly above
    /// it, and the glyphs sat three pixels high of where the reference draws them.
    /// </remarks>
    private const double ButtonHeight = 48;

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
    private readonly Button? _minimize;
    private readonly Button? _maximize;
    private readonly Button _close;

    /// <summary>
    /// The button a pose names. Minimize and maximize both wear the <c>caption</c> class, so
    /// they cannot be told apart by it — asking for the class and taking the first match is
    /// how <c>MAILBOX_HOVER=maximize</c> spent this project's life photographing the minimize
    /// button instead, at a size and shape close enough that nobody noticed.
    /// </summary>
    private Button? ButtonFor(string which) => which.ToLowerInvariant() switch
    {
        "minimize" => _minimize,
        "maximize" or "restore" => _maximize,
        "close" => _close,
        _ => null,
    };

    /// <summary>
    /// Forces a caption button into its hover state so the fidelity harness can photograph it.
    /// A screenshot cannot move the pointer, and the close button's red is the one caption
    /// colour that only exists on hover — it went unverified, and wrong, for two sessions.
    /// </summary>
    public bool ForceHover(string which)
    {
        if (ButtonFor(which) is not { } button) return false;
        ((IPseudoClasses)button.Classes).Add(":pointerover");
        return true;
    }

    /// <summary>
    /// Forces a caption button into its held state, which is a different token from the hover
    /// and was the half of the pair no capture could reach: every theme defines
    /// <c>titlebar.caption.pressed</c> and <c>titlebar.caption.close.pressed</c>, and until this
    /// existed nothing in the tree could photograph either.
    /// </summary>
    /// <remarks>
    /// Both pseudo-classes, because that is the real state: a pointer holding a button down is
    /// also over it, and the styles are written to layer that way.
    /// </remarks>
    public bool ForcePressed(string which)
    {
        if (ButtonFor(which) is not { } button) return false;
        ((IPseudoClasses)button.Classes).Add(":pointerover");
        ((IPseudoClasses)button.Classes).Add(":pressed");
        return true;
    }

    /// <summary>
    /// Clicks a caption button through its own <see cref="Button.ClickEvent"/> — the path a
    /// pointer takes — so a pose proves the button acts rather than that the window state can
    /// be assigned.
    /// </summary>
    public bool Press(string which)
    {
        if (ButtonFor(which) is not { } button) return false;
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>What the maximize button's tooltip reads, which is how its glyph is known.</summary>
    public string? MaximizeTip => _maximize is null ? null : ToolTip.GetTip(_maximize) as string;

    /// <summary>
    /// What a caption button is actually painted with: the brush on the button, and the brush on
    /// the content presenter inside it, which are two different answers.
    /// </summary>
    /// <remarks>
    /// A capture measures the pixel, and the pixel says which token won without saying why. The
    /// control theme's own <c>:pressed</c> rule paints <c>PART_ContentPresenter</c>, and a
    /// presenter with a background of its own covers the button's — so a style that sets the
    /// button's <see cref="TemplatedControl.Background"/> is drawn underneath and reads as if the
    /// token were never defined. Reporting both is what tells those two apart from one line of
    /// log rather than a session of guessing.
    /// </remarks>
    public string Describe(string which)
    {
        if (ButtonFor(which) is not { } button) return $"“{which}” is not a caption button";

        button.UpdateLayout();
        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();
        return $"{which}: classes [{string.Join(" ", button.Classes)}], "
               + $"button background {button.Background?.ToString() ?? "null"}, "
               + $"presenter background {presenter?.Background?.ToString() ?? "null"}, "
               + $"foreground {button.Foreground?.ToString() ?? "null"}";
    }

    /// <param name="dialog">
    /// True for a dialog: a shorter caption with a close button only, painted from the dialog
    /// tokens rather than the title bar's.
    /// </param>
    /// <param name="system">
    /// True for a system window — Account Settings, the Address Book and their children — whose
    /// caption is the desktop's light one in every theme: shorter again, and painted from the
    /// system dialog tokens.
    /// </param>
    /// <remarks>
    /// <paramref name="system"/> used to imply <paramref name="dialog"/>, on the grounds that
    /// every window in that family was a fixed dialog. The Address Book is not: the reference
    /// draws it with all three buttons and lets it be resized, so the two are independent now —
    /// system says which palette, dialog says which buttons.
    /// </remarks>
    public CaptionButtons(Window window, bool dialog = false, bool system = false)
    {
        _window = window;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Top;

        if (!dialog)
        {
            _minimize = Build(MinimizeGlyph(), "Minimize", isClose: false, dialog, system,
                () => _window.WindowState = WindowState.Minimized);
            Children.Add(_minimize);

            _maximize = Build(MaximizeGlyph(), "Maximize", isClose: false, dialog, system, ToggleMaximize);
            Children.Add(_maximize);

            _window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty) SyncMaximizeGlyph();
            };
        }

        _close = Build(CloseGlyph(), "Close", isClose: true, dialog, system, () => _window.Close());
        Children.Add(_close);
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
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
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
