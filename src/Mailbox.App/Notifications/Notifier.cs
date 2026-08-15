using System.Diagnostics;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Notifications;

/// <summary>A button on a notification: the id handed back when it is pressed, and its label.</summary>
public sealed record NotificationAction(string Id, string Label)
{
    /// <summary>
    /// The id the notification server gives a click on the notification itself, rather than on
    /// one of its buttons. Servers that honour it do not draw a button for it.
    /// </summary>
    public const string Default = "default";
}

/// <summary>A desktop notification: what it says, what can be pressed on it, and what to do then.</summary>
public sealed record Notification(string Summary, string Body)
{
    public IReadOnlyList<NotificationAction> Actions { get; init; } = [];

    /// <summary>
    /// Called with the id of the action the reader chose, on a background thread — the caller
    /// marshals to the UI. Never called when the notification simply expired or was dismissed.
    /// </summary>
    public Action<string>? Activated { get; init; }

    /// <summary>
    /// Whether the server may drop the notification from its history once it has been shown.
    /// A count of new mail is worth a glance and not a log; a toast with buttons on it is worth
    /// keeping where the buttons can still be reached after the popup has gone.
    /// </summary>
    public bool Transient { get; init; } = true;
}

/// <summary>Shows a desktop notification, or does nothing where the desktop offers no way to.</summary>
public interface INotifier
{
    void Notify(Notification notification);
}

/// <summary>
/// A desktop notification over the freedesktop notification service, through <c>notify-send</c>.
/// </summary>
/// <remarks>
/// <c>notify-send</c> rather than a D-Bus binding, for the same reason credentials go through
/// <c>secret-tool</c>: it keeps the dependency out of the binary and works across every desktop
/// that implements the spec, which is all of them. Where it is not installed — a minimal session,
/// a container — the notification is quietly skipped rather than made an error, because a mail
/// client that cannot pop a toast is not a mail client that should fail to run.
/// <para>
/// Actions ride on <c>--action</c>, which makes the process wait until the notification is
/// closed and print the id of whatever was pressed. So a notification with buttons is a child
/// process for as long as it is on screen or in the server's history; each is watched on the
/// thread pool, never the UI thread, and killed if the server keeps it for longer than anyone
/// could still want to press it.
/// </para>
/// </remarks>
public sealed class DesktopNotifier : INotifier, IDisposable
{
    /// <summary>How long a notification with buttons is watched before it is given up on.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(30);

    private readonly List<Process> _waiting = [];
    private bool _available = true;

    public DesktopNotifier()
    {
        // The waiters are child processes and would outlive an exit that skipped Dispose — a
        // crash, or the harness's Environment.Exit — sitting on the session until every toast
        // they belong to is dismissed. Process exit is the last chance to take them along.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public void Notify(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!_available) return;

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "notify-send",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            start.ArgumentList.Add("--app-name=Mailbox");
            start.ArgumentList.Add("--icon=mailbox");
            start.ArgumentList.Add("--category=email.arrived");
            if (notification.Transient) start.ArgumentList.Add("--hint=int:transient:1");

            foreach (var action in notification.Actions)
            {
                start.ArgumentList.Add($"--action={action.Id}={action.Label}");
            }

            start.ArgumentList.Add(notification.Summary);
            start.ArgumentList.Add(notification.Body);

            var process = Process.Start(start);
            if (process is null)
            {
                _available = false;
                return;
            }

            if (notification.Actions.Count == 0)
            {
                // Nothing to wait for: the process exits as soon as the server has the toast.
                _ = process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
                return;
            }

            lock (_waiting) _waiting.Add(process);
            _ = WatchAsync(process, notification);
        }
        catch (Exception ex)
        {
            // Missing, or refused: note it once and stop trying, so a session without a
            // notification server does not shell out on every send/receive.
            Log.Info($"Desktop notifications are unavailable ({ex.Message}); they will be skipped.");
            _available = false;
        }
    }

    /// <summary>
    /// Waits for the reader's answer. <c>notify-send</c> prints the chosen action's id and exits
    /// when the notification closes; nothing printed means it expired or was dismissed.
    /// </summary>
    private async Task WatchAsync(Process process, Notification notification)
    {
        try
        {
            var read = process.StandardOutput.ReadToEndAsync();
            var finished = await Task.WhenAny(read, Task.Delay(Patience));

            if (!ReferenceEquals(finished, read))
            {
                Kill(process);
                return;
            }

            var chosen = (await read).Trim();
            await process.WaitForExitAsync();

            if (chosen.Length > 0) notification.Activated?.Invoke(chosen);
        }
        catch (Exception ex)
        {
            Log.Warn("A notification's answer could not be read.", ex);
        }
        finally
        {
            lock (_waiting) _waiting.Remove(process);
            process.Dispose();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception)
        {
            // Already gone, which is what was wanted.
        }
    }

    /// <summary>Stops watching. The toasts themselves stay with the server; their buttons go quiet.</summary>
    public void Dispose()
    {
        List<Process> pending;
        lock (_waiting)
        {
            pending = [.. _waiting];
            _waiting.Clear();
        }

        foreach (var process in pending) Kill(process);
    }
}
