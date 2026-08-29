using Avalonia.Controls;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;
using Mailbox.Scheduling;

namespace Mailbox.App.Views;

/// <summary>
/// The Tags group on the Tasks bar: Flag Task, Private and the two Importance buttons.
/// </summary>
/// <remarks>
/// The three that were drawn and inert. Each has two meanings, as everything on this bar does,
/// because the list holds tasks and flagged mail together:
/// <list type="bullet">
/// <item><b>Flag Task</b> — a task's flag <em>is</em> its due date, which is what the list is
/// arranged by; on a message it is the message's own follow-up flag, set through the same
/// arithmetic the mail module uses so the two cannot disagree about what "This Week" means.</item>
/// <item><b>Private</b> — RFC 5545's CLASS on the task. A message has no such mark, so the bar
/// says so rather than doing nothing.</item>
/// <item><b>Importance</b> — the task's PRIORITY, and a message's own importance column.</item>
/// </list>
/// <para>
/// Both Importance buttons toggle: pressing High on a task that is already high puts it back to
/// normal, which is what the reference's pair of pressed-in buttons does and what a bar with no
/// "Normal Importance" button has to do.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The moment the flag presets are worked out from — the pinned day when the harness pinned
    /// one, so a posed run flags the same date next year as it does today.
    /// </summary>
    private static DateTimeOffset FlagClock =>
        CalendarNow is { } live
            ? new DateTimeOffset(live, DateTimeOffset.Now.Offset)
            : new DateTimeOffset(CalendarToday.ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset);

    /// <summary>
    /// Flag Task: the reference's own flag menu over whichever kind of row is selected.
    /// </summary>
    /// <remarks>
    /// The presets are <see cref="QuickClickSettings.DueDate"/>'s, which is where the message
    /// list's flag column gets its dates — one arithmetic, so a task flagged "This Week" and a
    /// message flagged "This Week" fall due on the same day.
    /// <para>
    /// Set Quick Click… is not on this menu: a Quick Click is a single click in the message list's
    /// own Flag column, and there is no such column here.
    /// </para>
    /// </remarks>
    private void ShowTaskFlagMenu(ShellViewModel shell)
    {
        var tasks = EnsureTasks(shell);
        if (tasks.Selected is not { } row)
        {
            shell.StatusRight = "Select something on the list first.";
            return;
        }

        var now = FlagClock;

        // A menu is a surface no capture can show, so the harness presses one of its entries
        // instead and the store is read back — the same bargain the Categorize menu makes.
        if (Environment.GetEnvironmentVariable("MAILBOX_FLAG")?.Trim() is { Length: > 0 } posed)
        {
            PressFlagEntry(shell, row, posed, now);
            return;
        }

        var flyout = new MenuFlyout();

        void Preset(QuickFlag flag)
        {
            var item = new MenuItem { Header = QuickClickSettings.Label(flag), Icon = FlagArtwork() };
            item.Click += (_, _) => FlagToDo(shell, row, QuickClickSettings.DueDate(flag, now));
            flyout.Items.Add(item);
        }

        Preset(QuickFlag.Today);
        Preset(QuickFlag.Tomorrow);
        Preset(QuickFlag.ThisWeek);
        Preset(QuickFlag.NextWeek);
        Preset(QuickFlag.NoDate);

        var custom = new MenuItem { Header = "Custom…", Icon = FlagArtwork() };
        custom.Click += async (_, _) => await CustomFlagToDoAsync(shell, row);
        flyout.Items.Add(custom);

        flyout.Items.Add(new Separator());

        var complete = new MenuItem { Header = "Mark Complete", Icon = Tick() };
        complete.Click += (_, _) => ToggleToDo(shell, row, complete: true);
        flyout.Items.Add(complete);

        // On a task the flag is the due date, so clearing it and the No Date preset settle the
        // same way — the same shape Delete and Remove from List have on this list.
        var clear = new MenuItem { Header = "Clear Flag", IsEnabled = row.Task.Due is not null };
        clear.Click += (_, _) => FlagToDo(shell, row, null);
        flyout.Items.Add(clear);

        Log.Info($"Flag Task: the row is due {(row.Task.Due is { } due ? due.Wall.ToString("yyyy-MM-dd") : "—")}"
            + (row.IsMessage ? ", a flagged message." : "."));
        Log.Debug($"Flag Task: the row is “{row.Summary}”.");
        MenuProbe.Show("the task flag menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    /// <summary>
    /// The flag menu over anything that can carry a flag, in the reference's own order.
    /// </summary>
    /// <remarks>
    /// One menu built once, as the Categorize menu is: what differs between a task, a message and
    /// a contact is what the flag is written into, and that is the caller's business. What comes
    /// back is the due date the reader chose — or null for No Date and Clear Flag — and
    /// <paramref name="complete"/> for Mark Complete.
    /// </remarks>
    private void ShowFlagMenu(
        string subject,
        DateTimeOffset? current,
        Action<DateTimeOffset?> apply,
        Action complete)
    {
        var now = FlagClock;

        if (Environment.GetEnvironmentVariable("MAILBOX_FLAG")?.Trim() is { Length: > 0 } posed)
        {
            switch (posed.Replace(" ", string.Empty).ToLowerInvariant())
            {
                case "complete": complete(); break;
                case "clear" or "nodate": apply(null); break;
                case "today": apply(QuickClickSettings.DueDate(QuickFlag.Today, now)); break;
                case "tomorrow": apply(QuickClickSettings.DueDate(QuickFlag.Tomorrow, now)); break;
                case "thisweek": apply(QuickClickSettings.DueDate(QuickFlag.ThisWeek, now)); break;
                case "nextweek": apply(QuickClickSettings.DueDate(QuickFlag.NextWeek, now)); break;
                default:
                    Log.Info($"Harness: “{posed}” is not on the flag menu — say today, tomorrow, thisweek, nextweek, nodate, complete or clear.");
                    break;
            }

            return;
        }

        var flyout = new MenuFlyout();

        void Preset(QuickFlag flag)
        {
            var item = new MenuItem { Header = QuickClickSettings.Label(flag), Icon = FlagArtwork() };
            item.Click += (_, _) => apply(QuickClickSettings.DueDate(flag, now));
            flyout.Items.Add(item);
        }

        Preset(QuickFlag.Today);
        Preset(QuickFlag.Tomorrow);
        Preset(QuickFlag.ThisWeek);
        Preset(QuickFlag.NextWeek);
        Preset(QuickFlag.NoDate);

        flyout.Items.Add(new Separator());

        var done = new MenuItem { Header = "Mark Complete", Icon = Tick() };
        done.Click += (_, _) => complete();
        flyout.Items.Add(done);

        var clear = new MenuItem { Header = "Clear Flag", IsEnabled = current is not null };
        clear.Click += (_, _) => apply(null);
        flyout.Items.Add(clear);

        Log.Info($"Flag: the item is due {current?.LocalDateTime.ToString("yyyy-MM-dd") ?? "—"}.");
        Log.Debug($"Flag: the item is “{subject}”.");
        MenuProbe.Show("the flag menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    /// <summary>The harness's press of one entry on the flag menu.</summary>
    private void PressFlagEntry(ShellViewModel shell, TaskRow row, string spec, DateTimeOffset now)
    {
        switch (spec.Replace(" ", string.Empty).ToLowerInvariant())
        {
            case "complete":
                ToggleToDo(shell, row, complete: true);
                break;
            case "clear":
                FlagToDo(shell, row, null);
                break;
            case "today": FlagToDo(shell, row, QuickClickSettings.DueDate(QuickFlag.Today, now)); break;
            case "tomorrow": FlagToDo(shell, row, QuickClickSettings.DueDate(QuickFlag.Tomorrow, now)); break;
            case "thisweek": FlagToDo(shell, row, QuickClickSettings.DueDate(QuickFlag.ThisWeek, now)); break;
            case "nextweek": FlagToDo(shell, row, QuickClickSettings.DueDate(QuickFlag.NextWeek, now)); break;
            case "nodate": FlagToDo(shell, row, null); break;
            default:
                Log.Info($"Harness: “{spec}” is not on the flag menu — say today, tomorrow, thisweek, nextweek, nodate, complete or clear.");
                return;
        }

        ReadFlagBack(row);
    }

    /// <summary>
    /// Sets when a row is due: a task's own due date, or a message's follow-up flag.
    /// </summary>
    /// <remarks>
    /// A task keeps a date rather than an instant — its window writes one and its list draws one —
    /// so the preset's end-of-day time picks the day and nothing else. A message keeps the instant,
    /// which is what its own flag column shows and what its reminder is measured from.
    /// </remarks>
    private void FlagToDo(ShellViewModel shell, TaskRow row, DateTimeOffset? due)
    {
        if (row.Message is { } message)
        {
            if (AccountOf(message) is not { } account) return;

            if (due is null) account.Mail.ClearFollowUp([message.MessageId]);
            else account.Mail.SetFollowUp([message.MessageId], due);

            AfterFlaggedChange(shell);
            shell.StatusRight = due is { } when
                ? $"“{row.Summary}” is due {when.LocalDateTime:d}."
                : $"The flag is off “{row.Summary}”.";
            Log.Info($"Flag Task: message {message.MessageId} in {message.Account} due {due?.LocalDateTime.ToString("yyyy-MM-dd") ?? "—"}.");
            return;
        }

        if (row.IsContact)
        {
            FlagFlaggedContact(shell, row, due);
            return;
        }

        if (App.Pim.Item(row.ItemId) is not { } item) return;
        var task = PimTodoCodec.FromItem(item);

        SaveTask(
            task with
            {
                Due = due is { } date ? EventTime.Date(DateOnly.FromDateTime(date.LocalDateTime)) : null,
                LastModified = TaskNowUtc,
            },
            item,
            item.CollectionId);

        shell.StatusRight = due is { } day
            ? $"“{task.Summary}” is due {day.LocalDateTime:d}."
            : $"“{task.Summary}” has no due date.";
        Log.Info($"Flag Task: task {item.Id} due {due?.ToString("yyyy-MM-dd") ?? "—"}.");
    }

    /// <summary>
    /// Custom… on the flag menu: the reference's own dialog, over a task or a message.
    /// </summary>
    /// <remarks>
    /// The same dialog the message list opens, because it edits exactly what a task's flag is —
    /// a start date, a due date and a reminder. Its "Flag to" line is the one part a task has no
    /// field for: a task's own words are its subject, so what is chosen there is read for the
    /// message case and dropped for the task one.
    /// </remarks>
    private async Task CustomFlagToDoAsync(ShellViewModel shell, TaskRow row)
    {
        if (row.Message is { } message)
        {
            if (AccountOf(message) is not { } account) return;
            var summary = account.Mail.GetMessage(message.MessageId);

            var mail = new CustomFlagDialog(summary);
            await mail.ShowDialog(this);

            if (mail.Cleared) account.Mail.ClearFollowUp([message.MessageId]);
            else if (mail.Result is { } set) account.Mail.SetCustomFollowUp([message.MessageId], set.Type, set.Start, set.Due, set.Reminder);
            else return;

            AfterFlaggedChange(shell);
            shell.StatusRight = $"“{row.Summary}” flagged.";
            return;
        }

        // A card's flag is a due date and nothing else, so the dialog's start date and reminder
        // have nowhere to go — the same shape as its "Flag to" line, which a task has no field
        // for either. What it is for is picking a date that is not one of the five presets.
        if (row.IsContact)
        {
            var picked = new CustomFlagDialog(null);
            await picked.ShowDialog(this);

            if (picked.Cleared) FlagFlaggedContact(shell, row, null);
            else if (picked.Result is { } chosen) FlagFlaggedContact(shell, row, chosen.Due);
            return;
        }

        if (App.Pim.Item(row.ItemId) is not { } item) return;
        var task = PimTodoCodec.FromItem(item);

        var dialog = new CustomFlagDialog(null);
        await dialog.ShowDialog(this);

        if (dialog.Cleared)
        {
            FlagToDo(shell, row, null);
            return;
        }

        if (dialog.Result is not { } flag) return;

        SaveTask(
            task with
            {
                Start = flag.Start is { } start ? EventTime.Date(DateOnly.FromDateTime(start.LocalDateTime)) : null,
                Due = flag.Due is { } due ? EventTime.Date(DateOnly.FromDateTime(due.LocalDateTime)) : null,
                ReminderMinutes = ReminderBefore(flag.Reminder, flag.Due),
                LastModified = TaskNowUtc,
            },
            item,
            item.CollectionId);

        shell.StatusRight = $"“{task.Summary}” flagged.";
        Log.Info($"Flag Task: task {item.Id} set from the Custom dialog — due {flag.Due?.ToString("yyyy-MM-dd") ?? "—"}, "
            + $"reminder {flag.Reminder?.ToString("yyyy-MM-dd HH:mm") ?? "—"}.");
    }

    /// <summary>
    /// A reminder's instant as the minutes before the due date that RFC 5545 states one in.
    /// </summary>
    /// <remarks>
    /// A VALARM on a VTODO is relative to the DUE, so a reminder with no due date to hang from
    /// cannot be written at all — the dialog can ask for one and this is where it is refused.
    /// A reminder after the due date is kept as zero rather than as a negative offset, which would
    /// mean "after" to some readers and be dropped by others.
    /// </remarks>
    private static int? ReminderBefore(DateTimeOffset? reminder, DateTimeOffset? due)
    {
        if (reminder is not { } at || due is not { } deadline) return null;
        var minutes = (deadline - at).TotalMinutes;
        return minutes <= 0 ? 0 : (int)Math.Round(minutes);
    }

    // ---- Private ---------------------------------------------------------------------------

    /// <summary>
    /// Private on the selected row: the task is kept to oneself when the list is shared.
    /// </summary>
    /// <remarks>
    /// A toggle, and the ribbon draws no pressed state, so what it did is said on the status line
    /// and written into the task's own text — a reader of the list learns it from the task window's
    /// own tick.
    /// </remarks>
    private void SetToDoPrivate(ShellViewModel shell)
    {
        var tasks = EnsureTasks(shell);
        if (tasks.Selected is not { } row)
        {
            shell.StatusRight = "Select a task first.";
            return;
        }

        // Private is CLASS on a VTODO. A message's own privacy is a header its sender wrote and
        // not a mark its reader sets, and a contact's Private is on the card and means something
        // else — so a borrowed row says so in words, where the reference greys the button.
        if (row.IsBorrowed)
        {
            var kind = row.IsContact ? "a flagged contact" : "a flagged message";
            shell.StatusRight = $"Private marks a task; this row is {kind}.";
            Log.Info($"Private: the row is {kind} — nothing set.");
            return;
        }

        if (App.Pim.Item(row.ItemId) is not { } item) return;
        var task = PimTodoCodec.FromItem(item);
        var now = !task.IsPrivate;

        SaveTask(task with { IsPrivate = now, LastModified = TaskNowUtc }, item, item.CollectionId);

        shell.StatusRight = now ? $"“{task.Summary}” is private." : $"“{task.Summary}” is no longer private.";
        Log.Info($"Private: task {item.Id} is {(now ? "private" : "not private")}.");
    }

    // ---- Importance ------------------------------------------------------------------------

    /// <summary>
    /// High or Low Importance on the selected row, each a toggle back to normal.
    /// </summary>
    /// <remarks>
    /// A task's importance is RFC 5545's PRIORITY, which the task window's own Priority box sets
    /// and which the store keeps a column of; a message's is the column its list draws the mark
    /// from. Setting a message's is local — the header it arrived with is not rewritten.
    /// </remarks>
    private void SetToDoImportance(ShellViewModel shell, TaskUrgency urgency)
    {
        var tasks = EnsureTasks(shell);
        if (tasks.Selected is not { } row)
        {
            shell.StatusRight = "Select something on the list first.";
            return;
        }

        if (row.Message is { } message)
        {
            if (AccountOf(message) is not { } account) return;

            var was = account.Mail.GetMessage(message.MessageId)?.Importance ?? 1;
            var wanted = urgency == TaskUrgency.High ? 2 : 0;
            var level = was == wanted ? 1 : wanted;

            account.Mail.SetImportance([message.MessageId], level);
            AfterFlaggedChange(shell);

            shell.StatusRight = $"“{row.Summary}” is {Importance(level)} importance.";
            Log.Info($"Importance: message {message.MessageId} in {message.Account} is now {level}.");
            return;
        }

        // A card has no importance: a person is not high or low priority, and the reference's
        // own contact form offers no such field.
        if (row.IsContact)
        {
            shell.StatusRight = "Importance marks a task or a message; this row is a flagged contact.";
            Log.Info("Importance: the row is a flagged contact — nothing set.");
            return;
        }

        if (App.Pim.Item(row.ItemId) is not { } item) return;
        var task = PimTodoCodec.FromItem(item);
        var next = task.Urgency == urgency ? TaskUrgency.Normal : urgency;

        SaveTask(task with { Urgency = next, LastModified = TaskNowUtc }, item, item.CollectionId);

        shell.StatusRight = $"“{task.Summary}” is {next.ToString().ToLowerInvariant()} importance.";
        Log.Info($"Importance: task {item.Id} is {next} (PRIORITY {(task with { Urgency = next }).PriorityNumber}).");
    }

    /// <summary>The reference's three words for the column's three values.</summary>
    private static string Importance(int level) => level switch
    {
        2 => "high",
        0 => "low",
        _ => "normal",
    };

    /// <summary>What a row carries after a flag was pressed, out of whichever store holds it.</summary>
    private static void ReadFlagBack(TaskRow row)
    {
        if (row.Message is { } message)
        {
            if (AccountOf(message)?.Mail.GetMessage(message.MessageId) is not { } after) return;

            // The local date, not the stored instant's: a flag falls due at the end of a day
            // here, which west of Greenwich is the next day in UTC — and a harness line that
            // printed that would read as an off-by-one that is not there.
            Log.Info($"Harness: “{after.Subject}” is now "
                + $"{(after.FollowUpComplete ? "complete" : after.IsFlagged ? "flagged" : "unflagged")}, "
                + $"due {after.FollowUpDue?.LocalDateTime.ToString("yyyy-MM-dd") ?? "—"}.");
            return;
        }

        if (row.Contact is { } flagged)
        {
            if (App.Contacts.Repository.Item(flagged.ItemId) is not { } card) return;
            Log.Info($"Harness: “{flagged.Name}” is now "
                + $"{(card.FollowUpComplete ? "complete" : card.FollowUpDue is not null ? "flagged" : "unflagged")}, "
                + $"due {card.FollowUpDue?.LocalDateTime.ToString("yyyy-MM-dd") ?? "—"}.");
            return;
        }

        if (App.Pim.Item(row.ItemId) is not { } written) return;
        var task = PimTodoCodec.FromItem(written);
        Log.Info($"Harness: “{task.Summary}” is now due {(task.Due is { } due ? due.Wall.ToString("yyyy-MM-dd") : "—")}, "
            + $"{TodoCodec.ProgressWord(task.Progress)}, sync {written.SyncState}.");
    }
}
