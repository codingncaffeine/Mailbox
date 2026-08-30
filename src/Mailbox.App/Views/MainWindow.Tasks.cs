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

    /// <summary>
    /// The moment a change to a task is stamped with: the pinned one when a day has been pinned,
    /// the real clock otherwise.
    /// </summary>
    /// <remarks>
    /// Written through the posed clock rather than <c>DateTimeOffset.UtcNow</c> for the reason
    /// that clock exists. A task ticked under a pinned day recorded the machine's own date as its
    /// completion, so the row read back after a tick said something different every day it was
    /// run while everything around it stood still — the same fault the reminders queue had. The
    /// Tags group's flag presets already worked this way; the write paths did not.
    /// </remarks>
    private static DateTimeOffset TaskNowUtc => Mailbox.Core.PosedClock.UtcNow;

    /// <summary>The Tasks ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout TasksRibbon() => App.RibbonEdits.Apply(App.Plugins.InjectRibbon(TasksRibbonLayout.Build()));

    private TasksWorkspace EnsureTasks(ShellViewModel shell)
    {
        if (_taskModule is not null) return _taskModule;

        var workspace = new TasksWorkspace(App.Pim, CalendarToday, App.Mailboxes)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) =>
        {
            shell.ModuleStatusLeft = workspace.Status;

            // A module's own selection decides what its ribbon can do, the same way the message
            // list's does.
            RefreshCommandEnablement();
        };
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

    private TodayWorkspace? _today;

    /// <summary>
    /// The summary page: what an account's heading opens, and what takes it away again.
    /// </summary>
    /// <remarks>
    /// It covers the list and the reading pane and leaves the folder pane alone: the page is
    /// opened *from* that pane — an account's own heading is the row that shows it — and hiding the
    /// row that was clicked would be odd. The reference's own is a page in the mail module rather
    /// than a seventh module, and this is that.
    /// </remarks>
    private void ShowToday(ShellViewModel shell, string address)
    {
        var host = this.FindControl<ContentControl>("TodayHost")!;

        if (address.Length == 0)
        {
            host.Content = null;
            return;
        }

        // One account's day, because it is that account's heading that opened it.
        if (_today is null)
        {
            _today = new TodayWorkspace(
                App.Pim,
                () =>
                [
                    .. App.Accounts.All.Where(a =>
                        string.Equals(a.Account.Address, shell.TodayAccount, StringComparison.OrdinalIgnoreCase)),
                ],
                CalendarToday);

            _today.FolderRequested += (_, ask) => RevealFolder(shell, ask.Address, ask.Folder);

            // Without the module, which is what the page is: a folder line takes the reader to
            // that folder because that is what it names, and an appointment or a task line opens
            // the item over the page it was pressed on and leaves the page where it was.
            _today.AppointmentRequested += (_, id) => _ = OpenAppointmentByIdAsync(shell, id, andShowTheModule: false);
            _today.TaskRequested += (_, id) => _ = OpenTaskByIdAsync(shell, id, andShowTheModule: false);
        }

        _today.Reload();
        host.Content = _today;
        shell.ModuleStatusLeft = _today.Status;
        Log.Info($"Today: showing {address} — {_today.Status}.");
    }

    /// <summary>
    /// The harness's feed: a file parsed and delivered as a poll would deliver it.
    /// </summary>
    /// <remarks>
    /// What a run has to be able to prove is that an entry becomes a message in its own folder,
    /// unread, once — not that HTTP works, which is somebody else's software.
    /// </remarks>
    private void PoseFeed(ShellViewModel shell, string spec)
    {
        var parts = spec.Split('|', 2, StringSplitOptions.TrimEntries);
        var path = parts[0];

        // The feed store, not the first mail account. Feeds moved into a store of their own, and
        // this door had stayed pointed at the mail one — so a posed delivery landed where the
        // module cannot see it, and only the migration at the *next* launch brought it across. A
        // pose whose result appears one run late is worse than one that does nothing.
        if (FeedAccount() is not { } account)
        {
            Log.Info("Harness: no feed store to deliver a feed into.");
            return;
        }

        try
        {
            var channel = Mailbox.Core.Feeds.FeedParser.Parse(File.ReadAllText(path));
            var name = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : channel.Title;
            var feed = App.Feeds.Add(path, name);

            // The reader's mute filters go in with it. Without them a posed delivery files
            // everything, which is not what a poll does — so the one claim about muting that
            // matters, that a muted article is never filed at all, could not be posed.
            var delivered = Mailbox.Protocols.FeedReceiver.Deliver(
                account, feed, channel, Mailbox.Core.PosedClock.UtcNow,
                App.MailOptions.RulesOnFeeds ? App.Arrival : null,
                App.Mutes);
            shell.Refresh();

            Log.Info($"Harness: feed “{channel.Title}” delivered {delivered} of {channel.Items.Count} item(s) "
                + $"into {Mailbox.Protocols.FeedReceiver.RootFolder}/{name}; "
                + $"{App.Mutes.Live(Mailbox.Core.PosedClock.UtcNow).Count} mute filter(s) in force, "
                + $"kept out {string.Join(", ", App.Mutes.All.Select(f => $"“{f.Text}” {f.Muted}"))}.");

            foreach (var folder in account.Mail.Folders(account.Account.Id).Where(f => f.Name == name))
            {
                foreach (var message in account.Mail.Messages(folder.Id))
                {
                    Log.Info($"Harness: feed item “{message.Subject}” from {message.FromName}, "
                        + $"{(message.IsRead ? "read" : "unread")}.");
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Log.Info($"Harness: the feed at {path} could not be read — {ex.Message}");
        }
    }

    /// <summary>
    /// The harness's Google Tasks answer: a saved one applied as a poll would apply it.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_GOOGLE=&lt;path&gt;[|list name]</c>. The request half is HTTPS to somebody
    /// else's API and a capture run has no business making it; the half that has to be provable is
    /// what reaches the store — a task arriving, a tombstone removing one, and above all a merge
    /// keeping the priority and the categories Google has never heard of.
    /// <para>
    /// A list is made if the named one is not there, so a run needs no posed store beyond the
    /// ordinary one.
    /// </para>
    /// </remarks>
    private void PoseGoogleTasks(string spec)
    {
        // This pose writes to the PIM store, and a capture run's PIM store is the machine's own
        // unless MAILBOX_STORE says otherwise — the scratch settings and the in-memory keyring do
        // not cover it. So it refuses rather than putting invented tasks in a real task list,
        // which is exactly what it did once.
        if (Environment.GetEnvironmentVariable("MAILBOX_STORE") is not { Length: > 0 })
        {
            Log.Warn("Harness: MAILBOX_GOOGLE writes to the store, so it wants MAILBOX_STORE posed as well.");
            return;
        }

        var parts = spec.Split('|', 2, StringSplitOptions.TrimEntries);
        var path = parts[0];
        var name = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "My Tasks";

        try
        {
            var tasks = Mailbox.Google.GoogleTask.ReadAll(File.ReadAllText(path));

            var list = App.Pim.Collections().FirstOrDefault(
                           c => Mailbox.Google.GoogleTasks.Owns(c)
                                && string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                       ?? App.Pim.AddCollection(
                           Mailbox.Store.Pim.CollectionKind.Tasks, name, "#0078D4", "you@example.com",
                           Mailbox.Google.GoogleTasks.UrlFor("list-1"));

            // No API is built: what is exercised is the half of the sync that reads and writes,
            // handed the answer a request would have brought back.
            var (pulled, removed, conflicts, _) =
                Mailbox.Google.GoogleTasksSync.Apply(App.Pim, list, tasks);

            Log.Info(
                $"Harness: Google Tasks — {tasks.Count} in the answer; {pulled} written, {removed} removed, "
                + $"{conflicts.Count} conflict(s), into “{list.DisplayName}”.");

            foreach (var conflict in conflicts)
            {
                Log.Info($"Harness: Google Tasks conflict — here “{conflict.Summary}”, there “{conflict.TheirTitle}”.");
            }

            foreach (var row in App.Pim.Items(list.Id))
            {
                var task = Mailbox.Scheduling.PimTodoCodec.FromItem(row);
                Log.Info(
                    $"Harness: Google task “{task.Summary}” — {(task.IsComplete ? "done" : "open")}, "
                    + $"due {(task.Due is { } d ? d.Wall.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "none")}, "
                    + $"priority {task.Urgency}, categories [{string.Join(", ", task.Categories)}], "
                    + $"recurrence {task.Rrule ?? "none"}.");
            }

            // The module drew before this ran, so it is told to read the store again — the nav
            // pane picks the new list up on its own and the list of tasks does not.
            _taskModule?.Reload();
            RefreshToDoTasks();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            Log.Info($"Harness: the Google Tasks answer at {path} could not be read — {ex.Message}");
        }
    }

    /// <summary>Opens a folder by its account and name, which is what the summary page's links do.</summary>
    private void RevealFolder(ShellViewModel shell, string address, string folder)
    {
        var node = shell.Folders.FirstOrDefault(f => f.Name == folder && shell.FolderAddress(f) == address);
        if (node is null) return;

        shell.SelectedFolder = node;
    }

    /// <summary>
    /// Instant Search in whichever module is open — the fifth and sixth of the six §14 asks for.
    /// </summary>
    /// <remarks>
    /// One index behind all of them: <c>pim_fts</c>, which every PIM item is written into as it is
    /// saved and which nothing read until now. Each module narrows its own list to what the index
    /// found, because a module's list is what the reference's own search narrows — it does not open
    /// a seventh window to answer in.
    /// <para>
    /// The mail module is not here: its search is the message list's own, over its account's FTS5
    /// index, and it has been since Phase 6.
    /// </para>
    /// </remarks>
    private void SearchModule(ShellViewModel shell, string words)
    {
        switch (shell.Module)
        {
            case MailboxModule.People:
            {
                var people = EnsurePeople(shell);
                people.Search = words;
                shell.ModuleStatusLeft = people.Status;
                break;
            }

            case MailboxModule.Tasks:
            {
                var tasks = EnsureTasks(shell);
                tasks.Search = words;
                shell.ModuleStatusLeft = tasks.Status;
                break;
            }

            case MailboxModule.Notes:
            {
                var notes = EnsureNotes(shell);
                notes.Search = words;
                shell.ModuleStatusLeft = notes.Status;
                break;
            }

            case MailboxModule.Journal:
            {
                var journal = EnsureJournal(shell);
                journal.Search = words;
                shell.ModuleStatusLeft = journal.Status;
                break;
            }

            case MailboxModule.Calendar:
            {
                var calendar = EnsureCalendar(shell);
                calendar.Search = words;
                shell.ModuleStatusLeft = calendar.Status;
                break;
            }

            default:
                return;
        }

        shell.StatusRight = words.Length == 0
            ? string.Empty
            : $"{shell.ModuleStatusLeft} for “{words}”.";
        Log.Info($"Search: {shell.Module} — “{words}” → {shell.ModuleStatusLeft}.");
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
            // it. A task is nothing but its own entry on the list, so removing one is deleting
            // it, and the two only part company on a borrowed row. On a contact they meet again
            // for a different reason — see MainWindow.FlaggedContacts: deleting a person because
            // a to-do was ticked is not something to do on a guess.
            case "tasks.delete" when tasks.Selected is { } gone:
                if (gone.IsMessage) DeleteFlaggedMessage(shell, gone);
                else if (gone.IsContact) FlagFlaggedContact(shell, gone, null);
                else DeleteTask(shell, gone);
                return true;

            case "tasks.remove" when tasks.Selected is { } removed:
                if (removed.IsMessage) RemoveFlaggedMessage(shell, removed);
                else if (removed.IsContact) FlagFlaggedContact(shell, removed, null);
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

            case "tasks.new.items":
                ShowNewItemsMenu();
                return true;

            case "tasks.moveto":
                MoveTask(shell);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Move: the selected task to another task list.
    /// </summary>
    /// <remarks>
    /// The same shape the Notes module's Move has, and the same reason for it: a move between
    /// server-backed lists is a delete on one and a create on the other, so it goes through
    /// <c>PimSync.Move</c> rather than a column edit — which is what makes the change reach both
    /// servers instead of quietly relabelling the row here. A flagged message is not a task and
    /// has no list to move to; the button says so rather than moving the mail somewhere.
    /// </remarks>
    private void MoveTask(ShellViewModel shell)
    {
        var tasks = EnsureTasks(shell);
        if (tasks.Selected is not { } row)
        {
            shell.StatusRight = "Select a task first.";
            return;
        }

        if (row.IsMessage)
        {
            shell.StatusRight = "Move acts on a task; this row is a flagged message.";
            Log.Info("Move: the row is a flagged message — nothing moved.");
            return;
        }

        if (App.Pim.Item(row.ItemId) is not { } item) return;

        var lists = App.Pim.Collections(CollectionKind.Tasks).Where(l => l.Id != item.CollectionId).ToList();
        if (lists.Count == 0)
        {
            shell.StatusRight = "There is nowhere else to keep a task: this is the only list.";
            return;
        }

        void MoveTo(Collection list)
        {
            var moved = App.PimSync.Move(item, list.Id);
            _taskModule?.Reload();
            RebuildToDoBar(shell);
            shell.StatusRight = $"“{row.Summary}” moved to {list.DisplayName}.";
            Log.Info($"Task {item.Id} moved to {list.DisplayName} as {moved.Id}; "
                + $"the old row is {(App.Pim.Item(item.Id) is { } old ? old.SyncState.ToString() : "gone")}.");
        }

        // A menu is a surface no capture can show, so the harness names the list instead.
        if (Environment.GetEnvironmentVariable("MAILBOX_MOVE")?.Trim() is { Length: > 0 } posed)
        {
            if (lists.FirstOrDefault(l => l.DisplayName.Contains(posed, StringComparison.OrdinalIgnoreCase)) is not { } wanted)
            {
                Log.Info($"Harness: no task list matching “{posed}” to move “{row.Summary}” to.");
                return;
            }

            MoveTo(wanted);
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var list in lists)
        {
            var entry = new MenuItem { Header = list.DisplayName };
            var chosen = list;
            entry.Click += (_, _) => MoveTo(chosen);
            flyout.Items.Add(entry);
        }

        MenuProbe.Show("the move-task menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    // ---- A row is a task or a flagged message ---------------------------------------------------

    /// <summary>Opening a row: the task window, or the message the flag is on.</summary>
    private void OpenToDo(ShellViewModel shell, TaskRow row)
    {
        if (row.IsContact) OpenFlaggedContact(shell, row);
        else if (row.IsMessage) OpenFlaggedMessage(shell, row);
        else _ = OpenTaskAsync(shell, row);
    }

    /// <summary>The tick box: finishing a task, or completing a message's follow-up.</summary>
    private void ToggleToDo(ShellViewModel shell, TaskRow row, bool? complete = null)
    {
        if (row.IsMessage) ToggleFlaggedMessage(shell, row, complete);
        else if (row.IsContact) ToggleFlaggedContact(shell, row, complete);
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
        var written = Persisted("The task", () => existing is null ? App.Pim.AddItem(row) : Store(row));

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
            LastModified = TaskNowUtc,
        });

        shell.StatusRight = $"“{subject}” added.";
        Log.Info($"Task {written.Id} added.");
        Log.Debug($"Task {written.Id} is “{subject}”.");
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

        // Finishing a repeating task finishes this occurrence and moves the master to the next,
        // so the chore comes round again instead of the whole series dying under one tick.
        if (done && PimTodoCodec.CompleteOccurrence(task, TaskNowUtc, TimeZoneInfo.Local) is
            { Advanced: { } advanced } stepped)
        {
            SaveTask(stepped.Done, collectionId: item.CollectionId);
            SaveTask(advanced, item, item.CollectionId);
            shell.StatusRight = $"“{task.Summary}” marked complete; the next is due {stepped.Advanced!.Due?.Wall:d}.";
            Log.Info($"Task {item.Id} completed for this occurrence; the series moves on.");
            return;
        }

        SaveTask(PimTodoCodec.Complete(task, done, TaskNowUtc), item, item.CollectionId);

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
            LastModified = TaskNowUtc,
        });

        WirePhase8AForm(window);
        await window.ShowDialog(this);
        if (window.Result is not { } made) return;

        SaveTask(made, collectionId: tasks.DefaultList().Id);
        shell.StatusRight = $"“{made.Summary}” added.";
    }

    /// <summary>
    /// Opens a task by its row in the store, which is what the Reminders window has to hand.
    /// </summary>
    /// <remarks>
    /// It switches to the module first when it is asked to, as the calendar's own by-id opener
    /// does: a reminder that opened a window over the mail would leave the reader nowhere when it
    /// was closed.
    /// </remarks>
    /// <param name="andShowTheModule">
    /// Whether to take the window to Tasks on the way. A reminder wants that; the summary page
    /// does not — see <see cref="OpenAppointmentByIdAsync"/>, which the same argument is written
    /// out on.
    /// </param>
    internal async Task OpenTaskByIdAsync(ShellViewModel shell, long itemId, bool andShowTheModule = true)
    {
        if (App.Pim.Item(itemId) is not { } stored) return;

        if (andShowTheModule) SwitchModule(shell, MailboxModule.Tasks);
        var window = new TaskWindow(PimTodoCodec.FromItem(stored));
        WirePhase8AForm(window);
        await window.ShowDialog(this);

        if (window.Deleted)
        {
            App.PimSync.Remove(stored);
            _taskModule?.Reload();
            // The module drew before this ran, so it is told to read the store again — the nav
            // pane picks the new list up on its own and the list of tasks does not.
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
        WirePhase8AForm(window);
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

        if (Environment.GetEnvironmentVariable("MAILBOX_TASK_FOLDER") is { Length: > 0 } folder)
        {
            Log.Info(tasks.OpenFolderByName(folder.Trim())
                ? $"Harness: the tasks pane opened “{tasks.OpenFolderName}”."
                : $"Harness: no folder matching “{folder}” is on the tasks pane.");
        }

        Log.Info($"Harness: tasks showing {tasks.Kind} in “{tasks.OpenFolderName}”, {tasks.Status}.");
        Log.Info($"Harness: tasks pane lists [{string.Join(" | ", tasks.PaneNames)}].");
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

        // A flagged contact is a vCard, not a VTODO: read its follow-up columns rather than
        // parsing the card as a task — which is how a tick on one used to read back as a task
        // that was never started while the store said complete.
        if (row.Contact is not null)
        {
            if (App.Pim.Item(row.ItemId) is { } card)
            {
                Log.Info($"Harness: “{(card.FileAs.Length > 0 ? card.FileAs : card.Summary)}” is now "
                    + $"{(card.FollowUpComplete ? "complete" : card.FollowUpDue is not null ? "flagged" : "unflagged")}, "
                    + $"due {card.FollowUpDue?.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—"}, "
                    + $"sync {card.SyncState}.");
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
