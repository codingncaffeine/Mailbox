using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The folder tree's own doors: the menu a right-click opens over a row of it, one of its verbs
/// pressed, and what the store holds afterwards.
/// </summary>
/// <remarks>
/// <para>
/// The tree's counts could be read back before this and four of its verbs could be driven, but
/// the verbs were driven through <see cref="Mailbox.Protocols.FolderManager"/> directly — the
/// layer under the menu rather than the menu — so nothing had ever pressed an entry of it. That
/// leaves the half a reader actually touches unproven: whether the entry is there, whether it is
/// greyed over the row it is greyed over, whether its handler runs, and what it says afterwards.
/// A dozen of the entries have no other route at all: Copy Folder, Sort Subfolders A to Z, Move
/// Up, Move Down, Clean Up Folder, Properties, and every one of the search-folder entries.
/// </para>
/// <para>
/// Two things make this awkward enough to want a door rather than a pose line. A menu is a popup,
/// so a capture photographs the shell behind it and its being open proves nothing — the entries
/// and the presenter's size are the claim, which is what <see cref="FlyoutProbe"/> reads. And the
/// tree's rows are not all folders: a heading, a favourite, the Search Folders heading and a
/// saved search each get a different menu, and only the ordinary folder's row can be selected, so
/// a door that acted on the selection could reach one kind out of five.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The menu the folder pane last opened, for reading back what a reader would see.</summary>
    /// <remarks>
    /// Assigned where the pane shows it, the same way the message list's own menu is: a flyout
    /// built inside a handler and shown is unreachable from anywhere else, and rebuilding a second
    /// one here would prove something about a copy rather than about the menu.
    /// </remarks>
    private MenuFlyout? _folderMenu;

    /// <summary>Whether this pose photographed a dialog, and so owns ending the run.</summary>
    private bool _folderShotTaken;

    private void WirePhase10ADoors()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_FOLDERMENU") is not { Length: > 0 } spec) return;

        // Background, below the folder pose at Normal: the row this acts on is usually the one
        // MAILBOX_FOLDER opened, and a menu built before that has had its say is a menu over the
        // wrong folder. Below the pose rather than beside it for the same reason the folder pose
        // itself is posted rather than set in the constructor.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    if (DataContext is ShellViewModel shell) PoseFolderMenu(shell, spec);
                }
                catch (Exception ex)
                {
                    Log.Warn("Harness: the folder-menu door failed.", ex);
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Opens the folder pane's menu over a chosen row, says what is in it, and presses one entry.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_FOLDERMENU=dump</c> reports it; <c>MAILBOX_FOLDERMENU=Rename Folder</c> reports
    /// it and then presses that entry, submenus reached with <c>/</c>.
    /// <c>MAILBOX_FOLDERMENU_ROW</c> chooses the row: <c>#7</c> by the index the dump prints,
    /// <c>Junk</c> by name, or <c>work@example.net/Inbox</c> when three accounts have one. Without
    /// it the row is whichever folder is open, which is what a reader right-clicking the folder
    /// they are reading would get.
    /// </remarks>
    private void PoseFolderMenu(ShellViewModel shell, string spec)
    {
        if (this.FindControl<ListBox>("FolderList") is not { } pane)
        {
            Log.Warn("Harness: folder menu — the folder pane was not found.");
            return;
        }

        if (RowForMenu(shell) is not { } node)
        {
            Log.Warn("Harness: folder menu — no row matched. The pane holds: "
                     + string.Join(", ", shell.Folders.Select((f, i) => $"#{i} {f.Kind} “{f.Name}”")) + ".");
            return;
        }

        var at = shell.Folders.IndexOf(node);
        var target = pane.ContainerFromIndex(at) as Control;
        if (target is null)
        {
            Log.Warn($"Harness: folder menu — row #{at} “{node.Name}” has no container to right-click, "
                     + "so the pane has not laid out that far.");
            return;
        }

        Log.Info($"Harness: folder menu — right-clicking row #{at} “{node.Name}” [{node.Kind}].");
        RightClick(pane, target);

        // Both halves, as the row menu's own door does: the press-and-release is the route a
        // reader takes, and the context request is the same question asked directly, for telling
        // "the button never became a context request" from "the menu is empty".
        var opened = _folderMenu;
        if (opened is null)
        {
            target.RaiseEvent(new ContextRequestedEventArgs());
            opened = _folderMenu;
            Log.Info($"Harness: folder menu — the right-click opened nothing; after ContextRequested "
                     + $"the menu is {(opened is null ? "still nothing" : "open")}.");
        }

        if (opened is null)
        {
            Log.Warn("Harness: folder menu — no menu was built for that row.");
            return;
        }

        Log.Info("Harness: " + FlyoutProbe.Describe($"the folder menu over “{node.Name}”", opened));

        // Resolved before the press, and held: half these verbs end in a Refresh, which rebuilds
        // every node in the pane, and the node in hand is then in none of the maps that answer
        // which account it belongs to. Asked afterwards, a rename reported the folder it had just
        // renamed as belonging to no account — which reads exactly like a verb that lost the row.
        var owner = shell.FolderOf(node)?.Account ?? shell.SearchFolderAccount(node);

        var press = spec.Trim();
        if (press.Equals("dump", StringComparison.OrdinalIgnoreCase))
        {
            opened.Hide();
            ReportFolderStore(shell, owner, node.Name, "as it stands");
            return;
        }

        // Whether the entry a reader can reach is the entry this is about to press. PressMenuEntry
        // presses a greyed one exactly as it presses a live one, so a run that pressed Delete
        // Folder over an Inbox would report the delete working when a reader cannot even start it.
        var path = press.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entry = opened.Items.OfType<MenuItem>()
            .FirstOrDefault(i => (i.Header as string ?? string.Empty)
                .StartsWith(path[0], StringComparison.OrdinalIgnoreCase));

        Log.Info(entry is null
            ? $"Harness: folder menu — there is no entry “{press}” over “{node.Name}”."
            : $"Harness: folder menu — “{entry.Header}” is {(entry.IsEnabled ? "live" : "GREYED, so a reader cannot press it")}.");

        opened.Hide();
        if (entry is null) return;

        // The hold keeps the run alive past the press: a verb that opens a dialog answers on a
        // later pass, and a read-back taken on the next line reports the store as it was before.
        var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
        Pressed = null;
        PressMenuEntry(opened.Items.Cast<Control>(), path, 0);

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await AnswerFolderDialogAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(700));
                Log.Info($"Harness: folder menu — after “{press}”: status “{shell.StatusRight}”, "
                         + $"windows: {OtherWindows()}, "
                         + $"the pane holds {shell.Folders.Count} row(s).");
                ReportFolderStore(shell, owner, node.Name, $"after “{press}”");
            }
            finally
            {
                hold?.Dispose();
            }

            // A run whose picture was taken of a dialog has told the shell not to photograph
            // itself, and nothing else then ends it: the capture's own shutdown is the branch that
            // was skipped. Ended here, once the read-back is in the log.
            if (_folderShotTaken) Environment.Exit(0);
        });
    }

    /// <summary>
    /// Answers whatever dialog the pressed entry opened: <c>MAILBOX_FOLDERDIALOG</c>.
    /// </summary>
    /// <remarks>
    /// Steps separated by <c>;</c>, in order. <c>text:Reports</c> puts words in the dialog's first
    /// text box, <c>pick:Projects</c> or <c>pick:#3</c> chooses a row of its first list,
    /// <c>tab:1</c> turns to a tab of it, <c>press:OK</c> presses a button or a radio by what it
    /// reads, <c>shot</c> photographs it and <c>wait</c> gives an asynchronous step a beat.
    /// <para>
    /// Its own driver rather than the general one, for a reason the general one cannot fix: three
    /// of these dialogs need words typed into them before their OK does anything at all — Create
    /// New Folder discards an empty name, and Folder Properties keeps the old one — and a driver
    /// that presses buttons alone reports OK as having been pressed on all three while the store
    /// stays exactly as it was. And the steps have to run in the same continuation as the press,
    /// or a separately-posted driver races the menu and answers the dialog before it is up.
    /// </para>
    /// </remarks>
    private async Task AnswerFolderDialogAsync()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_FOLDERDIALOG") is not { Length: > 0 } spec) return;

        var first = true;

        foreach (var step in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (step.Equals("wait", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(400);
                continue;
            }

            if (await DialogAsync() is not { } dialog)
            {
                Log.Warn($"Harness: folder dialog — nothing is open for “{step}”.");
                return;
            }

            // A window that has just appeared is not yet answering the pointer, which the general
            // dialog driver records too: a press in the pass it opened in raises the handler and
            // the handler does nothing, and that reads exactly like a button wired to nothing.
            if (first)
            {
                first = false;
                await Task.Delay(400);
            }

            dialog.UpdateLayout();

            // Photographed on the spot rather than by arming the shell's own "capture the next
            // window": that one waits for this pose's hold to be let go, and by then the steps
            // after it have answered the dialog and closed it, so it photographs nothing at all.
            if (step.Equals("shot", StringComparison.OrdinalIgnoreCase))
            {
                if (WindowCapture.RequestedPath is not { } path) continue;
                WindowCapture.AnotherWindowWillBeCaptured = true;
                _folderShotTaken = true;
                if (dialog.ClientSize.Height <= 1 && WindowCapture.SizeFromContent(dialog)) await Task.Delay(400);
                WindowCapture.Capture(dialog, path, WindowCapture.Scale);
                Log.Info($"Harness: folder dialog — photographed {dialog.GetType().Name} “{dialog.Title}” "
                         + $"at {dialog.ClientSize.Width:0}x{dialog.ClientSize.Height:0}.");
                continue;
            }

            var colon = step.IndexOf(':');
            var verb = (colon > 0 ? step[..colon] : step).ToLowerInvariant();
            var arg = colon > 0 ? step[(colon + 1)..] : string.Empty;

            switch (verb)
            {
                case "text":
                {
                    if (dialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is not { } box)
                    {
                        Log.Warn($"Harness: folder dialog — {dialog.GetType().Name} draws no text box.");
                        break;
                    }

                    box.Text = arg;
                    Log.Info($"Harness: folder dialog — typed “{arg}” into {dialog.GetType().Name}.");
                    break;
                }

                case "tab":
                {
                    if (dialog.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is not { } tabs)
                    {
                        Log.Warn($"Harness: folder dialog — {dialog.GetType().Name} draws no tabs.");
                        break;
                    }

                    tabs.SelectedIndex = int.TryParse(arg, out var index) ? index : 0;
                    Log.Info($"Harness: folder dialog — turned to tab {tabs.SelectedIndex} of {tabs.ItemCount}: "
                             + string.Join(", ", tabs.Items.OfType<TabItem>().Select(t => t.Header)) + ".");
                    break;
                }

                case "pick":
                {
                    Pick(dialog, arg);
                    break;
                }

                // The two controls on the AutoArchive tab that are neither a button nor a list:
                // the number and the unit behind "Clean out items older than". Without them the
                // custom policy can only ever be written at its default of six months, and
                // whether the number and the unit reach the archiver at all is the half of that
                // tab worth proving.
                case "spin":
                {
                    if (dialog.GetVisualDescendants().OfType<NumericUpDown>().FirstOrDefault() is not { } spinner)
                    {
                        Log.Warn($"Harness: folder dialog — {dialog.GetType().Name} draws no number box.");
                        break;
                    }

                    spinner.Value = decimal.TryParse(arg, out var value) ? value : spinner.Value;
                    Log.Info($"Harness: folder dialog — the number box now reads {spinner.Value}"
                             + $"{(spinner.IsEffectivelyEnabled ? string.Empty : " (which is GREYED)")}.");
                    break;
                }

                case "choose":
                {
                    if (dialog.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault() is not { } combo)
                    {
                        Log.Warn($"Harness: folder dialog — {dialog.GetType().Name} draws no drop-down.");
                        break;
                    }

                    var items = combo.ItemsSource?.Cast<object?>().ToList() ?? [];
                    var index = items.FindIndex(i => (i?.ToString() ?? string.Empty)
                        .Contains(arg, StringComparison.OrdinalIgnoreCase));

                    if (index < 0)
                    {
                        Log.Warn($"Harness: folder dialog — the drop-down has no “{arg}”. It offers: "
                                 + string.Join(", ", items) + ".");
                        break;
                    }

                    combo.SelectedIndex = index;
                    Log.Info($"Harness: folder dialog — the drop-down now reads “{combo.SelectedItem}”"
                             + $"{(combo.IsEffectivelyEnabled ? string.Empty : " (which is GREYED)")}.");
                    break;
                }

                case "press":
                {
                    if (Buttons(dialog).FirstOrDefault(b => Reads(b, arg)) is not { } button)
                    {
                        Log.Warn($"Harness: folder dialog — {dialog.GetType().Name} has no “{arg}”. It has: "
                                 + string.Join(", ", Buttons(dialog).Select(Caption).Where(t => t.Length > 0)) + ".");
                        return;
                    }

                    Log.Info($"Harness: folder dialog — pressing “{Caption(button)}” in {dialog.GetType().Name}"
                             + $"{(button.IsEffectivelyEnabled ? string.Empty : " (which is GREYED)")}.");
                    Press(button, new Point(button.Bounds.Width / 2, button.Bounds.Height / 2));
                    break;
                }

                default:
                    Log.Warn($"Harness: folder dialog — “{step}” is not a step.");
                    break;
            }

            await Task.Delay(250);
        }
    }

    /// <summary>The pane row the menu is wanted over: the posed one, or the folder that is open.</summary>
    private FolderNode? RowForMenu(ShellViewModel shell)
    {
        var wanted = Environment.GetEnvironmentVariable("MAILBOX_FOLDERMENU_ROW")?.Trim();
        if (string.IsNullOrEmpty(wanted)) return shell.SelectedFolder;

        if (wanted.StartsWith('#') && int.TryParse(wanted[1..], out var index))
        {
            return index >= 0 && index < shell.Folders.Count ? shell.Folders[index] : null;
        }

        // "work@example.net/Inbox" — three accounts have an Inbox and a name alone reaches the
        // first, which is the trap the folder pose recorded one step earlier.
        var slash = wanted.LastIndexOf('/');
        var address = slash > 0 ? wanted[..slash] : null;
        var name = slash > 0 ? wanted[(slash + 1)..] : wanted;

        return shell.Folders.FirstOrDefault(f =>
            f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (address is null
                || (shell.FolderOf(f) is { } where
                    && where.Account.Account.Address.Equals(address, StringComparison.OrdinalIgnoreCase))
                || (shell.SearchFolderAccount(f) is { } root
                    && root.Account.Address.Equals(address, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>A right-click on a pane row, pressed and released the way a mouse does it.</summary>
    /// <remarks>
    /// On the row's own container rather than on the pane: the menu is filled from whatever the
    /// tunnelling press found under the pointer, so a press raised on the list itself builds the
    /// menu for no row at all and reports "No actions here yet" about a folder.
    /// </remarks>
    private static void RightClick(ListBox pane, Control target)
    {
        var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
        var at = new Point(12, 8);

        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, pane, at, 0,
            new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
            KeyModifiers.None));

        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, pane, at, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased),
            KeyModifiers.None, MouseButton.Right));
    }

    /// <summary>
    /// What the store holds for the account the row belongs to: every folder row, and every saved
    /// search, beside the pane's own count of the same folder.
    /// </summary>
    /// <remarks>
    /// The whole account rather than the one folder, because half these verbs act on something
    /// other than the row they were pressed over — New Folder makes a child, Move Up rewrites
    /// every sibling's ordinal, Delete takes a subtree, Copy adds one. A read-back of the row
    /// alone would report every one of those as having done nothing.
    /// </remarks>
    private static void ReportFolderStore(ShellViewModel shell, OpenAccount? account, string row, string when)
    {
        if (account is null)
        {
            Log.Info($"Harness: folder store {when} — “{row}” belongs to no account, so there is nothing to read back.");
            return;
        }

        var folders = account.Mail.Folders(account.Account.Id);
        Log.Info($"Harness: folder store {when} — {account.Account.Address}: {folders.Count} folder(s).");

        foreach (var folder in folders.OrderBy(f => f.ParentId ?? 0).ThenBy(f => f.Ordinal).ThenBy(f => f.Id))
        {
            var pane = shell.Folders.FirstOrDefault(n =>
                n.Kind != FolderNodeKind.Favourite && shell.FolderOf(n) is { } w && w.Folder.Id == folder.Id);

            var seen = pane is null
                ? "not in the pane"
                : $"pane {pane.Unread} unread" + (pane.Unread == folder.Unread ? string.Empty : "  ← DISAGREES");

            Log.Info($"Harness: folder store — id {folder.Id} “{folder.Name}” "
                     + $"[{folder.Role}, parent {folder.ParentId?.ToString() ?? "top"}, ordinal {folder.Ordinal}] "
                     + $"{folder.Unread} unread of {folder.Total}, "
                     + $"server {folder.ImapPath ?? "none"}, "
                     + $"autoarchive {account.Mail.FolderAutoArchive(folder.Id) ?? "default"}; {seen}.");
        }

        foreach (var search in account.Mail.SearchFolders())
        {
            Log.Info($"Harness: search folder — id {search.Id} “{search.Name}” "
                     + $"[{search.Query.Kind}, ordinal {search.Ordinal}, "
                     + $"threshold {search.Query.Threshold}, "
                     + $"values {(search.Query.Values.Count == 0 ? "none" : string.Join("; ", search.Query.Values))}, "
                     + $"deleted-too {search.Query.IncludeDeleted}].");
        }
    }
}
