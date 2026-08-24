using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Mailbox.App.Theming;

/// <summary>
/// Renders the window to a PNG from inside the application.
/// </summary>
/// <remarks>
/// This is the capture half of the fidelity harness. Doing it in-process rather than through a
/// desktop screenshot tool matters for three reasons: it works headlessly in CI, it is
/// unaffected by compositors and portal permission prompts, and it captures at an exact
/// requested size and scale so a diff against a reference is meaningful rather than
/// approximate.
/// <para>
/// Usage: <c>MAILBOX_CAPTURE=/path/out.png mailbox</c> renders once and exits. Combine with
/// <c>MAILBOX_THEME</c> and <c>MAILBOX_CAPTURE_SCALE</c> to sweep every theme at every DPI.
/// </para>
/// </remarks>
public static class WindowCapture
{
    public const string PathVariable = "MAILBOX_CAPTURE";
    public const string ScaleVariable = "MAILBOX_CAPTURE_SCALE";
    public const string SizeVariable = "MAILBOX_SIZE";

    /// <summary>How long to let layout, fonts and the first render settle before capturing.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// Where a window being photographed is put: far enough off any desktop to not be seen.
    /// </summary>
    /// <remarks>
    /// A capture still has to open a real window — the renderer walks a live visual tree, and
    /// nothing is laid out until a top level exists. What it does not have to do is appear.
    /// Sweeping a dozen themes used to flash a dozen windows across whatever the owner was
    /// working on, which is distracting enough to stop people running the harness.
    /// <para>
    /// Off-screen rather than transparent: opacity is applied while rendering, so a window
    /// hidden that way photographs as nothing at all.
    /// </para>
    /// </remarks>
    private static readonly PixelPoint OffScreen = new(-32000, -32000);

    public static string? RequestedPath => Environment.GetEnvironmentVariable(PathVariable);

    public static bool IsRequested => !string.IsNullOrWhiteSpace(RequestedPath);

    /// <summary>
    /// Set by whoever is about to photograph a different window, so the main window's own
    /// capture stands down rather than racing it to the file and the shutdown.
    /// </summary>
    public static bool AnotherWindowWillBeCaptured { get; set; }

    public static double Scale
        => double.TryParse(Environment.GetEnvironmentVariable(ScaleVariable), out var s) && s > 0
            ? s
            : 1.0;

    /// <summary>
    /// Lays the window out at the capture's scale, whatever the X server says the screen's is.
    /// Call before the platform initialises.
    /// </summary>
    /// <remarks>
    /// The X11 backend takes its scaling from <c>Xft.dpi</c> — 100 on the machine this was
    /// found on, so the window is laid out at 1.0417 on a device grid of 0.96 logical pixels,
    /// and a capture that then renders at 1× puts every edge on a fraction: a panel's top row 20%
    /// covered, a hairline as two grey rows. Every measurement against a 100% reference has been
    /// a pixel noisy for that reason. Naming a screen that does not exist in
    /// <c>AVALONIA_SCREEN_SCALE_FACTORS</c> is what makes the backend stop consulting
    /// <c>Xft.dpi</c> (any other screen falls back to the global factor), and the global factor
    /// then carries the capture's own scale — so <c>MAILBOX_CAPTURE_SCALE=1.5</c> is a real 1.5×
    /// layout rendered at 1.5×, not a 1.04× layout blown up. Interactive runs are untouched: what
    /// the app should make of <c>Xft.dpi</c> on screen is the HiDPI item's question.
    /// </remarks>
    public static void PinLayoutScale()
    {
        if (!IsRequested) return;

        Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", "mailbox-capture=1");
        Environment.SetEnvironmentVariable(
            "AVALONIA_GLOBAL_SCALE_FACTOR",
            Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Poses the window at an exact size, given as <c>MAILBOX_SIZE=1024x820</c>.
    /// </summary>
    /// <remarks>
    /// Anything that responds to width — ribbon group collapse, the splitters, the search box's
    /// alignment — can only be checked at a width the harness chose. Dragging a window by hand
    /// is not a measurement anyone can repeat, and the collapse ladder in particular has a
    /// different answer every hundred pixels.
    /// <para>
    /// Applied on an interactive run too, so a width can be eyeballed before it is photographed.
    /// </para>
    /// </remarks>
    public static void ApplyRequestedSize(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var requested = Environment.GetEnvironmentVariable(SizeVariable);
        if (string.IsNullOrWhiteSpace(requested)) return;

        var parts = requested.Split('x', 'X', '*', ',');
        if (parts.Length != 2
            || !double.TryParse(parts[0], out var width)
            || !double.TryParse(parts[1], out var height)
            || width <= 0
            || height <= 0)
        {
            Console.Error.WriteLine($"{SizeVariable}='{requested}' is not WIDTHxHEIGHT; ignoring.");
            return;
        }

        // Below the window's own minimum the request cannot be honoured, and silently getting a
        // different size than the one asked for is how a fidelity measurement goes wrong.
        if (width < window.MinWidth || height < window.MinHeight)
        {
            Console.Error.WriteLine(
                $"{SizeVariable}='{requested}' is below the window minimum of " +
                $"{window.MinWidth}x{window.MinHeight}; clamping.");
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = Math.Max(width, window.MinWidth);
        window.Height = Math.Max(height, window.MinHeight);
    }

    /// <summary>
    /// Keeps the capture from shooting and exiting while a pose's work is still running.
    /// </summary>
    /// <remarks>
    /// The settle delay is for layout, and it is 900ms because layout is; a pose that computes —
    /// key generation is seconds of RSA — is not layout, and without this the run photographs
    /// whatever the window held when the timer went off and then exits under the work. Taken on
    /// the dispatcher thread in the same pass the pose starts, so the timer cannot beat it.
    /// </remarks>
    public static IDisposable Hold()
    {
        Interlocked.Increment(ref _holds);
        return new Released();
    }

    private static int _holds;

    private static bool Held => Volatile.Read(ref _holds) > 0;

    /// <summary>Waits out any holds, checking gently — a pose's work is not a spin target.</summary>
    internal static async Task WhileHeldAsync()
    {
        while (Held) await Task.Delay(100);
    }

    private sealed class Released : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _holds);
        }
    }

    /// <summary>
    /// Captures once the window has settled, then shuts the application down. Wired only when
    /// <see cref="IsRequested"/>, so it never affects an interactive run.
    /// </summary>
    public static void AttachTo(Window window, Action shutdown)
    {
        if (RequestedPath is not { } path) return;

        HideWhileCapturing(window);

        window.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(SettleDelay);
                await WhileHeldAsync();

                // A harness pose that opens another window photographs that one instead, and
                // says so before this timer is up. Two captures racing to one path and one exit
                // is how a run photographs the wrong window and calls it done.
                if (AnotherWindowWillBeCaptured) return;

                await Dispatcher.UIThread.InvokeAsync(() => Capture(window, path, Scale));
                Console.WriteLine($"Captured {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Capture failed: {ex.Message}");
            }
            finally
            {
                if (!AnotherWindowWillBeCaptured) shutdown();
            }
        };
    }

    /// <summary>
    /// Puts a window somewhere it will not be seen, for a run that only wants a picture of it.
    /// </summary>
    /// <remarks>
    /// Called for every window the harness photographs, including the dialogs it opens on the
    /// way. Nothing here changes what is rendered — the position is where the compositor puts
    /// the surface, and the bitmap comes from the visual tree either way.
    /// </remarks>
    public static void HideWhileCapturing(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!IsRequested) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.Position = OffScreen;

        // Set again once it exists: a window manager may place it where it likes on mapping,
        // and the position asked for before that is a request rather than a fact.
        window.Opened += (_, _) =>
        {
            window.Position = OffScreen;
            SizeFromContent(window);
        };
    }

    /// <summary>
    /// A SizeToContent window off-screen measures against no screen at all — the platform's
    /// auto-size hint is nothing there — and comes out a pixel high. Its content measured on
    /// its own is the height it would have had; that becomes an explicit height, and the
    /// window is laid out and photographed at it.
    /// </summary>
    public static bool SizeFromContent(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.SizeToContent == SizeToContent.Manual || window.Content is not Control content) return false;

        var width = window.Width is > 1 and not double.NaN ? window.Width
            : window.ClientSize.Width > 1 ? window.ClientSize.Width : 640;
        content.Measure(new Size(width, double.PositiveInfinity));
        var desired = content.DesiredSize;
        if (desired.Height <= 1) return false;

        window.SizeToContent = SizeToContent.Manual;
        window.Width = width;
        window.Height = Math.Ceiling(desired.Height);
        window.InvalidateMeasure();
        return true;
    }

    public static void Capture(Window window, string path, double scale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Anything posed after the window opened has invalidated measure but may not have been
        // through a layout pass yet, and the renderer will happily photograph the stale
        // arrangement — a field made visible shows up while the panel holding it is still its
        // old height, so whatever was below is clipped away.
        window.UpdateLayout();

        var size = window.ClientSize;

        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException(
                "Window has no client size yet; capture ran before the first layout pass.");
        }

        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Round(size.Width * scale)),
            Math.Max(1, (int)Math.Round(size.Height * scale)));

        var dpi = new Vector(96 * scale, 96 * scale);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var bitmap = new RenderTargetBitmap(pixelSize, dpi);
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
