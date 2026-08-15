using Avalonia;
using Mailbox.App.Diagnostics;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Log.Initialize(ThisAssembly.Version);
        CrashHandler.Install();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Anything escaping the lifetime is fatal, but it still gets recorded properly
            // rather than printing a truncated trace to a terminal nobody was watching.
            Console.Error.WriteLine(Log.Crash("startup", ex));
            return 1;
        }
    }

    private static class ThisAssembly
    {
        public static string Version =>
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Called by the Avalonia designer and by <see cref="Main"/>.
    /// </summary>
    /// <remarks>
    /// Platform detection selects X11. The native Wayland backend replaces it only when
    /// <c>MAILBOX_WAYLAND=1</c> asks for it — see <see cref="WindowingBackend"/> for why X11
    /// stays the default and why the flag is strict.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        return WindowingBackend.Apply(builder);
    }
}
