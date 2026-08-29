using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Mailbox.App.ViewModels;
using Mailbox.Contacts;
using Mailbox.Controls.Calendar;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Three doors onto the small surfaces: the summary page's links, the People peek's two buttons,
/// and what the calendar peek's agenda is hiding below its own floor.
/// </summary>
/// <remarks>
/// <para>
/// All three exist because every one of these surfaces could be photographed and none of them
/// could be pressed. The summary page is made entirely of links — a folder line, an appointment
/// line, a task line — and not one of them had ever been followed by anything, so "it draws three
/// columns" was the whole of what was known about it; whether a line opens what it names, and
/// whether one of them opens nothing at all, are different questions and only a press answers
/// them. The People peek's corner button and its Search People box are buttons inside a control
/// the shell parks on a canvas: no command reaches them, and a capture cannot click.
/// </para>
/// <para>
/// The third is a measurement rather than a press. A peek photographed on a busy day looks
/// exactly like a peek photographed on a quiet one — the clip is silent — so the claim worth
/// reading back is arithmetic: how tall the day's agenda is, how much room it has, and therefore
/// whether anything is being hidden and whether the gutter's scrollbar is drawn.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's doors. Called once, from the constructor.</summary>
    private void WirePhase10BDoors()
    {
        // MAILBOX_SUMMARY=dump — the summary page's heading and every line of its three columns,
        // with the store's own counts beside them so the page can be held to what it read.
        if (Environment.GetEnvironmentVariable("MAILBOX_SUMMARY") is { Length: > 0 } summary)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => DumpSummaryPage(summary.Trim()), DispatcherPriority.ApplicationIdle);
        }

        // MAILBOX_SUMMARY_PRESS=<column>:<n> — one of its lines, pressed through its own button.
        // Below the dump, so a run can say what was there and then what pressing it did.
        if (Environment.GetEnvironmentVariable("MAILBOX_SUMMARY_PRESS") is { Length: > 0 } press)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => PressSummaryLine(press.Trim()), DispatcherPriority.ApplicationIdle);
        }

        // MAILBOX_PEOPLEPEEK_PRESS=corner|search|contact:<n>|dump — the People peek's own verbs.
        if (Environment.GetEnvironmentVariable("MAILBOX_PEOPLEPEEK_PRESS") is { Length: > 0 } people)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => PressPeoplePeek(people.Trim()), DispatcherPriority.ApplicationIdle);
        }

        // MAILBOX_PEEK_PROBE=agenda — what the calendar peek is drawing and what it is hiding.
        if (Environment.GetEnvironmentVariable("MAILBOX_PEEK_PROBE") is { Length: > 0 } probe)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => ProbePeek(probe.Trim()), DispatcherPriority.ApplicationIdle);
        }

        // MAILBOX_PEEK_HOVER=day:<yyyy-MM-dd>|corner — the pointer inside the peek, which is the
        // one state its own two hover tokens are for and which no capture had ever held.
        if (Environment.GetEnvironmentVariable("MAILBOX_PEEK_HOVER") is { Length: > 0 } hover)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => HoverPeek(hover.Trim()), DispatcherPriority.ApplicationIdle);
        }
    }

    // ---- The summary page --------------------------------------------------------------------

    /// <summary>The summary page, when it is the thing in the window.</summary>
    private TodayWorkspace? SummaryPage
        => this.FindControl<ContentControl>("TodayHost")?.Content as TodayWorkspace;

    /// <summary>
    /// Writes the page out line by line, and beside it what the stores it read hold.
    /// </summary>
    /// <remarks>
    /// The counts are read again here, from the repositories rather than from the page, because
    /// "the page agrees with the store" is a claim about two readings and a dump of one of them
    /// cannot be held to it. The mail half is counted the way the page counts it — unread in the
    /// Inbox, everything in Drafts and the Outbox — so a disagreement is the page's and not a
    /// difference of definition.
    /// </remarks>
    private void DumpSummaryPage(string spec)
    {
        if (SummaryPage is not { } page)
        {
            Log.Info("Harness: the summary page is not showing — pose MAILBOX_FOLDER=<an account's address>.");
            return;
        }

        if (DataContext is not ShellViewModel shell) return;

        Log.Info($"Harness: summary “{page.Heading}” for {shell.TodayAccount} — status “{page.Status}”, "
                 + $"{page.Lines.Count} line(s).");

        foreach (var line in page.Lines)
        {
            Log.Info($"Harness: summary {line.Column} · “{line.Text}”{(line.Acts ? string.Empty : " (not a link)")}");
        }

        if (!string.Equals(spec, "dump", StringComparison.OrdinalIgnoreCase)) return;

        foreach (var account in App.Accounts.All.Where(
                     a => string.Equals(a.Account.Address, shell.TodayAccount, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var folder in account.Mail.Folders(account.Account.Id)
                         .Where(f => f.Role is FolderRole.Inbox or FolderRole.Drafts or FolderRole.Outbox)
                         .OrderBy(f => f.Role))
            {
                Log.Info($"Harness: store {account.Account.Address} · {folder.Name} — "
                         + $"{folder.Unread} unread of {folder.Total}.");
            }
        }
    }

    /// <summary>
    /// Presses one line of the page, and says what happened afterwards rather than that it was
    /// pressed.
    /// </summary>
    private void PressSummaryLine(string spec)
    {
        if (SummaryPage is not { } page)
        {
            Log.Info("Harness: the summary page is not showing — pose MAILBOX_FOLDER=<an account's address>.");
            return;
        }

        if (DataContext is not ShellViewModel shell) return;

        var parts = spec.Split(':', 2, StringSplitOptions.TrimEntries);
        var column = parts[0];
        var index = parts.Length > 1 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var n) ? n : 0;

        var before = shell.SelectedFolder?.Name ?? "none";
        var showing = shell.IsTodayShowing;

        CaptureNextWindow();
        var pressed = page.Press(column, index);

        // At the next idle, so a window on its way up has been counted: a read-back taken in the
        // same beat as the press reports "windows: none" about a window that was opening.
        Dispatcher.UIThread.Post(
            () => Log.Info(
                $"Harness: summary {column}:{index} — {(pressed ? "pressed" : "no such line, or it is not a link")}; "
                + $"folder was “{before}”, now “{shell.SelectedFolder?.Name ?? "none"}”; "
                + $"summary page {(showing ? "was" : "was not")} showing and {(shell.IsTodayShowing ? "still is" : "is not now")}; "
                + $"module {shell.Module}, workspace "
                + $"{this.FindControl<ContentControl>("ModuleHost")?.Content?.GetType().Name ?? "none"}, "
                + $"status bar “{shell.StatusLeft}”; status “{shell.StatusRight}”; windows: {OtherWindows()}."),
            DispatcherPriority.ApplicationIdle);
    }

    // ---- The People peek ---------------------------------------------------------------------

    /// <summary>
    /// Presses one of the People peek's own buttons, or opens somebody on it, and reads back what
    /// it did.
    /// </summary>
    private void PressPeoplePeek(string spec)
    {
        // The same list in the other place it is drawn: the To-Do Bar's People section, which is
        // where the corner button puts it and so where a reader keeps their favourites.
        if (spec.StartsWith("barmenu:", StringComparison.OrdinalIgnoreCase))
        {
            PressBarPeopleMenu(spec["barmenu:".Length..].Trim());
            return;
        }

        if (_peekPopup is not PeoplePeek peek)
        {
            Log.Info("Harness: the People peek is not open — pose MAILBOX_PEEK=peoplepeek as well.");
            return;
        }

        if (DataContext is not ShellViewModel shell) return;

        Log.Info($"Harness: the People peek holds {peek.Rows.Count} favourite(s)"
                 + (peek.Rows.Count == 0
                     ? "."
                     : $": {string.Join(" | ", peek.Rows.Select(r => r.Named()))}."));

        if (string.Equals(spec, "dump", StringComparison.OrdinalIgnoreCase)) return;

        if (spec.StartsWith("menu:", StringComparison.OrdinalIgnoreCase))
        {
            PressPeoplePeekMenu(shell, peek, spec["menu:".Length..].Trim());
            return;
        }

        if (spec.StartsWith("contact:", StringComparison.OrdinalIgnoreCase))
        {
            var index = int.TryParse(spec["contact:".Length..].Trim(), CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (index < 0 || index >= peek.Rows.Count)
            {
                Log.Info($"Harness: the People peek has no favourite {index}.");
                return;
            }

            var row = peek.Rows[index];
            CaptureNextWindow();
            OpenPeoplePeekContact(shell, row);
            Dispatcher.UIThread.Post(
                () => Log.Info($"Harness: the People peek opened “{row.Named()}”; windows: {OtherWindows()}."),
                DispatcherPriority.ApplicationIdle);
            return;
        }

        if (!peek.Press(spec))
        {
            Log.Info($"Harness: “{spec}” is not a People peek press — say corner, search, contact:0 or dump.");
            return;
        }

        Dispatcher.UIThread.Post(
            () => Log.Info(
                $"Harness: People peek {spec} — the peek is {(_peekPopup is null ? "closed" : "still open")}, "
                + $"the bar's People section is {(shell.ArePeopleDocked ? "on" : "off")}, "
                + $"the module is {shell.Module}, search “{shell.SearchText}”; windows: {OtherWindows()}."),
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Right-clicks a favourite in the People peek, which is where the peek's own empty-list
    /// sentence sends a reader.
    /// </summary>
    /// <remarks>
    /// The press has to be a real right button at a point the list really drew at, and the
    /// read-back has to separate two different negatives: a click that missed the row, and a
    /// click that landed and found nothing wired to it. The list's own selection is what tells
    /// them apart — it moves inside the control before the menu is asked for, whether or not
    /// anybody is listening — so a run that reports the row selected and no menu is reporting on
    /// the wiring rather than on its own aim.
    /// </remarks>
    private void PressPeoplePeekMenu(ShellViewModel shell, PeoplePeek peek, string which)
    {
        var index = int.TryParse(which, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (peek.List.BoxOf(index) is not { } box)
        {
            peek.UpdateLayout();
            if (peek.List.BoxOf(index) is null)
            {
                Log.Info($"Harness: the People peek drew no row {index} to right-click.");
                return;
            }

            box = peek.List.BoxOf(index)!.Value;
        }

        var before = _people is null ? "not built" : "built";
        RightPress(peek.List, box.Center);

        Dispatcher.UIThread.Post(
            () => Log.Info(
                $"Harness: right-clicked row {index} in the People peek — "
                + $"the list selected “{peek.List.Selected?.Named() ?? "nobody"}”, "
                + $"the People module was {before} and is {(_people is null ? "not built" : "built")}, "
                + $"module {shell.Module}; windows: {OtherWindows()}."),
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Right-clicks a favourite in the To-Do Bar's People section.</summary>
    private void PressBarPeopleMenu(string which)
    {
        if (DataContext is not ShellViewModel shell) return;

        if ((this.FindControl<ContentControl>("DockHost")?.Content as ToDoBar)?.People is not { } list)
        {
            Log.Info("Harness: the To-Do Bar has no People section — pose MAILBOX_PEEK=todopeople as well.");
            return;
        }

        list.UpdateLayout();
        var index = int.TryParse(which, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (list.BoxOf(index) is not { } box)
        {
            Log.Info($"Harness: the To-Do Bar's People section drew no row {index} — it holds {list.Count}.");
            return;
        }

        var before = _people is null ? "not built" : "built";
        RightPress(list, box.Center);

        Dispatcher.UIThread.Post(
            () => Log.Info(
                $"Harness: right-clicked row {index} in the To-Do Bar's People section — "
                + $"the list selected “{list.Selected?.Named() ?? "nobody"}”, "
                + $"the People module was {before} and is {(_people is null ? "not built" : "built")}, "
                + $"module {shell.Module}; windows: {OtherWindows()}."),
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>One right-button click at a point a drawn view drew at.</summary>
    private static void RightPress(Control view, Point point)
    {
        var root = TopLevel.GetTopLevel(view) as Visual ?? view;
        var at = view.TranslatePoint(point, root) ?? point;

        var pointer = new Pointer(4, PointerType.Mouse, isPrimary: true);
        var down = new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased);

        view.RaiseEvent(new PointerPressedEventArgs(view, pointer, root, at, 0, down, KeyModifiers.None));
        view.RaiseEvent(new PointerReleasedEventArgs(view, pointer, root, at, 1, up, KeyModifiers.None, MouseButton.Right));
    }

    /// <summary>
    /// Opens a favourite from the peek the way the peek's own handler does.
    /// </summary>
    /// <remarks>
    /// Through the same two steps — close the popup, then open the card — because a run that only
    /// opened the window would leave a popup on screen the reader's own press takes away, and the
    /// capture would then be of a state no reader can reach.
    /// </remarks>
    private void OpenPeoplePeekContact(ShellViewModel shell, ContactRow row)
    {
        ClosePeek();
        _ = OpenContactAsync(shell, row);
    }

    // ---- The calendar peek's agenda ----------------------------------------------------------

    /// <summary>
    /// Puts the pointer inside the peek — over a day of its grid, or over its corner button.
    /// </summary>
    /// <remarks>
    /// Both states are drawn from tokens every theme states and neither had ever been in a
    /// picture: the peek repaints for its own hover and no pose could put a pointer in one. The
    /// move is raised at the view, so the control's own hit testing decides what is under it,
    /// which is the difference between photographing the hover and photographing a flag.
    /// </remarks>
    private void HoverPeek(string spec)
    {
        var peek = _floatingPeek ?? DockedPeek;
        if (peek is null)
        {
            Log.Info("Harness: no peek is open — pose MAILBOX_PEEK=calendar or =docked as well.");
            return;
        }

        peek.UpdateLayout();

        Point at;
        if (spec.StartsWith("day:", StringComparison.OrdinalIgnoreCase))
        {
            var when = spec["day:".Length..].Trim();
            if (!DateOnly.TryParseExact(when, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || peek.BoxOf(day) is not { } cell)
            {
                Log.Info($"Harness: {when} is not a date on the peek's grid.");
                return;
            }

            at = cell.Center;
        }
        else if (string.Equals(spec, "corner", StringComparison.OrdinalIgnoreCase))
        {
            at = peek.CornerBox.Center;
        }
        else
        {
            Log.Info($"Harness: “{spec}” is not a peek hover — say day:yyyy-MM-dd or corner.");
            return;
        }

        var root = TopLevel.GetTopLevel(peek) as Visual ?? peek;
        peek.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, peek, new Pointer(5, PointerType.Mouse, isPrimary: true),
            root, peek.TranslatePoint(at, root) ?? at, 0, new PointerPointProperties(), KeyModifiers.None));

        Log.Info($"Harness: the pointer is over the peek at {at.X:0},{at.Y:0} ({spec}).");
    }

    /// <summary>
    /// What the peek's agenda is drawing, how much room it has, and what it is hiding.
    /// </summary>
    private void ProbePeek(string spec)
    {
        var peek = _floatingPeek ?? DockedPeek;
        if (peek is null)
        {
            Log.Info("Harness: no peek is open — pose MAILBOX_PEEK=calendar or =docked as well.");
            return;
        }

        peek.UpdateLayout();

        if (!string.Equals(spec, "agenda", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Harness: “{spec}” is not a peek probe — say agenda.");
            return;
        }

        var layout = new PeekLayout(
            peek.IsDocked,
            peek.IsDocked ? Math.Max(0, peek.Bounds.Width - PeekLayout.DividerWidth) : PeekLayout.PopupWidth,
            peek.ShowWeekNumbers);

        Log.Info($"Harness: peek {(peek.IsDocked ? "docked" : "floating")} {peek.Bounds.Width:0}x{peek.Bounds.Height:0}, "
                 + $"gutter {layout.Gutter:0}, grid at {layout.Grid.X:0},{layout.Grid.Y:0} {layout.Grid.Width:0} wide, "
                 + $"agenda from {layout.AgendaTop:0} for {layout.AgendaWidth:0}.");

        Log.Info($"Harness: peek agenda {peek.Selected:yyyy-MM-dd} holds {peek.Agenda.Count}, "
                 + $"scrolled {peek.Scroll:0}, hidden {peek.Overflow:0}px, "
                 + $"scrollbar {(peek.Overflow > 0 ? "drawn" : "not drawn")}.");

        foreach (var row in peek.Agenda)
        {
            Log.Info($"Harness: peek agenda · {row.Time} {row.Subject}");
        }
    }
}
