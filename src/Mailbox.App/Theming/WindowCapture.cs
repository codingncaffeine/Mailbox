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

    public static string? RequestedPath => Environment.GetEnvironmentVariable(PathVariable);

    public static bool IsRequested => !string.IsNullOrWhiteSpace(RequestedPath);

    public static double Scale
        => double.TryParse(Environment.GetEnvironmentVariable(ScaleVariable), out var s) && s > 0
            ? s
            : 1.0;

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
    /// Captures once the window has settled, then shuts the application down. Wired only when
    /// <see cref="IsRequested"/>, so it never affects an interactive run.
    /// </summary>
    public static void AttachTo(Window window, Action shutdown)
    {
        if (RequestedPath is not { } path) return;

        window.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(SettleDelay);
                await Dispatcher.UIThread.InvokeAsync(() => Capture(window, path, Scale));
                Console.WriteLine($"Captured {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Capture failed: {ex.Message}");
            }
            finally
            {
                shutdown();
            }
        };
    }

    public static void Capture(Window window, string path, double scale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

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
