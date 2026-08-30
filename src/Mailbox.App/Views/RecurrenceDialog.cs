using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;

namespace Mailbox.App.Views;

/// <summary>
/// The reference's Appointment Recurrence dialog: the appointment's own time, the pattern, and
/// the range the series runs over.
/// </summary>
/// <remarks>
/// A pattern editor, not an RRULE builder (§9). It asks the questions the reference asks — "the
/// second Tuesday of every 1 month(s)", "end after 10 occurrences" — and
/// <see cref="RecurrencePattern"/> turns the answers into RFC 5545. A rule this editor cannot
/// state is left exactly as it was found rather than flattened into one it can, so a series
/// another client wrote survives being looked at.
/// </remarks>
public sealed class RecurrenceDialog : Window
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The rule as edited: null when the series was made a single appointment again.</summary>
    public string? Rrule { get; private set; }

    /// <summary>True when nothing was changed.</summary>
    public bool Cancelled { get; private set; } = true;

    private readonly RadioButton _daily = Radio("frequency", "Daily");
    private readonly RadioButton _weekly = Radio("frequency", "Weekly");
    private readonly RadioButton _monthly = Radio("frequency", "Monthly");
    private readonly RadioButton _yearly = Radio("frequency", "Yearly");

    private readonly NumericUpDown _dailyEvery = Spinner(1, 999);
    private readonly RadioButton _dailyEveryDay = Radio("daily", "day(s)", true);
    private readonly RadioButton _dailyWeekday = Radio("daily", "Every weekday");

    private readonly NumericUpDown _weeklyEvery = Spinner(1, 99);
    private readonly CheckBox[] _weekDays;

    private readonly RadioButton _monthlyByDay = Radio("monthly", "Day", true);
    private readonly RadioButton _monthlyByWeekday = Radio("monthly", "The");
    private readonly NumericUpDown _monthlyDay = Spinner(1, 31);
    private readonly NumericUpDown _monthlyEvery = Spinner(1, 99);
    private readonly ComboBox _monthlyOrdinal = new() { MinWidth = 90 };
    private readonly ComboBox _monthlyWeekday = new() { MinWidth = 110 };

    private readonly ComboBox _yearlyMonth = new() { MinWidth = 120 };
    private readonly NumericUpDown _yearlyDay = Spinner(1, 31);

    private readonly RadioButton _noEnd = Radio("range", "No end date", true);
    private readonly RadioButton _endAfter = Radio("range", "End after:");
    private readonly RadioButton _endBy = Radio("range", "End by:");
    private readonly NumericUpDown _occurrences = Spinner(1, 999);
    private readonly CalendarDatePicker _until = new() { MinWidth = 160 };

    private static readonly string[] Ordinals = ["first", "second", "third", "fourth", "last"];

    private readonly string? _existing;

    /// <summary>The appointment's zone, which is what "end by" a date means in.</summary>
    private readonly TimeZoneInfo? _zone;

    private readonly (DateOnly Date, TimeOnly? Time, TimeSpan Duration) _openedOn;

    private readonly ComboBox _startTimeBox = new() { MinWidth = 110 };
    private readonly ComboBox _endTimeBox = new() { MinWidth = 110 };
    private readonly ComboBox _durationBox = new() { MinWidth = 110 };
    private readonly CalendarDatePicker _rangeStart = new() { MinWidth = 160 };
    private readonly List<TimeOnly> _timeSlots = [];
    private bool _interlocking;

    /// <summary>The reference's own duration list for this dialog's drop-down.</summary>
    private static readonly TimeSpan[] Durations =
    [
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45),
        TimeSpan.FromHours(1), TimeSpan.FromMinutes(90), TimeSpan.FromHours(2),
        TimeSpan.FromHours(3), TimeSpan.FromHours(4), TimeSpan.FromHours(8),
    ];

    /// <summary>The appointment time as edited here, or null for each half left alone.</summary>
    public TimeOnly? EditedStartTime { get; private set; }
    public TimeSpan? EditedDuration { get; private set; }

    /// <summary>The range's Start date as edited, or null when it was left where it was.</summary>
    public DateOnly? EditedStartDate { get; private set; }

    private int Slot(TimeOnly time) => Math.Clamp((time.Hour * 2) + (time.Minute >= 30 ? 1 : 0), 0, 47);

    private void MoveEndKeepingDuration()
    {
        _interlocking = true;
        var start = _timeSlots[Math.Clamp(_startTimeBox.SelectedIndex, 0, 47)];
        _endTimeBox.SelectedIndex = Slot(start.AddMinutes(Math.Clamp(CurrentDuration().TotalMinutes, 0, 1439)));
        _interlocking = false;
    }

    private void RecomputeDuration()
    {
        _interlocking = true;
        var start = _timeSlots[Math.Clamp(_startTimeBox.SelectedIndex, 0, 47)];
        var end = _timeSlots[Math.Clamp(_endTimeBox.SelectedIndex, 0, 47)];
        var minutes = (end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;
        if (minutes <= 0) minutes += 24 * 60;
        ShowDuration(TimeSpan.FromMinutes(minutes));
        _interlocking = false;
    }

    private void MoveEndFromDuration()
    {
        _interlocking = true;
        var start = _timeSlots[Math.Clamp(_startTimeBox.SelectedIndex, 0, 47)];
        _endTimeBox.SelectedIndex = Slot(start.AddMinutes(Math.Clamp(CurrentDuration().TotalMinutes, 0, 1439)));
        _interlocking = false;
    }

    /// <summary>What the duration list says, offered or written in as the appointment's own.</summary>
    private TimeSpan CurrentDuration()
    {
        var index = _durationBox.SelectedIndex;
        if (index >= 0 && index < Durations.Length) return Durations[index];
        return _openedOn.Duration;
    }

    /// <summary>Puts a duration on the list even when it is not one of the drop-down's own.</summary>
    private void ShowDuration(TimeSpan duration)
    {
        var label = Length(duration);
        var items = Durations.Select(Length).ToList();
        if (!items.Contains(label)) items.Add(label);
        _durationBox.ItemsSource = items;
        _durationBox.SelectedIndex = items.IndexOf(label);
    }

    /// <summary>The reference's first group: the appointment's own Start, End and Duration.</summary>
    private Control TimeBox()
    {
        var row = Row(
            Text("Start:"), _startTimeBox,
            Text("End:"), _endTimeBox,
            Text("Duration:"), _durationBox);
        return Group("Appointment time", row);
    }

    public RecurrenceDialog(string? rrule, DateOnly start, TimeSpan duration, TimeZoneInfo? zone = null, TimeOnly? startTime = null)
    {
        _existing = rrule;
        _zone = zone;
        _openedOn = (start, startTime, duration);
        Title = "Appointment Recurrence";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Spelt out, as the reference spells them: Sunday, not Sun.
        var names = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
        _weekDays =
        [
            .. Enumerable.Range(0, 7).Select(i => new CheckBox { Content = names[i], MinWidth = 110 }),
        ];
        foreach (var box in _weekDays) Bind(box, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");

        _monthlyOrdinal.ItemsSource = Ordinals;
        _monthlyOrdinal.SelectedIndex = 0;
        _monthlyWeekday.ItemsSource = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
        _monthlyWeekday.SelectedIndex = (int)start.DayOfWeek;
        _yearlyMonth.ItemsSource = CultureInfo.CurrentCulture.DateTimeFormat.MonthNames.Take(12).ToList();
        _yearlyMonth.SelectedIndex = start.Month - 1;
        _yearlyDay.Value = start.Day;
        _monthlyDay.Value = start.Day;
        _occurrences.Value = 10;
        _until.SelectedDate = start.AddMonths(3).ToDateTime(TimeOnly.MinValue);

        // The reference's first group: Start, End and Duration drop-downs — the appointment's
        // own time, editable here as it is there. Interlocked the way its form interlocks:
        // moving Start keeps the duration, moving End or Duration recomputes the other.
        for (var half = 0; half < 48; half++)
        {
            _timeSlots.Add(new TimeOnly(half / 2, (half % 2) * 30));
        }

        _startTimeBox.ItemsSource = _timeSlots.Select(t => t.ToString("t", CultureInfo.CurrentCulture)).ToList();
        _endTimeBox.ItemsSource = _timeSlots.Select(t => t.ToString("t", CultureInfo.CurrentCulture)).ToList();
        _durationBox.ItemsSource = Durations.Select(Length).ToList();

        var opens = startTime ?? new TimeOnly(8, 0);
        _startTimeBox.SelectedIndex = Slot(opens);
        _endTimeBox.SelectedIndex = Slot(opens.AddMinutes(Math.Clamp(duration.TotalMinutes, 0, 1439)));
        ShowDuration(duration);

        var whole = startTime is null;
        _startTimeBox.IsEnabled = !whole;
        _endTimeBox.IsEnabled = !whole;
        _durationBox.IsEnabled = !whole;

        _startTimeBox.SelectionChanged += (_, _) => { if (_interlocking) return; MoveEndKeepingDuration(); };
        _endTimeBox.SelectionChanged += (_, _) => { if (_interlocking) return; RecomputeDuration(); };
        _durationBox.SelectionChanged += (_, _) => { if (_interlocking) return; MoveEndFromDuration(); };

        // And the range's own Start date, which the reference's group begins with.
        _rangeStart.SelectedDate = start.ToDateTime(TimeOnly.MinValue);

        Load(RecurrencePattern.Parse(rrule) ?? RecurrencePattern.Weekly(start));

        var ok = new Button { Content = "OK", Width = 84, IsDefault = true };
        ok.Click += (_, _) =>
        {
            Rrule = Build().ToRrule(_zone);
            CollectTime();
            Cancelled = false;
            Close();
        };

        var remove = new Button { Content = "Remove Recurrence", Width = 160 };
        remove.Click += (_, _) =>
        {
            Rrule = null;
            Cancelled = false;
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { ok, cancel, remove },
        };

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                buttons,
                new StackPanel
                {
                    Spacing = 10,
                    Children = { TimeBox(), PatternBox(), RangeBox() },
                },
            },
        };

        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");
        Refresh();

        // MAILBOX_RECURRENCE states a pattern in this dialog's own terms and presses one of its
        // three buttons. Every route to a repeating appointment runs through here, so without it
        // the only pattern anything could ever be given is the one the dialog opens on.
        if (Theming.WindowCapture.IsRequested
            && Environment.GetEnvironmentVariable("MAILBOX_RECURRENCE") is { Length: > 0 } posed)
        {
            Opened += (_, _) => Pose(posed);
        }
    }

    /// <summary>
    /// Fills the dialog from a pose and presses a button. Harness only.
    /// </summary>
    /// <remarks>
    /// The spec is <c>&lt;pattern&gt;[;&lt;range&gt;]</c>, both stated the way the dialog asks
    /// them rather than as an RRULE — the point is to prove the editor, and a pose that handed it
    /// a rule would prove the parser instead:
    /// <list type="bullet">
    /// <item><description><c>daily:3</c>, <c>daily:weekday</c></description></item>
    /// <item><description><c>weekly:2:MO,WE</c></description></item>
    /// <item><description><c>monthly:1:day:15</c>, <c>monthly:2:the:second:tuesday</c>,
    /// <c>monthly:1:the:last:friday</c></description></item>
    /// <item><description><c>yearly:11:5</c> — month then day</description></item>
    /// <item><description>range: <c>;count=10</c>, <c>;until=2026-12-31</c>, or nothing for no
    /// end</description></item>
    /// <item><description><c>remove</c> presses Remove Recurrence, <c>cancel</c> presses
    /// Cancel</description></item>
    /// </list>
    /// </remarks>
    internal void Pose(string spec)
    {
        var text = spec.Trim();

        if (string.Equals(text, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Harness: recurrence — Cancel.");
            Close();
            return;
        }

        if (string.Equals(text, "remove", StringComparison.OrdinalIgnoreCase))
        {
            Rrule = null;
            Cancelled = false;
            Log.Info("Harness: recurrence — Remove Recurrence.");
            Close();
            return;
        }

        var halves = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parts = halves[0].Split(':', StringSplitOptions.TrimEntries);

        switch (parts[0].ToLowerInvariant())
        {
            case "daily":
                _daily.IsChecked = true;
                if (parts.Length > 1 && string.Equals(parts[1], "weekday", StringComparison.OrdinalIgnoreCase))
                {
                    _dailyWeekday.IsChecked = true;
                }
                else
                {
                    _dailyEveryDay.IsChecked = true;
                    _dailyEvery.Value = Number(parts, 1, 1);
                }

                break;

            case "weekly":
                _weekly.IsChecked = true;
                _weeklyEvery.Value = Number(parts, 1, 1);
                foreach (var box in _weekDays) box.IsChecked = false;
                if (parts.Length > 2)
                {
                    foreach (var day in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (WeekdayIndex(day) is { } at) _weekDays[at].IsChecked = true;
                    }
                }

                break;

            case "monthly":
                _monthly.IsChecked = true;
                _monthlyEvery.Value = Number(parts, 1, 1);
                if (parts.Length > 2 && string.Equals(parts[2], "the", StringComparison.OrdinalIgnoreCase))
                {
                    _monthlyByWeekday.IsChecked = true;
                    if (parts.Length > 3)
                    {
                        var ordinal = Array.FindIndex(Ordinals, o => string.Equals(o, parts[3], StringComparison.OrdinalIgnoreCase));
                        _monthlyOrdinal.SelectedIndex = ordinal < 0 ? 0 : ordinal;
                    }

                    if (parts.Length > 4 && WeekdayIndex(parts[4]) is { } weekday) _monthlyWeekday.SelectedIndex = weekday;
                }
                else
                {
                    _monthlyByDay.IsChecked = true;
                    _monthlyDay.Value = Number(parts, 3, 1);
                }

                break;

            case "yearly":
                _yearly.IsChecked = true;
                _yearlyMonth.SelectedIndex = Math.Clamp(Number(parts, 1, 1) - 1, 0, 11);
                _yearlyDay.Value = Number(parts, 2, 1);
                break;

            default:
                Log.Info($"Harness: “{halves[0]}” is not a recurrence pattern — say daily:, weekly:, monthly: or yearly:.");
                Close();
                return;
        }

        var range = halves.Length > 1 ? halves[1] : string.Empty;
        if (range.StartsWith("count=", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(range[6..], CultureInfo.InvariantCulture, out var count))
        {
            _endAfter.IsChecked = true;
            _occurrences.Value = count;
        }
        else if (range.StartsWith("until=", StringComparison.OrdinalIgnoreCase)
                 && DateOnly.TryParseExact(range[6..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
        {
            _endBy.IsChecked = true;
            _until.SelectedDate = until.ToDateTime(TimeOnly.MinValue);
        }
        else
        {
            _noEnd.IsChecked = true;
        }

        Refresh();
        Rrule = Build().ToRrule(_zone);
        Cancelled = false;
        Log.Info($"Harness: recurrence — “{spec}” built RRULE={Rrule ?? "(none)"}.");
        Close();
    }

    private static int Number(string[] parts, int at, int fallback)
        => parts.Length > at && int.TryParse(parts[at], CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>A weekday by its English two-letter iCalendar code or by its own name.</summary>
    private static int? WeekdayIndex(string text)
    {
        var code = text.Trim().ToUpperInvariant();
        var at = Array.IndexOf(new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" }, code);
        if (at >= 0) return at;

        for (var i = 0; i < 7; i++)
        {
            if (Enum.GetName((DayOfWeek)i)?.Equals(text, StringComparison.OrdinalIgnoreCase) == true) return i;
        }

        return null;
    }

    private static string Length(TimeSpan duration)
        => duration.TotalMinutes >= 60
            ? $"{duration.TotalHours.ToString("0.#", CultureInfo.CurrentCulture)} hours"
            : $"{duration.TotalMinutes.ToString("0", CultureInfo.CurrentCulture)} minutes";

    private Control PatternBox()
    {
        foreach (var radio in (RadioButton[])[_daily, _weekly, _monthly, _yearly])
        {
            radio.IsCheckedChanged += (_, _) => Refresh();
        }

        foreach (var radio in (RadioButton[])[_dailyEveryDay, _dailyWeekday, _monthlyByDay, _monthlyByWeekday])
        {
            radio.IsCheckedChanged += (_, _) => Refresh();
        }

        var left = new StackPanel { Spacing = 4, MinWidth = 110, Children = { _daily, _weekly, _monthly, _yearly } };

        _dailyPane = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Row(Radio(_dailyEveryDay), Text("Every"), _dailyEvery, Text("day(s)")),
                _dailyWeekday,
            },
        };
        _dailyEveryDay.Content = string.Empty;

        _weeklyPane = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Row(Text("Recur every"), _weeklyEvery, Text("week(s) on:")),
                new WrapPanel { Children = { _weekDays[0], _weekDays[1], _weekDays[2], _weekDays[3] } },
                new WrapPanel { Children = { _weekDays[4], _weekDays[5], _weekDays[6] } },
            },
        };

        _monthlyPane = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Row(_monthlyByDay, _monthlyDay, Text("of every"), _monthlyEvery, Text("month(s)")),
                Row(_monthlyByWeekday, _monthlyOrdinal, _monthlyWeekday, Text("of every month")),
            },
        };

        _yearlyPane = new StackPanel
        {
            Spacing = 6,
            Children = { Row(Text("Every"), _yearlyMonth, _yearlyDay) },
        };

        var right = new Panel { Children = { _dailyPane, _weeklyPane, _monthlyPane, _yearlyPane } };

        var box = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(left, 0);
        box.Children.Add(left);
        Grid.SetColumn(right, 1);
        box.Children.Add(right);
        return Group("Recurrence pattern", box);
    }

    private StackPanel _dailyPane = new();
    private StackPanel _weeklyPane = new();
    private StackPanel _monthlyPane = new();
    private StackPanel _yearlyPane = new();

    private Control RangeBox()
    {
        foreach (var radio in (RadioButton[])[_noEnd, _endAfter, _endBy])
        {
            radio.IsCheckedChanged += (_, _) => Refresh();
        }

        var stack = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Row(Text("Start:"), _rangeStart),
                _noEnd,
                Row(_endAfter, _occurrences, Text("occurrences")),
                Row(_endBy, _until),
            },
        };
        return Group("Range of recurrence", stack);
    }

    private void Refresh()
    {
        _dailyPane.IsVisible = _daily.IsChecked == true;
        _weeklyPane.IsVisible = _weekly.IsChecked == true;
        _monthlyPane.IsVisible = _monthly.IsChecked == true;
        _yearlyPane.IsVisible = _yearly.IsChecked == true;

        _dailyEvery.IsEnabled = _dailyEveryDay.IsChecked == true;
        _monthlyDay.IsEnabled = _monthlyByDay.IsChecked == true;
        _monthlyEvery.IsEnabled = _monthlyByDay.IsChecked == true;
        _monthlyOrdinal.IsEnabled = _monthlyByWeekday.IsChecked == true;
        _monthlyWeekday.IsEnabled = _monthlyByWeekday.IsChecked == true;

        _occurrences.IsEnabled = _endAfter.IsChecked == true;
        _until.IsEnabled = _endBy.IsChecked == true;
    }

    private void Load(RecurrencePattern pattern)
    {
        switch (pattern.Frequency)
        {
            case RecurrenceFrequency.Daily:
                _daily.IsChecked = true;
                _dailyWeekday.IsChecked = pattern.EveryWeekday;
                _dailyEveryDay.IsChecked = !pattern.EveryWeekday;
                _dailyEvery.Value = pattern.Interval;
                break;
            case RecurrenceFrequency.Monthly:
                _monthly.IsChecked = true;
                _monthlyEvery.Value = pattern.Interval;
                _monthlyByDay.IsChecked = pattern.Monthly == MonthlyMode.DayOfMonth;
                _monthlyByWeekday.IsChecked = pattern.Monthly == MonthlyMode.Weekday;
                _monthlyDay.Value = pattern.DayOfMonth;
                _monthlyOrdinal.SelectedIndex = pattern.WeekOrdinal < 0 ? 4 : Math.Clamp(pattern.WeekOrdinal - 1, 0, 4);
                _monthlyWeekday.SelectedIndex = (int)pattern.WeekDay;
                break;
            case RecurrenceFrequency.Yearly:
                _yearly.IsChecked = true;
                _yearlyMonth.SelectedIndex = Math.Clamp(pattern.Month - 1, 0, 11);
                _yearlyDay.Value = pattern.DayOfMonth;
                break;
            default:
                _weekly.IsChecked = true;
                _weeklyEvery.Value = pattern.Interval;
                foreach (var day in pattern.Days) _weekDays[(int)day].IsChecked = true;
                break;
        }

        if (pattern.Count is { } count)
        {
            _endAfter.IsChecked = true;
            _occurrences.Value = count;
        }
        else if (pattern.Until is { } until)
        {
            _endBy.IsChecked = true;
            _until.SelectedDate = until.ToDateTime(TimeOnly.MinValue);
        }
        else
        {
            _noEnd.IsChecked = true;
        }
    }

    private RecurrencePattern Build()
    {
        var pattern = new RecurrencePattern();

        if (_daily.IsChecked == true)
        {
            pattern = _dailyWeekday.IsChecked == true
                ? pattern with { Frequency = RecurrenceFrequency.Daily, EveryWeekday = true }
                : pattern with { Frequency = RecurrenceFrequency.Daily, Interval = Whole(_dailyEvery) };
        }
        else if (_monthly.IsChecked == true)
        {
            pattern = pattern with
            {
                Frequency = RecurrenceFrequency.Monthly,
                Interval = Whole(_monthlyEvery),
                Monthly = _monthlyByWeekday.IsChecked == true ? MonthlyMode.Weekday : MonthlyMode.DayOfMonth,
                DayOfMonth = Whole(_monthlyDay),
                WeekOrdinal = _monthlyOrdinal.SelectedIndex == 4 ? -1 : Math.Max(1, _monthlyOrdinal.SelectedIndex + 1),
                WeekDay = (DayOfWeek)Math.Clamp(_monthlyWeekday.SelectedIndex, 0, 6),
            };
        }
        else if (_yearly.IsChecked == true)
        {
            pattern = pattern with
            {
                Frequency = RecurrenceFrequency.Yearly,
                Month = Math.Clamp(_yearlyMonth.SelectedIndex + 1, 1, 12),
                DayOfMonth = Whole(_yearlyDay),
            };
        }
        else
        {
            var days = new List<DayOfWeek>();
            for (var i = 0; i < 7; i++)
            {
                if (_weekDays[i].IsChecked == true) days.Add((DayOfWeek)i);
            }

            pattern = pattern with { Frequency = RecurrenceFrequency.Weekly, Interval = Whole(_weeklyEvery), Days = days };
        }

        if (_endAfter.IsChecked == true) return pattern with { Count = Whole(_occurrences) };
        if (_endBy.IsChecked == true && _until.SelectedDate is { } until) return pattern with { Until = DateOnly.FromDateTime(until.Date) };
        return pattern;
    }

    /// <summary>What the Appointment time group and the range's Start now say, where edited.</summary>
    private void CollectTime()
    {
        if (_startTimeBox.IsEnabled && _openedOn.Time is { } wasAt)
        {
            var start = _timeSlots[Math.Clamp(_startTimeBox.SelectedIndex, 0, 47)];
            if (start != wasAt) EditedStartTime = start;

            var duration = CurrentDuration();
            if (duration != _openedOn.Duration && duration > TimeSpan.Zero) EditedDuration = duration;
        }

        if (_rangeStart.SelectedDate is { } picked && DateOnly.FromDateTime(picked) != _openedOn.Date)
        {
            EditedStartDate = DateOnly.FromDateTime(picked);
        }
    }

    /// <summary>The rule the dialog was opened on, for a caller that wants to know it changed.</summary>
    public bool Changed => !string.Equals(_existing, Rrule, StringComparison.Ordinal);

    private static int Whole(NumericUpDown spinner) => (int)Math.Max(1, spinner.Value ?? 1);

    private static NumericUpDown Spinner(int minimum, int maximum)
        => new() { Minimum = minimum, Maximum = maximum, Value = minimum, Width = 86, FormatString = "0" };

    private static RadioButton Radio(string group, string label, bool isChecked = false)
    {
        var radio = new RadioButton { GroupName = group, Content = label, IsChecked = isChecked };
        Bind(radio, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
        return radio;
    }

    private static RadioButton Radio(RadioButton existing) => existing;

    private static TextBlock Text(string label)
    {
        var block = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private static Control Row(params Control[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    private static Control Group(string heading, Control content)
    {
        var label = new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        Bind(label, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var box = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = new StackPanel { Children = { label, content } } };
        Bind(box, Border.BorderBrushProperty, "dialog.border.brush");
        return box;
    }
}
