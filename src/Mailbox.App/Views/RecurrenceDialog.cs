using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
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

    public RecurrenceDialog(string? rrule, DateOnly start, TimeSpan duration)
    {
        _existing = rrule;
        Title = "Appointment Recurrence";
        Width = 520;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        _weekDays =
        [
            .. Enumerable.Range(0, 7).Select(i => new CheckBox { Content = names[i], MinWidth = 70 }),
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

        Load(RecurrencePattern.Parse(rrule) ?? RecurrencePattern.Weekly(start));

        var summary = new TextBlock
        {
            Text = $"Appointment time: {start.ToDateTime(TimeOnly.MinValue).ToString("d", CultureInfo.CurrentCulture)}, "
                   + $"{Length(duration)}",
            Margin = new Thickness(0, 0, 0, 12),
        };
        Bind(summary, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var ok = new Button { Content = "OK", Width = 84, IsDefault = true };
        ok.Click += (_, _) =>
        {
            Rrule = Build().ToRrule();
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

        var buttons = new Grid
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(0, 14, 0, 0),
        };
        Grid.SetColumn(remove, 0);
        buttons.Children.Add(remove);
        ok.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(ok, 2);
        buttons.Children.Add(ok);
        Grid.SetColumn(cancel, 3);
        buttons.Children.Add(cancel);

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                buttons,
                new StackPanel
                {
                    Spacing = 10,
                    Children = { summary, PatternBox(), RangeBox() },
                },
            },
        };

        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");
        Refresh();
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
