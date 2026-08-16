using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Commands;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>What the appointment window came back with.</summary>
/// <param name="Event">The appointment as edited.</param>
/// <param name="CollectionId">The calendar it belongs on.</param>
/// <param name="Deleted">True when Delete was pressed rather than Save &amp; Close.</param>
/// <param name="Sent">True when a meeting invitation was sent rather than saved.</param>
public sealed record AppointmentResult(CalendarEvent Event, long CollectionId, bool Deleted, bool Sent = false);

/// <summary>
/// Everything below an appointment window's ribbon: the big button on the left, the form, and
/// the notes.
/// </summary>
/// <remarks>
/// Measured off the two captures at 1595 wide. The form band is 187 tall on the workspace's own
/// ground: the button is 60×80 at x=31, the labels are centred on x=137, every field starts at
/// x=180 and runs to 31px short of the window's right edge, the date boxes are 28 tall, and the
/// notes begin under a rule at the band's foot.
/// <para>
/// Host-neutral like <see cref="ComposeSurface"/>, so the same control can be dropped into a
/// window now and into a reading-pane strip when an invitation is opened in place.
/// </para>
/// </remarks>
public sealed class AppointmentSurface : UserControl
{
    /// <summary>The form band above the notes, measured.</summary>
    private const double FormHeight = 187;

    /// <summary>Where every field starts, and how far short of the right edge it stops.</summary>
    private const double FieldLeft = 180;
    private const double FieldRightInset = 31;

    /// <summary>The label column: labels are centred on this, not right-aligned to it.</summary>
    private const double LabelCentre = 137;

    /// <summary>The big button: 60 wide, 80 tall, 31 in from the left, 26 down from the band's top.</summary>
    private const double ButtonWidth = 60;
    private const double ButtonHeight = 80;

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    private static readonly string[] Times =
        [.. Enumerable.Range(0, 48).Select(h => DateTime.Today.AddMinutes(30 * h).ToString("h:mm tt", CultureInfo.CurrentCulture))];

    /// <summary>What the Reminder picker offers, in the reference's own order.</summary>
    public static readonly (string Label, int? Minutes)[] Reminders =
    [
        ("None", null), ("0 minutes", 0), ("5 minutes", 5), ("10 minutes", 10), ("15 minutes", 15),
        ("30 minutes", 30), ("1 hour", 60), ("2 hours", 120), ("1 day", 1440),
    ];

    public static readonly string[] ShowAs = ["Free", "Tentative", "Busy", "Out of Office"];

    private readonly TextBox _title = Field();
    private readonly TextBox _location = Field();
    private readonly TextBox _required = Field();
    private readonly TextBox _optional = Field();
    private readonly TextBox _notes = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = default,
        Padding = new Thickness(10, 8),
    };

    private readonly CalendarDatePicker _startDate = DatePicker();
    private readonly ComboBox _startTime = new() { Width = 110, Height = 28 };
    private readonly CalendarDatePicker _endDate = DatePicker();
    private readonly ComboBox _endTime = new() { Width = 110, Height = 28 };
    private readonly CheckBox _allDay = new() { Content = "All day" };
    private readonly CheckBox _timeZones = new();
    private readonly Button _recurring = new();
    private readonly TextBlock _infoBarText = new();
    private readonly Border _infoBar;
    private readonly StackPanel _meetingRows = new() { Spacing = 0 };

    private readonly CalendarEvent _original;
    private readonly IReadOnlyList<Collection> _calendars;
    private readonly bool _meeting;
    private string? _rrule;
    private int _showAs;
    private int? _reminderMinutes;
    private long _collectionId;

    public AppointmentSurface(CalendarEvent appointment, IReadOnlyList<Collection> calendars, long collectionId, bool meeting)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        _original = appointment;
        _calendars = calendars ?? [];
        _meeting = meeting;
        _rrule = appointment.Rrule;
        _collectionId = collectionId;
        _reminderMinutes = appointment.ReminderMinutes;
        _showAs = appointment.Busy switch
        {
            BusyStatus.Free => 0,
            BusyStatus.Tentative => 1,
            BusyStatus.OutOfOffice => 3,
            _ => 2,
        };

        _title.Text = appointment.Summary;
        _location.Text = appointment.Location;
        _notes.Text = appointment.Description;
        _required.Text = string.Join("; ", appointment.Attendees.Where(a => a.Role != "OPT-PARTICIPANT").Select(a => a.Address));
        _optional.Text = string.Join("; ", appointment.Attendees.Where(a => a.Role == "OPT-PARTICIPANT").Select(a => a.Address));

        _startTime.ItemsSource = Times;
        _endTime.ItemsSource = Times;
        _startDate.SelectedDate = appointment.Start.Wall.Date;
        _endDate.SelectedDate = (appointment.AllDay ? appointment.End.Wall.AddDays(-1) : appointment.End.Wall).Date;
        _startTime.SelectedIndex = Slot(appointment.Start.Wall);
        _endTime.SelectedIndex = Slot(appointment.End.Wall);
        _allDay.IsChecked = appointment.AllDay;
        _allDay.IsCheckedChanged += (_, _) =>
        {
            ApplyAllDay();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _title.TextChanged += (_, _) => TitleChanged?.Invoke(this, EventArgs.Empty);

        Bind(_allDay, TemplatedControl.ForegroundProperty, "compose.header.text.brush");
        Bind(_timeZones, TemplatedControl.ForegroundProperty, "compose.header.text.brush");

        _infoBar = BuildInfoBar();
        Content = BuildRoot();
        ApplyAllDay();
    }

    /// <summary>The window's caption: the subject, then what kind of item this is.</summary>
    public string Title => (_title.Text is { Length: > 0 } text ? text : "Untitled")
                           + (_meeting ? " - Meeting" : " - Appointment");

    public event EventHandler? TitleChanged;

    /// <summary>Raised when something the ribbon's enablement depends on changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The window asks to close: Save &amp; Close, Send, Delete or Escape.</summary>
    public event EventHandler<AppointmentResult>? Finished;

    public event EventHandler? Cancelled;

    /// <summary>Shown above the form, as the reference shows one on a meeting not yet sent.</summary>
    public string InfoBar
    {
        get => _infoBarText.Text ?? string.Empty;
        set
        {
            _infoBarText.Text = value;
            _infoBar.IsVisible = value.Length > 0;
        }
    }

    // ---- Layout ------------------------------------------------------------------------------

    private Control BuildRoot()
    {
        var band = new Panel { Height = FormHeight };
        Bind(band, BackgroundProperty, "list.background.brush");

        var big = BuildBigButton();
        Canvas.SetLeft(big, 31);
        Canvas.SetTop(big, 26);

        var canvas = new Canvas();
        canvas.Children.Add(big);
        band.Children.Add(canvas);

        // The rows themselves stretch, so the fields follow the window's width. The label column
        // is a fixed width whose centre is the measured 137.
        var rows = new StackPanel { Margin = new Thickness(0, _meeting ? 4 : 30, FieldRightInset, 0) };

        if (_meeting)
        {
            _meetingRows.Children.Add(FromRow());
            _meetingRows.Children.Add(Row("Title", _title, underline: true));
            _meetingRows.Children.Add(Row("Required", _required, underline: true, labelIsButton: true));
            _meetingRows.Children.Add(Row("Optional", _optional, underline: true, labelIsButton: true));
            rows.Children.Add(_meetingRows);
        }
        else
        {
            rows.Children.Add(Row("Title", _title, underline: true));
        }

        rows.Children.Add(Row("Start time", TimeLine(_startDate, _startTime, AllDayBlock()), underline: false));
        rows.Children.Add(Row("End time", TimeLine(_endDate, _endTime, RecurringButton()), underline: false));

        var rule = new Border { Height = 1, Margin = new Thickness(FieldLeft, 6, 0, 0) };
        Bind(rule, BackgroundProperty, "border.subtle.brush");
        rows.Children.Add(rule);
        rows.Children.Add(Row("Location", _location, underline: true, extra: _meeting ? RoomsButton() : null));

        band.Children.Add(rows);

        var body = new Border { Padding = default };
        Bind(body, BackgroundProperty, "compose.body.background.brush");
        Bind(_notes, TemplatedControl.ForegroundProperty, "compose.body.text.brush");
        Bind(_notes, TemplatedControl.BackgroundProperty, "compose.body.background.brush");
        body.Child = _notes;

        var root = new DockPanel();
        DockPanel.SetDock(_infoBar, Dock.Top);
        root.Children.Add(_infoBar);
        DockPanel.SetDock(band, Dock.Top);
        root.Children.Add(band);
        root.Children.Add(body);
        return root;
    }

    private Border BuildInfoBar()
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("info", 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");

        _infoBarText.VerticalAlignment = VerticalAlignment.Center;
        Bind(_infoBarText, TextBlock.ForegroundProperty, "compose.header.text.brush");

        var bar = new Border
        {
            Padding = new Thickness(30, 7),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { glyph, _infoBarText },
            },
        };
        Bind(bar, BackgroundProperty, "list.background.brush");
        return bar;
    }

    /// <summary>Save &amp; Close on an appointment, Send on a meeting: the same 60×80 slot.</summary>
    private Control BuildBigButton()
    {
        var command = _meeting ? AppointmentCommands.Send : AppointmentCommands.SaveAndClose;
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(command.Icon, 24),
            FontFamily = IconFont.Family,
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "text.primary.brush");

        var label = new TextBlock
        {
            Text = command.Label,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = ButtonWidth - 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");

        var button = new Button
        {
            Width = ButtonWidth,
            Height = ButtonHeight,
            Padding = new Thickness(2, 6),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { glyph, label } },
        };
        Bind(button, BackgroundProperty, "surface.raised.brush");
        Bind(button, BorderBrushProperty, "border.strong.brush");
        button.Click += (_, _) => Commit(deleted: false, sent: _meeting);
        return button;
    }

    private Control FromRow()
    {
        var address = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = _original.Organizer };
        Bind(address, TextBlock.ForegroundProperty, "compose.header.text.brush");
        return Row("From", address, underline: true, labelIsButton: true);
    }

    private Control AllDayBlock()
    {
        var globe = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("publish-calendar", 16),
            FontFamily = IconFont.Family,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(globe, TextBlock.ForegroundProperty, "accent.rest.brush");

        var zones = new TextBlock { Text = "Time zones", VerticalAlignment = VerticalAlignment.Center };
        Bind(zones, TextBlock.ForegroundProperty, "compose.header.text.brush");

        return Line(_allDay, _timeZones, globe, zones);
    }

    private Control RecurringButton()
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("recurrence", 16),
            FontFamily = IconFont.Family,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "calendar.link.brush");

        var caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        Bind(caption, TextBlock.ForegroundProperty, "calendar.link.brush");

        _recurring.Background = Brushes.Transparent;
        _recurring.BorderThickness = default;
        _recurring.Padding = new Thickness(4, 0);
        _recurring.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { glyph, caption } };
        _recurring.Click += async (_, _) => await EditRecurrenceAsync();
        RefreshRecurrence(caption);
        _recurringCaption = caption;
        return _recurring;
    }

    private TextBlock? _recurringCaption;

    private Control RoomsButton()
    {
        var button = new Button
        {
            Content = "Rooms",
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent,
            BorderThickness = default,
        };
        button.Click += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        return button;
    }

    private Control Row(string label, Control control, bool underline, bool labelIsButton = false, Control? extra = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{FieldLeft},*,Auto") };

        // A centred child in a column of FieldLeft sits at half of it, so the shift that puts
        // its centre on the measured 137 is twice the difference.
        var shift = new Thickness((LabelCentre * 2) - FieldLeft, 0, 0, 0);

        Control caption;
        if (labelIsButton)
        {
            var face = new Button
            {
                Content = label == "From" ? label + "  \u2304" : label,
                Width = 78,
                Height = 26,
                Padding = default,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = shift,
            };

            // These read as pressable in the reference — a light face inside a line — and the
            // window is not a dialog, so it does not inherit the dialog stylesheet's flat buttons.
            Bind(face, BackgroundProperty, "surface.raised.brush");
            Bind(face, BorderBrushProperty, "border.strong.brush");
            Bind(face, TemplatedControl.ForegroundProperty, "text.primary.brush");
            caption = face;
        }
        else
        {
            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                // Centred on the measured 137 rather than right-aligned: the reference's own
                // labels do not share a right edge, and they do share a centre.
                Margin = shift,
            };
            Bind(text, TextBlock.ForegroundProperty, "compose.header.label.brush");
            caption = text;
        }

        Grid.SetColumn(caption, 0);
        grid.Children.Add(caption);

        var host = underline
            ? new Border { Child = control, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4) }
            : (Control)new Border { Child = control, Padding = new Thickness(0, 3) };
        if (host is Border bordered && underline) Bind(bordered, BorderBrushProperty, "compose.field.rule.brush");
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);

        if (extra is not null)
        {
            Grid.SetColumn(extra, 2);
            grid.Children.Add(extra);
        }

        return grid;
    }

    private static Control TimeLine(Control date, Control time, Control tail)
        => Line(date, time, tail);

    private static Control Line(params Control[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    private static TextBox Field()
    {
        var box = new TextBox { BorderThickness = default, Background = Brushes.Transparent, Padding = new Thickness(1, 0) };
        Bind(box, TemplatedControl.ForegroundProperty, "compose.header.text.brush");
        return box;
    }

    private static int Slot(DateTime when) => Math.Clamp((int)(when.TimeOfDay.TotalMinutes / 30), 0, 47);

    /// <summary>A date box written the reference's way: "Sun 8/16/2026", weekday and all.</summary>
    private static CalendarDatePicker DatePicker() => new()
    {
        Width = 150,
        Height = 28,
        SelectedDateFormat = CalendarDatePickerFormat.Custom,
        CustomDateFormatString = "ddd M/d/yyyy",
    };

    private void ApplyAllDay()
    {
        var whole = _allDay.IsChecked == true;
        _startTime.IsVisible = !whole;
        _endTime.IsVisible = !whole;
    }

    // ---- Commands the ribbon presses ----------------------------------------------------------

    /// <summary>Whether a command is available on this window right now.</summary>
    public bool IsCommandEnabled(CommandId id)
    {
        if (id == AppointmentCommands.ResponseOptions.Id || id == AppointmentCommands.Rooms.Id) return _meeting;
        if (id == AppointmentCommands.InviteAttendees.Id) return !_meeting;
        return true;
    }

    /// <summary>Runs a command from the window's ribbon. Anything unknown is reported, not swallowed.</summary>
    public string? Invoke(CommandId id)
    {
        if (id == AppointmentCommands.SaveAndClose.Id) { Commit(deleted: false, sent: false); return null; }
        if (id == AppointmentCommands.Send.Id) { Commit(deleted: false, sent: true); return null; }
        if (id == AppointmentCommands.Delete.Id) { Commit(deleted: true, sent: false); return null; }
        if (id == AppointmentCommands.MakeRecurring.Id) { _ = EditRecurrenceAsync(); return null; }
        if (id == AppointmentCommands.ShowAs.Id) { Cycle(ref _showAs, ShowAs.Length); return null; }
        if (id == AppointmentCommands.Reminder.Id) { CycleReminder(); return null; }
        if (id == AppointmentCommands.InviteAttendees.Id) { return "Invite Attendees turns this into a meeting — open New Meeting instead for now."; }
        return null;
    }

    private void Cycle(ref int index, int length)
    {
        index = (index + 1) % length;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CycleReminder()
    {
        var at = Array.FindIndex(Reminders, r => r.Minutes == _reminderMinutes);
        _reminderMinutes = Reminders[(Math.Max(0, at) + 1) % Reminders.Length].Minutes;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the ribbon's two pickers currently read.</summary>
    public string ShowAsText => ShowAs[Math.Clamp(_showAs, 0, ShowAs.Length - 1)];

    public string ReminderText => Reminders[Math.Max(0, Array.FindIndex(Reminders, r => r.Minutes == _reminderMinutes))].Label;

    private async Task EditRecurrenceAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var start = StartWall();
        var dialog = new RecurrenceDialog(_rrule, DateOnly.FromDateTime(start), EndWall() - start);
        await dialog.ShowDialog(owner);
        if (dialog.Cancelled) return;
        _rrule = dialog.Rrule;
        if (_recurringCaption is { } caption) RefreshRecurrence(caption);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshRecurrence(TextBlock caption)
        => caption.Text = string.IsNullOrEmpty(_rrule)
            ? "Make Recurring"
            : RecurrenceText.Describe(
                _rrule,
                EventTime.At(StartWall(), TimeZoneInfo.Local.Id),
                EventTime.At(EndWall(), TimeZoneInfo.Local.Id));

    private DateTime StartWall()
    {
        var date = _startDate.SelectedDate?.Date ?? DateTime.Today;
        return _allDay.IsChecked == true ? date : date.AddMinutes(Math.Max(0, _startTime.SelectedIndex) * 30);
    }

    private DateTime EndWall()
    {
        var date = _endDate.SelectedDate?.Date ?? _startDate.SelectedDate?.Date ?? DateTime.Today;
        if (_allDay.IsChecked == true) return date.AddDays(1);
        var end = date.AddMinutes(Math.Max(0, _endTime.SelectedIndex) * 30);
        return end <= StartWall() ? StartWall().AddMinutes(30) : end;
    }

    /// <summary>The appointment as the form now states it.</summary>
    public CalendarEvent Current()
    {
        var zone = TimeZoneInfo.Local.Id;
        var whole = _allDay.IsChecked == true;
        var start = whole ? EventTime.Date(DateOnly.FromDateTime(StartWall())) : EventTime.At(StartWall(), zone);
        var end = whole ? EventTime.Date(DateOnly.FromDateTime(EndWall())) : EventTime.At(EndWall(), zone);

        var attendees = new List<EventAttendee>();
        attendees.AddRange(Addresses(_required.Text).Select(a => new EventAttendee(a, Rsvp: true)));
        attendees.AddRange(Addresses(_optional.Text).Select(a => new EventAttendee(a, Role: "OPT-PARTICIPANT", Rsvp: true)));

        return _original with
        {
            Summary = _title.Text ?? string.Empty,
            Location = _location.Text ?? string.Empty,
            Description = _notes.Text ?? string.Empty,
            Start = start,
            End = end,
            Rrule = string.IsNullOrEmpty(_rrule) ? null : _rrule,
            Busy = _showAs switch
            {
                0 => BusyStatus.Free,
                1 => BusyStatus.Tentative,
                3 => BusyStatus.OutOfOffice,
                _ => BusyStatus.Busy,
            },
            ReminderMinutes = _reminderMinutes,
            Attendees = attendees,
            // A change is a change the server has to be told about, and iTIP says so with the
            // sequence. Bumped here rather than in the store, which does not know what changed.
            Sequence = _original.Sequence + 1,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    private static IEnumerable<string> Addresses(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Commit(bool deleted, bool sent)
    {
        var calendar = _calendars.Count == 0
            ? _collectionId
            : _calendars.FirstOrDefault(c => c.Id == _collectionId)?.Id ?? _calendars[0].Id;
        Finished?.Invoke(this, new AppointmentResult(Current(), calendar, deleted, sent));
    }

    /// <summary>Escape, or the window's close button with nothing changed.</summary>
    public void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    // ---- Harness poses ------------------------------------------------------------------------

    /// <summary>Fills the form, so a capture shows something to measure.</summary>
    public void Pose(string title, string location, string notes = "")
    {
        _title.Text = title;
        _location.Text = location;
        if (notes.Length > 0) _notes.Text = notes;
    }

    /// <summary>Presses Save &amp; Close (or Send) from the harness, which cannot click.</summary>
    public void PressPrimary() => Commit(deleted: false, sent: _meeting);
}
