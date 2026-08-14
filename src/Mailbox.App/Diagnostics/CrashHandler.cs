using Avalonia.Threading;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Diagnostics;

/// <summary>
/// Catches what would otherwise kill the process silently.
/// </summary>
/// <remarks>
/// There are three separate ways a .NET UI application dies, and each needs its own hook:
/// an exception on the UI thread, an exception on a background thread, and a faulted task
/// nobody awaited. Only the first is recoverable — a click handler that throws should log and
/// leave the application standing rather than take the user's unsaved mail with it.
/// </remarks>
public static class CrashHandler
{
    private static int _uiFailures;

    /// <summary>Beyond this many UI exceptions the app is presumed unusable and gives up.</summary>
    private const int UiFailureLimit = 10;

    public static void Install()
    {
        // Background threads. Not recoverable — the runtime is already unwinding.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
            {
                Log.Crash("a background thread", error);
            }
            else
            {
                Log.Error($"Unhandled non-exception throw: {e.ExceptionObject}");
            }
        };

        // Faulted tasks nobody awaited. Observing them stops the finalizer escalating.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Crash("an unobserved task", e.Exception);
            e.SetObserved();
        };

        // The UI thread. This one is worth surviving.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var report = Log.Crash("the UI thread", e.Exception);

            if (++_uiFailures >= UiFailureLimit)
            {
                Log.Error($"Giving up after {_uiFailures} UI exceptions.");
                return;
            }

            // Handled, so the window stays up. A broken dialog should not cost the session.
            e.Handled = true;
            Console.Error.WriteLine(report);
        };

        Log.Debug("Crash handlers installed.");
    }
}
