using System.Diagnostics;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Notifications;

/// <summary>Shows a desktop notification, or does nothing where the desktop offers no way to.</summary>
public interface INotifier
{
    void Notify(string summary, string body);
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
/// The transient hint asks the server not to keep the notification in its history: "3 new
/// messages" is worth a glance, not a log the reader has to clear later.
/// </para>
/// </remarks>
public sealed class DesktopNotifier : INotifier
{
    private bool _available = true;

    public void Notify(string summary, string body)
    {
        if (!_available) return;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList =
                {
                    "--app-name=Mailbox",
                    "--icon=mailbox",
                    "--category=email.arrived",
                    "--hint=int:transient:1",
                    summary,
                    body,
                },
                UseShellExecute = false,
                RedirectStandardError = true,
            });

            process?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            // Missing, or refused: note it once and stop trying, so a session without a
            // notification server does not shell out on every send/receive.
            Log.Info($"Desktop notifications are unavailable ({ex.Message}); they will be skipped.");
            _available = false;
        }
    }
}
