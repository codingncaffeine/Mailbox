using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.App.Views;

/// <summary>
/// Where the Send/Receive Progress dialog comes up: where the reader last put it.
/// </summary>
/// <remarks>
/// Only where a window can be put anywhere at all, which is X11 — the toaster process always, and
/// the application itself when it runs on X11. A native Wayland window is placed by the compositor
/// and is never told where it went, so there is nothing to keep and nowhere to put it back; the
/// place kept is therefore always in the X server's own pixels, the ones it is used in.
/// </remarks>
internal static class ProgressPlacement
{
    /// <summary>
    /// How long after the window opens a move is still the window manager placing it rather than
    /// the reader moving it. The window manager's adjustments come as it maps the window; a reader
    /// cannot have found the caption and dragged it by then, and a drag that began sooner is
    /// still moving after.
    /// </summary>
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(1);

    /// <summary>How long a moved window has to stay put before its place is kept.</summary>
    private static readonly TimeSpan AtRest = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Puts the dialog where the reader left it, if that is still on a screen. False when it is
    /// not, or there is no such place yet, and the caller's own placement stands.
    /// </summary>
    public static bool Restore(Window dialog, SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(settings);

        if (WindowPlace.Parse(settings.GetString(SendReceiveProgressDialog.PlaceSetting)) is not { } left) return false;

        var screens = dialog.Screens;
        var scaling = screens.ScreenFromPoint(new PixelPoint(left.X, left.Y))?.Scaling
                      ?? screens.Primary?.Scaling
                      ?? 1;
        var caption = (
            (int)Math.Ceiling(dialog.Width * scaling),
            (int)Math.Ceiling(DialogChrome.TitleBarHeight * scaling));
        var areas = screens.All.Select(s => new DesktopArea(
            s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height));

        if (WindowPlace.Fit(left, caption, areas) is not { } place)
        {
            Log.Info($"Send/receive progress: the place it was left at, {left.X},{left.Y}, is on no screen now, so it opens where it would have.");
            return false;
        }

        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(place.X, place.Y);
        return true;
    }

    /// <summary>
    /// Hands <paramref name="moved"/> each place the reader moves the dialog to, once it has come
    /// to rest there — and the last one when the dialog closes before it has.
    /// </summary>
    public static void Remember(Window dialog, Action<PixelPoint> moved)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(moved);

        Stopwatch? open = null;
        PixelPoint? waiting = null;
        IDisposable? rest = null;

        void Keep()
        {
            rest?.Dispose();
            rest = null;

            if (waiting is not { } place) return;
            waiting = null;
            moved(place);
        }

        dialog.Opened += (_, _) => open = Stopwatch.StartNew();

        dialog.PositionChanged += (_, e) =>
        {
            if (open is null || open.Elapsed < Settling) return;

            waiting = e.Point;
            rest?.Dispose();
            rest = DispatcherTimer.RunOnce(Keep, AtRest);
        };

        dialog.Closed += (_, _) => Keep();
    }
}
