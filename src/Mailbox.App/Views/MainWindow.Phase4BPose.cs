using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The doors the rules, send/receive and notification surfaces needed before they could be
/// audited: pressing a button inside a dialog, walking the Rules Wizard, running Run Rules Now,
/// and offering — then taking — a real Undo Send.
/// </summary>
/// <remarks>
/// Every one of these exists because a capture answers the wrong question. A photograph of the
/// Rules Wizard proves a wizard was drawn; it says nothing about whether Next reaches the next
/// page, whether Finish builds the rule the description claims, or whether the rule that comes
/// out is the rule the store ends up holding. A photograph of the undo toast proves a toast was
/// drawn; the button on it had never been pressed by anything.
/// <para>
/// The presses go through <c>Button.Click</c> — the real event a pointer raises — rather than
/// through the handlers behind them, so a button that is disabled, unreachable or wired to
/// nothing reads as a miss rather than as a pass.
/// </para>
/// </remarks>
public partial class MainWindow
{
    // ---- Finding and pressing --------------------------------------------------------------

    /// <summary>A button's word: its own text, or the first text inside whatever it holds.</summary>
    /// <remarks>
    /// Two shapes on one toolbar: <c>PushButton</c> puts a string straight in Content, and
    /// <c>ToolButton</c> puts an icon and a TextBlock in a StackPanel. Reading only the first
    /// would find half the buttons in the dialog and miss the half the reference draws with icons.
    /// </remarks>
    internal static string LabelOf(Button button) => button.Content switch
    {
        string text => text,
        Control control => control.GetSelfAndVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .FirstOrDefault(t => t.Length > 0) ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>
    /// Everything of a kind inside a window, laid out first and looked for in both trees.
    /// </summary>
    /// <remarks>
    /// A page swapped into a ContentControl is in the logical tree at once and in the visual tree
    /// only after a layout pass, so a door that presses one button and then looks for the next on
    /// the same dispatcher pass finds a stale page and reports a button that is plainly there as
    /// missing. Laying out first and reading both trees is what makes a sequence of presses mean
    /// what it says.
    /// </remarks>
    private static IEnumerable<T> Descendants<T>(Visual root) where T : Visual
    {
        if (root is Layoutable layout) layout.UpdateLayout();

        return root.GetSelfAndVisualDescendants().OfType<T>()
            .Concat(((ILogical)root).GetSelfAndLogicalDescendants().OfType<T>())
            .Distinct();
    }

    /// <summary>
    /// Presses the button whose word starts with <paramref name="label"/>. Says what it found and
    /// what it did, including when the button was there and greyed — which is a different answer
    /// from "no such button" and the two must never read alike.
    /// </summary>
    internal static bool PressLabelled(Visual root, string label, string where)
    {
        var buttons = Descendants<Button>(root).ToList();
        var match = buttons.FirstOrDefault(
            b => LabelOf(b).StartsWith(label, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Log.Info($"Harness: {where} has no button “{label}”. It has: "
                     + string.Join(" | ", buttons.Select(LabelOf).Where(t => t.Length > 0)));
            return false;
        }

        if (!match.IsEffectivelyEnabled)
        {
            Log.Info($"Harness: {where} — “{LabelOf(match)}” is greyed; not pressed.");
            return false;
        }

        Log.Info($"Harness: {where} — pressing “{LabelOf(match)}”.");
        PressButton(match);
        return true;
    }

    /// <summary>Raises the click a pointer would, without needing a pointer.</summary>
    private static void PressButton(Button button)
        => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>Ticks or clears the check box whose label contains <paramref name="text"/>.</summary>
    internal static bool TickLabelled(Visual root, string text, bool on, string where)
    {
        var boxes = Descendants<CheckBox>(root).ToList();
        var match = boxes.FirstOrDefault(
            b => (b.Content as string ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Log.Info($"Harness: {where} has no tick box matching “{text}”. It has: "
                     + string.Join(" | ", boxes.Select(b => b.Content as string ?? "?")));
            return false;
        }

        match.IsChecked = on;
        Log.Info($"Harness: {where} — “{match.Content}” {(on ? "ticked" : "cleared")}.");
        return true;
    }

    // ---- Notifications ---------------------------------------------------------------------

    /// <summary>
    /// What the notification actually carries, beside the words the toast pose already logs.
    /// </summary>
    /// <remarks>
    /// The claim under audit is not the text: it is that a toast about one message carries Reply,
    /// Delete and Mark Read and is <em>not</em> transient, so the buttons still work from the
    /// notification server's history after the popup has gone. Neither the actions nor the
    /// transient flag can be seen in a capture, and <c>notify-send</c> is a separate process.
    /// </remarks>
    private static void Phase4BReportToast(Notifications.Notification notification)
        => Log.Info($"Harness: notification — {notification.Actions.Count} action(s) "
                    + $"[{string.Join(", ", notification.Actions.Select(a => $"{a.Id}=“{a.Label}”"))}], "
                    + $"transient {notification.Transient}.");

    // ---- The Rules Wizard ------------------------------------------------------------------

    /// <summary>
    /// Walks the wizard: <c>MAILBOX_WIZARD_PRESS=template:1,next,tick:subject,next,tick:mark,name:Receipts,runnow,finish</c>.
    /// </summary>
    /// <remarks>
    /// One step per comma, each pressed on the real control, so what is proven is that the button
    /// is there, is enabled at that point, and reaches the page it claims to. The rule the wizard
    /// hands back is logged clause by clause afterwards — a wizard that draws five pages and
    /// builds the wrong rule is the failure this exists to catch.
    /// </remarks>
    private static void Phase4BWizard(RuleWizard wizard, string spec)
    {
        foreach (var step in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = step.IndexOf(':');
            var verb = (colon > 0 ? step[..colon] : step).ToLowerInvariant();
            var value = colon > 0 ? step[(colon + 1)..] : string.Empty;

            switch (verb)
            {
                case "next":
                case "back":
                case "finish":
                case "cancel":
                    PressLabelled(wizard, verb switch
                    {
                        "next" => "Next",
                        "back" => "< Back",
                        "finish" => "Finish",
                        _ => "Cancel",
                    }, "the wizard");
                    break;

                // A template by index within the list, which is what a click on a row does.
                case "template":
                {
                    var list = Descendants<ListBox>(wizard).FirstOrDefault();
                    if (list is null || !int.TryParse(value, out var index))
                    {
                        Log.Info("Harness: the wizard has no template list to choose from.");
                        break;
                    }

                    list.SelectedIndex = index;
                    var chosen = (list.SelectedItem as ListBoxItem)?.Content is TextBlock t ? t.Text : "?";
                    Log.Info($"Harness: the wizard — template {index} chosen (“{chosen}”).");
                    break;
                }

                case "tick":
                    TickLabelled(wizard, value, true, "the wizard");
                    break;

                case "untick":
                    TickLabelled(wizard, value, false, "the wizard");
                    break;

                case "name":
                {
                    var box = Descendants<TextBox>(wizard).FirstOrDefault();
                    if (box is null) { Log.Info("Harness: the wizard has no name box."); break; }
                    box.Text = value;
                    Log.Info($"Harness: the wizard — named “{value}”.");
                    break;
                }

                default:
                    Log.Info($"Harness: the wizard does not know the step “{step}”.");
                    break;
            }
        }
    }

    /// <summary>Writes what the wizard built, clause by clause, so the store can be held to it.</summary>
    private static void Phase4BReportRule(Core.Rules.MailRule? rule, bool runNow, string where)
    {
        if (rule is null)
        {
            Log.Info($"Harness: {where} — no rule (cancelled, or Finish refused).");
            return;
        }

        Log.Info($"Harness: {where} — rule “{rule.Name}”, "
                 + $"{(rule.Enabled ? "on" : "off")}, {(rule.ServerSide ? "on the server" : "on this computer")}, "
                 + $"{(rule.AppliesToSent ? "on messages I send" : "on messages I receive")}, run now {runNow}.");

        foreach (var condition in rule.Conditions)
        {
            Log.Info($"Harness: {where} — condition {condition.Kind} [{string.Join(", ", condition.Values)}]"
                     + $"{(condition.Level is { } l ? $" level {l}" : string.Empty)}.");
        }

        foreach (var exception in rule.Exceptions)
        {
            Log.Info($"Harness: {where} — except if {exception.Kind} [{string.Join(", ", exception.Values)}].");
        }

        foreach (var action in rule.Actions)
        {
            Log.Info($"Harness: {where} — action {action.Kind} [{string.Join(", ", action.Values)}]"
                     + $"{(action.FolderName is { Length: > 0 } f ? $" folder “{f}”" : string.Empty)}.");
        }
    }

    // ---- Rules and Alerts ----------------------------------------------------------------------

    /// <summary>
    /// Reports every button on the Rules and Alerts toolbar and footer with the state it is in,
    /// then presses the ones <paramref name="spec"/> names:
    /// <c>MAILBOX_RULES_PRESS=report</c>, or <c>press:Delete,press:OK</c>.
    /// </summary>
    /// <remarks>
    /// The states are the point. The reference greys the six buttons that need a rule selected
    /// while there is none, and a button that is drawn at full strength and does nothing is worse
    /// than one that is greyed — so what has to be read back is not "did it act" but "did it look
    /// like it would". <see cref="PressLabelled"/> distinguishes the two.
    /// </remarks>
    private static void Phase4BRulesDialog(Window dialog, string spec)
    {
        foreach (var step in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = step.IndexOf(':');
            var verb = (colon > 0 ? step[..colon] : step).ToLowerInvariant();
            var value = colon > 0 ? step[(colon + 1)..] : string.Empty;

            switch (verb)
            {
                case "report":
                    foreach (var button in Descendants<Button>(dialog))
                    {
                        if (LabelOf(button) is not { Length: > 0 } label) continue;
                        Log.Info($"Harness: Rules and Alerts — “{label}” is "
                                 + $"{(button.IsEffectivelyEnabled ? "enabled" : "greyed")}.");
                    }
                    break;

                case "press":
                    PressLabelled(dialog, value, "Rules and Alerts");
                    break;

                case "select":
                {
                    var list = Descendants<ClassicListView>(dialog).FirstOrDefault();
                    if (list is null) { Log.Info("Harness: Rules and Alerts has no rule list."); break; }
                    list.SelectedIndex = int.TryParse(value, out var index) ? index : 0;
                    Log.Info($"Harness: Rules and Alerts — row {list.SelectedIndex} selected.");
                    break;
                }

                default:
                    Log.Info($"Harness: Rules and Alerts does not know the step “{step}”.");
                    break;
            }
        }
    }

    // ---- Run Rules Now ---------------------------------------------------------------------

    /// <summary>
    /// Presses Run Rules Now: <c>MAILBOX_RULES_RUN=all</c>, or <c>folder:Inbox,which:1,all,run</c>.
    /// </summary>
    /// <remarks>
    /// The dialog's own buttons, in the order a reader presses them, and the status line it leaves
    /// behind is logged — but the verdict is the store, which the caller reads with a query. What
    /// this door provides is the press; the run is the product's.
    /// </remarks>
    private static void Phase4BRunRules(Window dialog, string spec)
    {
        foreach (var step in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = step.IndexOf(':');
            var verb = (colon > 0 ? step[..colon] : step).ToLowerInvariant();
            var value = colon > 0 ? step[(colon + 1)..] : string.Empty;

            switch (verb)
            {
                case "all": PressLabelled(dialog, "Select All", "Run Rules Now"); break;
                case "none": PressLabelled(dialog, "Unselect All", "Run Rules Now"); break;
                case "run": PressLabelled(dialog, "Run Now", "Run Rules Now"); break;
                case "close": PressLabelled(dialog, "Close", "Run Rules Now"); break;
                case "rule": TickLabelled(dialog, value, true, "Run Rules Now"); break;

                // The two combo boxes, in the order the dialog lays them out: the folder to run
                // in, then which of its messages.
                case "folder":
                case "which":
                {
                    var combos = Descendants<ComboBox>(dialog).ToList();
                    var combo = verb == "folder" ? combos.ElementAtOrDefault(0) : combos.ElementAtOrDefault(1);
                    if (combo is null) { Log.Info($"Harness: Run Rules Now has no {verb} list."); break; }

                    if (int.TryParse(value, out var index)) combo.SelectedIndex = index;
                    else if (combo.ItemsSource is System.Collections.IEnumerable items)
                    {
                        var all = items.Cast<object?>().Select(o => o?.ToString() ?? string.Empty).ToList();
                        var found = all.FindIndex(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase));
                        if (found >= 0) combo.SelectedIndex = found;
                        else Log.Info($"Harness: Run Rules Now has no {verb} “{value}” in [{string.Join(" | ", all)}].");
                    }

                    Log.Info($"Harness: Run Rules Now — {verb} is now “{combo.SelectedItem}”.");
                    break;
                }

                default:
                    Log.Info($"Harness: Run Rules Now does not know the step “{step}”.");
                    break;
            }
        }

        // The line the dialog writes after a run: how many messages a rule acted on, in its own
        // words. Read back beside the store rather than instead of it.
        foreach (var block in Descendants<TextBlock>(dialog))
        {
            if (block.Text is { Length: > 0 } text
                && (text.Contains("acted on") || text.Contains("matched") || text.Contains("Choose at least")))
            {
                Log.Info($"Harness: Run Rules Now says “{text}”.");
            }
        }
    }

    // ---- Send/Receive groups ---------------------------------------------------------------------

    /// <summary>
    /// Which accounts each group covers, and which a Send/Receive All would reach.
    /// </summary>
    /// <remarks>
    /// The same two calls the shell's own <c>InGroup</c> makes, over the accounts really open — a
    /// real run needs a password out of the keyring and a server, neither of which a capture run
    /// may have, so what is proven here is the decision rather than the connection. A group left
    /// out of Send/Receive All is the case that matters: nothing else says whether the tick in the
    /// dialog is read by anything.
    /// </remarks>
    private static void Phase4BGroups(IReadOnlyList<string> addresses)
    {
        Log.Info($"Harness: send/receive groups — {App.Groups.All.Count} group(s) over "
                 + $"{addresses.Count} account(s): {string.Join(", ", addresses)}.");

        foreach (var group in App.Groups.All)
        {
            Log.Info($"Harness: group “{group.Name}” — included in Send/Receive All: {group.IncludeInSendReceiveAll}; "
                     + $"schedule {(group.ScheduleEnabled ? $"every {group.ScheduleMinutes} minute(s)" : "off")}; "
                     + $"covers {string.Join(", ", App.Groups.AccountsIn(group, addresses))}.");
        }

        Log.Info("Harness: Send/Receive All would reach "
                 + $"{string.Join(", ", App.Groups.AccountsForSendReceiveAll(addresses))}.");
    }

    // ---- Selecting more than one row -----------------------------------------------------------

    /// <summary>
    /// Selects every message row the list is drawing, through the list's own selection.
    /// </summary>
    /// <remarks>
    /// Not <c>SelectAll</c>: the list holds group headers as well as messages, and selecting those
    /// too would hand a command rows it cannot act on. What is written back is the accounts the
    /// selection spans, because that is the fact the per-account guard turns on.
    /// </remarks>
    private void Phase4BSelectAll()
    {
        if (List is not { } list || DataContext is not ShellViewModel shell)
        {
            Log.Info("Harness: select all — no list.");
            return;
        }

        var rows = shell.VisibleRows.OfType<ViewModels.MessageRow>().ToList();
        list.SelectedItems?.Clear();
        foreach (var row in rows) list.SelectedItems?.Add(row);

        var accounts = rows.Select(r => r.Address).Where(a => a.Length > 0).Distinct().Order().ToList();
        Log.Info($"Harness: select all — {list.SelectedItems?.Count ?? 0} row(s) selected of {rows.Count} drawn"
                 + (accounts.Count > 0 ? $", across {string.Join(", ", accounts)}" : string.Empty) + ".");
    }

    // ---- Search folders ------------------------------------------------------------------------

    /// <summary>
    /// Chooses a criterion in the New Search Folder list and presses OK:
    /// <c>MAILBOX_SEARCHFOLDER=Unread mail</c>, or an index.
    /// </summary>
    /// <remarks>
    /// The dialog is found in the application's own window list rather than being handed over,
    /// because the pose goes through <c>NewSearchFolderAsync</c> — the shell's real consumer, the
    /// one that writes the folder and selects it — and that method owns its dialog.
    /// </remarks>
    private static void Phase4BSearchFolder(string pick)
    {
        var dialog = (Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.Windows.OfType<NewSearchFolderDialog>().FirstOrDefault();

        if (dialog is null) { Log.Info("Harness: no New Search Folder dialog is open."); return; }

        var list = Descendants<ListBox>(dialog).FirstOrDefault();
        if (list is null) { Log.Info("Harness: the New Search Folder dialog has no list."); return; }

        var rows = (list.ItemsSource as System.Collections.IEnumerable)?.Cast<object?>().ToList() ?? [];
        string Text(object? row) => (row as ListBoxItem)?.Content is TextBlock t ? t.Text ?? string.Empty : string.Empty;

        var index = int.TryParse(pick, out var n)
            ? n
            : rows.FindIndex(r => Text(r).StartsWith(pick, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            Log.Info($"Harness: the New Search Folder dialog offers no “{pick}”. It offers: "
                     + string.Join(" | ", rows.Select(Text).Where(t => t.Length > 0)));
            return;
        }

        list.SelectedIndex = index;
        Log.Info($"Harness: New Search Folder — chose “{Text(rows[index])}”.");
        PressLabelled(dialog, "OK", "New Search Folder");
    }

    /// <summary>The search folders the pane and the store hold, side by side.</summary>
    /// <remarks>
    /// A search folder has no store folder of its own, so the folder-pane dump can only say
    /// "no store folder — SearchFolder" about it: what its count means has to be asked of the
    /// saved query instead, which is what this does.
    /// </remarks>
    private static void Phase4BReportSearchFolders(ShellViewModel shell)
    {
        foreach (var account in App.Accounts.All)
        {
            var own = App.Accounts.All.Select(a => a.Account.Address).ToList();
            foreach (var search in account.Mail.SearchFolders())
            {
                var rows = account.Mail.SearchFolderResults(search.Query, own, Core.PosedClock.Now);
                Log.Info($"Harness: search folder “{search.Name}” on {account.Account.Address} — "
                         + $"{search.Query.Kind}, {rows.Count} row(s), "
                         + $"{account.Mail.SearchFolderUnread(search.Query, own, Core.PosedClock.Now)} unread: "
                         + string.Join(" | ", rows.Select(r => r.Subject)));
            }
        }

        foreach (var node in shell.Folders.Where(f => f.Kind == FolderNodeKind.SearchFolder))
        {
            Log.Info($"Harness: search folder row “{node.Name}” — pane {node.Unread} unread "
                     + $"(shows “{node.UnreadDisplay}”).");
        }
    }

    // ---- The Send/Receive Progress dialog -----------------------------------------------------

    /// <summary>
    /// Selects one of the dialog's two tabs and writes what it holds:
    /// <c>MAILBOX_PROGRESS_TAB=errors</c>.
    /// </summary>
    /// <remarks>
    /// The dialog always opens on Tasks, so the Errors tab — which is where an account that could
    /// not be signed in to or whose certificate was refused says so — had no way of being reached
    /// by a capture at all. The rows are logged as well as shown: an error is a sentence a reader
    /// has to be able to act on, and a photograph of a list is a poor way to read one back.
    /// </remarks>
    private static void Phase4BProgressTab(Window dialog, string which)
    {
        var tabs = Descendants<TabControl>(dialog).FirstOrDefault();
        if (tabs is null) { Log.Info("Harness: the progress dialog has no tabs."); return; }

        tabs.SelectedIndex = which.Trim().ToLowerInvariant() switch { "errors" => 1, _ => 0 };
        dialog.UpdateLayout();

        var header = (tabs.SelectedItem as TabItem)?.Header?.ToString() ?? "?";
        Log.Info($"Harness: the progress dialog is on the “{header}” tab.");

        foreach (var list in Descendants<ItemsControl>(dialog))
        {
            if (list.ItemsSource is not System.Collections.IEnumerable rows) continue;
            foreach (var row in rows.Cast<object?>().OfType<string>())
            {
                Log.Info($"Harness: the progress dialog's Errors tab says “{row}”.");
            }
        }
    }

    // ---- Undo Send ---------------------------------------------------------------------------

    /// <summary>
    /// The undo toast over a message that is genuinely in the outbox, with the real callback
    /// behind it: <c>MAILBOX_UNDOSEND=offer</c>, <c>=undo</c>, or <c>=expire</c>.
    /// </summary>
    /// <remarks>
    /// The peek that existed handed <c>Offer</c> a no-op and an outbox id of zero, so the button
    /// could be photographed and never pressed — and the real path exits before the toast is on
    /// screen, so nothing else reached it either. This one finds a queued row, gives it a hold as
    /// the compose window does, and goes in through <see cref="OnQueued"/> — the same entry the
    /// compose window's Queued event uses. What the press did is then read out of the outbox
    /// table, which is the only answer that counts.
    /// <para>
    /// <c>expire</c> hands it a hold of a third of a second and leaves it alone: the toast's own
    /// quarter-second timer takes it past zero before the capture settles, which is how the
    /// "Message sent." branch is reached through the timer that really drives it rather than by
    /// posing the end state.
    /// </para>
    /// </remarks>
    private void Phase4BUndoSend(string spec)
    {
        if (DataContext is not ShellViewModel shell) return;

        // The open account first, then any other: a seeded store queues mail in the accounts that
        // have an outbox, which is not always the one the folder pane opened on, and a door that
        // reported "nothing queued" about a store that plainly has mail waiting would be reporting
        // its own arithmetic.
        var accounts = App.Accounts.All
            .OrderByDescending(a => string.Equals(a.Account.Address, shell.CurrentAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var found = accounts
            .Select(a => (Account: a, Item: a.Mail.Outbox(a.Account.Id).FirstOrDefault(o => o.State == OutboxState.Queued)))
            .FirstOrDefault(pair => pair.Item is not null);

        if (found.Account is not { } account || found.Item is not { } queued)
        {
            Log.Info("Harness: undo send — nothing queued in any outbox; pose a seeded store.");
            return;
        }

        var verb = spec.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        // The hold the compose window gives a message it has just queued, applied here for the
        // same reason: without one WithdrawOutbox refuses, because a row whose hold has passed is
        // a row that may already be going.
        var hold = verb == "expire" ? now.AddMilliseconds(330) : now.AddSeconds(App.UndoSend.Seconds);
        account.Mail.ScheduleOutbox(queued.Id, hold);

        var subject = Subject(account, queued.BlobId);
        Log.Info($"Harness: undo send — outbox #{queued.Id} “{subject}” in "
                 + $"{account.Account.Address}, held until {hold:HH:mm:ss.fff}.");

        OnQueued(new QueuedMessageEventArgs(account.Account.Address, queued.Id, hold, subject));

        if (verb != "undo") return;

        // Pressed at Background so the toast has laid out; PressLabelled says so if it has not.
        Dispatcher.UIThread.Post(
            () =>
            {
                PressLabelled(_undoSend, "Undo", "the undo-send toast");

                // The three halves of what Undo means: the row is gone from the outbox, the
                // status line says so, and the message comes back as a window — the last being
                // the reason anybody presses it.
                var windows = (Application.Current?.ApplicationLifetime
                        as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                    ?.Windows.Select(w => w.GetType().Name).ToList() ?? [];

                Log.Info($"Harness: undo send — the outbox now holds "
                         + $"{account.Mail.Outbox(account.Account.Id).Count} row(s); status “{shell.StatusRight}”; "
                         + $"windows: {string.Join(", ", windows)}; toast visible {_undoSend.IsVisible}.");
            },
            DispatcherPriority.Background);
    }

    /// <summary>The subject of a queued message, read back off the blob it was queued as.</summary>
    private static string Subject(OpenAccount account, long blobId)
    {
        try
        {
            if (account.Mail.LoadBlob(blobId) is not { } raw) return string.Empty;
            using var stream = new MemoryStream(raw);
            return MimeKit.MimeMessage.Load(stream).Subject ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Harness: a queued message would not parse.", ex);
            return string.Empty;
        }
    }
}
