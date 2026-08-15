using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

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

    /// <summary>Turns off the system frame and gives the window its own resize edges.</summary>
    internal static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // None rather than BorderOnly: the system frame is drawn square, and around a rounded
        // window it traced the curve with a hard right angle and a transparent wedge between.
        window.ExtendClientAreaToDecorationsHint = true;
        window.WindowDecorations = WindowDecorations.None;

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
