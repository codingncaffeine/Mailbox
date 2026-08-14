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

    /// <summary>How long to let layout, fonts and the first render settle before capturing.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(900);

    public static string? RequestedPath => Environment.GetEnvironmentVariable(PathVariable);

    public static bool IsRequested => !string.IsNullOrWhiteSpace(RequestedPath);

    public static double Scale
        => double.TryParse(Environment.GetEnvironmentVariable(ScaleVariable), out var s) && s > 0
            ? s
            : 1.0;

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
