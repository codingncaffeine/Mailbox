using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.App.Views;

/// <summary>
/// The main window opens the size it was left at, where it was left, maximised if it was.
/// </summary>
/// <remarks>
/// It used to open at 1280×820 every time, whatever the reader had made of it. The state is
/// read once the window has rested for half a second rather than as each change arrives:
/// maximising reports the new size a moment before it reports being maximised, and reading in
/// that moment would remember the whole screen as the window's ordinary size. Closing writes at
/// once. A capture run is left alone: it poses the window at a size of its own.
/// </remarks>
internal static class ShellWindowMemory
{
    private static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Sizes and places the window as it was left and keeps track from now on. True when it was
    /// left maximised, which the caller applies: the shell is the one window that changes its
    /// own state other than through a caption button.
    /// </summary>
    public static bool Attach(Window window, SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);
        if (WindowCapture.IsRequested) return false;

        var kept = ShellWindowState.Parse(settings.GetString(ShellWindowState.Key));
        var maximized = kept is not null && Restore(window, kept);
        Remember(window, settings, kept);

        if (Environment.GetEnvironmentVariable(StepsVariable) is { Length: > 0 } steps)
        {
            window.Opened += async (_, _) => await StepAsync(window, steps);
        }

        return maximized;
    }

    /// <summary>
    /// Harness only: what the caption buttons do, in order — <c>max</c>, <c>normal</c>, <c>close</c>,
    /// and <c>wait:ms</c> between them — so a run can leave the window as a reader would and the
    /// next run can be asked how it came back. A compositor cannot do it from outside: the window
    /// draws its own caption, and KWin will neither maximise nor close it when a script asks.
    /// </summary>
    public const string StepsVariable = "MAILBOX_WINDOW_STEPS";

    private static async Task StepAsync(Window window, string steps)
    {
        foreach (var raw in steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (step, argument) = raw.Split(':', 2) is [var a, var b] ? (a, b) : (raw, string.Empty);
            Log.Info($"Harness: window step {raw} — now {window.WindowState}, "
                     + $"{window.ClientSize.Width:0}×{window.ClientSize.Height:0} at {window.Position.X},{window.Position.Y}; "
                     + $"enabled {window.IsEnabled}, owns [{string.Join(", ", window.OwnedWindows.Select(o => $"{o.GetType().Name} \"{o.Title}\" visible={o.IsVisible}"))}].");

            switch (step)
            {
                case "wait": await Task.Delay(int.TryParse(argument, out var ms) ? ms : 1000); break;
                // Through the caption's own button, as a pointer would press it.
                case "max" or "normal":
                    var caption = window.FindControl<ContentControl>("CaptionHost")?.Content as CaptionButtons;
                    if (caption?.Press(step == "max" ? "maximize" : "restore") != true)
                    {
                        Log.Warn($"Harness: window step {raw} — no caption button to press.");
                    }
                    break;

                // An empty profile opens Add Account over the window, modally, and a window
                // under a modal dialog can be neither resized nor maximised.
                case "dismiss":
                    foreach (var owned in window.OwnedWindows.ToList()) owned.Close();
                    break;

                case "close": window.Close(); return;
            }
        }
    }

    private static bool Restore(Window window, ShellWindowState kept)
    {
        var screens = window.Screens?.All
            .Select(s => (new DesktopArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height), s.Scaling))
            .ToList() ?? [];
        var fit = kept.Fit(screens, window.MinWidth, window.MinHeight);

        window.Width = fit.Width;
        window.Height = fit.Height;

        // Native Wayland neither tells an application where its window is nor lets it choose;
        // there the compositor places it, as it would any new window.
        if (fit.Place is { } place && !WindowingBackend.IsNativeWayland(window))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(place.X, place.Y);
        }

        Log.Info($"Main window: opening at {fit.Width:0}×{fit.Height:0}"
                 + (window.WindowStartupLocation == WindowStartupLocation.Manual ? $" at {fit.Place!.Value.X},{fit.Place.Value.Y}" : string.Empty)
                 + (fit.Maximized ? ", maximised." : "."));
        return fit.Maximized;
    }

    private static void Remember(Window window, SettingsStore settings, ShellWindowState? kept)
    {
        // The last ordinary state seen, which a maximised window goes on remembering underneath.
        var normal = kept;
        var written = kept?.Format();

        void Write()
        {
            var now = Read(window, normal);
            if (now is null) return;
            if (!now.Maximized) normal = now;

            var text = now.Format();
            if (text == written) return;
            settings.Set(ShellWindowState.Key, text);
            written = text;
            Log.Info($"Main window: kept {text} (width,height,x,y,maximised).");
        }

        var timer = new DispatcherTimer { Interval = Rest };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Write();
        };

        void Settle()
        {
            timer.Stop();
            timer.Start();
        }

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ClientSizeProperty || e.Property == Window.WindowStateProperty) Settle();
        };
        window.PositionChanged += (_, _) => Settle();
        window.Closing += (_, e) =>
        {
            if (e.Cancel) return;
            timer.Stop();
            Write();
            Log.Info($"Main window: closing as {written ?? "it opened"}.");
        };
    }

    /// <summary>The state the window is in now, or null for one not worth remembering — minimised, or full screen.</summary>
    private static ShellWindowState? Read(Window window, ShellWindowState? normal)
    {
        switch (window.WindowState)
        {
            case WindowState.Normal:
                (int, int)? place = WindowingBackend.IsNativeWayland(window) ? null : (window.Position.X, window.Position.Y);
                return new ShellWindowState(window.ClientSize.Width, window.ClientSize.Height, place, Maximized: false);

            case WindowState.Maximized:
                // Maximised over whatever ordinary size it had, so restoring it gives that back.
                return (normal ?? new ShellWindowState(window.Width, window.Height, null, false)) with { Maximized = true };

            default:
                return null;
        }
    }
}
