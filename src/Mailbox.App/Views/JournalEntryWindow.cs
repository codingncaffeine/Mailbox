using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Mailbox.Core;
using Mailbox.Scheduling;

namespace Mailbox.App.Views;

/// <summary>
/// The journal entry window: one entry's form, as the reference opens one.
/// </summary>
/// <remarks>
/// <b>No capture of this window exists</b>, so its fields are the reference's own, in the order
/// its form lists them: the subject; the entry type beside the company; the start time with
/// Start Timer; the duration with Pause Timer, greyed until the timer runs; the notes; and a
/// footer of Contacts… and Categories… buttons with a Private tick at the right. The geometry is
/// this application's dialog chrome rather than a measurement.
/// <para>
/// The timer is what a journal entry is for: press it and the duration grows from the clock
/// rather than from a dropdown. What it accumulates is added to whatever the duration already
/// said, so timing something twice adds up.
/// </para>
/// </remarks>
public sealed class JournalEntryWindow : Window
{
    private readonly JournalEntry _original;
    private readonly TextBox _subject = new() { PlaceholderText = "Subject" };
    private readonly ComboBox _type = new();
    private readonly TextBox _company = new();
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

    // The reference's own pair: one starts, the other pauses, and a reader can see the second
    // exists before the first has ever been pressed — which one toggling button could not say.
    private readonly Button _start = new() { Content = "Start Timer", Width = 110 };
    private readonly Button _pause = new() { Content = "Pause Timer", Width = 110, IsEnabled = false };

    private readonly Button _contactsButton = new() { Content = "Contacts…", Width = 100 };
    private readonly Button _categoriesButton = new() { Content = "Categories…", Width = 100 };
    private readonly TextBox _contacts = new() { MinWidth = 150 };
    private readonly TextBox _categories = new() { MinWidth = 150 };
    private readonly CheckBox _private = new() { Content = "Private" };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 120 };
    private readonly Button _save = new() { Content = "Save & Close", Width = 110, IsDefault = true };
    private readonly Button _delete = new() { Content = "Delete", Width = 84 };
    private readonly Button _cancel = new() { Content = "Cancel", Width = 84, IsCancel = true };

    /// <summary>The reference's own list, down to its one- and three-minute entries.</summary>
    private static readonly TimeSpan[] Durations =
    [
        TimeSpan.Zero,
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3),
        TimeSpan.FromHours(4), TimeSpan.FromHours(8), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1), TimeSpan.FromDays(2),
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
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // The application's own clock rather than the machine's, so an entry made under a pinned
        // day opens on that day: this form is the one surface that puts a moment on screen before
        // anything has been written, and a picture of it was otherwise unique to the afternoon it
        // was taken.
        var when = entry.When?.Wall ?? PosedClock.Now.DateTime;
        _subject.Text = entry.Summary;

        // Editable rather than a closed list: the reference's own types are a starting point and
        // an entry another client wrote saying something else keeps what it says.
        _type.ItemsSource = Types(entry.EntryType);
        _type.SelectedIndex = Math.Max(0, Types(entry.EntryType).ToList().IndexOf(entry.EntryType));
        _company.Text = entry.Company;
        _startDate.SelectedDate = when.Date;
        _startTime.Text = when.ToString("t", CultureInfo.CurrentCulture);
        // The entry's own duration, put in the list rather than snapped to the nearest thing on
        // it: a timer produces seventeen minutes and no dropdown offers that, so choosing the
        // nearest meant opening a timed entry and pressing Save wrote fifteen — two minutes of a
        // recorded call destroyed by reading it.
        _timed = entry.Duration ?? TimeSpan.Zero;
        ShowDuration(_timed);
        _contacts.Text = string.Join("; ", entry.Contacts);
        _categories.Text = string.Join(", ", entry.Categories);
        _private.IsChecked = entry.IsPrivate;
        _notes.Text = entry.Description;

        _start.Click += (_, _) => StartTimer();
        _pause.Click += (_, _) => StopTimer();
        _contactsButton.Click += async (_, _) => await PickContactsAsync();
        _categoriesButton.Click += async (_, _) => await PickCategoriesAsync();

        DialogChrome.Apply(this, BuildBody(), "journal-entry");

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
        Place(grid, 1, 2, "Company:", _company);

        var start = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        start.Children.Add(_startDate);
        start.Children.Add(_startTime);
        start.Children.Add(_start);
        Place(grid, 2, 0, "Start time:", start, span: 3);

        var timing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        timing.Children.Add(_duration);
        timing.Children.Add(_pause);
        Place(grid, 3, 0, "Duration:", timing, span: 3);

        _save.Click += (_, _) =>
        {
            StopTimer();
            Result = Collect();
            Close();
        };

        _delete.Click += (_, _) =>
        {
            Deleted = true;
            Close();
        };

        _cancel.Click += (_, _) => Close();

        // The reference's footer: the two picker buttons with their boxes, and Private at the
        // right — a button that opens a chooser, not a label that only looks like one.
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        PlaceCell(footer, 0, _contactsButton);
        PlaceCell(footer, 1, _contacts);
        PlaceCell(footer, 2, _categoriesButton);
        PlaceCell(footer, 3, _categories);
        _private.VerticalAlignment = VerticalAlignment.Center;
        _private.Margin = new Thickness(6, 0, 0, 0);
        PlaceCell(footer, 4, _private);

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
                    Children = { _save, _delete, _cancel },
                },
                new Border { [DockPanel.DockProperty] = Dock.Bottom, Child = footer },
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

    private static void PlaceCell(Grid grid, int column, Control control)
    {
        control.Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    // ---- The pickers ---------------------------------------------------------------------------

    /// <summary>Contacts…: the address book, its picks appended to the box.</summary>
    private async Task PickContactsAsync()
    {
        var picked = await AddressBookDialog.PickAsync(this, App.Contacts);
        if (picked is null || picked.IsEmpty) return;

        var have = (_contacts.Text ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var name in picked.To.Concat(picked.Cc).Concat(picked.Bcc))
        {
            if (!have.Contains(name, StringComparer.OrdinalIgnoreCase)) have.Add(name);
        }

        _contacts.Text = string.Join("; ", have);
    }

    /// <summary>Categories…: the reader's own set, ticked, written back to the box.</summary>
    private async Task PickCategoriesAsync()
    {
        var offered = App.Categories.All()
            .Select(c => new PickListDialog.Item(c.Name, c.Name)).ToList();
        var ticked = (_categories.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var chosen = await PickListDialog.PickAsync(this, "Categorize", "Categories:", offered, ticked);
        if (chosen is null) return;
        _categories.Text = string.Join(", ", chosen);
    }

    // ---- The timer -----------------------------------------------------------------------------

    /// <summary>
    /// The clock the timer counts against. Real time in the application; a harness pose replaces
    /// it, because an elapsed duration read off the wall is a different number every run and no
    /// claim about what the timer wrote could ever be repeated.
    /// </summary>
    internal Func<DateTimeOffset> TimerClock { get; set; } = () => DateTimeOffset.UtcNow;

    private void StartTimer()
    {
        if (_startedAt is not null) return;

        _timed = Chosen();
        _startedAt = TimerClock();
        _start.IsEnabled = false;
        _pause.IsEnabled = true;

        // A second rather than a minute, so that the duration a reader sees moves while they
        // watch it — the reference's own timer does, and a minute of nothing looks broken.
        _ticking = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticking.Tick += (_, _) => ShowElapsed();
        _ticking.Start();
    }

    private void StopTimer()
    {
        if (_startedAt is not { } since) return;

        _timed += TimerClock() - since;
        _startedAt = null;
        _ticking?.Stop();
        _ticking = null;
        _start.IsEnabled = true;
        _pause.IsEnabled = false;
        ShowDuration(_timed);
    }

    private void ShowElapsed()
    {
        if (_startedAt is not { } since) return;
        ShowDuration(_timed + (TimerClock() - since));
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
        return _startedAt is { } since ? _timed + (TimerClock() - since) : _timed;
    }

    private static string Label(TimeSpan duration)
        => duration == TimeSpan.Zero ? "0 minutes" : JournalCodec.DurationText(duration, CultureInfo.CurrentCulture);

    /// <summary>What the form now says the entry is.</summary>
    private JournalEntry Collect()
    {
        var date = _startDate.SelectedDate?.Date ?? PosedClock.Today.ToDateTime(TimeOnly.MinValue);
        var time = TimeOnly.TryParse(_startTime.Text, CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : TimeOnly.FromDateTime(PosedClock.Now.DateTime);
        var duration = Chosen();

        return _original with
        {
            Summary = _subject.Text?.Trim() ?? string.Empty,
            Description = _notes.Text ?? string.Empty,
            EntryType = _type.SelectedItem as string ?? _original.EntryType,
            Company = _company.Text?.Trim() ?? string.Empty,
            IsPrivate = _private.IsChecked == true,
            When = EventTime.At(date.Add(time.ToTimeSpan()), TimeZoneInfo.Local.Id),
            Duration = duration > TimeSpan.Zero ? duration : null,
            Contacts = Named(),
            Links = Resolved(Named()),
            Categories = (_categories.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LastModified = PosedClock.UtcNow,
        };
    }

    /// <summary>The names in the Contacts box, as somebody wrote them.</summary>
    private IReadOnlyList<string> Named()
        => (_contacts.Text ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Which of those names are people this application can follow back to a card.
    /// </summary>
    /// <remarks>
    /// Resolved here, on the way to being saved, rather than remembered from whichever press put
    /// a name in the box — so a name typed straight into it links exactly as well as one picked
    /// out of the address book, and a name deleted from the box takes its link with it. A name
    /// that matches no card, or matches two, stays a name: an entry with a contact it cannot
    /// resolve is an ordinary entry, and it is the reference's own field either way.
    /// </remarks>
    private IReadOnlyList<string> Resolved(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return [];

        var uids = new List<string>();
        foreach (var name in names)
        {
            if (App.Contacts.NamedExactly(name) is { } row
                && row.Contact.Uid is { Length: > 0 } uid
                && !uids.Contains(uid, StringComparer.Ordinal))
            {
                uids.Add(uid);
            }
        }

        return uids;
    }

    // ---- The door onto this form -----------------------------------------------------------
    //
    // Every seed in this project writes through the repository rather than through a form, so
    // nothing had ever typed into this one, chosen a duration from its list, or pressed its
    // timer. What a form does to a value on the way past is precisely what that arrangement
    // cannot see.

    /// <summary>
    /// One posed step against this form: <c>field=value</c> for a box, or a bare verb for a
    /// button. Answers what it did, so a step that named nothing is not read as a step that
    /// worked.
    /// </summary>
    internal string Pose(string step)
    {
        var text = (step ?? string.Empty).Trim();
        if (text.Length == 0) return "nothing to do";

        var equals = text.IndexOf('=', StringComparison.Ordinal);
        var verb = (equals > 0 ? text[..equals] : text).Trim().ToLowerInvariant();
        var value = equals > 0 ? text[(equals + 1)..].Trim() : string.Empty;

        switch (verb)
        {
            case "subject": _subject.Text = value; return $"subject is now “{_subject.Text}”";
            case "notes": _notes.Text = value; return $"notes are now {(_notes.Text ?? string.Empty).Length} characters";
            case "contacts": _contacts.Text = value; return $"contacts box says “{_contacts.Text}”";
            case "categories": _categories.Text = value; return $"categories box says “{_categories.Text}”";
            case "company": _company.Text = value; return $"company box says “{_company.Text}”";

            case "private":
                _private.IsChecked = value.Length == 0 || value is "1" or "on" or "true" or "yes";
                return $"private is now {(_private.IsChecked == true ? "ticked" : "clear")}";

            case "type":
            {
                var items = _type.ItemsSource?.Cast<string>().ToList() ?? [];
                var index = items.FindIndex(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return $"“{value}” is not one of the {items.Count} entry types the list offers";
                _type.SelectedIndex = index;
                return $"entry type is now “{_type.SelectedItem}”";
            }

            case "date":
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    return $"“{value}” is not a date";
                _startDate.SelectedDate = day.Date;
                return $"start date is now {_startDate.SelectedDate:yyyy-MM-dd}";

            case "time": _startTime.Text = value; return $"start time box says “{_startTime.Text}”";

            case "duration":
            {
                var items = _duration.ItemsSource?.Cast<string>().ToList() ?? [];
                var index = items.FindIndex(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return $"“{value}” is not one of the {items.Count} durations the list offers: {string.Join(", ", items)}";
                _duration.SelectedIndex = index;
                return $"duration list is now on “{_duration.SelectedItem}”";
            }

            // The buttons, not the methods behind them: whether each is wired is half the claim.
            case "timer" or "starttimer":
                _start.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return $"Start Timer {(_start.IsEnabled ? "stands" : "greyed")}, Pause Timer {(_pause.IsEnabled ? "stands" : "greyed")}, running: {IsTiming}";

            case "pausetimer":
                _pause.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return $"Start Timer {(_start.IsEnabled ? "stands" : "greyed")}, Pause Timer {(_pause.IsEnabled ? "stands" : "greyed")}, running: {IsTiming}";

            case "contactspicker":
                _contactsButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return "Contacts… pressed";

            case "categoriespicker":
                _categoriesButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return "Categories… pressed";

            // What child the pickers opened, and a way to shut it so the run can end.
            case "windows":
            {
                var owned = OwnedWindows;
                return owned.Count == 0 ? "no child window is open"
                    : $"open: {string.Join(" | ", owned.Select(w => $"“{w.Title}” {w.Bounds.Width:0}x{w.Bounds.Height:0}"))}";
            }

            case "closechild":
            {
                var owned = OwnedWindows;
                if (owned.Count == 0) return "no child window to close";
                var last = owned[^1];
                var title = last.Title;
                last.Close();
                return $"closed “{title}”";
            }

            // Moves the clock the timer is counting against, which is the only way an elapsed
            // duration can be the same number twice.
            case "elapse":
                if (!double.TryParse(value, CultureInfo.InvariantCulture, out var seconds))
                    return $"“{value}” is not a number of seconds";
                _posedElapsed += TimeSpan.FromSeconds(seconds);
                return $"the timer's clock is {_posedElapsed.TotalSeconds:0} second(s) on";

            case "save": _save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); return "Save & Close pressed";
            case "delete": _delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); return "Delete pressed";
            case "cancel": _cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); return "Cancel pressed";

            default: return $"“{verb}” is not a field or a button on this form";
        }
    }

    /// <summary>Pins the timer's clock, so what it counts is stated rather than observed.</summary>
    internal void PoseTimerClock()
    {
        var from = PosedClock.UtcNow;
        _posedElapsed = TimeSpan.Zero;
        TimerClock = () => from + _posedElapsed;
    }

    private TimeSpan _posedElapsed;

    /// <summary>What every box on the form says, and what Save would write from it.</summary>
    internal string FormState()
    {
        var would = Collect();
        return $"subject “{_subject.Text}”, type “{_type.SelectedItem}”, company “{_company.Text}”, "
               + $"start {_startDate.SelectedDate:yyyy-MM-dd} {_startTime.Text}, "
               + $"duration list “{_duration.SelectedItem}” of {_duration.ItemsSource?.Cast<string>().Count() ?? 0}, "
               + $"timer {(IsTiming ? "running" : "stopped")} (Start {(_start.IsEnabled ? "stands" : "greyed")}, Pause {(_pause.IsEnabled ? "stands" : "greyed")}), "
               + $"private {(_private.IsChecked == true ? "ticked" : "clear")}, "
               + $"contacts “{_contacts.Text}”, categories “{_categories.Text}”, "
               + $"notes {(_notes.Text ?? string.Empty).Length} characters "
               + $"→ would save: duration {(would.Duration is { } d ? JournalCodec.DurationText(d, CultureInfo.InvariantCulture) + $" ({d})" : "none")}, "
               + $"starts {would.When?.Wall:yyyy-MM-dd HH:mm}, type “{would.EntryType}”, company “{would.Company}”, "
               + $"{(would.IsPrivate ? "private, " : string.Empty)}"
               + $"contacts [{string.Join(" | ", would.Contacts)}], "
               // Which of those names resolved to a card, which is the half a name alone cannot
               // show: a box reading the same before and after can still have gained a link.
               + $"linked [{string.Join(" | ", would.Links)}], "
               + $"categories [{string.Join(" | ", would.Categories)}]";
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
