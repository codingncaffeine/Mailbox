using System.Globalization;

namespace Mailbox.Core.Settings;

/// <summary>A rectangle on the desktop, in the pixels the window system places windows in.</summary>
public readonly record struct DesktopArea(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// Where the reader last left a window that keeps its place, and whether that place can still be
/// used.
/// </summary>
/// <remarks>
/// Kept as one string, <c>x,y</c>, so the two halves are written together or not at all. A place
/// is only as good as the screens it was on: a monitor unplugged or rearranged since leaves it
/// pointing at nothing, and a window put there opens where nobody can see it. So a place is used
/// only while enough of the window's caption lands on a screen to take hold of — the hundred
/// pixels KWin itself leaves showing of a window dragged off an edge — and otherwise the window
/// opens where it would have opened had it never been moved.
/// </remarks>
public static class WindowPlace
{
    /// <summary>How much of the caption has to be on a screen for the place to count.</summary>
    public const int GripWidth = 100;

    public static string Format(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"{x},{y}");

    /// <summary>The place a setting holds, or null when it holds none.</summary>
    public static (int X, int Y)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split(',');
        if (parts.Length != 2) return null;

        return int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
               && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y)
            ? (x, y)
            : null;
    }

    /// <summary>
    /// Where a window left at <paramref name="place"/> should open on the screens there are now,
    /// or null when that place is on none of them.
    /// </summary>
    /// <param name="place">The window's top-left corner, as it was left.</param>
    /// <param name="caption">The strip along the window's top that the reader takes hold of.</param>
    /// <param name="screens">Each screen's working area: what the panels leave of it.</param>
    /// <returns>
    /// The place, brought in so the whole caption is on the screen that holds the most of it —
    /// which is where a window manager would put it anyway, and where a desktop that does not
    /// would otherwise leave a window hanging off the edge.
    /// </returns>
    public static (int X, int Y)? Fit((int X, int Y) place, (int Width, int Height) caption, IEnumerable<DesktopArea> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);

        DesktopArea? best = null;
        long most = 0;

        foreach (var screen in screens)
        {
            var across = Math.Min(place.X + caption.Width, screen.Right) - Math.Max(place.X, screen.X);
            var down = Math.Min(place.Y + caption.Height, screen.Bottom) - Math.Max(place.Y, screen.Y);
            if (across < Math.Min(GripWidth, caption.Width) || down <= 0) continue;

            var overlap = (long)across * down;
            if (overlap <= most) continue;

            best = screen;
            most = overlap;
        }

        if (best is not { } area) return null;

        return (
            Math.Clamp(place.X, area.X, Math.Max(area.X, area.Right - caption.Width)),
            Math.Clamp(place.Y, area.Y, Math.Max(area.Y, area.Bottom - caption.Height)));
    }
}
