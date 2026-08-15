using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Store;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>One line in the Reminders window: a flagged message whose reminder time has come.</summary>
public sealed record DueReminder(OpenAccount Account, MessageSummary Message)
{
    public string Subject => Message.Subject.Length > 0 ? Message.Subject : "(no subject)";

    /// <summary>"Due in 2 hours", "Overdue by 15 minutes", "No due date" — the reference's column.</summary>
    public string DueIn(DateTimeOffset now)
    {
        if (Message.FollowUpDue is not { } due) return "No due date";

        var span = due - now;
        var overdue = span < TimeSpan.Zero;
        span = span.Duration();

        var words = span.TotalDays >= 1 ? $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")}"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")}"
            : $"{Math.Max(1, (int)span.TotalMinutes)} minute{((int)span.TotalMinutes == 1 ? "" : "s")}";

        return overdue ? $"Overdue by {words}" : $"Due in {words}";
    }
}

/// <summary>
/// The Reminders window (§9): one queue of what is due — flagged mail for now; appointments and
/// tasks join it with their phases — with Dismiss, Dismiss All, Open Item and Snooze.
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

        _list.ItemTemplate = new FuncDataTemplate<DueReminder>((item, _) => Row(item));
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
            if (Selected().FirstOrDefault() is { } first) OpenRequested?.Invoke(this, (first.Account.Account.Address, first.Message.Id));
        };

        var dismiss = new Button { Content = "Dismiss" };
        dismiss.Click += (_, _) => Dismiss(Selected());

        var snooze = new Button { Content = "Snooze" };
        snooze.Click += (_, _) => Snooze(Selected(), SnoozeSpans[Math.Max(0, _snooze.SelectedIndex)].Span);

        _list.DoubleTapped += (_, _) =>
        {
            if (Selected().FirstOrDefault() is { } first) OpenRequested?.Invoke(this, (first.Account.Account.Address, first.Message.Id));
        };

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

    private Control Row(DueReminder item)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("flag", 16),
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

    private List<DueReminder> Selected()
        => (_list.SelectedItems ?? new List<object>()).OfType<DueReminder>().ToList();

    private void Dismiss(List<DueReminder> items)
    {
        foreach (var group in items.GroupBy(i => i.Account.Account.Address))
        {
            group.First().Account.Mail.SetReminder([.. group.Select(i => i.Message.Id)], null);
        }

        Show([.. _due.Except(items)]);
    }

    private void Snooze(List<DueReminder> items, TimeSpan span)
    {
        var later = DateTimeOffset.UtcNow + span;
        foreach (var group in items.GroupBy(i => i.Account.Account.Address))
        {
            group.First().Account.Mail.SetReminder([.. group.Select(i => i.Message.Id)], later);
        }

        Show([.. _due.Except(items)]);
    }
}
