using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Controls.Ribbon;

namespace Mailbox.App.Views;

/// <summary>
/// The frame a window has to draw for itself once the system's is turned off.
/// </summary>
/// <remarks>
/// Mailbox draws its own caption buttons, which means every window carrying them sets
/// <c>WindowDecorations = None</c> — and that takes the compositor's resize borders and its
/// title-bar drag with it. This puts both back.
/// <para>
/// Shared rather than written per window. A second copy is how one window ends up resizable
/// from three edges, and how the compose window ended up with two sets of caption buttons: its
/// own drawn over the system's, because the system's were never turned off.
/// </para>
/// </remarks>
internal static class WindowFrame
{
    /// <summary>How close to an edge the pointer has to be to start a resize.</summary>
    private const double ResizeMargin = 6;

    private static readonly (WindowEdge Edge, StandardCursorType Cursor)[] EdgeCursors =
    [
        (WindowEdge.NorthWest, StandardCursorType.TopLeftCorner),
        (WindowEdge.NorthEast, StandardCursorType.TopRightCorner),
        (WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner),
        (WindowEdge.SouthEast, StandardCursorType.BottomRightCorner),
        (WindowEdge.North, StandardCursorType.TopSide),
        (WindowEdge.South, StandardCursorType.BottomSide),
        (WindowEdge.West, StandardCursorType.LeftSide),
        (WindowEdge.East, StandardCursorType.RightSide),
    ];

    /// <summary>
    /// Turns off the system frame, makes the window transparent so it can draw its own rounded
    /// shape, and gives it its own resize edges.
    /// </summary>
    /// <remarks>
    /// The transparent background is half of the rounding and is easy to miss: the window is
    /// not the thing with corners — <see cref="Rounded"/> is — so anything the window itself
    /// paints shows as a square behind the curve. Leaving it opaque is precisely how a rounded
    /// window ends up with leftover corners.
    /// </remarks>
    internal static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // None rather than BorderOnly: the system frame is drawn square, and around a rounded
        // window it traced the curve with a hard right angle and a transparent wedge between.
        window.ExtendClientAreaToDecorationsHint = true;
        window.WindowDecorations = WindowDecorations.None;

        window.Background = Brushes.Transparent;
        window.TransparencyLevelHint =
            [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];

        window.PointerMoved += (_, e) =>
        {
            window.Cursor = EdgeAt(window, e.GetPosition(window)) is { } edge
                ? new Cursor(EdgeCursors.First(c => c.Edge == edge).Cursor)
                : Cursor.Default;
        };

        window.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (EdgeAt(window, e.GetPosition(window)) is not { } edge) return;

            e.Handled = true;
            window.BeginResizeDrag(edge, e);
        });
    }

    /// <summary>
    /// Wraps a window's content in the rounded, clipping surface that draws its shape.
    /// </summary>
    /// <remarks>
    /// The clip is the load-bearing half. The reference rounds a window's outer corners at the
    /// same radius as the panels inside it, and without clipping any child painting into a
    /// corner shows through as a wedge — the same failure the workspace's own corners had.
    /// The background belongs here rather than on the window, which is transparent.
    /// </remarks>
    /// <param name="background">
    /// Which token fills the shape. The shell's chrome by default; a dialog passes its own,
    /// because the two are opposite ends of the ramp in half the themes.
    /// </param>
    internal static Control Rounded(
        Control content, string background = "ribbon.tabstrip.background.brush")
    {
        ArgumentNullException.ThrowIfNull(content);

        var border = new Border
        {
            CornerRadius = new CornerRadius(RibbonMetrics.BodyCornerRadius),
            ClipToBounds = true,
            Child = content,

            // The hairline round the window, by class so one stylesheet rule owns it — and
            // drops it when the window is maximized, where there is no edge to tell apart.
            Classes = { "windowshape" },
        };
        border[!Border.BackgroundProperty] = new DynamicResourceExtension(background);

        return border;
    }

    /// <summary>Makes a control drag the window, and double-click toggle maximize.</summary>
    internal static void Drags(Window window, Control titleBar)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(titleBar);

        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;

            // A double-click on the bar toggles maximize, as every desktop expects.
            if (e.ClickCount == 2)
            {
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return;
            }

            window.BeginMoveDrag(e);
        };
    }

    /// <summary>
    /// The visible resize grip — the classic triangle of dots at a bottom-right corner. The
    /// window's edges already resize; the grip is the promise a reader can see, and the note
    /// window is the surface the reference draws one on.
    /// </summary>
    internal static Control Grip(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new ResizeGrip { Window = window };
    }

    private sealed class ResizeGrip : Control
    {
        public Window? Window { get; init; }

        public static readonly StyledProperty<IBrush?> InkProperty =
            AvaloniaProperty.Register<ResizeGrip, IBrush?>(nameof(Ink));

        public IBrush? Ink
        {
            get => GetValue(InkProperty);
            set => SetValue(InkProperty, value);
        }

        static ResizeGrip()
        {
            AffectsRender<ResizeGrip>(InkProperty);
        }

        public ResizeGrip()
        {
            Width = 14;
            Height = 14;
            Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        }

        public override void Render(DrawingContext context)
        {
            // Its own ink when one was set, else the text colour inherited from the surface it
            // sits on — which is how the note window tints it without reaching the private type.
            if ((Ink ?? GetValue(TextBlock.ForegroundProperty)) is not { } ink) return;

            // Every 4px cell on the corner's own diagonal or below it.
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    if (i + j < 2) continue;
                    context.FillRectangle(ink, new Rect(2 + (i * 4), 2 + (j * 4), 2, 2));
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (Window is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Window.BeginResizeDrag(WindowEdge.SouthEast, e);
        }
    }

    /// <summary>Which edge the pointer is over, or null when it is not near one.</summary>
    internal static WindowEdge? EdgeAt(Window window, Point p)
    {
        if (window.WindowState != WindowState.Normal) return null;

        var west = p.X <= ResizeMargin;
        var east = p.X >= window.Bounds.Width - ResizeMargin;
        var north = p.Y <= ResizeMargin;
        var south = p.Y >= window.Bounds.Height - ResizeMargin;

        return (north, south, west, east) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (true, _, _, true) => WindowEdge.NorthEast,
            (_, true, true, _) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.North,
            (_, true, _, _) => WindowEdge.South,
            (_, _, true, _) => WindowEdge.West,
            (_, _, _, true) => WindowEdge.East,
            _ => null,
        };
    }
}
