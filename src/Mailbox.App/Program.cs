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

        // One instance per session: a second launch — a mailto: click while Mailbox is open —
        // hands its command line to the running one and exits, rather than starting a second
        // copy. Skipped for a capture run, where the fidelity harness deliberately starts many
        // instances at once and they must not collapse into one.
        if (!Theming.WindowCapture.IsRequested)
        {
            App.Instance = new Mailbox.Core.SingleInstance();
            if (App.Instance.TryHandOff(args)) return 0;

            // The reader's display choices — backend and scale — go into the environment the
            // platform reads, before it reads it. A capture run pins its own scale instead.
            try
            {
                var display = new Mailbox.Core.Platform.DisplaySettings(new Mailbox.Core.Settings.SettingsStore());
                if (display.ApplyToEnvironment(Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable) is { } applied)
                {
                    Log.Info(applied);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("The display settings could not be read; starting with the desktop's own.", ex);
            }
        }
        else
        {
            Theming.WindowCapture.PinLayoutScale();
        }

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
