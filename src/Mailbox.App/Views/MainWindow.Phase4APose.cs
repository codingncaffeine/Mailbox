using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// Two doors the actions-and-undo sweep needed: pressing a button inside a dialog, and reading
/// the undo stack back.
/// </summary>
/// <remarks>
/// <b>Why a dialog needs its own press.</b> The peek doors open a dialog and photograph it, which
/// proves it was drawn. Several of the mail actions only finish <em>inside</em> one — Recover
/// Deleted Items restores from a list, the Custom flag dialog is where a reminder-bearing flag is
/// set, the Quick Steps editor is where a multi-action step is built — and a picture of an open
/// dialog says nothing about whether its buttons act. Nothing in the harness could press one, so
/// every one of those flows was unaudited by construction.
/// <para>
/// <b>Why the undo stack needs reading back.</b> The contract on <see cref="ShellViewModel.Undo"/>
/// says which commands record a step and which four deliberately record none. The status line
/// after Ctrl+Z reports only what came off the top, so a command that pushed nothing and a command
/// that pushed two are indistinguishable from outside: the second is the one that matters, because
/// a reader who presses Ctrl+Z once and gets half their action back has been lied to.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's poses. Called last, so its work lands after every other pose.</summary>
    private void WirePhase4APoses()
    {
        // The stack, after everything else has acted. ApplicationIdle for the reason the list dump
        // is: what it reports has to be the stack the run finished with.
        if (Environment.GetEnvironmentVariable("MAILBOX_UNDO") is { Length: > 0 } undo)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is ShellViewModel s) PoseUndoDump(s, undo);
                },
                DispatcherPriority.ApplicationIdle);
        }

        // Closing the window, which is not the same thing as the capture's own exit.
        //
        // A capture run ends in `IClassicDesktopStyleApplicationLifetime.Shutdown()`, and that is
        // a *forced* shutdown: Avalonia raises `ShutdownRequested` only on the unforced path, so
        // none of the handlers wired to it runs. "Empty Deleted Items folders when exiting" is one
        // of those handlers, which made it unprovable through the harness — a run with the switch
        // on left the folder full and looked exactly like a broken feature.
        //
        // MAILBOX_EXIT=close closes the window instead, which is what a reader does, and with
        // ShutdownMode.OnLastWindowClose that is the unforced path. The automatic capture is stood
        // down first, or it would photograph a window that is closing and then shut down under
        // this: the claim here is what the store holds afterwards, not a picture.
        if (Environment.GetEnvironmentVariable("MAILBOX_EXIT") == "close")
        {
            WindowCapture.AnotherWindowWillBeCaptured = true;

            Opened += (_, _) => Dispatcher.UIThread.Post(
                async () =>
                {
                    await Task.Delay(600);
                    Log.Info("Harness: exit — closing the window, so the shutdown handlers run.");
                    Close();
                },
                DispatcherPriority.ApplicationIdle);
        }

        // More than one row selected, which nothing could pose.
        //
        // MAILBOX_SELECT picks one row, and every action in the mail module takes a list. The
        // difference matters for more than coverage: a selection spanning two accounts goes
        // through ShellViewModel.Split, which calls the command once per account — so one press
        // records one undo step per account, and one Ctrl+Z takes back half of what was done.
        // Neither the multi-row path nor that hazard could be reached from the harness at all.
        //
        // Loaded priority, below the folder pose and above MAILBOX_RUN's Background, so a run
        // presses its commands on the selection this made.
        if (Environment.GetEnvironmentVariable("MAILBOX_SELECT_MANY") is { Length: > 0 } many)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() => PoseSelectMany(many), DispatcherPriority.Loaded);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_DIALOG_PRESS") is { Length: > 0 } steps)
        {
            // The hold is taken here, on the dispatcher, in the same pass the window is built —
            // before the capture's own timer can start counting. Taken later, the picture is
            // already gone and the process with it.
            var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
            Opened += (_, _) => _ = PressInDialogAsync(steps, hold);
        }

        // MAILBOX_DRAG_OUT=<part of a subject> builds the list's own drag payload over the
        // matching row and consumes its file half, which is what a drop target outside would
        // do — the one half of a drag a run can hold in its hands. The written file is read
        // back by size, which is the byte-exact claim; a click writes nothing, which the lazy
        // provider is the point of.
        if (Environment.GetEnvironmentVariable("MAILBOX_DRAG_OUT") is { Length: > 0 } dragOut)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(async () => await PoseDragOutAsync(dragOut), DispatcherPriority.ApplicationIdle);
        }
    }

    private async Task PoseDragOutAsync(string wanted)
    {
        try
        {
            if (DataContext is not ShellViewModel shell) return;

            var row = shell.Messages.FirstOrDefault(
                m => m.Subject.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                Log.Info($"Harness: no row matches “{wanted}” to drag.");
                return;
            }

            using var transfer = await TransferForAsync(shell, [row]);
            IDataTransfer payload = transfer;
            Log.Info($"Harness: the drag offers ids={payload.Contains(MessageDragFormat)}, "
                     + $"files={payload.Contains(DataFormat.File)}.");

            foreach (var item in payload.GetItems(DataFormat.File))
            {
                var raw = item.TryGetRaw(DataFormat.File);
                if (raw is not IStorageFile file)
                {
                    Log.Info($"Harness: a file item answered {raw?.GetType().Name ?? "null"}.");
                    continue;
                }

                var path = file.TryGetLocalPath();
                Log.Info($"Harness: the drop would take “{file.Name}” "
                         + $"({(path is null ? "no local path" : new FileInfo(path).Length + " bytes")}).");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the drag-out pose failed.", ex);
        }
    }

    /// <summary>
    /// Selects every row whose subject matches one of the given fragments:
    /// <c>MAILBOX_SELECT_MANY=Q3|briefing</c>, or <c>all</c> for the whole folder.
    /// </summary>
    /// <remarks>
    /// Through the list's own <c>SelectedItems</c>, which is what a Ctrl-click builds and what
    /// <c>SelectedRows</c> reads, so a command presses against the same object a reader's
    /// selection would hand it.
    /// </remarks>
    private void PoseSelectMany(string spec)
    {
        if (List is not { } list || DataContext is not ShellViewModel shell)
        {
            Log.Warn("Harness: select-many — there is no message list.");
            return;
        }

        var wanted = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var all = wanted is ["all"];

        var rows = shell.Messages
            .Where(m => all || wanted.Any(w => m.Subject.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (rows.Count == 0)
        {
            Log.Info($"Harness: select-many — nothing in {shell.SelectedFolderName} matches “{spec}”. "
                     + $"It holds: {string.Join(", ", shell.Messages.Select(m => m.Subject))}.");
            return;
        }

        list.SelectedItems?.Clear();
        foreach (var row in rows) list.SelectedItems?.Add(row);

        shell.SelectedRow = rows[0];
        shell.SelectedMessage = rows[0];

        Log.Info($"Harness: select-many — {list.SelectedItems?.Count ?? 0} row(s) selected across "
                 + $"{rows.Select(r => r.Address.Length > 0 ? r.Address : shell.CurrentAddress ?? "?").Distinct().Count()} "
                 + $"account(s): {string.Join(", ", rows.Select(r => $"“{r.Subject}”"))}.");
    }

    /// <summary>
    /// Presses an entry in the mail module's Categorize menu: a category's name — or several,
    /// comma-separated — or <c>clear</c>.
    /// </summary>
    /// <remarks>
    /// Through the shell's own <see cref="ShellViewModel.ToggleCategory"/>, which is what the menu
    /// item behind the name does, so what a pose proves is the command rather than a way round it.
    /// A name already on every selected row comes off again, because that entry is a toggle.
    /// </remarks>
    private static void PoseCategorizeMail(ShellViewModel shell, IReadOnlyList<MessageRow> rows, string spec)
    {
        if (rows.Count == 0)
        {
            Log.Info("Harness: categorize — nothing is selected.");
            return;
        }

        if (spec.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            shell.ClearCategories(rows);
        }
        else
        {
            foreach (var name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (shell.Categories().FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    is not { } category)
                {
                    Log.Info($"Harness: categorize — no category matching “{name}” is in the set of "
                             + $"{shell.Categories().Count}: {string.Join(", ", shell.Categories().Select(c => c.Name))}.");
                    continue;
                }

                shell.ToggleCategory(rows, category);
            }
        }

        // From the store rather than from the row: the claim is what was written, and the row in
        // hand may have been replaced by the reload the command asked for.
        foreach (var row in rows)
        {
            var carried = shell.CurrentAccountForCategories()?.Mail.CategoriesFor([row.Id]) is { } map
                          && map.TryGetValue(row.Id, out var assigned) && assigned.Count > 0
                ? string.Join(", ", assigned.Select(c => $"{c.Name} ({c.ColourToken})"))
                : "none";

            Log.Info($"Harness: categorize — “{row.Subject}” now carries {carried}; status “{shell.StatusRight}”.");
        }
    }

    /// <summary>
    /// Writes what Ctrl+Z would take back, deepest step first.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_UNDO=dump</c> reports it; <c>=undo</c> and <c>=redo</c> report it, press that
    /// many times — <c>undo:2</c> — and report it again, which is how "one press takes back one
    /// press" is checked on a command that is several operations underneath.
    /// </remarks>
    private static void PoseUndoDump(ShellViewModel shell, string spec)
    {
        void Write(string when) => Log.Info(
            $"Harness: undo {when} — {shell.Undo.Count} step(s) [{string.Join(" | ", shell.Undo.Descriptions)}], "
            + $"{shell.Undo.RedoCount} redoable [{string.Join(" | ", shell.Undo.RedoDescriptions)}]; "
            + $"next undo {shell.Undo.NextUndo ?? "nothing"}, next redo {shell.Undo.NextRedo ?? "nothing"}.");

        Write("holds");

        var text = spec.Trim();
        var colon = text.IndexOf(':');
        var verb = (colon > 0 ? text[..colon] : text).ToLowerInvariant();
        var times = colon > 0 && int.TryParse(text[(colon + 1)..], out var n) ? n : 1;

        if (verb is not ("undo" or "redo")) return;

        for (var i = 0; i < times; i++)
        {
            var did = verb == "undo" ? shell.Undo.Undo() : shell.Undo.Redo();
            Log.Info($"Harness: {verb} {i + 1} — {(did is null ? $"there is nothing to {verb}" : did)}.");
        }

        Write("now holds");
    }

    /// <summary>
    /// Presses buttons inside whichever dialog is open, in order.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_DIALOG_PRESS=Select All;Restore Selected Items</c>. Each step is a button's
    /// caption; <c>pick:&lt;text&gt;</c> or <c>pick:#2</c> chooses a row in the dialog's first list
    /// first, for a dialog with no Select All of its own, and <c>wait</c> gives an asynchronous
    /// press a beat to land.
    /// <para>
    /// The newest window each time, not the one found at the start: pressing Purge opens a
    /// confirmation over the dialog, and its button is the one the next step means. A real pointer
    /// press rather than <c>Command.Execute</c>, so what is proven is that the button is wired.
    /// </para>
    /// </remarks>
    private async Task PressInDialogAsync(string spec, IDisposable? hold)
    {
        try
        {
            var first = true;

            foreach (var step in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // The steps that want no dialog, taken first — a dialog that has done its work has
                // closed, and requiring one here meant the step after an OK could never run.
                if (step.Equals("wait", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(400);
                    continue;
                }

                if (step.Equals("undo", StringComparison.OrdinalIgnoreCase)
                    || step.Equals("redo", StringComparison.OrdinalIgnoreCase))
                {
                    // Ctrl+Z after a dialog has done its work. MAILBOX_UNDO runs at
                    // ApplicationIdle, which is still before an awaited dialog has been answered,
                    // so an action that only finishes inside one — Copy to Folder, a Quick Step's
                    // first run — could not have its undo step read back at all.
                    if (DataContext is ShellViewModel s) PoseUndoDump(s, step.ToLowerInvariant());
                    continue;
                }

                if (await DialogAsync() is not { } dialog)
                {
                    Log.Warn($"Harness: dialog press — no dialog is open for “{step}”.");
                    return;
                }

                // A window that has just appeared is not yet answering the pointer: a press in the
                // pass it opened in raised its handler and the handler did nothing, which read
                // exactly like a button that is not wired. Settle once, before the first step.
                if (first)
                {
                    first = false;
                    await Task.Delay(400);
                }

                dialog.UpdateLayout();

                if (step.StartsWith("pick:", StringComparison.OrdinalIgnoreCase))
                {
                    Pick(dialog, step["pick:".Length..].Trim());

                    // A settle before the next step, because a list's SelectedItems is built from
                    // its selection model rather than assigned: pressing the button that reads it
                    // in the same pass as the pick read an empty selection and reported the dialog
                    // as ignoring its own button.
                    await Task.Delay(250);
                    continue;
                }

                // Photograph the dialog rather than the shell behind it.
                //
                // MAILBOX_RUN has MAILBOX_CAPTURE_DIALOG for this; the row menu has nothing, so
                // every dialog reachable only from a context-menu entry — the Custom flag dialog,
                // Add Reminder, a Quick Step's first-time setup — was photographed as a picture of
                // the shell. Best placed last: the shot waits out this pose's hold, so what it
                // photographs is the dialog as the last step left it.
                if (step.Equals("shot", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"Harness: dialog press — photographing {dialog.GetType().Name}.");
                    CaptureNextWindow();
                    continue;
                }

                // What a dialog's own Select All button calls, called directly — for telling a
                // button that is not wired from a selection call that does nothing.
                if (step.Equals("selectall", StringComparison.OrdinalIgnoreCase))
                {
                    if (ListOf(dialog) is ListBox box) box.SelectAll();
                    await Task.Delay(250);
                    Report(dialog);
                    continue;
                }

                if (Buttons(dialog).FirstOrDefault(b => Reads(b, step)) is not { } button)
                {
                    Log.Warn($"Harness: dialog press — {dialog.GetType().Name} has no “{step}”. "
                             + $"It has: {string.Join(", ", Buttons(dialog).Select(Caption).Where(t => t.Length > 0))}.");
                    return;
                }

                Log.Info($"Harness: dialog press — “{Caption(button)}” in {dialog.GetType().Name}"
                         + $"{(button.IsEffectivelyEnabled ? string.Empty : " (which is greyed)")}.");

                Press(button, new Point(button.Bounds.Width / 2, button.Bounds.Height / 2));

                // A beat for a handler that awaits — Purge asks before it purges — and for the
                // dialog to rebuild whatever the press changed.
                await Task.Delay(250);
                Report(dialog);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the dialog press pose failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    /// <summary>The newest window that is not this one, once one is up — or null after two seconds.</summary>
    private async Task<Window?> DialogAsync()
    {
        for (var waited = 0; waited < 2000; waited += 50)
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
                && life.Windows.LastOrDefault(w => !ReferenceEquals(w, this) && w.IsVisible) is { } window)
            {
                return window;
            }

            await Task.Delay(50);
        }

        return null;
    }

    /// <summary>Every button the dialog draws, innermost last so a nested one is still reachable.</summary>
    private static List<Button> Buttons(Window dialog) => [.. dialog.GetVisualDescendants().OfType<Button>()];

    private static string Caption(Button button) => button.Content switch
    {
        string text => text,
        TextBlock block => block.Text ?? string.Empty,
        ContentControl { Content: string inner } => inner,
        _ => (button.Content as Control)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text
             ?? string.Empty,
    };

    /// <summary>Whether a button's caption is the one asked for — exact first, then contained.</summary>
    private static bool Reads(Button button, string wanted)
    {
        var caption = Caption(button).Replace("_", string.Empty);
        return caption.Equals(wanted, StringComparison.OrdinalIgnoreCase)
               || caption.Contains(wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The dialog's list — a <see cref="ListBox"/> in preference to anything else that selects.
    /// </summary>
    /// <remarks>
    /// A ComboBox is a <c>SelectingItemsControl</c> too, and Recover Deleted Items draws its
    /// account picker above its list: taking the first of either picked an account and reported it
    /// as having picked a message.
    /// </remarks>
    private static SelectingItemsControl? ListOf(Window dialog)
        => dialog.GetVisualDescendants().OfType<ListBox>().FirstOrDefault()
           ?? dialog.GetVisualDescendants().OfType<SelectingItemsControl>().FirstOrDefault(c => c is not ComboBox);

    /// <summary>Chooses a row in the dialog's list, by what it reads or by its index.</summary>
    private static void Pick(Window dialog, string wanted)
    {
        if (ListOf(dialog) is not { } list)
        {
            Log.Warn($"Harness: dialog press — {dialog.GetType().Name} draws no list to pick from.");
            return;
        }

        if (wanted.StartsWith('#') && int.TryParse(wanted[1..], out var index))
        {
            list.SelectedIndex = index;
        }
        else
        {
            // Matched on what the row draws rather than on the item behind it: a list bound to
            // records has no text of its own, and what a reader picks is what they can read.
            var found = list.GetRealizedContainers()
                .FirstOrDefault(c => c.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true));

            if (found is not null) list.SelectedItem = list.ItemFromContainer(found);
        }

        Log.Info($"Harness: dialog press — picked row {list.SelectedIndex} of "
                 + $"{list.ItemCount} in {dialog.GetType().Name}.");
    }

    /// <summary>What the dialog says after a press: its own status text, which is the read-back.</summary>
    private static void Report(Window dialog)
    {
        if (!dialog.IsVisible)
        {
            Log.Info("Harness: dialog press — the dialog closed.");
            return;
        }

        var lines = dialog.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0)
            .TakeLast(6);

        // The list's own selection beside the words, because "press Select All, then Restore" is
        // two claims and the second reports nothing about the first.
        var selection = ListOf(dialog) is { } list
            ? $"list holds {list.ItemCount}, "
              + $"{(list as ListBox)?.SelectedItems?.Count ?? (list.SelectedIndex >= 0 ? 1 : 0)} selected; "
            : string.Empty;

        Log.Info($"Harness: dialog press — {dialog.GetType().Name} {selection}now reads “{string.Join(" / ", lines)}”.");
    }
}
