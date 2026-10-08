using System.Diagnostics;
using Mailbox.Core.Diagnostics;
using Xilium.CefGlue;

namespace Mailbox.App.Chromium;

/// <summary>
/// The Chromium that draws messages: started once per process, before the interface, and shut
/// down after it.
/// </summary>
/// <remarks>
/// Chromium ships inside Mailbox rather than coming from the system. The system web engines were
/// called by function name at run time with nothing checking in advance that the functions were
/// still there, and a distribution update that removed some (WPE WebKit 2.54 as Arch built it)
/// turned every message into an empty body. A copy we ship is the version we tested.
/// <para>
/// <b>The sandbox is not optional.</b> Mail is a stranger's markup, and the renderer is the part
/// of a browser that gets attacked. Chromium's helper processes are <c>mailbox-cef-helper</c>, a
/// native program of a few lines, because the Linux sandbox enters its namespaces with a call a
/// multi-threaded process is refused — and every .NET process is multi-threaded. Where the
/// sandbox cannot be had at all, Chromium is not started and messages render as text: Chromium
/// aborts the whole process when it finds no usable sandbox, so that is asked first, here.
/// </para>
/// <para>
/// JavaScript is off in every view, the network is closed to everything but the document being
/// shown, and nothing is kept: the cache lives in a folder made for this process and removed at
/// exit.
/// </para>
/// </remarks>
internal static class ChromiumRuntime
{
    /// <summary>The API version the helper and this binding agree on.</summary>
    internal const int ApiVersion = 15400;

    private static string? _root;

    /// <summary>Whether Chromium is up and views may be made.</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>Why it is not, in words for the log; null while it is.</summary>
    public static string? Unavailable { get; private set; } = "Chromium was not started.";

    /// <summary>The folder Chromium's files ship in, beside the application's own.</summary>
    private static string Directory => Path.Combine(AppContext.BaseDirectory, "chromium");

    private static string Helper => Path.Combine(Directory, "mailbox-cef-helper");

    /// <summary>
    /// Starts Chromium, or says why not. Never throws: a pane without Chromium renders text.
    /// </summary>
    public static void Start()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_NO_CHROMIUM") == "1")
        {
            Refuse("MAILBOX_NO_CHROMIUM=1 asked for text");
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            Refuse("this build carries Chromium for Linux only");
            return;
        }

        if (!File.Exists(Path.Combine(Directory, "libcef.so")) || !File.Exists(Helper))
        {
            Refuse("Chromium's files are not beside the application");
            return;
        }

        // Chromium will not sandbox itself as root and aborts the process rather than run
        // without one; mail read as root renders as text instead.
        if (Environment.UserName == "root" || geteuid() == 0)
        {
            Refuse("Chromium does not run as root");
            return;
        }

        if (SandboxProblem() is { } problem)
        {
            Refuse(problem);
            return;
        }

        if (DiedLastTime())
        {
            Refuse($"Chromium stopped Mailbox the last time it started; delete {Marker} to try again "
                   + "(this version will not try on its own)");
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
            File.WriteAllText(Marker, Program.ThisAssembly.Stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Chromium's start marker could not be written: {ex.Message}");
        }

        try
        {
            var watch = Stopwatch.StartNew();

            // The binding asks for "libcef" by name; it lives in Chromium's own folder, and the
            // resolver is the one place that says so. Load takes no path on Linux.
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                typeof(CefRuntime).Assembly,
                (name, _, _) => name == "libcef"
                    ? System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(Directory, "libcef.so"))
                    : IntPtr.Zero);
            CefRuntime.Load();

            _root = Path.Combine(Path.GetTempPath(), $"mailbox-chromium-{Environment.ProcessId}");
            System.IO.Directory.CreateDirectory(_root);

            var settings = new CefSettings
            {
                WindowlessRenderingEnabled = true,
                MultiThreadedMessageLoop = true,
                NoSandbox = false,
                BrowserSubprocessPath = Helper,
                ResourcesDirPath = Directory,
                LocalesDirPath = Path.Combine(Directory, "locales"),
                RootCachePath = _root,
                CachePath = string.Empty,
                PersistSessionCookies = false,
                CommandLineArgsDisabled = true,
                // MAILBOX_CHROMIUM_LOG=<file> is the debugging escape: everything Chromium says,
                // to a file that outlives the run. Otherwise warnings only, in a folder removed at exit.
                LogSeverity = Environment.GetEnvironmentVariable("MAILBOX_CHROMIUM_LOG") is { Length: > 0 }
                    ? CefLogSeverity.Verbose
                    : CefLogSeverity.Warning,
                LogFile = Environment.GetEnvironmentVariable("MAILBOX_CHROMIUM_LOG") is { Length: > 0 } wanted
                    ? wanted
                    : Path.Combine(_root, "chromium.log"),
                Locale = "en-US",
            };

            // argv[0] first: Chromium reads the program from it, and a .NET args array has none.
            var handlers = SignalHandlers.Save();
            try
            {
                CefRuntime.Initialize(new CefMainArgs([Helper]), settings, new MailboxCefApp(), IntPtr.Zero);
            }
            finally
            {
                var restored = handlers.Restore();
                if (restored > 0) Log.Info($"Chromium reset {restored} signal handler(s); .NET's are back.");
            }

            IsRunning = true;
            Unavailable = null;
            Log.Info($"Reading pane engine: Chromium {CefRuntime.ChromeVersion}, sandboxed, started in {watch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Refuse("Chromium would not start");
            Log.Warn("Chromium would not start; messages render as text.", ex);
        }
    }

    /// <summary>
    /// The start marker: written before Chromium starts, removed once it has drawn — or once
    /// the application ends normally, which it could not have done had Chromium aborted it.
    /// </summary>
    /// <remarks>
    /// The one failure nothing in this process can catch. Chromium aborts the whole application
    /// when its sandbox or a helper dies at start — a syscall filter that refuses one call is
    /// enough — and it would do so again at every start, so Mailbox would never open. A marker
    /// still there at the next start says that happened; that start renders text instead, and so
    /// does every start of the same version, until a new one or the reader deletes the file.
    /// </remarks>
    private static string Marker =>
        Path.Combine(Path.GetDirectoryName(Log.LogDirectory()) ?? Log.LogDirectory(), "chromium-starting");

    private static int _confirmed;

    private static bool DiedLastTime()
    {
        try
        {
            return File.Exists(Marker) && File.ReadAllText(Marker).Trim() == Program.ThisAssembly.Stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Chromium has drawn: it started, sandbox and all. Called from its paint thread.</summary>
    internal static void Confirmed()
    {
        if (Interlocked.Exchange(ref _confirmed, 1) == 1) return;
        ClearMarker();
    }

    private static void ClearMarker()
    {
        try
        {
            File.Delete(Marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Chromium's start marker could not be removed: {ex.Message}");
        }
    }

    /// <summary>Shuts Chromium down and removes its folder. Safe to call when it never started.</summary>
    public static void Stop()
    {
        if (IsRunning)
        {
            ClearMarker();
            IsRunning = false;
            try
            {
                CefRuntime.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Debug($"Chromium did not shut down cleanly: {ex.Message}");
            }
        }

        if (_root is { } root)
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"Chromium's folder could not be removed: {ex.Message}");
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();

    private static void Refuse(string why)
    {
        IsRunning = false;
        Unavailable = why;
        Log.Info($"Reading pane engine: none — {why}; messages render as text.");
    }

    /// <summary>
    /// Whether Chromium can sandbox its helpers here; null when it can, the reason when not.
    /// </summary>
    /// <remarks>
    /// The sandbox lives in user, PID and network namespaces of its own. Most desktops allow them;
    /// Ubuntu 24.04 allows them only to a program its AppArmor profile names, which the .deb
    /// installs. The helper walks the sandbox's own steps and answers, because only it knows what
    /// its own profile allows — and creating the namespace is not enough of a test, since Ubuntu
    /// permits that and refuses what follows. Chromium's other way in, a setuid helper, is not
    /// shipped: no package sets it up, and the launcher's NoNewPrivileges would defeat it anyway.
    /// </remarks>
    private static string? SandboxProblem()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(Helper, "--mailbox-probe-sandbox")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (probe is null) return "Chromium's helper would not run";
            if (!probe.WaitForExit(5_000))
            {
                probe.Kill();
                return "Chromium's helper did not answer";
            }

            return probe.ExitCode == 0
                ? null
                : "Chromium's sandbox is not available here (its helper may not make the user "
                  + "namespaces it runs in — on Ubuntu, the .deb's AppArmor profile allows them)";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Chromium's helper would not run ({ex.Message})";
        }
    }
}

/// <summary>The browser process's half of Chromium's application: the switches it starts with.</summary>
internal sealed class MailboxCefApp : CefApp
{
    protected override void OnBeforeCommandLineProcessing(string processType, CefCommandLine commandLine)
    {
        if (!string.IsNullOrEmpty(processType)) return;

        // No display connection of its own: every frame comes to the pane as pixels, so the
        // same Chromium works under X11, Wayland and nothing at all.
        commandLine.AppendSwitch("ozone-platform", "headless");

        // Software drawing: one message at a time needs no GPU, and the GPU process is the
        // part of Chromium most likely to meet a driver it disagrees with.
        commandLine.AppendSwitch("disable-gpu");
        commandLine.AppendSwitch("disable-gpu-compositing");
        commandLine.AppendSwitch("enable-begin-frame-scheduling");

        // Nothing asks the desktop for anything: no keyring for passwords Chromium will never
        // see, no spelling service, no background networking of its own.
        commandLine.AppendSwitch("password-store", "basic");
        commandLine.AppendSwitch("disable-background-networking");
        commandLine.AppendSwitch("disable-component-update");
        commandLine.AppendSwitch("disable-sync");
        commandLine.AppendSwitch("disable-extensions");
        commandLine.AppendSwitch("disable-spell-checking");
        commandLine.AppendSwitch("no-pings");
        commandLine.AppendSwitch("mute-audio");
    }
}

/// <summary>
/// The process's signal handlers, as they stood before Chromium started.
/// </summary>
/// <remarks>
/// Chromium resets every signal to its default as it initialises. One of them is SIGCHLD, which
/// is how .NET learns that a program it started has finished: with it gone, every child's output
/// still arrives and its exit is never reported, so every wait runs to its timeout. That is what
/// broke send/receive in 0.6.7 — each password lookup through secret-tool "timed out" after its
/// ten seconds although the keyring had answered at once, and the accounts signed in with no
/// password. It needed only one child to have been started before Chromium (the sandbox check
/// starts one), so the handlers are taken back afterwards whatever ran first. Chromium itself
/// waits for its own helpers by their ids and does not need them.
/// </remarks>
internal sealed class SignalHandlers
{
    // Signals 1-31; SIGKILL and SIGSTOP cannot have handlers. A struct sigaction is under 160
    // bytes on every Linux; it is copied, never read, apart from its first word, the handler.
    private const int Size = 256;
    private readonly byte[]?[] _saved = new byte[32][];

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int sigaction(int signal, byte[]? action, byte[]? previous);

    public static SignalHandlers Save()
    {
        var handlers = new SignalHandlers();
        for (var signal = 1; signal < 32; signal++)
        {
            if (signal is 9 or 19) continue;
            var buffer = new byte[Size];
            if (sigaction(signal, null, buffer) == 0) handlers._saved[signal] = buffer;
        }

        return handlers;
    }

    /// <summary>Puts back every handler that changed. Returns how many did.</summary>
    public int Restore()
    {
        var restored = 0;
        for (var signal = 1; signal < 32; signal++)
        {
            if (_saved[signal] is not { } before) continue;

            var now = new byte[Size];
            if (sigaction(signal, null, now) != 0) continue;
            if (BitConverter.ToInt64(before, 0) == BitConverter.ToInt64(now, 0)) continue;

            if (sigaction(signal, before, null) == 0) restored++;
        }

        return restored;
    }
}
