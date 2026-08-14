using Avalonia;

namespace Mailbox.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Called by the Avalonia designer and by <see cref="Main"/>.
    /// </summary>
    /// <remarks>
    /// X11 is the default rather than Wayland. Avalonia 12.1's native Wayland backend has
    /// graduated from private preview but is still opt-in and experimental, so it sits behind
    /// <c>MAILBOX_WAYLAND=1</c> until it settles. XWayland covers the gap meanwhile.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        return builder;
    }
}
