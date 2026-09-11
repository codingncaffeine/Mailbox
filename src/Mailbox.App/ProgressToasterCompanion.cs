using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Mailbox.App.Diagnostics;
using Mailbox.App.Views;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;

namespace Mailbox.App;

/// <summary>
/// The process the Send/Receive Progress dialog is shown from when the application is on native
/// Wayland: <c>mailbox --progress-toaster</c>, started and fed by <see cref="ProgressToaster"/>.
/// </summary>
/// <remarks>
/// It is the same dialog, drawn by the same class with the reader's theme; what is different is
/// only who the window belongs to. This process is an X11 client — platform detection on Linux
/// picks nothing else — and an X11 window can be mapped with a user time of zero, which KWin
/// reads as "explicitly asked not to get focus" and honours: the window comes up without taking
/// the keyboard. A native Wayland window cannot ask that at all. See <see cref="ProgressToaster"/>
/// for the whole of it.
/// <para>
/// It starts none of the application: no accounts, no stores, no keyring, no tray, no instance
/// socket. Just the fonts, the theme, the language and the one window, and the conversation with
/// the application on standard input and output.
/// </para>
/// </remarks>
internal static class ProgressToasterCompanion
{
    /// <summary>The command-line switch that makes a <c>mailbox</c> process this one.</summary>
    public const string Switch = "--progress-toaster";

    /// <summary>True in the toaster's process, where <see cref="App"/> starts nothing but the theme.</summary>
    public static bool IsRunning { get; private set; }

    private static readonly object WireGate = new();
    private static TextWriter? _wire;

    private static IClassicDesktopStyleApplicationLifetime? _lifetime;
    private static SendReceiveTasks? _tasks;
    private static SendReceiveProgressDialog? _dialog;
    private static PixelPoint _anchor;
    private static bool _ended;

    /// <summary>The process's whole life. Called from Main before the log is opened.</summary>
    public static int Run(string[] args)
    {
        IsRunning = true;

        // Standard output is the conversation with the application. The log writes there when it
        // has no file, so it is moved to standard error, which the application reads into its own
        // log. And the file is never opened: opening it rolls the application's logs along, and a
        // process started every half hour would roll the reader's history out of existence.
        _wire = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(Console.Error);
        CrashHandler.Install();

        try
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Log.Crash("the progress toaster", ex);
            return 1;
        }
    }

    /// <summary>Called by <see cref="App"/> once the theme is in place.</summary>
    public static void Start(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;

        // The box on the dialog writes to this process's copy of the settings, which goes nowhere;
        // the application is told, and it is the one that keeps it.
        App.Settings.Changed += (_, key) =>
        {
            if (key == SendReceiveProgressDialog.HideSetting)
            {
                Send(new ToasterMessage.HideChanged(App.Settings.GetBool(key)));
            }
        };

        new Thread(Listen) { IsBackground = true, Name = "Progress toaster wire" }.Start();
    }

    private static void Listen()
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput());
            while (input.ReadLine() is { } line)
            {
                if (ToasterWire.Decode(line) is { } message)
                {
                    Dispatcher.UIThread.Post(() => Handle(message));
                }
            }
        }
        catch (IOException)
        {
            // The same as the end of the stream: the application is not there to talk to.
        }

        // The application closed its end — it has quit, or it has crashed. Either way nobody is
        // left to report a run to, and a toaster outliving its application is an orphan window.
        Dispatcher.UIThread.Post(End);
    }

    private static void Handle(ToasterMessage message)
    {
        switch (message)
        {
            case ToasterMessage.Begin begin:
                _tasks = begin.Table();
                _anchor = new PixelPoint(begin.X, begin.Y);
                Send(new ToasterMessage.Ready());
                break;

            case ToasterMessage.Progress progress:
                _tasks?.Report(progress.Report);
                _dialog?.Refresh();
                break;

            case ToasterMessage.Finish finish:
                _tasks?.Finish(new SendReceiveResult(finish.Accounts));
                _dialog?.Refresh();
                break;

            case ToasterMessage.Show:
                Show();
                break;

            case ToasterMessage.Close:
                End();
                break;
        }
    }

    private static void Show()
    {
        if (_tasks is null || _dialog is not null || _ended) return;

        var dialog = new SendReceiveProgressDialog(_tasks, App.Settings, () => Send(new ToasterMessage.CancelAll()))
        {
            // The two properties the whole process exists for. Not activated: the X11 backend maps
            // the window with a user time of zero, and KWin will not give it the keyboard. Kept
            // above: it comes up over the windows the reader is working in, as it always has — and
            // still under a full-screen game, whose layer is above this one.
            ShowActivated = false,
            Topmost = true,

            // Over the middle of the screen the application is on. Nothing here can see the
            // application's window, which is on another display connection; its screen is the
            // nearest thing, and for a window that fills it, the same place.
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Position = _anchor,
        };

        // A capture run the application was started for puts its windows off-screen, and the
        // toaster it starts is one of them: a batch on the owner's desktop must not stack a
        // kept-above window over their work. Nothing here changes what the harness photographs.
        Theming.WindowCapture.HideWhileCapturing(dialog);

        dialog.Opened += (_, _) =>
        {
            Send(new ToasterMessage.Shown(dialog.PlatformImpl?.GetType().Namespace ?? "unknown"));
            CaptureIfAsked(dialog);
        };
        dialog.Closed += (_, _) =>
        {
            _dialog = null;
            End();
        };

        _dialog = dialog;
        dialog.Show();
    }

    /// <summary>
    /// The harness's photograph of the toaster: <c>MAILBOX_TOASTER_CAPTURE=&lt;png&gt;</c>, taken
    /// from the visual tree as every capture is, so it can be set against the in-process dialog's
    /// photograph of the same run. The claim under test is that moving the dialog into this process
    /// changed nothing anybody can see, and a diff of the two is that claim with a number on it.
    /// Two and a half seconds after the window opens, which is after the toaster door's report
    /// and before its run ends — the state <c>MAILBOX_PEEK=progress</c> photographs.
    /// </summary>
    private static void CaptureIfAsked(SendReceiveProgressDialog dialog)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_TOASTER_CAPTURE") is not { Length: > 0 } path) return;

        DispatcherTimer.RunOnce(
            () =>
            {
                try
                {
                    Theming.WindowCapture.Capture(dialog, path);
                    Log.Info($"Harness: toaster captured to {path} at {dialog.ClientSize.Width:0}x{dialog.ClientSize.Height:0}.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Harness: the toaster could not be captured ({ex.Message}).");
                }
            },
            TimeSpan.FromSeconds(2.5));
    }

    private static void End()
    {
        if (_ended) return;
        _ended = true;

        _dialog?.Close();
        Send(new ToasterMessage.Closed());
        _lifetime?.Shutdown();
    }

    private static void Send(ToasterMessage message)
    {
        lock (WireGate)
        {
            try
            {
                _wire?.WriteLine(ToasterWire.Encode(message));
            }
            catch (IOException)
            {
                // The application has gone. The reader thread sees the same and ends the process.
            }
        }
    }
}
