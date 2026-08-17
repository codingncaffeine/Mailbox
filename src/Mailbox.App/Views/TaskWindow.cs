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
/// start and due dates, status, priority, percent complete, a reminder, categories and the notes
/// underneath — in the order its form lists them, and the geometry is this application's dialog
/// chrome rather than a measurement. The appointment window's measured form is what it should
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
    private readonly TextBox _categories = new() { PlaceholderText = "Categories" };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 120 };

    private static readonly TaskProgress[] Progresses =
        [TaskProgress.NotStarted, TaskProgress.InProgress, TaskProgress.Completed, TaskProgress.Waiting, TaskProgress.Deferred];

    private static readonly TaskUrgency[] Urgencies = [TaskUrgency.Low, TaskUrgency.Normal, TaskUrgency.High];

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
        _categories.Text = string.Join(", ", task.Categories);
        _notes.Text = task.Description;

        // The two that say the same thing, kept saying it: status to percent and back.
        _status.SelectionChanged += (_, _) =>
        {
            if (Chosen(_status, Progresses) == TaskProgress.Completed) _percent.Value = 100;
            else if (_percent.Value >= 100) _percent.Value = 0;
        };

        _percent.ValueChanged += (_, _) =>
        {
            var complete = _percent.Value >= 100;
            var progress = Chosen(_status, Progresses);
            if (complete && progress != TaskProgress.Completed) _status.SelectedIndex = Array.IndexOf(Progresses, TaskProgress.Completed);
            else if (!complete && progress == TaskProgress.Completed) _status.SelectedIndex = Array.IndexOf(Progresses, TaskProgress.NotStarted);
        };

        DialogChrome.Apply(this, BuildBody());
        Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    /// <summary>The task as it was left, or null when the window was closed without saving.</summary>
    public TaskItem? Result { get; private set; }

    /// <summary>True when Delete was pressed rather than Save &amp; Close.</summary>
    public bool Deleted { get; private set; }

    private Control BuildBody()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
        };

        Place(grid, 0, 0, "Subject:", _subject, span: 3);
        Place(grid, 1, 0, "Start date:", _start);
        Place(grid, 1, 2, "Due date:", _due);
        Place(grid, 2, 0, "Status:", _status);
        Place(grid, 2, 2, "Priority:", _priority);
        Place(grid, 3, 0, "% Complete:", _percent);
        Place(grid, 3, 2, string.Empty, _reminder);
        Place(grid, 4, 0, "Categories:", _categories, span: 3);

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
            CompletedUtc = complete ? _original.CompletedUtc ?? DateTimeOffset.UtcNow : null,
            Urgency = Chosen(_priority, Urgencies),
            ReminderMinutes = _reminder.IsChecked == true ? _original.ReminderMinutes ?? 0 : null,
            Categories = (_categories.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    private static T Chosen<T>(ComboBox box, T[] values) => values[Math.Clamp(box.SelectedIndex, 0, values.Length - 1)];

    /// <summary>The reference's own words for the five states.</summary>
    public static string Label(TaskProgress progress) => progress switch
    {
        TaskProgress.NotStarted => "Not Started",
        TaskProgress.InProgress => "In Progress",
        TaskProgress.Completed => "Completed",
        TaskProgress.Waiting => "Waiting on someone else",
        _ => "Deferred",
    };

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
