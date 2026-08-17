using System.Globalization;
using Avalonia.Controls;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Tasks;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The Tasks module in the shell: switching to it, the workspace it puts in the window, and the
/// commands its ribbon presses.
/// </summary>
/// <remarks>
/// A partial of the shell for the reason the calendar's and People's halves are: it needs the
/// window's ribbon, its dialogs and its status line.
/// </remarks>
public partial class MainWindow
{
    private TasksWorkspace? _taskModule;

    /// <summary>The Tasks ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout TasksRibbon() => App.RibbonEdits.Apply(TasksRibbonLayout.Build());

    private TasksWorkspace EnsureTasks(ShellViewModel shell)
    {
        if (_taskModule is not null) return _taskModule;

        var workspace = new TasksWorkspace(App.Pim, CalendarToday, App.Mailboxes)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) => shell.ModuleStatusLeft = workspace.Status;
        workspace.TaskOpened += (_, row) => OpenToDo(shell, row);
        workspace.TaskToggled += (_, row) => ToggleToDo(shell, row);
        workspace.TaskTyped += (_, text) => AddTypedTask(shell, text);

        _taskModule = workspace;
        return workspace;
    }

    /// <summary>
    /// The To-Do Bar's tasks section: the same drawn list the module shows, over the same rows.
    /// </summary>
    /// <remarks>
    /// The list itself rather than something like it, which is the whole point of the pane — the
    /// tick box, the row that makes a task by being typed in and the bands all come with it, and
    /// a second implementation of them would be a second thing to keep right.
    /// </remarks>
    private TaskListView BuildToDoTasks(ShellViewModel shell)
    {
        var view = new TaskListView();
        var book = new TaskBook(App.Pim, App.Mailboxes);

        view.Rows = book.Rows(CalendarToday);
        view.TaskActivated += (_, row) => OpenToDo(shell, row);
        view.TaskToggled += (_, row) => ToggleToDo(shell, row);
        view.TaskTyped += (_, text) => AddTypedTask(shell, text);
        return view;
    }

    /// <summary>
    /// The Tasks module's commands. Returns false for anything it does not own, so the shell's
    /// own list carries on.
    /// </summary>
    private bool RunTaskCommand(ShellViewModel shell, CommandId id)
    {
        if (shell.Module != MailboxModule.Tasks) return false;
        var tasks = EnsureTasks(shell);

        switch (id.Value)
        {
            case "tasks.new":
                _ = NewTaskAsync(shell);
                return true;

            case "tasks.open" when tasks.Selected is { } open:
                OpenToDo(shell, open);
                return true;

            case "tasks.complete" when tasks.Selected is { } done:
                ToggleToDo(shell, done, complete: true);
                return true;

            // Delete deletes the thing; Remove from List takes it off the list without deleting
            // it. The two differ only on a flagged message — a task is nothing but its own entry
            // on the list, so removing one is deleting it.
            case "tasks.delete" when tasks.Selected is { } gone:
                if (gone.IsMessage) DeleteFlaggedMessage(shell, gone);
                else DeleteTask(shell, gone);
                return true;

            case "tasks.remove" when tasks.Selected is { } removed:
                if (removed.IsMessage) RemoveFlaggedMessage(shell, removed);
                else DeleteTask(shell, removed);
                return true;

            case "tasks.view.todo":
                tasks.SetView(TaskViewKind.Todo);
                shell.ModuleStatusLeft = tasks.Status;
                return true;

            case "tasks.view.simple":
                tasks.SetView(TaskViewKind.Simple);
                shell.ModuleStatusLeft = tasks.Status;
                return true;

            case "tasks.view.detailed":
                tasks.SetView(TaskViewKind.Detailed);
                shell.ModuleStatusLeft = tasks.Status;
                return true;

            case "tasks.categorize":
                CategorizeTask(shell);
                return true;

            // The rest of the Tags group. Each has two meanings, as everything on this bar does:
            // see MainWindow.TaskTags.cs.
            case "tasks.followup":
                ShowTaskFlagMenu(shell);
                return true;

            case "tasks.private":
                SetToDoPrivate(shell);
                return true;

            case "tasks.importance.high":
                SetToDoImportance(shell, TaskUrgency.High);
                return true;

            case "tasks.importance.low":
                SetToDoImportance(shell, TaskUrgency.Low);
                return true;

            // The reference's list holds flagged mail beside the tasks, and these three are what
            // it is for: answering the message the flag is on.
            case "tasks.reply" or "tasks.replyall" or "tasks.forward":
                if (tasks.Selected is not { IsMessage: true } answered)
                {
                    shell.StatusRight = "Reply, Reply All and Forward act on a flagged message; select one.";
                    return true;
                }

                RespondToFlaggedMessage(shell, answered, id.Value switch
                {
                    "tasks.reply" => Mailbox.Rendering.ReplyKind.Reply,
                    "tasks.replyall" => Mailbox.Rendering.ReplyKind.ReplyAll,
                    _ => Mailbox.Rendering.ReplyKind.Forward,
                });
                return true;

            case "tasks.new.email":
                RunCommand(MailCommands.NewEmail.Id);
                return true;

            default:
                return false;
        }
    }

    // ---- A row is a task or a flagged message ---------------------------------------------------

    /// <summary>Opening a row: the task window, or the message the flag is on.</summary>
    private void OpenToDo(ShellViewModel shell, TaskRow row)
    {
        if (row.IsMessage) OpenFlaggedMessage(shell, row);
        else _ = OpenTaskAsync(shell, row);
    }

    /// <summary>The tick box: finishing a task, or completing a message's follow-up.</summary>
    private void ToggleToDo(ShellViewModel shell, TaskRow row, bool? complete = null)
    {
        if (row.IsMessage) ToggleFlaggedMessage(shell, row, complete);
        else ToggleTask(shell, row, complete);
    }

    // ---- Writing -------------------------------------------------------------------------------

    /// <summary>
    /// Writes a task into a list and refreshes what is showing it.
    /// </summary>
    /// <remarks>
    /// The same shape <see cref="SaveAppointment"/> has, and for the same reasons: the change is
    /// queued rather than sent, so an edit made with the network down is a longer queue and not a
    /// lost edit.
    /// </remarks>
    internal PimItem SaveTask(TaskItem task, PimItem? existing = null, long? collectionId = null)
    {
        var list = collectionId ?? EnsureTasks((ShellViewModel)DataContext!).DefaultList().Id;
        var row = PimTodoCodec.ToItem(task, list, existing);
        var written = existing is null ? App.Pim.AddItem(row) : Store(row);

        App.PimSync.QueuePut(written);
        _taskModule?.Reload();
        RefreshToDoTasks();
        return written;

        PimItem Store(PimItem item)
        {
            App.Pim.UpdateItem(item);
            return item;
        }
    }

    /// <summary>
    /// The row at the top of the list: a subject and nothing else, due today — which is what the
    /// reference makes of a typed line, and why the row is there at all.
    /// </summary>
    private void AddTypedTask(ShellViewModel shell, string subject)
    {
        var written = SaveTask(new TaskItem
        {
            Uid = TaskItem.NewUid(),
            Summary = subject,
            Due = EventTime.Date(CalendarToday),
            LastModified = DateTimeOffset.UtcNow,
        });

        shell.StatusRight = $"“{subject}” added.";
        Log.Info($"Task {written.Id} added: {subject}.");
    }

    /// <summary>
    /// The tick box, and Mark Complete. Both settle on the same thing: a task that says it is
    /// done in all three of the ways a task can.
    /// </summary>
    private void ToggleTask(ShellViewModel shell, TaskRow row, bool? complete = null)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        var task = PimTodoCodec.FromItem(item);
        var done = complete ?? !task.IsComplete;
        SaveTask(PimTodoCodec.Complete(task, done, DateTimeOffset.UtcNow), item, item.CollectionId);

        shell.StatusRight = done ? $"“{task.Summary}” marked complete." : $"“{task.Summary}” put back.";
        Log.Info($"Task {item.Id} {(done ? "completed" : "reopened")}.");
    }

    private void DeleteTask(ShellViewModel shell, TaskRow row)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        // Remove, not Delete: on a server-backed list the row is kept, marked and queued, so a
        // delete made offline still reaches the server.
        App.PimSync.Remove(item);
        _taskModule?.Reload();
        RefreshToDoTasks();

        shell.StatusRight = $"“{item.Summary}” deleted.";
        Log.Info($"Task {item.Id} deleted.");
    }

    /// <summary>Opens the task window on a new task, and writes it if it is saved.</summary>
    private async Task NewTaskAsync(ShellViewModel shell)
    {
        var tasks = EnsureTasks(shell);
        var window = new TaskWindow(new TaskItem
        {
            Uid = TaskItem.NewUid(),
            Due = EventTime.Date(CalendarToday),
            LastModified = DateTimeOffset.UtcNow,
        });

        await window.ShowDialog(this);
        if (window.Result is not { } made) return;

        SaveTask(made, collectionId: tasks.DefaultList().Id);
        shell.StatusRight = $"“{made.Summary}” added.";
    }

    /// <summary>
    /// Opens a task by its row in the store, which is what the Reminders window has to hand.
    /// </summary>
    /// <remarks>
    /// It switches to the module first, as the calendar's own by-id opener does: a reminder that
    /// opened a window over the mail would leave the reader nowhere when it was closed.
    /// </remarks>
    internal async Task OpenTaskByIdAsync(ShellViewModel shell, long itemId)
    {
        if (App.Pim.Item(itemId) is not { } stored) return;

        SwitchModule(shell, MailboxModule.Tasks);
        var window = new TaskWindow(PimTodoCodec.FromItem(stored));
        await window.ShowDialog(this);

        if (window.Deleted)
        {
            App.PimSync.Remove(stored);
            _taskModule?.Reload();
            RefreshToDoTasks();
            return;
        }

        if (window.Result is not { } edited) return;
        SaveTask(edited, stored, stored.CollectionId);
        shell.StatusRight = $"“{edited.Summary}” saved.";
    }

    /// <summary>Opens a task that is already on the list.</summary>
    private async Task OpenTaskAsync(ShellViewModel shell, TaskRow row)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        var window = new TaskWindow(PimTodoCodec.FromItem(item));
        await window.ShowDialog(this);

        if (window.Deleted)
        {
            DeleteTask(shell, row);
            return;
        }

        if (window.Result is not { } edited) return;
        SaveTask(edited, item, item.CollectionId);
        shell.StatusRight = $"“{edited.Summary}” saved.";
    }

    /// <summary>
    /// The Tasks harness poses: the module opened on a chosen view, and what the list holds
    /// read back — a drawn list cannot be inspected any other way.
    /// </summary>
    private void PoseTasks(ShellViewModel shell)
    {
        var tasks = EnsureTasks(shell);

        if (Environment.GetEnvironmentVariable("MAILBOX_TASK_VIEW")?.Trim().ToLowerInvariant() is { Length: > 0 } view)
        {
            tasks.SetView(view switch
            {
                "simple" => TaskViewKind.Simple,
                "detailed" => TaskViewKind.Detailed,
                _ => TaskViewKind.Todo,
            });
        }

        Log.Info($"Harness: tasks showing {tasks.Kind}, {tasks.Status}.");
        foreach (var row in tasks.Rows)
        {
            Log.Info($"Harness: task “{row.Summary}” — {TaskBook.Heading(row.Band)}"
                + (row.IsOverdue ? ", overdue" : string.Empty)
                + (row.IsComplete ? ", done" : string.Empty)
                + $", due {(row.DueText(CultureInfo.InvariantCulture) is { Length: > 0 } d ? d : "—")}.");
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_TASK_PRESS") is { Length: > 0 } press)
        {
            PressTask(shell, tasks.List, press.Trim());
        }
    }

    /// <summary>
    /// Presses one thing in a to-do list — the module's, or the To-Do Bar's, which is the same
    /// control over the same rows: <c>tick:part of a subject</c> ticks that task's box,
    /// <c>open:…</c> opens it, and <c>type:some words</c> types into the row at the top and
    /// presses Enter. The store is read back afterwards, which is the claim.
    /// </summary>
    private void PressTask(ShellViewModel shell, TaskListView list, string spec)
    {
        // The window's own layout, not the list's: a control lays out inside its parent, and
        // the module has only just been put in one — a list of zero width hits nothing.
        UpdateLayout();

        if (spec.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
        {
            var text = spec["type:".Length..].Trim();
            AddTypedTask(shell, text);
            Log.Info($"Harness: the list now holds {list.Rows.Count}.");
            return;
        }

        var wanted = spec.Contains(':', StringComparison.Ordinal) ? spec[(spec.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim() : spec;
        var row = list.Rows.FirstOrDefault(r => r.Summary.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Log.Info($"Harness: no task matching “{wanted}” is on the list.");
            return;
        }

        if (spec.StartsWith("open:", StringComparison.OrdinalIgnoreCase))
        {
            CaptureNextWindow();
            OpenToDo(shell, row);
            return;
        }

        // select: presses the row itself rather than its tick box, which is what a command
        // pressed afterwards through MAILBOX_RUN acts on.
        if (spec.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
        {
            if (list.BoxOf(row.Key) is not { } line)
            {
                Log.Info($"Harness: “{row.Summary}” was not drawn — the list may not have laid out.");
                return;
            }

            Press(list, new Avalonia.Point(line.Center.X, line.Center.Y));
            Log.Info($"Harness: the list's selection is now “{list.Selected?.Summary ?? "—"}”"
                + (list.Selected?.IsMessage == true ? " (a flagged message)." : "."));
            return;
        }

        // The tick box, pressed where the view really drew it rather than called directly.
        if (list.TickOf(row.Key) is not { } box)
        {
            Log.Info($"Harness: “{row.Summary}” has no tick box drawn — the list may not have laid out.");
            return;
        }

        Press(list, box.Center);

        // Read back out of whichever store the row belongs to: a flagged message is in its
        // account's file and a task is in the PIM one, and the ids are not interchangeable.
        if (row.Message is { } message)
        {
            if (App.Accounts.Find(message.Account)?.Mail.GetMessage(message.MessageId) is { } after)
            {
                Log.Info($"Harness: “{after.Subject}” is now "
                    + $"{(after.FollowUpComplete ? "complete" : after.IsFlagged ? "flagged" : "unflagged")}, "
                    + $"due {after.FollowUpDue?.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—"}.");
            }

            return;
        }

        if (App.Pim.Item(row.ItemId) is { } written)
        {
            var task = PimTodoCodec.FromItem(written);
            Log.Info($"Harness: “{task.Summary}” is now {TodoCodec.ProgressWord(task.Progress)}, "
                + $"{task.PercentComplete}%, completed {task.CompletedUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—"}, "
                + $"sync {written.SyncState}.");
        }
    }
}
