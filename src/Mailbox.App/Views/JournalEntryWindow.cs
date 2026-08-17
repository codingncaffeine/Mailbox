using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Mailbox.Scheduling;

namespace Mailbox.App.Views;

/// <summary>
/// The journal entry window: one entry's form, as the reference opens one.
/// </summary>
/// <remarks>
/// <b>No capture of this window exists</b>, so its fields are the reference's own — subject, entry
/// type, start time, duration, the timer beside it, who it was with, categories and the notes
/// underneath — in the order its form lists them, and the geometry is this application's dialog
/// chrome rather than a measurement.
/// <para>
/// The timer is the reference's own Start Timer / Pause Timer, and it is what a journal entry is
/// for: press it and the duration grows from the clock rather than from a dropdown. What it
/// accumulates is added to whatever the duration already said, so timing something twice adds up.
/// </para>
/// </remarks>
public sealed class JournalEntryWindow : Window
{
    private readonly JournalEntry _original;
    private readonly TextBox _subject = new() { PlaceholderText = "Subject" };
    private readonly ComboBox _type = new();
    // The appointment window's own custom format, which is what puts the weekday in front of the
    // date — a CalendarDatePicker writes a bare one otherwise. Wider than that window's 150,
    // because this one draws its picker button inside the box rather than beside it.
    private readonly CalendarDatePicker _startDate = new()
    {
        Width = 172,
        SelectedDateFormat = CalendarDatePickerFormat.Custom,
        CustomDateFormatString = "ddd M/d/yyyy",
    };
    private readonly TextBox _startTime = new() { Width = 90 };
    private readonly ComboBox _duration = new();
    private readonly Button _timer = new() { Content = "Start Timer", Width = 110 };
    private readonly TextBox _contacts = new() { PlaceholderText = "Contacts" };
    private readonly TextBox _categories = new() { PlaceholderText = "Categories" };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 120 };

    /// <summary>The reference's own list, in its order — the last is what a timer produces.</summary>
    private static readonly TimeSpan[] Durations =
    [
        TimeSpan.Zero,
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3),
        TimeSpan.FromHours(4), TimeSpan.FromHours(8),
    ];

    private DispatcherTimer? _ticking;
    private DateTimeOffset? _startedAt;
    private TimeSpan _timed;

    public JournalEntryWindow(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _original = entry;

        Title = entry.Summary.Length > 0 ? entry.Summary + " — Journal Entry" : "Untitled — Journal Entry";

        // Wide enough for the form's two starred columns to hold what is in them: the right-hand
        // one carries the date box beside its time, and a narrower window clips the time.
        Width = 700;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var when = entry.When?.Wall ?? DateTime.Now;
        _subject.Text = entry.Summary;

        // Editable rather than a closed list: the reference's own types are a starting point and
        // an entry another client wrote saying something else keeps what it says.
        _type.ItemsSource = Types(entry.EntryType);
        _type.SelectedIndex = Math.Max(0, Types(entry.EntryType).ToList().IndexOf(entry.EntryType));
        _startDate.SelectedDate = when.Date;
        _startTime.Text = when.ToString("t", CultureInfo.CurrentCulture);
        _duration.ItemsSource = Durations.Select(Label).ToList();
        _duration.SelectedIndex = Nearest(entry.Duration ?? TimeSpan.Zero);
        _timed = entry.Duration ?? TimeSpan.Zero;
        _contacts.Text = string.Join("; ", entry.Contacts);
        _categories.Text = string.Join(", ", entry.Categories);
        _notes.Text = entry.Description;

        _timer.Click += (_, _) => ToggleTimer();

        DialogChrome.Apply(this, BuildBody());
        Bind(this, BackgroundProperty, "dialog.background.brush");

        // A timer left running when the window closes is stopped, so its time is counted once.
        Closing += (_, _) => StopTimer();
    }

    /// <summary>The entry as it was left, or null when the window was closed without saving.</summary>
    public JournalEntry? Result { get; private set; }

    /// <summary>True when Delete was pressed rather than Save &amp; Close.</summary>
    public bool Deleted { get; private set; }

    /// <summary>Whether the timer is running, which the harness reads back.</summary>
    public bool IsTiming => _startedAt is not null;

    private static IReadOnlyList<string> Types(string carried)
        => JournalBook.Types.Contains(carried, StringComparer.OrdinalIgnoreCase) || carried.Length == 0
            ? JournalBook.Types
            : [.. JournalBook.Types, carried];

    private Control BuildBody()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };

        Place(grid, 0, 0, "Subject:", _subject, span: 3);
        Place(grid, 1, 0, "Entry type:", _type);

        var start = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        start.Children.Add(_startDate);
        start.Children.Add(_startTime);
        Place(grid, 1, 2, "Start time:", start);

        var timing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        timing.Children.Add(_duration);
        timing.Children.Add(_timer);
        Place(grid, 2, 0, "Duration:", timing, span: 3);

        Place(grid, 3, 0, "Contacts:", _contacts);
        Place(grid, 3, 2, "Categories:", _categories);

        var save = new Button { Content = "Save & Close", Width = 110, IsDefault = true };
        save.Click += (_, _) =>
        {
            StopTimer();
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

    // ---- The timer -----------------------------------------------------------------------------

    private void ToggleTimer()
    {
        if (_startedAt is not null)
        {
            StopTimer();
            return;
        }

        _timed = Chosen();
        _startedAt = DateTimeOffset.UtcNow;
        _timer.Content = "Pause Timer";

        // A second rather than a minute, so that the duration a reader sees moves while they
        // watch it — the reference's own timer does, and a minute of nothing looks broken.
        _ticking = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticking.Tick += (_, _) => ShowElapsed();
        _ticking.Start();
    }

    private void StopTimer()
    {
        if (_startedAt is not { } since) return;

        _timed += DateTimeOffset.UtcNow - since;
        _startedAt = null;
        _ticking?.Stop();
        _ticking = null;
        _timer.Content = "Start Timer";
        ShowDuration(_timed);
    }

    private void ShowElapsed()
    {
        if (_startedAt is not { } since) return;
        ShowDuration(_timed + (DateTimeOffset.UtcNow - since));
    }

    /// <summary>
    /// Puts a duration in the list even when it is not one of the reference's own, which is what
    /// a timer produces: seventeen minutes is not on any dropdown.
    /// </summary>
    private void ShowDuration(TimeSpan duration)
    {
        var label = JournalCodec.DurationText(duration, CultureInfo.CurrentCulture);
        var items = Durations.Select(Label).ToList();
        if (!items.Contains(label)) items.Add(label);

        _duration.ItemsSource = items;
        _duration.SelectedIndex = items.IndexOf(label);
    }

    /// <summary>What the dropdown is showing, whether it came from the list or from the timer.</summary>
    private TimeSpan Chosen()
    {
        var index = _duration.SelectedIndex;
        if (index >= 0 && index < Durations.Length) return Durations[index];

        // The one entry beyond the list is what the timer put there, and while it is running
        // that entry is already stale by however long the tick was ago.
        return _startedAt is { } since ? _timed + (DateTimeOffset.UtcNow - since) : _timed;
    }

    private static int Nearest(TimeSpan duration)
    {
        var best = 0;
        for (var i = 0; i < Durations.Length; i++)
        {
            if (Durations[i] <= duration) best = i;
        }

        return best;
    }

    private static string Label(TimeSpan duration)
        => duration == TimeSpan.Zero ? "0 minutes" : JournalCodec.DurationText(duration, CultureInfo.CurrentCulture);

    /// <summary>What the form now says the entry is.</summary>
    private JournalEntry Collect()
    {
        var date = _startDate.SelectedDate?.Date ?? DateTime.Today;
        var time = TimeOnly.TryParse(_startTime.Text, CultureInfo.CurrentCulture, out var parsed) ? parsed : TimeOnly.FromDateTime(DateTime.Now);
        var duration = Chosen();

        return _original with
        {
            Summary = _subject.Text?.Trim() ?? string.Empty,
            Description = _notes.Text ?? string.Empty,
            EntryType = _type.SelectedItem as string ?? _original.EntryType,
            When = EventTime.At(date.Add(time.ToTimeSpan()), TimeZoneInfo.Local.Id),
            Duration = duration > TimeSpan.Zero ? duration : null,
            Contacts = (_contacts.Text ?? string.Empty)
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Categories = (_categories.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LastModified = DateTimeOffset.UtcNow,
        };
    }

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
