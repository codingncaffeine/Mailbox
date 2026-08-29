using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
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

    /// <summary>Every half hour of the day, which is what the two time lists offer.</summary>
    private static readonly TimeOnly[] HalfHours =
        [.. Enumerable.Range(0, 48).Select(h => new TimeOnly(0, 0).Add(TimeSpan.FromMinutes(30 * h)))];

    /// <summary>
    /// The times this appointment's two lists offer: every half hour, and its own start and end
    /// where they fall between two.
    /// </summary>
    /// <remarks>
    /// The lists used to be the forty-eight half hours and nothing else, and the form read a time
    /// back as <c>index × 30</c>. So an appointment that did not start on a half hour could not be
    /// stated by the form at all: a quarter-hour standup opened, was not touched, was saved, and
    /// came back half an hour long — and the same on every appointment written by anything but
    /// this window, which is most of them. Carrying its own times means opening and saving changes
    /// nothing, which is the least a form can promise.
    /// </remarks>
    private readonly List<TimeOnly> _slots;

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
    private readonly ComboBox _startZone = ZonePicker();
    private readonly ComboBox _endZone = ZonePicker();

    /// <summary>The system's zones, offset-ordered, which is how the reference lists them.</summary>
    private static readonly IReadOnlyList<TimeZoneInfo> Zones =
        [.. TimeZoneInfo.GetSystemTimeZones().OrderBy(z => z.BaseUtcOffset)];
    private readonly Button _recurring = new();
    private readonly TextBlock _infoBarText = new();
    private readonly Border _infoBar;
    private readonly StackPanel _meetingRows = new() { Spacing = 0 };

    /// <summary>
    /// What the Tags group has put on this appointment — the categories as chips, a padlock when
    /// it is private, and the importance mark.
    /// </summary>
    /// <remarks>
    /// Authored: no capture shows the reference's window with any of the three set, so the strip
    /// is this application's reading of where they would show. It earns its place because the
    /// ribbon draws no pressed state for a toggle — without it, three of the four Tags buttons
    /// would change something the reader cannot see. The chips are drawn as the message list's
    /// are, from the same token per category.
    /// </remarks>
    private readonly StackPanel _tagStrip = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Border _tags;

    private readonly CalendarEvent _original;
    private readonly IReadOnlyList<Collection> _calendars;
    private readonly bool _meeting;
    private string? _rrule;
    private int _showAs;
    private int? _reminderMinutes;
    private long _collectionId;
    private IReadOnlyList<string> _categories;
    private bool _private;
    private TaskUrgency _urgency;

    public AppointmentSurface(CalendarEvent appointment, IReadOnlyList<Collection> calendars, long collectionId, bool meeting)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        _original = appointment;
        _calendars = calendars ?? [];
        _meeting = meeting;
        _rrule = appointment.Rrule;
        _collectionId = collectionId;
        _reminderMinutes = appointment.ReminderMinutes;
        _categories = appointment.Categories;
        _private = appointment.IsPrivate;
        _urgency = appointment.Urgency;
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

        _slots = Slots(appointment);
        var labels = _slots.Select(t => t.ToString("h:mm tt", CultureInfo.CurrentCulture)).ToList();
        _startTime.ItemsSource = labels;
        _endTime.ItemsSource = labels;
        _startDate.SelectedDate = appointment.Start.Wall.Date;
        _endDate.SelectedDate = (appointment.AllDay ? appointment.End.Wall.AddDays(-1) : appointment.End.Wall).Date;
        _startTime.SelectedIndex = Slot(appointment.Start.Wall);
        _endTime.SelectedIndex = Slot(appointment.End.Wall);
        _allDay.IsChecked = appointment.AllDay;

        // The zone pickers carry the appointment's own zones, and the tick starts on for an
        // appointment already written in a zone that is not this machine's — otherwise saving
        // it would quietly re-file it as local.
        _startZone.ItemsSource = Zones.Select(z => z.DisplayName).ToList();
        _endZone.ItemsSource = _startZone.ItemsSource;
        SelectZone(_startZone, appointment.Start.TzId);
        SelectZone(_endZone, appointment.End.TzId);
        _timeZones.IsChecked = !IsLocal(appointment.Start.TzId) || !IsLocal(appointment.End.TzId);
        _timeZones.IsCheckedChanged += (_, _) =>
        {
            ApplyAllDay();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _startZone.SelectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _endZone.SelectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _allDay.IsCheckedChanged += (_, _) =>
        {
            ApplyAllDay();

            // Ticking All day shows the day as free, and clearing it shows it as busy — the
            // reference's own behaviour, and the reason its all-day events do not claim the day in
            // the date navigator. Only on a change, never on the way in: an all-day event somebody
            // deliberately marked Busy has to survive being looked at. It sets the value; it does
            // not lock it, and the picker still overrides it afterwards.
            _showAs = _allDay.IsChecked == true ? 0 : 2;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _title.TextChanged += (_, _) => TitleChanged?.Invoke(this, EventArgs.Empty);

        Bind(_allDay, TemplatedControl.ForegroundProperty, "compose.header.text.brush");
        Bind(_timeZones, TemplatedControl.ForegroundProperty, "compose.header.text.brush");

        _infoBar = BuildInfoBar();
        _tags = BuildTagStrip();
        Content = BuildRoot();
        RefreshTags();
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

        rows.Children.Add(Row("Start time", Line(_startDate, _startTime, _startZone, AllDayBlock()), underline: false));
        rows.Children.Add(Row("End time", Line(_endDate, _endTime, _endZone, RecurringButton()), underline: false));

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
        DockPanel.SetDock(_tags, Dock.Top);
        root.Children.Add(_tags);
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

    private Border BuildTagStrip()
    {
        var bar = new Border
        {
            Padding = new Thickness(30, 6),
            IsVisible = false,
            Child = _tagStrip,
        };
        Bind(bar, BackgroundProperty, "list.background.brush");
        return bar;
    }

    /// <summary>
    /// Redraws the strip from what the Tags group has set, and hides it when nothing is.
    /// </summary>
    private void RefreshTags()
    {
        _tagStrip.Children.Clear();

        if (_private) _tagStrip.Children.Add(TagMark("private", "Private", "text.primary"));
        if (_urgency == TaskUrgency.High) _tagStrip.Children.Add(TagMark("importance", "High importance", "status.danger"));
        if (_urgency == TaskUrgency.Low) _tagStrip.Children.Add(TagMark("importance-low", "Low importance", "ribbon.icon.blue"));

        foreach (var name in _categories) _tagStrip.Children.Add(CategoryChip(name));

        _tags.IsVisible = _tagStrip.Children.Count > 0;
    }

    /// <summary>One category, drawn as the message list draws it: its colour behind its name.</summary>
    private static Control CategoryChip(string name)
    {
        var token = App.Categories.Named(name)?.ColourToken;

        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = name };
        Bind(text, TextBlock.ForegroundProperty, "text.primary.brush");

        var chip = new Border
        {
            Padding = new Thickness(7, 1),
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Child = text,
        };
        Bind(chip, Border.BorderBrushProperty, "border.subtle.brush");
        if (token is { Length: > 0 }) Bind(chip, BackgroundProperty, token + ".brush");
        return chip;
    }

    /// <summary>The padlock and the two importance marks, each the icon its own button carries.</summary>
    private static Control TagMark(string icon, string caption, string tint)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The same tint the command's own icon carries, so the strip and the button that set it
        // cannot end up two different reds.
        Bind(glyph, TextBlock.ForegroundProperty, tint + ".brush");

        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = caption };
        Bind(text, TextBlock.ForegroundProperty, "compose.header.text.brush");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children = { glyph, text },
        };
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

    /// <summary>The half hours, with this appointment's own start and end folded in.</summary>
    private static List<TimeOnly> Slots(CalendarEvent appointment)
    {
        var slots = new SortedSet<TimeOnly>(HalfHours);
        if (!appointment.AllDay)
        {
            slots.Add(TimeOnly.FromDateTime(appointment.Start.Wall));
            slots.Add(TimeOnly.FromDateTime(appointment.End.Wall));
        }

        return [.. slots];
    }

    /// <summary>Which row of the list a time is, or the latest row before it.</summary>
    private int Slot(DateTime when)
    {
        var wanted = TimeOnly.FromDateTime(when);
        var at = _slots.BinarySearch(wanted);
        return at >= 0 ? at : Math.Clamp(~at - 1, 0, _slots.Count - 1);
    }

    private TimeOnly Chosen(ComboBox box) => _slots[Math.Clamp(box.SelectedIndex, 0, _slots.Count - 1)];

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

        // The reference's Time Zones control: the tick reveals a zone beside each time, and an
        // all-day event has no time for a zone to qualify.
        var zoned = !whole && _timeZones.IsChecked == true;
        _startZone.IsVisible = zoned;
        _endZone.IsVisible = zoned;
    }

    /// <summary>The picker's zone, or the machine's when the tick is off.</summary>
    private string ZoneId(ComboBox picker)
        => _timeZones.IsChecked == true && picker.SelectedIndex >= 0
            ? Zones[picker.SelectedIndex].Id
            : TimeZoneInfo.Local.Id;

    private static void SelectZone(ComboBox picker, string? tzId)
    {
        var wanted = tzId is { Length: > 0 } && !string.Equals(tzId, "UTC", StringComparison.OrdinalIgnoreCase)
            ? tzId
            : TimeZoneInfo.Local.Id;

        var at = 0;
        for (var i = 0; i < Zones.Count; i++)
        {
            if (string.Equals(Zones[i].Id, wanted, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            if (string.Equals(Zones[i].Id, TimeZoneInfo.Local.Id, StringComparison.OrdinalIgnoreCase)) at = i;
        }

        picker.SelectedIndex = at;
    }

    private static bool IsLocal(string? tzId)
        => tzId is null or { Length: 0 }
           || string.Equals(tzId, TimeZoneInfo.Local.Id, StringComparison.OrdinalIgnoreCase);

    private static ComboBox ZonePicker() => new()
    {
        Width = 230,
        Height = 28,
        IsVisible = false,
    };

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

        // The Tags group. Each writes onto the appointment being edited rather than into the
        // store: nothing here is saved until the big button is pressed, and a tag set on a
        // window that is then abandoned must go with it.
        if (id == AppointmentCommands.Categorize.Id) { ShowCategories(); return null; }
        if (id == AppointmentCommands.Private.Id) { SetPrivate(!_private); return null; }
        if (id == AppointmentCommands.HighImportance.Id) { SetUrgency(TaskUrgency.High); return null; }
        if (id == AppointmentCommands.LowImportance.Id) { SetUrgency(TaskUrgency.Low); return null; }

        // The two this window places and cannot do. Both ask a server for a directory of
        // resources or of who has answered, and no account here offers one.
        if (id == AppointmentCommands.Rooms.Id)
        {
            return "A room list is a directory of resources on a server, which no account here offers.";
        }

        if (id == AppointmentCommands.ResponseOptions.Id)
        {
            return "Response options are the server's — who may propose a new time, and whether "
                + "replies come back to it. There is no server here to hold them.";
        }

        // Anything else at all. The summary above this method promised as much and the method
        // did the opposite: a command with no branch left no status line, no log line and no
        // InfoBar, so a button that did nothing looked exactly like one that had worked.
        Log.Warn($"The appointment window has no handler for {id}.");
        return $"{id} is not something this window can do.";
    }

    /// <summary>The categories the form now carries — what Copy and Forward read.</summary>
    public IReadOnlyList<string> Categories => _categories;

    private void ShowCategories()
        => ItemCategoryMenu.Show(
            App.Categories,
            this,
            _title.Text is { Length: > 0 } named ? named : "Untitled",
            _categories,
            SetCategories,
            allCategories: null);

    /// <summary>What the menu came back with, whole.</summary>
    public void SetCategories(IReadOnlyList<string> categories)
    {
        _categories = categories ?? [];
        RefreshTags();
        Changed?.Invoke(this, EventArgs.Empty);
        Log.Info($"Appointment: categories are now {(_categories.Count == 0 ? "none" : string.Join(", ", _categories))}.");
    }

    public void SetPrivate(bool value)
    {
        _private = value;
        RefreshTags();
        Changed?.Invoke(this, EventArgs.Empty);
        Log.Info($"Appointment: {(value ? "private" : "not private")}.");
    }

    /// <summary>High or Low, each a toggle back to normal — the way the Tasks bar's two are.</summary>
    public void SetUrgency(TaskUrgency urgency)
    {
        _urgency = _urgency == urgency ? TaskUrgency.Normal : urgency;
        RefreshTags();
        Changed?.Invoke(this, EventArgs.Empty);
        Log.Info($"Appointment: {_urgency.ToString().ToLowerInvariant()} importance (PRIORITY {TaskItem.PriorityFor(_urgency)}).");
    }

    /// <summary>What the Show As picker offers, and what choosing one does.</summary>
    public void SetShowAs(int index)
    {
        _showAs = Math.Clamp(index, 0, ShowAs.Length - 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetReminder(int? minutes)
    {
        _reminderMinutes = minutes;
        Changed?.Invoke(this, EventArgs.Empty);
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
        var dialog = new RecurrenceDialog(_rrule, DateOnly.FromDateTime(start), EndWall() - start, TimeZoneInfo.Local);
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
                EventTime.At(StartWall(), ZoneId(_startZone)),
                EventTime.At(EndWall(), ZoneId(_endZone)));

    private DateTime StartWall()
    {
        var date = _startDate.SelectedDate?.Date ?? DateTime.Today;
        return _allDay.IsChecked == true ? date : date + Chosen(_startTime).ToTimeSpan();
    }

    private DateTime EndWall()
    {
        var date = _endDate.SelectedDate?.Date ?? _startDate.SelectedDate?.Date ?? DateTime.Today;
        if (_allDay.IsChecked == true) return date.AddDays(1);
        var end = date + Chosen(_endTime).ToTimeSpan();
        return end <= StartWall() ? StartWall().AddMinutes(30) : end;
    }

    /// <summary>The appointment as the form now states it.</summary>
    public CalendarEvent Current()
    {
        var whole = _allDay.IsChecked == true;
        var start = whole ? EventTime.Date(DateOnly.FromDateTime(StartWall())) : EventTime.At(StartWall(), ZoneId(_startZone));
        var end = whole ? EventTime.Date(DateOnly.FromDateTime(EndWall())) : EventTime.At(EndWall(), ZoneId(_endZone));

        // Somebody already asked keeps their name and their answer: the form holds addresses and
        // nothing else, so rebuilding an attendee from the box alone would throw away every reply
        // the meeting has had the moment it is saved.
        var attendees = new List<EventAttendee>();
        attendees.AddRange(Addresses(_required.Text).Select(a => Asked(a, "REQ-PARTICIPANT")));
        attendees.AddRange(Addresses(_optional.Text).Select(a => Asked(a, "OPT-PARTICIPANT")));

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
            Categories = _categories,
            IsPrivate = _private,
            Urgency = _urgency,
            Attendees = attendees,
            // A change is a change the server has to be told about, and iTIP says so with the
            // sequence. Bumped here rather than in the store, which does not know what changed.
            Sequence = _original.Sequence + 1,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// An attendee as the form now states them, carrying forward what the appointment already
    /// knew about anybody who was on it before.
    /// </summary>
    private EventAttendee Asked(string address, string role)
    {
        var known = _original.Attendees.FirstOrDefault(a => SameAddress(a.Address, address));
        return known is null
            ? new EventAttendee(address, Role: role, Rsvp: true)
            : known with { Address = address, Role = role };
    }

    /// <summary>Two addresses are the same person, <c>mailto:</c> and letter case aside.</summary>
    private static bool SameAddress(string left, string right)
        => string.Equals(Bare(left), Bare(right), StringComparison.OrdinalIgnoreCase);

    private static string Bare(string address)
    {
        var text = address.Trim();
        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) text = text[7..];

        // "A. Person <a.person@example.com>" is the same person as the address inside it.
        var open = text.LastIndexOf('<');
        var close = text.LastIndexOf('>');
        return open >= 0 && close > open ? text[(open + 1)..close].Trim() : text;
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
    /// <summary>
    /// Moves the appointment, keeping everything else — what the Scheduling Assistant does when
    /// a time is picked out of everybody's day.
    /// </summary>
    public void MoveTo(DateTime start, DateTime end)
    {
        _startDate.SelectedDate = start.Date;
        _startTime.SelectedIndex = Slot(start);
        _endDate.SelectedDate = end.Date;
        _endTime.SelectedIndex = Slot(end);
    }

    public void Pose(string title, string location, string notes = "")
    {
        _title.Text = title;
        _location.Text = location;
        if (notes.Length > 0) _notes.Text = notes;
    }

    /// <summary>Types into one field of the form, named the way the pose names it.</summary>
    /// <remarks>
    /// A start or an end is written <c>yyyy-MM-ddTHH:mm</c> and lands in the two controls the row
    /// really has — the date picker and the half-hour list — so what a pose can ask for is exactly
    /// what a reader can, half-hours included.
    /// </remarks>
    public void PoseField(string field, string value)
    {
        switch (field.ToLowerInvariant())
        {
            case "title": _title.Text = value; break;
            case "location": _location.Text = value; break;
            case "notes": _notes.Text = value.Replace("\\n", "\n", StringComparison.Ordinal); break;

            case "start" when DateTime.TryParse(value, CultureInfo.InvariantCulture, out var start):
                _startDate.SelectedDate = start.Date;
                _startTime.SelectedIndex = Slot(start);
                break;

            case "end" when DateTime.TryParse(value, CultureInfo.InvariantCulture, out var end):
                _endDate.SelectedDate = end.Date;
                _endTime.SelectedIndex = Slot(end);
                break;

            default:
                Log.Info($"Harness: the appointment form has no field called “{field}”, or “{value}” is not a time.");
                break;
        }
    }

    /// <summary>Ticks or clears All day, which is a checkbox and so out of a pose's reach.</summary>
    public void PoseAllDay(bool whole) => _allDay.IsChecked = whole;

    /// <summary>What the form now states, for a run to read back before it presses anything.</summary>
    public string FormLine
    {
        get
        {
            var current = Current();
            return $"“{current.Summary}” {current.Start.ToLocalText()}–{current.End.ToLocalText()}"
                   + $"{(current.AllDay ? " all-day" : string.Empty)} at “{current.Location}”, "
                   + $"{ShowAsText}, reminder {ReminderText}, "
                   + $"repeats {(current.Rrule is { Length: > 0 } rule ? rule : "never")}, "
                   + $"categories {(_categories.Count == 0 ? "none" : string.Join("/", _categories))}";
        }
    }

    /// <summary>Presses Save &amp; Close (or Send) from the harness, which cannot click.</summary>
    public void PressPrimary() => Commit(deleted: false, sent: _meeting);
}
