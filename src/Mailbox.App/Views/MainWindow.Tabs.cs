using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Mailbox.App.ViewModels;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The Folder tab and the View tab of the classic ribbon.
/// </summary>
/// <remarks>
/// Both tabs are placements rather than new behaviour: every command here already had a handler
/// somewhere — the folder pane's own menu, the Layout menu, the arrangement engine — and what
/// the reference gives them is a tab, so a reader looking at a folder rather than at a message
/// has somewhere to look. The handlers are shared with those menus rather than copied, so a
/// folder renamed from the tab and one renamed from the pane are the same operation.
/// </remarks>
public partial class MainWindow
{
    // ---- The Folder tab -----------------------------------------------------------------

    /// <summary>The folder the tab acts on: whichever the pane has selected, with its account.</summary>
    private (OpenAccount Account, Folder Folder)? SelectedFolderFor(ShellViewModel shell)
        => shell.SelectedFolder is { } node ? shell.FolderOf(node) : null;

    private bool RunFolderTabCommand(ShellViewModel shell, CommandId id)
    {
        // New Search Folder is the one entry that does not need a folder selected, and it is
        // already handled where the other search-folder commands are.
        if (SelectedFolderFor(shell) is not var (account, folder))
        {
            if (!IsFolderTabCommand(id)) return false;

            shell.StatusRight = "No folder is selected, and this acts on one.";
            return true;
        }

        // The three the bar greys on a folder the account cannot do without. The bar is not the
        // only way in — a keyboard shortcut reaches a greyed command — so the answer is here as
        // well, and it says which folder and why rather than doing nothing.
        if (folder.Role != FolderRole.None
            && (id == MailCommands.RenameFolder.Id || id == MailCommands.MoveFolder.Id || id == MailCommands.DeleteFolder.Id))
        {
            shell.StatusRight = $"{folder.Name} is one of the folders the account is built on, and cannot be "
                + (id == MailCommands.RenameFolder.Id ? "renamed." : id == MailCommands.MoveFolder.Id ? "moved." : "deleted.");
            return true;
        }

        if (id == MailCommands.NewFolder.Id) { _ = NewFolderAsync(shell, account, folder.Id); return true; }
        if (id == MailCommands.RenameFolder.Id) { _ = RenameFolderAsync(shell, account, folder); return true; }
        if (id == MailCommands.CopyFolder.Id) { _ = CopyFolderAsync(shell, account, folder); return true; }
        if (id == MailCommands.MoveFolder.Id) { _ = MoveFolderAsync(shell, account, folder); return true; }
        if (id == MailCommands.DeleteFolder.Id) { _ = DeleteFolderAsync(shell, account, folder); return true; }

        if (id == MailCommands.MarkAllAsRead.Id)
        {
            var count = shell.MarkFolderRead(account, folder.Id);
            shell.StatusRight = count == 0
                ? $"Nothing unread in {folder.Name}."
                : $"{count} message{(count == 1 ? "" : "s")} in {folder.Name} marked read.";
            return true;
        }

        if (id == MailCommands.RunRulesNow.Id) { _ = RunRulesNowAsync(shell, account, folder); return true; }

        // Show All Folders A to Z sorts the whole account rather than one folder's children,
        // which is what the pane's own Sort Subfolders does and what the label here promises.
        if (id == MailCommands.ShowAllFoldersAtoZ.Id)
        {
            var tree = account.Mail.Folders(account.Account.Id);
            var sorted = 0;
            foreach (var parent in tree.Select(f => f.ParentId).Distinct())
            {
                var children = tree
                    .Where(f => f.ParentId == parent)
                    .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(f => f.Id)
                    .ToList();

                if (children.Count < 2) continue;

                account.Mail.OrderFolders(children);
                sorted += children.Count;
            }

            shell.Refresh();
            shell.StatusRight = sorted == 0
                ? "There is nothing to sort: no folder here has more than one child."
                : $"{sorted} folders in {account.Account.Address} sorted A to Z.";
            return true;
        }

        if (id == MailCommands.DeleteAll.Id) { _ = EmptyFolderAsync(shell, account, folder); return true; }

        if (id == MailCommands.AddToFavorites.Id)
        {
            shell.ToggleFavourite(account, folder);
            shell.StatusRight = shell.IsFavourite(account, folder)
                ? $"{folder.Name} added to Favorites."
                : $"{folder.Name} removed from Favorites.";
            RefreshCommandChecked();
            return true;
        }

        if (id == MailCommands.AutoArchiveSettings.Id) { _ = FolderPropertiesAsync(shell, account, folder, tab: 1); return true; }
        if (id == MailCommands.FolderProperties.Id) { _ = FolderPropertiesAsync(shell, account, folder); return true; }

        // Folder Permissions is placed and greyed: sharing a folder needs a server that offers
        // it. The keyboard can still reach it, so it says what the ribbon's grey says.
        if (id == MailCommands.FolderPermissions.Id)
        {
            shell.StatusRight = MailCommands.FolderPermissions.Description;
            return true;
        }

        return false;
    }

    /// <summary>Whether an id belongs to the Folder tab, for the nothing-selected answer.</summary>
    private static bool IsFolderTabCommand(CommandId id)
        => id == MailCommands.NewFolder.Id || id == MailCommands.RenameFolder.Id
           || id == MailCommands.CopyFolder.Id || id == MailCommands.MoveFolder.Id
           || id == MailCommands.DeleteFolder.Id || id == MailCommands.MarkAllAsRead.Id
           || id == MailCommands.RunRulesNow.Id || id == MailCommands.ShowAllFoldersAtoZ.Id
           || id == MailCommands.DeleteAll.Id || id == MailCommands.AddToFavorites.Id
           || id == MailCommands.AutoArchiveSettings.Id || id == MailCommands.FolderProperties.Id;

    /// <summary>
    /// Run Rules Now: the dialog Rules and Alerts already opens, reached from the folder it
    /// would run over.
    /// </summary>
    /// <remarks>
    /// The same window rather than a second one: it lists the account's rules with a tick each,
    /// the folder to run in and whether to take All, Unread or Read, and it runs them through
    /// the engine that runs on arrival — so a rule cannot mean one thing here and another when
    /// mail comes in. The list is re-read afterwards because the rules will have moved messages
    /// out of the folder on screen.
    /// </remarks>
    private async Task RunRulesNowAsync(ShellViewModel shell, OpenAccount account, Folder folder)
    {
        _ = folder;
        await new RunRulesNowDialog(account).ShowDialog(this);
        shell.Refresh();
    }

    // ---- The View tab -------------------------------------------------------------------

    private bool RunViewTabCommand(ShellViewModel shell, CommandId id)
    {
        if (id == ViewCommands.ShowAsConversations.Id)
        {
            shell.ShowAsConversations = !shell.ShowAsConversations;
            shell.StatusRight = shell.ShowAsConversations
                ? "Messages are shown as conversations."
                : "Messages are shown one row each.";
            RefreshCommandChecked();
            return true;
        }

        // Conversation Settings is placed and greyed until there is more than one switch behind
        // it; the keyboard still reaches it, and it says so rather than doing nothing.
        if (id == ViewCommands.ConversationSettings.Id)
        {
            shell.StatusRight = shell.ShowAsConversations
                ? "Conversation settings arrive with the conversation options page."
                : "Conversation settings apply while messages are shown as conversations.";
            return true;
        }

        if (id == ViewCommands.MessagePreview.Id) { ShowMessagePreviewMenu(shell); return true; }
        if (id == ViewCommands.AddColumns.Id) { _ = AddColumnsAsync(shell); return true; }
        if (id == ViewCommands.ExpandCollapse.Id) { ShowExpandCollapseMenu(shell); return true; }
        if (id == ViewCommands.FolderPane.Id) { ShowPaneMenu("Folder Pane", FillFolderPaneMenu, shell); return true; }
        if (id == ViewCommands.ReadingPane.Id) { ShowPaneMenu("Reading Pane", FillReadingPaneMenu, shell); return true; }
        if (id == ViewCommands.ToDoBar.Id) { ShowPaneMenu("To-Do Bar", FillToDoBarMenu, shell); return true; }
        if (id == ViewCommands.RemindersWindow.Id) { ShowRemindersWindow(shell); return true; }
        if (id == ViewCommands.OpenInNewWindow.Id) { OpenShellInNewWindow(shell); return true; }
        if (id == ViewCommands.CloseAllItems.Id) { CloseAllItemWindows(shell); return true; }

        // The arrangement gallery: eight of the eleven the engine knows, which is the eight the
        // reference's own gallery shows. The chevron beside them opens Arrange By, which is all
        // of them with the current one ticked.
        if (MailArrangementFor(id) is { } arrangement)
        {
            shell.Arrangement = arrangement;
            shell.StatusRight = $"Arranged by {Store.Lists.Arrangements.Label(arrangement)}.";
            RefreshCommandChecked();
            return true;
        }

        return false;
    }

    /// <summary>The mail arrangement a gallery entry stands for, or null when the id is not one.</summary>
    private static Store.Lists.Arrangement? MailArrangementFor(CommandId id)
    {
        if (id == ViewCommands.ArrangeByDate.Id) return Store.Lists.Arrangement.Date;
        if (id == ViewCommands.ArrangeByFrom.Id) return Store.Lists.Arrangement.From;
        if (id == ViewCommands.ArrangeByTo.Id) return Store.Lists.Arrangement.To;
        if (id == ViewCommands.ArrangeByCategories.Id) return Store.Lists.Arrangement.Categories;
        if (id == ViewCommands.ArrangeByFlagStatus.Id) return Store.Lists.Arrangement.Flag;
        if (id == ViewCommands.ArrangeByFlagStart.Id) return Store.Lists.Arrangement.FlagStart;
        if (id == ViewCommands.ArrangeByFlagDue.Id) return Store.Lists.Arrangement.FlagDue;
        if (id == ViewCommands.ArrangeBySize.Id) return Store.Lists.Arrangement.Size;
        return null;
    }

    /// <summary>Message Preview: how many lines of the message the list shows under its subject.</summary>
    private void ShowMessagePreviewMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        void Entry(string header, int lines)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = shell.PreviewLines == lines ? new TextBlock { Text = "✓" } : null,
            };
            item.Click += (_, _) =>
            {
                shell.PreviewLines = lines;
                shell.StatusRight = lines == 0
                    ? "Message preview off."
                    : $"Message preview: {lines} line{(lines == 1 ? "" : "s")}.";
            };
            flyout.Items.Add(item);
        }

        Entry("Off", 0);
        Entry("1 Line", 1);
        Entry("2 Lines", 2);
        Entry("3 Lines", 3);
        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>Expand/Collapse: this group or every group, opened or shut.</summary>
    private void ShowExpandCollapseMenu(ShellViewModel shell)
    {
        var flyout = new MenuFlyout();

        void Entry(string header, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        var group = shell.SelectedGroupHeader;

        Entry("Collapse This Group", () => shell.SetGroupCollapsed(group!, true), group is not null);
        Entry("Expand This Group", () => shell.SetGroupCollapsed(group!, false), group is not null);
        flyout.Items.Add(new Separator());
        Entry("Collapse All Groups", () => shell.SetAllGroupsCollapsed(true));
        Entry("Expand All Groups", () => shell.SetAllGroupsCollapsed(false));

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    /// <summary>Add Columns: the column chooser the View Settings dialog opens, opened directly.</summary>
    private async Task AddColumnsAsync(ShellViewModel shell)
    {
        var dialog = new ShowColumnsDialog(shell.CurrentView.Columns);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } columns) return;

        shell.UpdateView(shell.CurrentView with { Columns = columns });
        shell.StatusRight = $"{columns.Count} column{(columns.Count == 1 ? "" : "s")} shown.";
    }

    /// <summary>One of the Layout menu's three submenus, opened from its own ribbon button.</summary>
    /// <remarks>
    /// Measured after it is shown rather than photographed. These three are the only popups the
    /// To-Do Bar has, and a popup is not in the application's window list, so a capture of a run
    /// that opened one is a picture of the shell behind it — which reads as a success. What the
    /// menu holds, which entry carries the tick and how big the presenter came out are the claims;
    /// <see cref="FlyoutProbe"/> reads all three from inside.
    /// </remarks>
    private void ShowPaneMenu(string named, Action<ItemCollection, ShellViewModel> fill, ShellViewModel shell)
    {
        var flyout = new MenuFlyout();
        fill(flyout.Items, shell);
        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);

        // After ShowAt: the entries have no top level until the popup is presented, and a probe
        // taken before it would report a menu that was built and never shown.
        if (!Mailbox.App.Theming.WindowCapture.IsRequested) return;

        Log.Info("Harness: " + FlyoutProbe.Describe(named, flyout));

        // And which entry carries the tick, which the probe does not read and which is the whole
        // claim of a menu whose entries are states rather than actions: To-Do Bar · Calendar is
        // ticked when the calendar is docked, and a tick that disagreed with the pane would be
        // invisible in any picture of the shell.
        var ticked = flyout.Items.OfType<MenuItem>().Where(i => i.Icon is not null).Select(i => i.Header?.ToString());
        Log.Info($"Harness: {named} ticks: {(ticked.Any() ? string.Join(", ", ticked) : "none")}.");

        // MAILBOX_PANEMENU=<entry> presses one of them. Reading what a menu holds and reading the
        // tick beside an entry are both statements about a menu that has never been used: these
        // four entries are the only way the To-Do Bar's sections go on and off, and until this
        // nothing had pressed one. Named by its header, so "Calendar", "Tasks", "People", "Off".
        if (Environment.GetEnvironmentVariable("MAILBOX_PANEMENU") is not { Length: > 0 } wanted) return;

        var entry = flyout.Items.OfType<MenuItem>().FirstOrDefault(
            i => string.Equals(i.Header?.ToString(), wanted.Trim(), StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            Log.Info($"Harness: {named} has no entry called “{wanted}”.");
            return;
        }

        entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Log.Info($"Harness: {named} · {wanted} pressed — calendar {(shell.IsCalendarDocked ? "on" : "off")}, "
                 + $"tasks {(shell.AreTasksDocked ? "on" : "off")}, people {(shell.ArePeopleDocked ? "on" : "off")}, "
                 + $"bar {(shell.IsToDoBarVisible ? "showing" : "away")}.");
    }

    /// <summary>Reminders Window: what is due now, and the window even when nothing is.</summary>
    private void ShowRemindersWindow(ShellViewModel shell)
    {
        _reminders ??= NewRemindersWindow(shell);
        CheckReminders(shell);

        // CheckReminders hides the window when nothing is due, which is right for the timer and
        // wrong for a press: a reader who asks for the window is asking to be told there is
        // nothing, not to have it flicker.
        if (!_reminders.IsVisible) _reminders.Show(_reminders.Current, evenWhenEmpty: true);
        _reminders.Activate();
    }

    /// <summary>
    /// Open in New Window: a second shell over the same store, on the same folder.
    /// </summary>
    /// <remarks>
    /// A real second window rather than a copy of the list: the reference's is the whole
    /// application again, and everything in it — the ribbon, the panes, the reading pane — has
    /// to work there too. Safe because the store gained one writer under a gate: two windows
    /// reading and writing the same account is exactly the case that fixed.
    /// </remarks>
    private void OpenShellInNewWindow(ShellViewModel shell)
    {
        var where = SelectedFolderFor(shell);

        var window = new MainWindow
        {
            Width = Width,
            Height = Height,
            Position = new Avalonia.PixelPoint(Position.X + 40, Position.Y + 40),
        };

        window.Show();

        // The folder the reader was looking at, once the new window has built its own tree.
        if (where is var (account, folder) && window.DataContext is ShellViewModel opened)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => opened.SelectFolder(account, folder.Id),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }

        shell.StatusRight = "Opened in a new window.";
    }

    /// <summary>
    /// Close All Items: every window this one has opened on an item, and nothing else.
    /// </summary>
    /// <remarks>
    /// Items, not windows: a dialog, the Reminders window and a second shell are not items, and
    /// closing them would be a different command. A compose window is one, and it asks about an
    /// unsaved draft on its own way out — which is why they are closed rather than disposed.
    /// </remarks>
    private void CloseAllItemWindows(ShellViewModel shell)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var items = desktop.Windows
            .Where(w => w is MessageWindow or ComposeWindow or ContactWindow or AppointmentWindow
                or TaskWindow or NoteWindow or JournalEntryWindow)
            .ToList();

        foreach (var window in items) window.Close();

        shell.StatusRight = items.Count == 0
            ? "Nothing is open."
            : $"{items.Count} window{(items.Count == 1 ? "" : "s")} closed.";
    }

    // ---- The Send/Receive tab's Server group ---------------------------------------------

    /// <summary>
    /// Download Headers and the three commands that act on what it brought back.
    /// </summary>
    /// <remarks>
    /// Headers are ordinary rows with nothing under them: they list, sort, flag and file like
    /// any other, and the reading pane says what is missing and offers to fetch it. Everything
    /// here goes to a background thread — each one talks to the server, and §4's rule is that
    /// the interface never waits on the network.
    /// </remarks>
    private bool RunServerCommand(ShellViewModel shell, CommandId id)
    {
        if (id == ViewCommands.DownloadHeaders.Id) { _ = DownloadHeadersAsync(shell); return true; }
        if (id == ViewCommands.ProcessMarkedHeaders.Id) { _ = ProcessMarkedHeadersAsync(shell); return true; }

        if (id == ViewCommands.MarkToDownload.Id || id == ViewCommands.UnmarkToDownload.Id)
        {
            var mark = id == ViewCommands.MarkToDownload.Id;
            var rows = SelectedRows();

            if (rows.Count == 0 || SelectedFolderFor(shell) is not var (account, _))
            {
                shell.StatusRight = $"{(mark ? "Mark" : "Unmark")} to Download acts on the headers selected.";
                return true;
            }

            var changed = account.Mail.MarkForDownload([.. rows.Select(r => r.Id)], mark);
            shell.StatusRight = changed == 0
                ? "Nothing selected here is a header waiting for its message."
                : $"{changed} header{(changed == 1 ? "" : "s")} {(mark ? "marked for download" : "unmarked")}.";

            shell.Refresh();
            return true;
        }

        return false;
    }

    /// <summary>The headers of the folder on screen, without their messages.</summary>
    private async Task DownloadHeadersAsync(ShellViewModel shell)
    {
        if (SelectedFolderFor(shell) is not var (account, folder))
        {
            shell.StatusRight = "No folder is selected, and Download Headers acts on one.";
            return;
        }

        if (await ConnectionForAsync(account) is not { } connection)
        {
            shell.StatusRight = $"{account.Account.Address} has no server settings to ask.";
            return;
        }

        shell.StatusRight = $"Asking {connection.Incoming.Host} what is in {folder.Name}…";

        try
        {
            var written = await Task.Run(() => new HeaderDownloader(account.Mail).HeadersAsync(connection, folder));

            shell.Refresh();
            shell.StatusRight = written == 0
                ? $"Nothing new in {folder.Name}."
                : $"{written} header{(written == 1 ? "" : "s")} downloaded into {folder.Name}.";
        }
        catch (Exception ex)
        {
            Log.Warn("Download Headers failed.", ex);
            shell.StatusRight = $"Could not download headers: {ex.Message}";
        }
    }

    /// <summary>The messages behind the headers the reader marked.</summary>
    private async Task ProcessMarkedHeadersAsync(ShellViewModel shell)
    {
        if (SelectedFolderFor(shell) is not var (account, _))
        {
            shell.StatusRight = "No account is selected.";
            return;
        }

        if (await ConnectionForAsync(account) is not { } connection)
        {
            shell.StatusRight = $"{account.Account.Address} has no server settings to ask.";
            return;
        }

        var waiting = account.Mail.MarkedForDownload(account.Account.Id).Count;
        if (waiting == 0)
        {
            shell.StatusRight = "No header is marked for download.";
            return;
        }

        shell.StatusRight = $"Fetching {waiting} message{(waiting == 1 ? "" : "s")}…";

        try
        {
            var filled = await Task.Run(() => new HeaderDownloader(account.Mail).ProcessMarkedAsync(connection));

            shell.Refresh();
            shell.StatusRight = filled == 0
                ? "Nothing came back: those headers are no longer on the server."
                : $"{filled} message{(filled == 1 ? "" : "s")} downloaded.";
        }
        catch (Exception ex)
        {
            Log.Warn("Process Marked Headers failed.", ex);
            shell.StatusRight = $"Could not fetch the marked messages: {ex.Message}";
        }
    }

    /// <summary>
    /// The reading pane's Download button: this one header, marked and fetched at once.
    /// </summary>
    private async Task DownloadSelectedHeaderAsync(ShellViewModel shell)
    {
        if (shell.SelectedMessage is not { IsHeaderOnly: true } row) return;
        if (SelectedFolderFor(shell) is not var (account, _)) return;

        account.Mail.MarkForDownload([row.Id], marked: true);
        await ProcessMarkedHeadersAsync(shell);
    }

    // ---- The Help tab -------------------------------------------------------------------

    /// <summary>
    /// The Help tab: four buttons that lead somewhere, and four that say what is not behind them.
    /// </summary>
    /// <remarks>
    /// The reference's Help tab is mostly links into its publisher's services — a support desk,
    /// training videos, a repair tool, a diagnostics collector. This project has none of those
    /// and is not going to pretend otherwise, so those four are drawn (the tab is the
    /// reference's tab) and each says plainly what it would need. The four that do lead
    /// somewhere go to the places this project actually keeps: the manual, the issues page, and
    /// the release notes.
    /// </remarks>
    private bool RunHelpCommand(ShellViewModel shell, CommandId id)
    {
        if (id == ViewCommands.Help.Id)
        {
            OpenProjectPage(shell, ViewCommands.Project.Manual, "the manual");
            return true;
        }

        if (id == ViewCommands.Feedback.Id || id == ViewCommands.SuggestFeature.Id)
        {
            OpenProjectPage(shell, ViewCommands.Project.Issues, "the issues page");
            return true;
        }

        if (id == ViewCommands.WhatsNew.Id)
        {
            OpenProjectPage(shell, ViewCommands.Project.Releases, "the releases page");
            return true;
        }

        // Get Diagnostics is the one of the four with something real to point at: the logs are on
        // disk whether or not anybody collects them, and where they are is the useful answer.
        if (id == ViewCommands.GetDiagnostics.Id)
        {
            shell.StatusRight = $"{ViewCommands.GetDiagnostics.Description} They are in {Mailbox.Core.Diagnostics.Log.LogDirectory()}.";
            return true;
        }

        if (id == ViewCommands.ContactSupport.Id || id == ViewCommands.ShowTraining.Id
            || id == ViewCommands.SupportTool.Id)
        {
            shell.StatusRight = App.Commands.TryGet(id, out var command) ? command.Description : string.Empty;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Hands one of the project's own pages to the desktop.
    /// </summary>
    /// <remarks>
    /// Deliberately not the reading pane's opener, which exists to follow a <em>stranger's</em>
    /// link and is written to be suspicious of one. These are three addresses compiled into the
    /// application, and what matters here instead is that a capture run never launches a
    /// browser: a harness that opened seven tabs while it photographed the Help tab would be
    /// unusable.
    /// </remarks>
    private static void OpenProjectPage(ShellViewModel shell, string url, string what)
    {
        shell.StatusRight = Mailbox.Core.Platform.DesktopOpen.Open(url) switch
        {
            Mailbox.Core.Platform.DesktopOpenResult.Opened => $"Opened {what} in your browser.",
            Mailbox.Core.Platform.DesktopOpenResult.Posed => $"Would open {what}: {url}",
            _ => $"Nothing on this desktop opened {url}.",
        };
    }

    // ---- What is on ---------------------------------------------------------------------

    /// <summary>
    /// Whether what a command turns on is on: the box the reference draws round a button, and
    /// the tick in a check box.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="IsCommandUsable"/>. Every one of these is a state the shell
    /// already holds — the arrangement, the panes, the spacing — so this reads it rather than
    /// keeping a second copy that could disagree.
    /// </remarks>
    private bool IsCommandChecked(CommandId id)
    {
        if (DataContext is not ShellViewModel shell) return false;

        if (id == MailCommands.WorkOffline.Id) return App.Transfer.WorkOffline;
        if (id == ViewCommands.ShowAsConversations.Id) return shell.ShowAsConversations;
        if (id == ViewCommands.TighterSpacing.Id) return shell.CompactRows;
        if (id == ViewCommands.ShowFocusedInbox.Id) return shell.FocusedInboxOn;
        if (id == MailCommands.AddToFavorites.Id)
            return SelectedFolderFor(shell) is var (account, folder) && shell.IsFavourite(account, folder);

        if (MailArrangementFor(id) is { } arrangement) return shell.Arrangement == arrangement;

        return false;
    }

    private void RefreshCommandChecked() => _ribbon?.RefreshChecked();
}
