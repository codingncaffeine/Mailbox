using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Mailbox.HeadlessTests;

/// <summary>
/// One headless Avalonia application for the whole assembly, and a way to run work on its UI
/// thread.
/// </summary>
/// <remarks>
/// Avalonia can be started exactly once per process and everything it owns — controls, styles,
/// the dispatcher — belongs to the thread that started it. So the platform is brought up on a
/// dedicated thread the first time anything asks, and every test hands its body to
/// <see cref="OnUiThread{T}"/> to run there.
/// <para>
/// <see cref="Mailbox.App.App"/> is used as the application type because the styles under test
/// are its own: <c>Shell.axaml</c> and the rest are merged by its XAML, and a control built under
/// a bare <c>Application</c> would resolve none of the tokens and prove nothing about what a
/// reader sees. Its <c>OnFrameworkInitializationCompleted</c> — which opens stores, starts
/// watchers and shows the shell — is never reached, because no lifetime is attached.
/// </para>
/// </remarks>
public static class HeadlessApp
{
    private static readonly object Gate = new();
    private static Thread? _ui;
    private static Dispatcher? _dispatcher;
    private static Exception? _startupFailure;

    /// <summary>Runs <paramref name="work"/> on the UI thread and returns what it produced.</summary>
    public static T OnUiThread<T>(Func<T> work)
    {
        Start();

        if (_startupFailure is not null)
        {
            throw new InvalidOperationException(
                "The headless Avalonia platform did not start; see the inner exception.",
                _startupFailure);
        }

        return _dispatcher!.Invoke(work, DispatcherPriority.Normal);
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread.</summary>
    public static void OnUiThread(Action work) => OnUiThread(() => { work(); return true; });

    private static void Start()
    {
        lock (Gate)
        {
            if (_ui is not null) return;

            var ready = new ManualResetEventSlim();

            _ui = new Thread(() =>
            {
                try
                {
                    AppBuilder.Configure<Mailbox.App.App>()
                        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                        .SetupWithoutStarting();

                    _dispatcher = Dispatcher.UIThread;
                }
                catch (Exception ex)
                {
                    _startupFailure = ex;
                }
                finally
                {
                    ready.Set();
                }

                // Only reached when startup worked: the dispatcher loop is what lets Invoke
                // marshal work here from a test thread.
                if (_startupFailure is null) Dispatcher.UIThread.MainLoop(CancellationToken.None);
            })
            {
                IsBackground = true,
                Name = "headless-ui",
            };

            // No apartment state: STA is a Windows concept and this application is X11/Wayland.
            _ui.Start();

            // Bounded: a platform that cannot start should fail the test that asked for it, not
            // hang the run.
            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                _startupFailure = new TimeoutException("Avalonia did not finish starting within 30 seconds.");
            }
        }
    }
}
