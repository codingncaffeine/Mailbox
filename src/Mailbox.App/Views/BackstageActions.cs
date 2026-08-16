using Avalonia.Controls;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// What the Backstage's buttons do, independent of which window opened it.
/// </summary>
/// <remarks>
/// File does the same thing from a compose window as from the shell — it simply takes over the
/// window it was opened from. That is only true if there is one implementation behind it; two
/// would drift, and the compose window's would be the one that quietly lagged.
/// <para>
/// The window supplies three things it alone knows: how to say something (its status line),
/// how to reload after a change, and how to put the Backstage away.
/// </para>
/// </remarks>
internal sealed record BackstageHost(
    Window Owner, Action<string> Report, Action Refresh, Action Close);

internal static class BackstageActions
{
    /// <summary>Runs one of the Backstage's actions against whichever window opened it.</summary>
    internal static async Task RunAsync(BackstageHost host, string action)
    {
        ArgumentNullException.ThrowIfNull(host);

        switch (action)
        {
            case "account.settings":
            {
                var dialog = new AccountSettingsDialog();
                await dialog.ShowDialog(host.Owner);
                if (dialog.Changed) { host.Close(); host.Refresh(); }
                break;
            }

            case "account.password":
                if (RequireAccount(host) is { } forPassword)
                {
                    await new UpdatePasswordDialog(forPassword).ShowDialog(host.Owner);
                }

                break;

            case "account.server":
                if (RequireAccount(host) is { } forServer)
                {
                    var dialog = new ServerSettingsDialog(forServer);
                    await dialog.ShowDialog(host.Owner);
                    if (dialog.Saved) { host.Close(); host.Refresh(); }
                }

                break;

            case "tools.emptydeleted":
                await EmptyDeletedItemsAsync(host);
                break;

            case "tools.cleanup":
            {
                var dialog = new MailboxCleanupDialog();
                await dialog.ShowDialog(host.Owner);
                if (dialog.SearchRequested is { } query && host.Owner is MainWindow window)
                {
                    host.Close();
                    window.SearchEverywhere(query);
                }

                if (dialog.Report is { } said) host.Report(said);
                host.Refresh();
                break;
            }

            case "tools.archive":
            {
                var shell = host.Owner.DataContext as ViewModels.ShellViewModel;
                var dialog = new ArchiveDialog(App.Accounts.All, App.AutoArchive, shell?.ViewAccount, shell?.ViewFolderId);
                await dialog.ShowDialog(host.Owner);
                if (dialog.Outcome is { } outcome) host.Report("Archive: " + outcome.Summary);
                host.Refresh();
                break;
            }

            case "tools.recover":
                await new RecoverDeletedItemsDialog().ShowDialog(host.Owner);
                host.Refresh();
                break;

            case "rules":
                await new RulesAndAlertsDialog().ShowDialog(host.Owner);
                host.Refresh();
                break;
        }
    }

    /// <summary>
    /// Opens the account wizard, and reloads once it closes so the new account appears without
    /// a restart.
    /// </summary>
    internal static async Task AddAccountAsync(BackstageHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var wizard = new AccountWizard();
        await wizard.ShowDialog(host.Owner);

        if (wizard.Created is null) return;

        host.Close();
        host.Refresh();
        host.Report($"{wizard.Created.Address} added. Press F9 to check for mail.");
    }

    /// <summary>
    /// The account these dialogs act on. The default one, or nothing when none exists — in
    /// which case saying so beats opening a dialog with no account behind it.
    /// </summary>
    private static Account? RequireAccount(BackstageHost host)
    {
        var account = App.Accounts.Default?.Account;
        if (account is null) host.Report("No account is set up yet. File, Add Account.");
        return account;
    }

    /// <summary>
    /// Empties Deleted Items across every account. Confirmed, and the wording says how many go,
    /// because with POP3 this store may hold the only copy.
    /// </summary>
    private static async Task EmptyDeletedItemsAsync(BackstageHost host)
    {
        var folders = App.Accounts.All
            .Select(a => (Open: a, Folder: a.Mail.FolderWithRole(a.Account.Id, FolderRole.Deleted)))
            .Where(x => x.Folder is not null)
            .ToList();

        var total = folders.Sum(x => x.Folder!.Total);
        if (total == 0)
        {
            host.Report("Deleted Items is already empty.");
            return;
        }

        var confirmed = await Confirm.AskAsync(
            host.Owner,
            "Empty Deleted Items",
            $"Permanently delete {total:N0} item{(total == 1 ? "" : "s")} from Deleted Items?\n\n"
            + "This cannot be undone, and where mail was removed from the server this is the "
            + "only copy.",
            "Delete");

        if (!confirmed) return;

        var deleted = EmptyDeletedItems();

        host.Refresh();
        host.Report($"{deleted:N0} item{(deleted == 1 ? "" : "s")} deleted.");
    }

    /// <summary>
    /// Empties Deleted Items across every account, without asking.
    /// </summary>
    /// <returns>How many went.</returns>
    /// <remarks>
    /// The unconfirmed half, for the Options page's "Empty Deleted Items folders when exiting":
    /// at exit there is nobody to ask, and the person answered when they ticked the box. From the
    /// Backstage the confirmation above wraps this.
    /// </remarks>
    public static int EmptyDeletedItems()
    {
        var deleted = 0;

        foreach (var open in App.Accounts.All)
        {
            if (open.Mail.FolderWithRole(open.Account.Id, FolderRole.Deleted) is not { } folder) continue;

            foreach (var message in open.Mail.Messages(folder.Id, int.MaxValue))
            {
                open.Mail.DeleteMessage(message.Id);
                deleted++;
            }
        }

        return deleted;
    }
}
