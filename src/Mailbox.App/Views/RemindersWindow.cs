using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// One line in the Reminders window: a flagged message, or an appointment, whose reminder time
/// has come.
/// </summary>
/// <remarks>
/// One queue across the modules is the point (§9) — the reference's window mixes mail, meetings
/// and tasks in one list with one Dismiss All — so this carries either rather than the window
/// holding two lists that would have to be interleaved by hand at every use.
/// </remarks>
public sealed record DueReminder
{
    /// <summary>The account a flagged message is in, or null for an appointment.</summary>
    public OpenAccount? Account { get; init; }

    public MessageSummary? Message { get; init; }

    /// <summary>The appointment, or null when this is a message or a task.</summary>
    public DueAppointment? Appointment { get; init; }

    /// <summary>The task, or null when this is a message or an appointment.</summary>
    public DueTask? Task { get; init; }

    public bool IsAppointment => Appointment is not null;

    public bool IsTask => Task is not null;

    /// <summary>A flagged message — which is what a reminder is when it is neither of the others.</summary>
    public bool IsMessage => Message is not null;

    public static DueReminder ForMessage(OpenAccount account, MessageSummary message)
        => new() { Account = account, Message = message };

    public static DueReminder ForAppointment(DueAppointment appointment)
        => new() { Appointment = appointment };

    public static DueReminder ForTask(DueTask task)
        => new() { Task = task };

    public string Subject => Named(
        IsAppointment ? Appointment!.Summary : IsTask ? Task!.Summary : Message!.Subject);

    private static string Named(string subject) => subject.Length > 0 ? subject : "(no subject)";

    /// <summary>The moment this item stands at: a meeting's start, a task's or a flag's due date.</summary>
    public DateTimeOffset? When
        => IsAppointment ? Appointment!.StartsUtc : IsTask ? Task!.DueUtc : Message!.FollowUpDue;

    /// <summary>
    /// The header block's second line — "12:00 AM Wednesday, June 27, 2012" in the reference.
    /// </summary>
    public string WhenSaid => When is { } when
        ? when.ToLocalTime().ToString("h:mm tt dddd, MMMM d, yyyy", CultureInfo.CurrentCulture)
        : "No due date";

    /// <summary>
    /// The header block's third line, which the reference draws as a link. Only a meeting has
    /// one; a task and a flagged message have nowhere to be, and the line is left out rather
    /// than drawn empty.
    /// </summary>
    public string Where => IsAppointment ? Appointment!.Location : string.Empty;

    /// <summary>The icon beside the block, and on the row: which of the three kinds this is.</summary>
    public string IconName => IsAppointment ? "calendar" : IsTask ? "todo-list" : "flag";

    /// <summary>"Due in 2 hours", "Overdue by 15 minutes", "No due date" — the reference's column.</summary>
    public string DueIn(DateTimeOffset now)
    {
        var when = IsAppointment ? Appointment!.StartsUtc : IsTask ? Task!.DueUtc : Message!.FollowUpDue;
        if (when is not { } due) return "No due date";

        var span = due - now;
        var overdue = span < TimeSpan.Zero;
        span = span.Duration();

        // The count is worked out once and the plural taken from it. Said twice, the two
        // disagreed for anything under a minute: the number was floored to 1 and the "s" was
        // decided from the unfloored 0, so a reminder half a minute away read "Due in 1 minutes".
        var (count, unit) = span.TotalDays >= 1 ? ((int)span.TotalDays, "day")
            : span.TotalHours >= 1 ? ((int)span.TotalHours, "hour")
            : (Math.Max(1, (int)span.TotalMinutes), "minute");

        var words = $"{count} {unit}{(count == 1 ? string.Empty : "s")}";

        return overdue
            ? (IsAppointment ? $"Started {words} ago" : $"Overdue by {words}")
            : $"Due in {words}";
    }
}

/// <summary>
/// The Reminders window (§9): one queue of what is due — flagged mail, appointments and tasks
/// together — with Dismiss, Dismiss All, Open Item and Snooze.
/// </summary>
/// <remarks>
/// One instance, kept while the shell runs and shown when something is due, so a second reminder
/// joins the list rather than opening a second window. It never takes focus from what the reader
/// is doing: it is shown, not shown modally, and stays on top only if the Options page says so.
/// </remarks>
public sealed class RemindersWindow : Window
{
    private readonly ListBox _list = new() { MinHeight = 90, SelectionMode = SelectionMode.Multiple };
    private readonly ComboBox _snooze = new() { MinWidth = 180 };
    private List<DueReminder> _due = [];

    // The header block the reference draws above the list: the item's own icon, its subject in
    // large type, when it is, and where. Measured off `reminder window.png` — icon at the left
    // margin, three lines beside it, the third a link.
    private readonly TextBlock _icon = new()
    {
        FontFamily = IconFont.Family,
        FontSize = 30,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 2, 12, 0),
    };

    private readonly TextBlock _subject = new() { FontSize = 19, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _when = new() { FontSize = 12 };
    private readonly TextBlock _where = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

    /// <summary>Asks the shell to open a message: the account's address and the message id.</summary>
    public event EventHandler<(string Address, long MessageId)>? OpenRequested;

    private static readonly (string Label, TimeSpan Span)[] SnoozeSpans =
    [
        ("5 minutes", TimeSpan.FromMinutes(5)), ("10 minutes", TimeSpan.FromMinutes(10)),
        ("15 minutes", TimeSpan.FromMinutes(15)), ("30 minutes", TimeSpan.FromMinutes(30)),
        ("1 hour", TimeSpan.FromHours(1)), ("2 hours", TimeSpan.FromHours(2)),
        ("4 hours", TimeSpan.FromHours(4)), ("8 hours", TimeSpan.FromHours(8)),
        ("0.5 days", TimeSpan.FromHours(12)), ("1 day", TimeSpan.FromDays(1)),
        ("2 days", TimeSpan.FromDays(2)), ("3 days", TimeSpan.FromDays(3)),
        ("4 days", TimeSpan.FromDays(4)), ("1 week", TimeSpan.FromDays(7)),
        ("2 weeks", TimeSpan.FromDays(14)),
    ];

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public RemindersWindow()
    {
        Title = "0 Reminder(s)";
        Width = 520;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = App.MailOptions.RemindersOnTop;

        // And it follows the switch: the window stays up for as long as there are reminders in
        // it, so a reader who turns "show reminders on top" on because this window is behind
        // something has to see it come forward now rather than the next time one is due.
        void OnSetting(object? sender, string key)
        {
            if (key == Mailbox.Core.Settings.MailOptions.RemindersOnTopKey)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Topmost = App.MailOptions.RemindersOnTop);
            }
        }

        App.Settings.Changed += OnSetting;
        Closed += (_, _) => App.Settings.Changed -= OnSetting;

        // A template is asked about a null item as the list settles — which is what dismissing the
        // last reminder does — so it is pattern-matched rather than dereferenced.
        _list.ItemTemplate = new FuncDataTemplate<DueReminder>((item, _) => item is { } due ? Row(due) : new Panel());
        Bind(_list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(_list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");

        // The block reads the selection, not the queue: with several reminders the reference
        // still shows one set of details, and they are the ones highlighted in the list.
        _list.SelectionChanged += (_, _) => ShowHeaderFor(Selected().FirstOrDefault() ?? _due.FirstOrDefault());
        BuildHeader();

        _snooze.ItemsSource = SnoozeSpans.Select(s => s.Label).ToList();
        _snooze.SelectedIndex = 0;

        var dismissAll = new Button { Content = "Dismiss All" };
        dismissAll.Click += (_, _) => Dismiss(_due.ToList());

        var dismiss = new Button { Content = "Dismiss" };
        dismiss.Click += (_, _) => Dismiss(Selected());

        var snooze = new Button { Content = "Snooze" };
        snooze.Click += (_, _) => Snooze(Selected(), SnoozeSpans[Math.Max(0, _snooze.SelectedIndex)].Span);

        // Double-click opens, which is the reference's only route to the item: its window has no
        // Open button — that was the shape two versions before this one.
        _list.DoubleTapped += (_, _) => Open(Selected().FirstOrDefault());

        var snoozeLabel = new TextBlock { Text = "Click Snooze to be reminded again in:", VerticalAlignment = VerticalAlignment.Center };
        Bind(snoozeLabel, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        // Dismiss stands alone over the list, right-aligned; Snooze and Dismiss All are in the
        // band underneath with the interval they act on. That is the reference's arrangement,
        // and it is the arrangement because the two halves answer different questions: what to
        // do with the one that is selected, and what to do with all of them.
        // The list takes what the block and the button leave rather than a height of its own, so
        // the window can be dragged taller and show more of the queue — which is what a window
        // holding a list is for, and what a fixed 200 meant it could not do.
        _header.Margin = new Thickness(0, 0, 0, 10);
        dismiss.Margin = new Thickness(0, 10, 0, 0);
        dismiss.HorizontalAlignment = HorizontalAlignment.Right;

        var upper = new DockPanel { Margin = new Thickness(18, 14, 18, 12) };
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(dismiss, Dock.Bottom);
        upper.Children.Add(_header);
        upper.Children.Add(dismiss);
        upper.Children.Add(_list);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(18, 10, 18, 14),
        };

        Grid.SetColumnSpan(snoozeLabel, 4);
        footer.Children.Add(snoozeLabel);
        Grid.SetRow(_snooze, 1);
        footer.Children.Add(_snooze);
        Grid.SetRow(snooze, 1); Grid.SetColumn(snooze, 1);
        snooze.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(snooze);
        Grid.SetRow(dismissAll, 1); Grid.SetColumn(dismissAll, 3);
        footer.Children.Add(dismissAll);

        // Ruled off rather than tinted: the reference's band is a shade of the Windows shell's
        // own grey, and inventing a token for it would be a colour taken from a photograph.
        var rule = new Border { Height = 1 };
        Bind(rule, BackgroundProperty, "dialog.border.brush");

        var body = new DockPanel();
        DockPanel.SetDock(rule, Dock.Bottom);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(rule);
        body.Children.Add(upper);

        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");

        // Closing hides rather than destroys, so the next due reminder reopens the same window.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>Asks the shell to open an appointment from the calendar.</summary>
    public event EventHandler<long>? OpenAppointmentRequested;

    /// <summary>Asks the shell to open a task from the to-do list.</summary>
    public event EventHandler<long>? OpenTaskRequested;

    private void Open(DueReminder? item)
    {
        if (item is null) return;
        if (item.Appointment is { } appointment) OpenAppointmentRequested?.Invoke(this, appointment.ItemId);
        else if (item.Task is { } task) OpenTaskRequested?.Invoke(this, task.ItemId);
        else if (item.Account is { } account && item.Message is { } message) OpenRequested?.Invoke(this, (account.Account.Address, message.Id));
    }

    /// <summary>
    /// The header block: the icon, the subject in large type, the time, and the location as a
    /// link, laid out as `reminder window.png` has them.
    /// </summary>
    /// <remarks>
    /// Built once and refilled, rather than rebuilt per selection: the block is the same three
    /// lines whichever item is highlighted, and a rebuilt one flickers as the list settles.
    /// </remarks>
    private void BuildHeader()
    {
        Bind(_icon, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Bind(_subject, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Bind(_when, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        Bind(_where, TextBlock.ForegroundProperty, "dialog.link.brush");

        var lines = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _subject, _when, _where },
        };

        Grid.SetColumn(_icon, 0);
        _header.Children.Add(_icon);
        Grid.SetColumn(lines, 1);
        _header.Children.Add(lines);
    }

    /// <summary>Fills the header block from one item, or empties it when there is nothing due.</summary>
    private void ShowHeaderFor(DueReminder? item)
    {
        _header.IsVisible = item is not null;
        if (item is null) return;

        _icon.Text = IconGlyphs.GetOrEmpty(item.IconName, 32);
        _subject.Text = item.Subject;
        _when.Text = item.WhenSaid;
        _where.Text = item.Where;
        _where.IsVisible = item.Where.Length > 0;
    }

    private Control Row(DueReminder item)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(item.IsAppointment ? "calendar" : item.IsTask ? "todo-list" : "flag", 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
        };
        Bind(glyph, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var subject = new TextBlock { Text = item.Subject, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Bind(subject, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var due = new TextBlock { Text = item.DueIn(Mailbox.Core.PosedClock.Now), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, Margin = new Thickness(12, 0, 4, 0) };
        Bind(due, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(2) };
        Grid.SetColumn(glyph, 0); grid.Children.Add(glyph);
        Grid.SetColumn(subject, 1); grid.Children.Add(subject);
        Grid.SetColumn(due, 2); grid.Children.Add(due);
        return grid;
    }

    /// <summary>Puts what is due on show, selects the first, and brings the window up if it was hidden.</summary>
    /// <param name="evenWhenEmpty">
    /// True for a reader who asked for the window: an empty list then says so rather than
    /// hiding, which is what the reference does. The timer passes false, so nothing pops up
    /// when the last reminder is dismissed.
    /// </param>
    public void Show(IReadOnlyList<DueReminder> due, bool evenWhenEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(due);

        _due = [.. due];
        _list.ItemsSource = _due;
        if (_due.Count > 0) _list.SelectedIndex = 0;

        // The count lives in the caption, exactly as the reference writes it — "1 Reminder(s)",
        // the bracketed plural and all. It had been a heading inside the window instead, which
        // is a line the reference gives to the item's own details.
        Title = $"{_due.Count} Reminder(s)";
        ShowHeaderFor(_due.FirstOrDefault());
        Topmost = App.MailOptions.RemindersOnTop;

        if (_due.Count == 0 && !evenWhenEmpty)
        {
            Hide();
            return;
        }

        if (!IsVisible) Show();
    }

    /// <summary>What is on show, for a caller merging a new due item into it.</summary>
    public IReadOnlyList<DueReminder> Current => _due;

    /// <summary>
    /// The two buttons, as the harness presses them.
    /// </summary>
    /// <remarks>
    /// The same two methods the buttons themselves call, over the items given, so what a pose
    /// proves is what a reader would get — and what each did is read back out of the store the
    /// item came from rather than out of this window.
    /// </remarks>
    internal void PressDismiss(IEnumerable<DueReminder> items) => Dismiss([.. items]);

    internal void PressSnooze(IEnumerable<DueReminder> items, TimeSpan span) => Snooze([.. items], span);

    private List<DueReminder> Selected()
        => (_list.SelectedItems ?? new List<object>()).OfType<DueReminder>().ToList();

    // Three kinds now, so each pass says which it means rather than taking "not an appointment"
    // for a message — which is what it meant when there were two.
    private void Dismiss(List<DueReminder> items)
    {
        foreach (var group in items.Where(i => i.IsMessage).GroupBy(i => i.Account!.Account.Address))
        {
            group.First().Account!.Mail.SetReminder([.. group.Select(i => i.Message!.Id)], null);
        }

        foreach (var item in items.Where(i => i.IsAppointment))
        {
            AppointmentReminders.Dismiss(App.Pim, item.Appointment!);
        }

        foreach (var item in items.Where(i => i.IsTask))
        {
            TaskReminders.Dismiss(App.Pim, item.Task!);
        }

        Show([.. _due.Except(items)]);
    }

    private void Snooze(List<DueReminder> items, TimeSpan span)
    {
        // From the same clock the queue is asked against, or a snooze under a pinned run would
        // be measured from the machine's afternoon and come round again immediately.
        var later = Mailbox.Core.PosedClock.UtcNow + span;
        foreach (var group in items.Where(i => i.IsMessage).GroupBy(i => i.Account!.Account.Address))
        {
            group.First().Account!.Mail.SetReminder([.. group.Select(i => i.Message!.Id)], later);
        }

        foreach (var item in items.Where(i => i.IsAppointment))
        {
            AppointmentReminders.Snooze(App.Pim, item.Appointment!, later);
        }

        foreach (var item in items.Where(i => i.IsTask))
        {
            TaskReminders.Snooze(App.Pim, item.Task!, later);
        }

        Show([.. _due.Except(items)]);
    }
}
