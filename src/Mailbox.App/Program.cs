using System.Reflection;
using Avalonia;
using Mailbox.App.Diagnostics;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Before the log opens: --version is one line on standard output and nothing else, so a
        // script can read it. The installer does exactly that.
        if (args.Length > 0 && string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Mailbox {ThisAssembly.Stamp}");
            return 0;
        }

        Log.Initialize(ThisAssembly.Stamp);
        CrashHandler.Install();

        // `mailbox --export-theme <id> [path]` writes a built-in as a theme file and exits: the
        // starting point for a theme of one's own, and the documentation of what one is made of.
        if (args.Length >= 2 && string.Equals(args[0], "--export-theme", StringComparison.OrdinalIgnoreCase))
        {
            return ExportTheme(args[1], args.Length > 2 ? args[2] : null);
        }

        // `mailbox --import-theme <file>` reads a browser theme — an .xpi, a zip, an unpacked
        // directory or a bare manifest.json — writes it into the themes directory, and exits;
        // the watcher of a running instance picks it up live.
        if (args.Length >= 2 && string.Equals(args[0], "--import-theme", StringComparison.OrdinalIgnoreCase))
        {
            return ImportTheme(args[1]);
        }

        // `mailbox --export-theme-pack <id> [path]` zips a user theme with its images — the
        // form a theme travels in when it carries more than colours.
        if (args.Length >= 2 && string.Equals(args[0], "--export-theme-pack", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pack = Mailbox.Theming.Import.ThemePack.Export(
                    args[1], Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory(), args.Length > 2 ? args[2] : null);
                Console.WriteLine($"Wrote {pack}. Anyone can install it with --import-theme, or by choosing it from Import… in Options.");
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

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

    private static int ExportTheme(string id, string? path)
    {
        try
        {
            var file = Mailbox.Theming.Files.ThemeLibrary.Export(id);
            var target = path ?? file.Id + Mailbox.Theming.Files.ThemeFileFormat.Extension;
            File.WriteAllText(target, Mailbox.Theming.Files.ThemeFileFormat.Write(file));
            Console.WriteLine($"Wrote {target} ({file.Tokens.Count} tokens). Put it in {Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory()} under a new id to make it yours.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Built-in themes: " + string.Join(", ", Mailbox.Theming.Themes.OfficeThemes.All));
            return 2;
        }
    }

    private static int ImportTheme(string path)
    {
        // The image half wants a decoder, which wants the rendering platform — and nothing
        // more: a bare Application, never Mailbox's own, whose startup would open the real
        // stores for what is a file-conversion command. A box where the platform cannot come
        // up — a bare build server — still imports the colours; the report says the image was
        // skipped rather than pretending.
        Mailbox.Theming.Import.ImageReencoder? reencode = null;
        try
        {
            AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
            reencode = Theming.ThemeImportDoor.Reencode;
        }
        catch (Exception ex)
        {
            Log.Warn($"No rendering platform for the import's image half: {ex.Message}");
        }

        try
        {
            var directory = Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory();
            var outcome = Mailbox.Theming.Import.ImportedThemes.Import(path, directory, reencode);
            foreach (var line in Mailbox.Theming.Import.ImportReport.Lines(outcome)) Console.WriteLine(line);
            return 0;
        }
        catch (Exception ex) when (ex is Mailbox.Theming.Import.BrowserThemeException
                                       or Mailbox.Theming.Files.ThemeFileException
                                       or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    internal static class ThisAssembly
    {
        public static string Version =>
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>
        /// The version, and when this build was made: "0.1.0 (built 2026-08-25 15:46)".
        /// </summary>
        /// <remarks>
        /// Every build of a working session carries the same version number, so the number alone
        /// cannot say whether what is running is the build that was just installed. The stamp
        /// can, and it is written to the log at startup and shown on the About panel.
        /// </remarks>
        public static string Stamp
        {
            get
            {
                var informational = typeof(Program).Assembly
                    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;

                // SourceLink appends the commit to the build metadata, so the stamp is the
                // part before it: "0.1.0+2026-08-25 15:51.<sha>" reads as 0.1.0 (built …15:51).
                return informational?.Split('+') is [var version, var built]
                    ? $"{version} (built {built.Split('.')[0]})"
                    : Version;
            }
        }
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
