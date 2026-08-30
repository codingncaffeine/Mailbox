using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Scheduling;

namespace Mailbox.App.Views;

/// <summary>
/// The task window: one task's form, as the reference opens one.
/// </summary>
/// <remarks>
/// <b>No capture of this window exists</b>, so its fields are the reference's own — subject,
/// start and due dates, status, priority, percent complete, a reminder, categories, the Private
/// tick its bar also carries, and the notes underneath — in the order its form lists them, and the
/// geometry is this application's dialog chrome rather than a measurement. The appointment window's measured form is what it should
/// eventually look like; a capture would settle it.
/// <para>
/// Status and percent complete are two views of the same thing, so they are kept in step here:
/// marking a task complete fills the bar, and filling the bar completes the task. A form that
/// let them disagree would write a task no reader could make sense of.
/// </para>
/// </remarks>
public sealed class TaskWindow : Window
{
    private readonly TaskItem _original;
    private readonly TextBox _subject = new() { PlaceholderText = "Subject" };
    private readonly CalendarDatePicker _start = new();
    private readonly CalendarDatePicker _due = new();
    private readonly ComboBox _status = new();
    private readonly ComboBox _priority = new();
    private readonly NumericUpDown _percent = new() { Minimum = 0, Maximum = 100, Increment = 25, FormatString = "0'%'" };
    private readonly CheckBox _reminder = new() { Content = "Reminder" };
    private readonly CheckBox _private = new() { Content = "Private" };
    private readonly TextBox _categories = new() { PlaceholderText = "Categories" };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 120 };

    private static readonly TaskProgress[] Progresses =
        [TaskProgress.NotStarted, TaskProgress.InProgress, TaskProgress.Completed, TaskProgress.Waiting, TaskProgress.Deferred];

    private static readonly TaskUrgency[] Urgencies = [TaskUrgency.Low, TaskUrgency.Normal, TaskUrgency.High];

    /// <summary>True while one of status and percentage is answering the other.</summary>
    private bool _syncing;

    public TaskWindow(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _original = task;

        Title = task.Summary.Length > 0 ? task.Summary + " — Task" : "Untitled — Task";
        Width = 560;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _subject.Text = task.Summary;
        _start.SelectedDate = task.Start is { } start ? start.Wall : null;
        _due.SelectedDate = task.Due is { } due ? due.Wall : null;
        _status.ItemsSource = Progresses.Select(Label).ToList();
        _status.SelectedIndex = Math.Max(0, Array.IndexOf(Progresses, task.Progress));
        _priority.ItemsSource = Urgencies.Select(u => u.ToString()).ToList();
        _priority.SelectedIndex = Math.Max(0, Array.IndexOf(Urgencies, task.Urgency));
        _percent.Value = task.PercentComplete;
        _reminder.IsChecked = task.ReminderMinutes is not null;
        _private.IsChecked = task.IsPrivate;
        _categories.Text = string.Join(", ", task.Categories);
        _notes.Text = task.Description;

        // The two that say the same thing, kept saying it: status to percent and back. Each
        // handler stands the other down while it is the one driving, because they answer each
        // other — without that, choosing In Progress on a finished task zeroes the bar and the
        // zeroed bar puts the status straight back to Not Started.
        _status.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                if (Chosen(_status, Progresses) == TaskProgress.Completed) _percent.Value = 100;
                else if (_percent.Value >= 100) _percent.Value = 0;
            }
            finally
            {
                _syncing = false;
            }
        };

        // The three the number settles: nothing done is Not Started, all of it done is Completed,
        // and anything between the two is In Progress. Waiting and Deferred say why the work has
        // stopped rather than how far it got, so a percentage does not overrule either — and it
        // need not, since both travel in a property of their own that outranks the numbers when
        // the text is read back.
        _percent.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                var done = (int)Math.Clamp(_percent.Value ?? 0, 0, 100);
                var progress = Chosen(_status, Progresses);

                var wanted = done switch
                {
                    >= 100 => TaskProgress.Completed,
                    > 0 when progress is TaskProgress.NotStarted or TaskProgress.Completed => TaskProgress.InProgress,
                    0 when progress is TaskProgress.InProgress or TaskProgress.Completed => TaskProgress.NotStarted,
                    _ => progress,
                };

                if (wanted != progress) _status.SelectedIndex = Array.IndexOf(Progresses, wanted);
            }
            finally
            {
                _syncing = false;
            }
        };

        DialogChrome.Apply(this, BuildBody());
    }

    /// <summary>The task as it was left, or null when the window was closed without saving.</summary>
    public TaskItem? Result { get; private set; }

    /// <summary>True when Delete was pressed rather than Save &amp; Close.</summary>
    public bool Deleted { get; private set; }

    /// <summary>
    /// Every field the form carries and what each of them says, in the order the form lists them.
    /// </summary>
    /// <remarks>
    /// A photograph of this window shows what the fields look like and not what they hold — a
    /// combo box drawn closed says nothing about the four values behind it — so the harness reads
    /// them here instead. The list is the window's own controls rather than a second description
    /// of them, so a field added to the form appears here without being added twice.
    /// </remarks>
    internal IReadOnlyList<(string Field, string Value)> FormFields =>
    [
        ("Subject", _subject.Text ?? string.Empty),
        ("Start date", Date(_start)),
        ("Due date", Date(_due)),
        ("Status", _status.SelectedItem as string ?? string.Empty),
        ("Status choices", string.Join(" / ", Progresses.Select(Label))),
        ("Priority", _priority.SelectedItem as string ?? string.Empty),
        ("Priority choices", string.Join(" / ", Urgencies.Select(u => u.ToString()))),
        ("% Complete", (_percent.Value ?? 0).ToString(CultureInfo.InvariantCulture)),
        ("Reminder", _reminder.IsChecked == true ? "on" : "off"),
        ("Categories", _categories.Text ?? string.Empty),
        ("Private", _private.IsChecked == true ? "on" : "off"),
        ("Notes", _notes.Text ?? string.Empty),
    ];

    /// <summary>
    /// Sets one field by the name <see cref="FormFields"/> reports it under, through the control's
    /// own property — so the two that keep each other honest, status and percentage, still do.
    /// </summary>
    /// <returns>False for a name the form has no field for, which is itself an answer.</returns>
    internal bool SetFormField(string field, string value)
    {
        switch (field.Trim().ToLowerInvariant())
        {
            case "subject": _subject.Text = value; return true;
            case "notes": _notes.Text = value; return true;
            case "categories": _categories.Text = value; return true;
            case "start date" or "start": _start.SelectedDate = Parse(value); return true;
            case "due date" or "due": _due.SelectedDate = Parse(value); return true;
            case "status": return Choose(_status, value);
            case "priority": return Choose(_priority, value);
            case "% complete" or "percent":
                if (!decimal.TryParse(value, CultureInfo.InvariantCulture, out var percent)) return false;
                _percent.Value = percent;
                return true;
            case "reminder": _reminder.IsChecked = Yes(value); return true;
            case "private": _private.IsChecked = Yes(value); return true;
            default: return false;
        }

        static bool Yes(string value) => value.Trim() is "1" or "on" or "true" or "yes";

        static DateTime? Parse(string value)
            => DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : null;

        static bool Choose(ComboBox box, string value)
        {
            var items = box.ItemsSource?.Cast<string>().ToList() ?? [];
            var at = items.FindIndex(i => string.Equals(i, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (at < 0) return false;
            box.SelectedIndex = at;
            return true;
        }
    }

    private static string Date(CalendarDatePicker picker)
        => picker.SelectedDate is { } day ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    private Control BuildBody()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
        };

        Place(grid, 0, 0, "Subject:", _subject, span: 3);
        Place(grid, 1, 0, "Start date:", _start);
        Place(grid, 1, 2, "Due date:", _due);
        Place(grid, 2, 0, "Status:", _status);
        Place(grid, 2, 2, "Priority:", _priority);
        Place(grid, 3, 0, "% Complete:", _percent);
        Place(grid, 3, 2, string.Empty, _reminder);
        Place(grid, 4, 0, "Categories:", _categories, span: 3);
        Place(grid, 5, 2, string.Empty, _private);

        var save = new Button { Content = "Save & Close", Width = 110, IsDefault = true };
        save.Click += (_, _) =>
        {
            Result = Collect();
            Close();
        };

        var delete = new Button { Content = "Delete", Width = 84 };
        delete.Click += (_, _) =>
        {
            Deleted = true;
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        cancel.Click += (_, _) => Close();

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { save, delete, cancel },
                },
                new DockPanel
                {
                    Children =
                    {
                        new Border { [DockPanel.DockProperty] = Dock.Top, Child = grid },
                        _notes,
                    },
                },
            },
        };
    }

    /// <summary>What the form now says the task is.</summary>
    private TaskItem Collect()
    {
        var progress = Chosen(_status, Progresses);
        var percent = (int)Math.Clamp(_percent.Value ?? 0, 0, 100);
        var complete = progress == TaskProgress.Completed;

        return _original with
        {
            Summary = _subject.Text?.Trim() ?? string.Empty,
            Description = _notes.Text ?? string.Empty,
            Start = _start.SelectedDate is { } start ? EventTime.Date(DateOnly.FromDateTime(start.Date)) : null,
            Due = _due.SelectedDate is { } due ? EventTime.Date(DateOnly.FromDateTime(due.Date)) : null,
            Progress = progress,
            PercentComplete = complete ? 100 : percent,
            CompletedUtc = complete ? _original.CompletedUtc ?? Mailbox.Core.PosedClock.UtcNow : null,
            Urgency = Chosen(_priority, Urgencies),
            IsPrivate = _private.IsChecked == true,
            ReminderMinutes = _reminder.IsChecked == true ? _original.ReminderMinutes ?? 0 : null,
            Categories = (_categories.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LastModified = Mailbox.Core.PosedClock.UtcNow,
        };
    }

    private static T Chosen<T>(ComboBox box, T[] values) => values[Math.Clamp(box.SelectedIndex, 0, values.Length - 1)];

    /// <summary>The reference's own words for the five states, which the detailed view shares.</summary>
    public static string Label(TaskProgress progress) => TodoCodec.ProgressLabel(progress);

    private static void Place(Grid grid, int row, int column, string label, Control control, int span = 1)
    {
        if (label.Length > 0)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
            Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
            Grid.SetRow(text, row);
            Grid.SetColumn(text, column);
            grid.Children.Add(text);
        }

        control.Margin = new Thickness(0, 0, 12, 8);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column + 1);
        Grid.SetColumnSpan(control, span);
        grid.Children.Add(control);
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
