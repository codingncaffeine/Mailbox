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

    /// <summary>"Due in 2 hours", "Overdue by 15 minutes", "No due date" — the reference's column.</summary>
    public string DueIn(DateTimeOffset now)
    {
        var when = IsAppointment ? Appointment!.StartsUtc : IsTask ? Task!.DueUtc : Message!.FollowUpDue;
        if (when is not { } due) return "No due date";

        var span = due - now;
        var overdue = span < TimeSpan.Zero;
        span = span.Duration();

        var words = span.TotalDays >= 1 ? $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")}"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")}"
            : $"{Math.Max(1, (int)span.TotalMinutes)} minute{((int)span.TotalMinutes == 1 ? "" : "s")}";

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
    private readonly ListBox _list = new() { Height = 200, SelectionMode = SelectionMode.Multiple };
    private readonly ComboBox _snooze = new() { MinWidth = 180 };
    private readonly TextBlock _heading = new() { FontWeight = FontWeight.SemiBold };
    private List<DueReminder> _due = [];

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
        Title = "Reminders";
        Width = 520;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = App.MailOptions.RemindersOnTop;

        // A template is asked about a null item as the list settles — which is what dismissing the
        // last reminder does — so it is pattern-matched rather than dereferenced.
        _list.ItemTemplate = new FuncDataTemplate<DueReminder>((item, _) => item is { } due ? Row(due) : new Panel());
        Bind(_list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(_list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");
        Bind(_heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        _snooze.ItemsSource = SnoozeSpans.Select(s => s.Label).ToList();
        _snooze.SelectedIndex = 0;

        var dismissAll = new Button { Content = "Dismiss All" };
        dismissAll.Click += (_, _) => Dismiss(_due.ToList());

        var open = new Button { Content = "Open Item" };
        open.Click += (_, _) =>
        {
            Open(Selected().FirstOrDefault());
        };

        var dismiss = new Button { Content = "Dismiss" };
        dismiss.Click += (_, _) => Dismiss(Selected());

        var snooze = new Button { Content = "Snooze" };
        snooze.Click += (_, _) => Snooze(Selected(), SnoozeSpans[Math.Max(0, _snooze.SelectedIndex)].Span);

        _list.DoubleTapped += (_, _) => Open(Selected().FirstOrDefault());

        var snoozeLabel = new TextBlock { Text = "Click Snooze to be reminded again in:", VerticalAlignment = VerticalAlignment.Center };
        Bind(snoozeLabel, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                _heading,
                _list,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { dismissAll, new Panel { Width = 120 }, open, dismiss } },
                snoozeLabel,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _snooze, snooze } },
            },
        };

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

        var due = new TextBlock { Text = item.DueIn(DateTimeOffset.Now), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, Margin = new Thickness(12, 0, 4, 0) };
        Bind(due, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(2) };
        Grid.SetColumn(glyph, 0); grid.Children.Add(glyph);
        Grid.SetColumn(subject, 1); grid.Children.Add(subject);
        Grid.SetColumn(due, 2); grid.Children.Add(due);
        return grid;
    }

    /// <summary>Puts what is due on show, selects the first, and brings the window up if it was hidden.</summary>
    public void Show(IReadOnlyList<DueReminder> due)
    {
        ArgumentNullException.ThrowIfNull(due);

        _due = [.. due];
        _list.ItemsSource = _due;
        if (_due.Count > 0) _list.SelectedIndex = 0;
        _heading.Text = _due.Count == 1 ? "1 Reminder" : $"{_due.Count} Reminders";
        Topmost = App.MailOptions.RemindersOnTop;

        if (_due.Count == 0)
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
        var later = DateTimeOffset.UtcNow + span;
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
