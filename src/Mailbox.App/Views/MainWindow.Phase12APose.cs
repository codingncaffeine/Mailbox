using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The doors Phase 12A needed: a message whose remote picture really can be fetched, and a report
/// of what the Trust Center's and Advanced page's switches are actually set to.
/// </summary>
/// <remarks>
/// <b>Why a delivered message rather than the seed's.</b> The audit's seed carries a newsletter
/// with two remote images on it, and both point at hosts that do not exist — which is right for
/// proving that nothing is fetched, and useless for proving that something is. "Don't download
/// pictures automatically in messages" is a switch whose off position means <em>a request goes
/// out and the picture appears</em>, and a host that never resolves cannot tell a request that
/// was made from one that was not. <c>MAILBOX_REMOTE_PICTURE=&lt;url&gt;</c> files a message
/// carrying one <c>img</c> at an address the caller controls — a local server — so both
/// directions are readable: the publisher's own request log says whether the fetch happened, and
/// the pane's inlined count says whether the picture arrived.
/// <para>
/// <b>And why the switches are logged.</b> Rule 2 of the audit's evidence: a capture run's
/// settings are a scratch copy, so what a run was posed with cannot be read out of the settings
/// file afterwards. The line below is that read-back — what this process believes, printed from
/// the same properties the behaviour asks.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's doors. Called once, from the constructor.</summary>
    private void WirePhase12ADoors()
    {
        // Before anything selects a row: the message has to be in the folder for the list to
        // draw it, and Loaded is the pass the folder pose runs in.
        if (Environment.GetEnvironmentVariable("MAILBOX_REMOTE_PICTURE") is { Length: > 0 } url)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => DeliverRemotePicture(url), DispatcherPriority.Send);
        }

        if (WindowCapture.IsRequested)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(ReportTrustSwitches, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// What the switches behind the Trust Center's and the Advanced page's rows hold in this run.
    /// </summary>
    /// <remarks>
    /// Read through the option objects rather than out of the store, so a row wired to the wrong
    /// key reads back wrong here too — which is the failure this line exists to catch. A settings
    /// file cannot be consulted instead: under a capture it is a scratch copy that dies with the
    /// process.
    /// </remarks>
    private static void ReportTrustSwitches()
    {
        Log.Info("Harness: trust switches — "
                 + $"block pictures {On(App.Security.BlockRemotePictures)}, "
                 + $"report hosts {On(App.Security.ReportTrackerHosts)}, "
                 + $"show authentication results {On(App.Security.ShowAuthenticationResults)}, "
                 + $"warn on display-name mismatch {On(App.Security.WarnDisplayNameMismatch)}, "
                 + $"warn on lookalike domains {On(App.MailOptions.WarnAboutSuspiciousDomains)}, "
                 + $"confirm permanent delete {On(App.MailOptions.ConfirmPermanentDelete)}.");

        static string On(bool value) => value ? "on" : "off";
    }

    /// <summary>
    /// Files a message carrying one remote picture into the open account's Inbox.
    /// </summary>
    /// <remarks>
    /// Through <see cref="MailRepository.AddMessage"/> with the real MIME beside the row, which is
    /// what a receiver writes — the pane parses the stored bytes, so a row without them renders
    /// its preview and blocks nothing at all. Invented start to finish, like every other fixture
    /// in this tree.
    /// </remarks>
    private void DeliverRemotePicture(string url)
    {
        if (DataContext is not ShellViewModel shell)
        {
            Log.Warn("Harness: remote picture — there is no shell to deliver into.");
            return;
        }

        // Into the account the run is going to open, not into whichever store sorts first: a
        // seeded run has three, and a message filed in the one the folder pose is about to leave
        // is a message no capture ever sees. MAILBOX_FOLDER names it as "address/Folder".
        var wanted = Environment.GetEnvironmentVariable("MAILBOX_FOLDER") ?? string.Empty;
        var slash = wanted.IndexOf('/', StringComparison.Ordinal);
        var address = slash > 0 ? wanted[..slash] : string.Empty;

        var account = App.Accounts.All.FirstOrDefault(a =>
                          string.Equals(a.Account.Address, address, StringComparison.OrdinalIgnoreCase))
                      ?? App.Accounts.All.FirstOrDefault();

        if (account is null)
        {
            Log.Warn("Harness: remote picture — no account is open.");
            return;
        }

        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox);
        if (inbox is null)
        {
            Log.Warn($"Harness: remote picture — {account.Account.Address} has no Inbox.");
            return;
        }

        var message = new MimeMessage
        {
            Subject = "A picture from elsewhere",
            Date = new DateTimeOffset(2026, 8, 16, 9, 15, 0, TimeSpan.Zero),
        };
        message.From.Add(new MailboxAddress("A. Publisher", "notices@example.net"));
        message.To.Add(new MailboxAddress("A. Person", account.Account.Address));
        message.MessageId = "harness-remote-picture@example.net";
        message.Body = new TextPart("html")
        {
            Text = $"""
                <html><body>
                <p>One picture, held at a distance.</p>
                <p><img src="{url}" width="16" height="16" alt="a picture"></p>
                </body></html>
                """,
        };

        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();

        var summary = new MessageSummary(
            0, inbox.Id, "harness-remote-picture", message.MessageId,
            "A. Publisher", "notices@example.net", message.Subject,
            "One picture, held at a distance.",
            message.Date, message.Date, raw.Length,
            IsRead: false, IsFlagged: false, HasAttachment: false);

        var id = account.Mail.AddMessage(inbox.Id, summary, raw);

        Log.Info(id is null
            ? "Harness: remote picture — the message was already in the Inbox."
            : $"Harness: remote picture — filed “{message.Subject}” into {account.Account.Address}/Inbox "
              + $"pointing at {url}.");

        shell.Refresh();
    }
}
