using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.ViewModels;
using Mailbox.Core;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The tasks lane's doors: making a task the interface cannot make, filling the task window, and
/// reading back what the module, the task store and the mail stores each hold.
/// </summary>
/// <remarks>
/// Three things could not be reached before these.
/// <list type="bullet">
/// <item><description><b>A repeating task.</b> A task carries an RRULE all the way from the store
/// row to the text that leaves for a server, and no surface in the application can put one there —
/// so what a repeating task does when it is ticked had never been run at all.
/// <c>MAILBOX_TASK_MAKE</c> writes one through the module's own save path before the window opens,
/// which is the only way a run can then tick it.</description></item>
/// <item><description><b>The task window's form.</b> A pose could open it and photograph it; nothing
/// could read a field or press a button, so its five states, its percentage and what it writes back
/// were all inference off the source.</description></item>
/// <item><description><b>What kind each row on the list is.</b> The list draws tasks, flagged mail
/// and flagged contacts alike and the existing dump names none of them, so the one claim the
/// module rests on — which of the two lists a flagged message belongs on — could not be read
/// back.</description></item>
/// </list>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Wires this lane's doors. Called once from the shell's constructor.
    /// </summary>
    /// <remarks>
    /// The write runs here rather than from a posted action, and for a reason worth stating: the
    /// module pose builds the module and dumps its rows from the first pass of the dispatcher, so
    /// anything posted lands after the list has already been read and the new task is not in it.
    /// Writing before the window opens is what the reader's own store looks like at start-up.
    /// </remarks>
    private void WirePhase8ADoors()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_TASK_MAKE") is { Length: > 0 } make)
        {
            GuardedTaskDoor(() => PoseMakeTask(make));
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_TASK_PROBE") is not { Length: > 0 } probe) return;

        // Last of all, so the probe reports the module as the presses and the commands left it
        // rather than as it opened: Background is where MAILBOX_RUN acts, and ApplicationIdle is
        // below it.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(
                () => GuardedTaskDoor(() => PoseTaskProbe(probe)),
                DispatcherPriority.ApplicationIdle),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Runs a door and says so when it throws.
    /// </summary>
    /// <remarks>
    /// A posted action that throws leaves a run with a plausible capture, no error and nothing to
    /// grep — the trap this sweep has been caught by once already.
    /// </remarks>
    private static void GuardedTaskDoor(Action door)
    {
        try
        {
            door();
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: a task door failed.", ex);
        }
    }

    // ---- Making a task the interface cannot make ------------------------------------------------

    /// <summary>
    /// <c>MAILBOX_TASK_MAKE=summary=Water the plants;due=2026-08-14;rrule=FREQ=WEEKLY</c> — a task
    /// written into the default list through the module's own save path, then read back out of the
    /// store.
    /// </summary>
    /// <remarks>
    /// Fields, separated by <c>;</c> and in any order: <c>summary</c>, <c>due</c> and <c>start</c>
    /// as <c>yyyy-MM-dd</c>, <c>rrule</c> (an RRULE without its property name, its own <c>;</c>
    /// written as <c>,</c> — <c>FREQ=WEEKLY,INTERVAL=2</c>), <c>status</c> as one of the five
    /// stored words, <c>percent</c>, <c>priority</c> as high/normal/low, <c>categories</c>
    /// comma-separated, <c>reminder</c> in minutes, and <c>private</c>.
    /// <para>
    /// Several tasks in one pose are separated by <c>|</c>, since a run that needs two of them
    /// gets one window.
    /// </para>
    /// </remarks>
    private void PoseMakeTask(string spec)
    {
        // This pose writes to the PIM store, and a capture run's PIM store is the machine's own
        // unless MAILBOX_STORE says otherwise — the same refusal the Google Tasks pose makes, and
        // for the same reason: invented tasks have no business in a reader's real list.
        if (Environment.GetEnvironmentVariable("MAILBOX_STORE") is not { Length: > 0 })
        {
            Log.Warn("Harness: MAILBOX_TASK_MAKE writes to the store, so it wants MAILBOX_STORE posed as well.");
            return;
        }

        var lists = App.Pim.Collections(CollectionKind.Tasks);
        if ((lists.FirstOrDefault(l => l.IsDefault) ?? lists.FirstOrDefault()) is not { } into)
        {
            Log.Info("Harness: there is no task list to write into.");
            return;
        }

        foreach (var one in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = Fields(one);
            var task = new TaskItem
            {
                Uid = TaskItem.NewUid(),
                Summary = Field(fields, "summary") is { Length: > 0 } s ? s : "A task",
                Description = Field(fields, "notes") ?? string.Empty,
                Start = Day(Field(fields, "start")),
                Due = Day(Field(fields, "due")),

                // An RRULE is full of semicolons and the pose separator is one, so a pose writes
                // commas and they are put back here. Nothing else in a rule uses a comma except a
                // BYDAY list, which is why that form is spelled with its own separator in the
                // examples rather than left to guess.
                Rrule = Field(fields, "rrule")?.Replace(',', ';'),
                Progress = TodoCodec.ProgressFromWord(Field(fields, "status") ?? "not-started"),
                PercentComplete = int.TryParse(Field(fields, "percent"), CultureInfo.InvariantCulture, out var pc) ? pc : 0,
                Urgency = Field(fields, "priority")?.ToLowerInvariant() switch
                {
                    "high" => TaskUrgency.High,
                    "low" => TaskUrgency.Low,
                    _ => TaskUrgency.Normal,
                },
                Categories = (Field(fields, "categories") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                ReminderMinutes = int.TryParse(Field(fields, "reminder"), CultureInfo.InvariantCulture, out var rm) ? rm : null,
                IsPrivate = Field(fields, "private") is "1" or "true" or "yes",
                LastModified = PosedClock.UtcNow,
            };

            var written = SaveTask(task, collectionId: into.Id);
            Log.Info($"Harness: task {written.Id} written into “{into.DisplayName}” — {Describe(written)}");
        }
    }

    private static IReadOnlyDictionary<string, string> Fields(string spec)
    {
        var got = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0) continue;
            got[pair[..at].Trim()] = pair[(at + 1)..].Trim();
        }

        return got;
    }

    private static string? Field(IReadOnlyDictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : null;

    private static EventTime? Day(string? text)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? EventTime.Date(day)
            : null;

    /// <summary>Everything a stored task says about itself, on one line.</summary>
    private static string Describe(PimItem item)
    {
        var task = PimTodoCodec.FromItem(item);
        return $"“{task.Summary}”, {TodoCodec.ProgressWord(task.Progress)}, {task.PercentComplete}%, "
            + $"priority {task.Urgency}, start {Text(task.Start)}, due {Text(task.Due)}, "
            + $"completed {task.CompletedUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—"}, "
            + $"recurrence {task.Rrule ?? "none"}, reminder {task.ReminderMinutes?.ToString(CultureInfo.InvariantCulture) ?? "none"}, "
            + $"categories [{string.Join(", ", task.Categories)}], private {task.IsPrivate}, "
            + $"uid {task.Uid}, sync {item.SyncState}";
    }

    private static string Text(EventTime? time)
        => time is { } when ? when.Wall.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    // ---- The task window's form -----------------------------------------------------------------

    /// <summary>
    /// Wires the form doors onto a task window the shell has just built.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_TASK_FORM=status=Completed;percent=75|save</c>: the fields before, the fields the
    /// pose sets, the fields after, and then one of <c>save</c>, <c>delete</c> or <c>cancel</c>
    /// pressed through the button's own <c>Click</c> — the path a pointer takes.
    /// <para>
    /// Every field is read back whether or not the pose set one, because "what fields does this
    /// window have" is itself a question the audit asks and a photograph answers badly.
    /// </para>
    /// </remarks>
    internal static void WirePhase8AForm(TaskWindow window)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_TASK_FORM") is not { Length: > 0 } spec) return;

        var parts = spec.Split('|', StringSplitOptions.TrimEntries);
        var sets = parts[0];
        var press = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

        window.Opened += (_, _) => Dispatcher.UIThread.Post(
            () => GuardedTaskDoor(() =>
            {
                foreach (var (field, value) in window.FormFields)
                {
                    Log.Info($"Harness: task form before — {field}: {value}");
                }

                foreach (var pair in Fields(sets))
                {
                    Log.Info(window.SetFormField(pair.Key, pair.Value)
                        ? $"Harness: task form set {pair.Key} to “{pair.Value}”."
                        : $"Harness: the task form has no field called “{pair.Key}”.");
                }

                foreach (var (field, value) in window.FormFields)
                {
                    Log.Info($"Harness: task form after — {field}: {value}");
                }

                if (press.Length == 0) return;

                var wanted = press switch
                {
                    "save" => "Save & Close",
                    "delete" => "Delete",
                    _ => "Cancel",
                };

                var button = window.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => string.Equals(b.Content as string, wanted, StringComparison.Ordinal));

                if (button is null)
                {
                    Log.Info($"Harness: the task form has no “{wanted}” button.");
                    return;
                }

                Log.Info($"Harness: pressing “{wanted}” on the task form.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }),
            DispatcherPriority.Loaded);
    }

    // ---- What the module, the task store and the mail stores each hold ---------------------------

    /// <summary>
    /// <c>MAILBOX_TASK_PROBE=rows|store|mail</c>, or <c>all</c>.
    /// </summary>
    /// <remarks>
    /// <c>rows</c> says what kind each line of the list is, which nothing else does — the whole
    /// question of what belongs on which list is unanswerable without it. <c>store</c> reads the
    /// task collections straight out of the repository, so the list can be held against them
    /// rather than against itself. <c>mail</c> reads every account's flagged mail, which is the
    /// other half of the same claim and lives in three files the PIM store knows nothing about.
    /// </remarks>
    private void PoseTaskProbe(string spec)
    {
        if (DataContext is not ShellViewModel shell) return;
        var wanted = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant()).ToHashSet();
        var all = wanted.Contains("all");

        if (all || wanted.Contains("rows"))
        {
            var tasks = EnsureTasks(shell);
            Log.Info($"Harness: probe rows — the module is showing {tasks.Kind}, {tasks.Rows.Count} line(s), "
                + $"search “{tasks.Search}”, arranged by “{tasks.List.ArrangedBy}”.");

            foreach (var row in tasks.Rows)
            {
                var kind = row switch
                {
                    { IsMessage: true } => "flagged mail",
                    { IsContact: true } => "flagged contact",
                    _ => "task",
                };

                // The band and the group are two different facts once the list can be arranged by
                // something other than the due date: the band is when it is owed, the group is
                // the heading it is drawn under. A probe that said only one of them could not
                // tell a working arrangement from a list that had ignored the press.
                Log.Info($"Harness: probe row — {kind}, key {row.Key}, list {row.CollectionId}, "
                    + $"band {TaskBook.Heading(row.Band)}, group “{row.Group.Heading}”, "
                    + $"overdue {row.IsOverdue}, done {row.IsComplete}, "
                    + $"due {(row.Task.Due is { } d ? d.Wall.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—")}, "
                    + $"“{row.Summary}”.");
            }

            // The headings the list actually draws, in order — which is the arrangement's own
            // claim, and the one thing the row lines above cannot make on their own.
            Log.Info("Harness: probe headings — "
                + string.Join(" | ", tasks.Rows.Select(r => r.Group.Heading).Distinct(StringComparer.Ordinal)));

            Log.Info($"Harness: probe rows — {tasks.Rows.Count(r => !r.IsBorrowed)} task(s), "
                + $"{tasks.Rows.Count(r => r.IsMessage)} flagged message(s), "
                + $"{tasks.Rows.Count(r => r.IsContact)} flagged contact(s).");
        }

        if (all || wanted.Contains("store"))
        {
            foreach (var list in App.Pim.Collections(CollectionKind.Tasks))
            {
                var rows = App.Pim.Items(list.Id).Where(i => i.SyncState != PimSyncState.Deleted).ToList();
                Log.Info($"Harness: probe store — list {list.Id} “{list.DisplayName}”, "
                    + $"default {list.IsDefault}, shown {list.IsVisible}, {rows.Count} row(s).");

                foreach (var item in rows)
                {
                    Log.Info($"Harness: probe store row {item.Id} — {Describe(item)}");
                }
            }
        }

        if (!all && !wanted.Contains("mail")) return;

        foreach (var (address, mail) in App.Mailboxes())
        {
            foreach (var message in mail.FlaggedMessages(includeComplete: true))
            {
                Log.Info($"Harness: probe mail — {address} message {message.Id}, "
                    + $"{(message.FollowUpComplete ? "complete" : message.IsFlagged ? "flagged" : "unflagged")}, "
                    + $"due {message.FollowUpDue?.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—"}, "
                    + $"importance {message.Importance}, “{message.Subject}”.");
            }
        }
    }
}
