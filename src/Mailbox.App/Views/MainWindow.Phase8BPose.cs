using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;

namespace Mailbox.App.Views;

/// <summary>
/// Two doors onto the panes: where each one actually ended up, and whether the To-Do Bar's task
/// section and the Tasks module agree about the store they both read.
/// </summary>
/// <remarks>
/// Both exist because a capture answers the wrong question. A picture of a narrow window shows
/// the reading pane's words over the bar's month grid and cannot say by how much, which column
/// gave way, or whether the bar is inside the window at all — and at 900 wide the part of the
/// bar that is missing is missing from the picture too, so the photograph of the fault looks
/// like a photograph of a bar with no close button. Numbers say it: every column's width, every
/// pane's rectangle in the window's own coordinates, and the two verdicts that matter — does a
/// pane hang past the right-hand edge, and does one draw over the one beside it.
/// <para>
/// The second door is the same argument about a different claim. The bar's task section and the
/// module's list are two views built from one store by two paths; "they agree" is a statement
/// about both, and a dump of either alone cannot be held to it. So both are read, in one run,
/// and a row that differs is marked rather than left for a reader to join by eye.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's doors. Called once, from the constructor.</summary>
    private void WirePhase8BPoses()
    {
        // MAILBOX_PANES=dump — every pane's geometry once the window has laid out. At
        // ApplicationIdle, one step below the Background the rest of the poses run at: a pane
        // measured before the arrangement those poses ask for is a measurement of the window
        // they were about to change.
        if (Environment.GetEnvironmentVariable("MAILBOX_PANES") is { Length: > 0 } panes)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => DumpPanes(panes.Trim()), DispatcherPriority.ApplicationIdle);
        }

        // MAILBOX_TODO_AGREE=1 — the bar's task rows beside the module's, from one store.
        if (Environment.GetEnvironmentVariable("MAILBOX_TODO_AGREE") is { Length: > 0 })
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is ShellViewModel shell) CompareToDoTasks(shell);
                },
                DispatcherPriority.ApplicationIdle);
        }

        // MAILBOX_MAIL_REMINDER=<yyyy-MM-ddTHH:mm> — a reminder on the selected message, at the
        // moment given. Ahead of the reminder check, which runs at Background.
        if (Environment.GetEnvironmentVariable("MAILBOX_MAIL_REMINDER") is { Length: > 0 } when)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is ShellViewModel shell) PoseMailReminder(shell, when.Trim());
                },
                DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// Puts a reminder on the selected message at a stated moment, and says what the store then
    /// holds.
    /// </summary>
    /// <remarks>
    /// The queue takes flagged mail, appointments and tasks, and only two of those three could
    /// ever be posed: the seed carries no message with a reminder time on it, and the one route
    /// to setting one is the Custom flag dialog's date and time pickers, which a capture cannot
    /// type into. So the mail third of the window had never been in a picture of it.
    /// <para>
    /// It writes through the shell's own <c>SetCustomFlag</c> — the method the dialog's OK
    /// calls, with the record the dialog builds — rather than into the store, so a pose proves
    /// the path a reader uses and the undo, the counts and the status line all happen too.
    /// </para>
    /// </remarks>
    private void PoseMailReminder(ShellViewModel shell, string when)
    {
        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            Log.Info("Harness: no message is selected — pose MAILBOX_SELECT as well.");
            return;
        }

        if (!DateTimeOffset.TryParse(
                when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var moment))
        {
            Log.Info($"Harness: “{when}” is not a moment — say yyyy-MM-ddTHH:mm.");
            return;
        }

        shell.SetCustomFlag(rows, new CustomFlag("Follow up", null, moment, moment));

        foreach (var row in rows)
        {
            var summary = shell.SummaryOf(row);
            Log.Info($"Harness: reminder on “{row.Subject}” — the store says "
                     + $"{(summary?.Reminder is { } stamp ? stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "none")}"
                     + $", flagged {summary?.IsFlagged}.");
        }

        Log.Info($"Harness: the mail queue now holds "
                 + $"{App.Accounts.All.Sum(a => a.Mail.DueReminders(Mailbox.Core.PosedClock.UtcNow).Count)} due message(s).");
    }

    /// <summary>
    /// Writes where every pane ended up, and says plainly when one is outside the window or over
    /// its neighbour.
    /// </summary>
    private void DumpPanes(string spec)
    {
        if (this.FindControl<Grid>("PaneGrid") is not { } grid)
        {
            Log.Info("Harness: no pane grid — the window has not built its panes.");
            return;
        }

        grid.UpdateLayout();

        Log.Info($"Harness: window {Width:0}x{Height:0}, client {ClientSize.Width:0}x{ClientSize.Height:0}; "
                 + $"pane grid {grid.Bounds.Width:0}x{grid.Bounds.Height:0} at "
                 + $"{Left(grid):0} of the window.");

        Log.Info("Harness: pane columns — " + string.Join(", ",
            grid.ColumnDefinitions.Select((c, i) => $"[{i}] {c.Width} = {c.ActualWidth:0}")));

        // The right-hand edge everything is measured against. The window's own width rather than
        // the grid's: a grid that itself overflows would otherwise report all its children as
        // comfortably inside it.
        var edge = ClientSize.Width;

        foreach (var name in new[] { "ListPane", "ReadingSplitter", "ReadingPane", "DockHost" })
        {
            if (this.FindControl<Control>(name) is not { } pane) continue;

            if (!pane.IsVisible || pane.Bounds.Width <= 0)
            {
                Log.Info($"Harness: pane {name} — not showing.");
                continue;
            }

            var left = Left(pane);
            var right = left + pane.Bounds.Width;

            Log.Info($"Harness: pane {name} — x {left:0}..{right:0} ({pane.Bounds.Width:0} wide), "
                     + $"window edge {edge:0}"
                     + (right > edge + 0.5 ? $"  ← {right - edge:0}px OUTSIDE THE WINDOW" : string.Empty));
        }

        // The one overlap worth naming: the reading pane and the bar share the right-hand half of
        // the window, and the bar is the one that loses.
        if (this.FindControl<Control>("ReadingPane") is { IsVisible: true } reading
            && this.FindControl<Control>("DockHost") is { IsVisible: true } bar
            && bar.Bounds.Width > 0 && reading.Bounds.Width > 0)
        {
            var over = Left(reading) + reading.Bounds.Width - Left(bar);
            Log.Info($"Harness: the reading pane ends {over:0}px "
                     + (over > 0.5 ? "PAST the To-Do Bar's left edge  ← OVERLAPS" : "before the To-Do Bar."));
        }

        // How the bar divided itself between its sections. The calendar section's height is
        // worked out from the peek's own layout plus a row per appointment, so an empty day is
        // the case where that sum has nothing to add and the section is whatever the month block
        // and its margins come to.
        if (this.FindControl<ContentControl>("DockHost")?.Content is ToDoBar sections)
        {
            foreach (var (name, section) in new (string, Control?)[]
                     { ("calendar", sections.Peek), ("tasks", sections.Tasks), ("people", sections.People) })
            {
                Log.Info(section is null
                    ? $"Harness: bar section {name} — off."
                    : $"Harness: bar section {name} — y {section.Bounds.Y:0}..{section.Bounds.Bottom:0} "
                      + $"({section.Bounds.Height:0} tall)"
                      + (name == "calendar" ? $", {sections.Peek!.Agenda.Count} appointment(s)" : string.Empty)
                      + ".");
            }
        }

        if (spec is "todobar" or "dump") LogToDoBar((ShellViewModel)DataContext!);
    }

    /// <summary>Where a control's left edge is, in the window's own coordinates.</summary>
    private double Left(Visual control)
        => control.TranslatePoint(new Point(0, 0), this) is { } point ? point.X : double.NaN;

    /// <summary>
    /// The To-Do Bar's task rows beside the Tasks module's, both read in this run, with a row
    /// that differs marked.
    /// </summary>
    /// <remarks>
    /// The module's rows come from the module itself rather than from a second call to the book:
    /// what is being checked is that the two <em>views</em> agree, and a comparison of one view
    /// against the reading it is built from proves only that a method is deterministic. The two
    /// cannot be on screen together — the bar lives in the mail module's cell and the module
    /// replaces it — so the module is built here, exactly as the rail builds it.
    /// </remarks>
    private void CompareToDoTasks(ShellViewModel shell)
    {
        var bar = (this.FindControl<ContentControl>("DockHost")?.Content as ToDoBar)?.Tasks;
        var module = EnsureTasks(shell);

        var mine = bar?.Rows ?? [];
        var theirs = module.Rows;

        Log.Info($"Harness: to-do bar {mine.Count} task(s), tasks module {theirs.Count} task(s)"
                 + (mine.Count == theirs.Count ? "." : "  ← DISAGREES on the count."));

        for (var i = 0; i < Math.Max(mine.Count, theirs.Count); i++)
        {
            var a = i < mine.Count ? mine[i] : null;
            var b = i < theirs.Count ? theirs[i] : null;

            var differs = a is null || b is null
                          || a.Key != b.Key
                          || a.Summary != b.Summary
                          || a.Band != b.Band
                          || a.IsComplete != b.IsComplete
                          || a.IsOverdue != b.IsOverdue
                          || a.DueText(CultureInfo.InvariantCulture) != b.DueText(CultureInfo.InvariantCulture);

            Log.Info($"Harness: to-do row {i} — bar {Said(a)}; module {Said(b)}"
                     + (differs ? "  ← DISAGREES" : string.Empty));
        }
    }

    /// <summary>One task row as a line: everything either view could disagree about.</summary>
    private static string Said(TaskRow? row) => row is null
        ? "nothing"
        : $"“{row.Summary}” [{row.Key}, {TaskBook.Heading(row.Band)}, due "
          + $"{(row.DueText(CultureInfo.InvariantCulture) is { Length: > 0 } due ? due : "none")}"
          + $", {(row.IsComplete ? "done" : "open")}{(row.IsOverdue ? ", overdue" : string.Empty)}]";
}
