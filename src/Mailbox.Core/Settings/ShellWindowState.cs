using System.Globalization;

namespace Mailbox.Core.Settings;

/// <summary>
/// How the reader last left the main window: its size when it was neither maximised nor
/// minimised, where it stood then, and whether it was maximised.
/// </summary>
/// <remarks>
/// The reference opens where it was closed, at the size it was closed at, and so does every mail
/// client a reader is likely to come from. Kept as one setting, <c>width,height,x,y,maximized</c>,
/// so a half-written pair can never be read back; the place is empty where the window system
/// does not let an application know or choose it (native Wayland), and then only the size and
/// the maximised state come back.
/// <para>
/// The size is in device-independent pixels, which is what a window is sized in; the place is in
/// the window system's pixels, which is what a window is placed in.
/// </para>
/// </remarks>
public sealed record ShellWindowState(double Width, double Height, (int X, int Y)? Place, bool Maximized)
{
    /// <summary>The settings key.</summary>
    public const string Key = "window.shell";

    /// <summary>How tall the strip a reader takes hold of is, for deciding a place is reachable.</summary>
    public const double CaptionHeight = 32;

    public string Format()
    {
        var place = Place is { } p ? string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y}") : ",";
        return string.Create(CultureInfo.InvariantCulture, $"{Width:0},{Height:0},{place},{(Maximized ? 1 : 0)}");
    }

    /// <summary>What a setting holds, or null for nothing usable.</summary>
    public static ShellWindowState? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split(',');
        if (parts.Length != 5) return null;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || width < 1 || height < 1 || double.IsNaN(width) || double.IsNaN(height)
            || double.IsInfinity(width) || double.IsInfinity(height))
        {
            return null;
        }

        (int, int)? place = int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
                            && int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y)
            ? (x, y)
            : null;

        return new ShellWindowState(width, height, place, parts[4].Trim() == "1");
    }

    /// <summary>
    /// The state to open with on the screens there are now.
    /// </summary>
    /// <param name="screens">Each screen's working area, in the window system's pixels, and its scaling.</param>
    /// <param name="minWidth">The window's own minimum width.</param>
    /// <param name="minHeight">The window's own minimum height.</param>
    /// <remarks>
    /// A place is kept only while enough of the caption lands on a screen to take hold of — a
    /// monitor unplugged since would otherwise open the window where nobody can see it — and is
    /// then brought in to that screen's edges. The size is never larger than the working area of
    /// the screen the window opens on, nor smaller than the window allows. With no screens to go
    /// by, the state comes back as it was left.
    /// </remarks>
    public ShellWindowState Fit(IReadOnlyList<(DesktopArea Area, double Scaling)> screens, double minWidth, double minHeight)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0) return this with { Width = Math.Max(Width, minWidth), Height = Math.Max(Height, minHeight) };

        (DesktopArea Area, double Scaling)? on = null;
        (int X, int Y)? place = null;

        if (Place is { } left)
        {
            foreach (var screen in screens)
            {
                var caption = ((int)Math.Round(Width * screen.Scaling), (int)Math.Round(CaptionHeight * screen.Scaling));
                if (WindowPlace.Fit(left, caption, [screen.Area]) is not null)
                {
                    on = screen;
                    break;
                }
            }
        }

        // The screen it opens on: the one its place is on, else the largest, which is where a
        // window manager centring a new window would most likely put it.
        var target = on ?? screens.MaxBy(s => (long)s.Area.Width * s.Area.Height);
        var scale = target.Scaling > 0 ? target.Scaling : 1;

        var width = Math.Clamp(Width, minWidth, Math.Max(minWidth, target.Area.Width / scale));
        var height = Math.Clamp(Height, minHeight, Math.Max(minHeight, target.Area.Height / scale));

        if (on is not null && Place is { } kept)
        {
            // Brought in so the whole window is on its screen, now that its size is settled.
            var pixelWidth = (int)Math.Round(width * scale);
            var pixelHeight = (int)Math.Round(height * scale);
            place = (
                Math.Clamp(kept.X, target.Area.X, Math.Max(target.Area.X, target.Area.Right - pixelWidth)),
                Math.Clamp(kept.Y, target.Area.Y, Math.Max(target.Area.Y, target.Area.Bottom - pixelHeight)));
        }

        return new ShellWindowState(width, height, place, Maximized);
    }
}
