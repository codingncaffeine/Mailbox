using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.Options;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The calendar peek: a mini month plus the day's agenda.
/// </summary>
/// <remarks>
/// the reference application shows this two ways from the same content — floating over the window when a rail
/// icon is clicked, and docked down the right-hand edge once the little button in the peek's
/// corner is pressed. Docked, it takes the reading pane's place and gains a close button that
/// undocks it again. Building one control for both keeps them from drifting apart.
/// </remarks>
public sealed class CalendarPeek : Border
{
    private readonly DateTime _today;

    /// <summary>The day whose agenda is on show. Starts on today and follows a click.</summary>
    private DateTime _selected;

    private readonly Panel _agendaHost = new();
    private DateTime _month;
    private readonly Panel _monthHost = new();
    private readonly TextBlock _monthLabel = new();

    public CalendarPeek(DateTime today, bool docked)
    {
        _today = today;
        _selected = today;
        _month = new DateTime(today.Year, today.Month, 1);
        IsDocked = docked;

        Width = 268;
        Padding = new Thickness(10, 8, 10, 10);
        BorderThickness = new Thickness(docked ? 1 : 1);
        BoxShadow = docked
            ? default
            : BoxShadows.Parse("0 4 16 0 #40000000");

        Bind(this, BackgroundProperty,
            docked ? "surface.ground.brush" : "surface.overlay.brush");
        Bind(this, BorderBrushProperty, "border.subtle.brush");

        Child = BuildContent();
        RenderMonth();
    }

    /// <summary>True when pinned to the right edge rather than floating.</summary>
    public bool IsDocked { get; }

    /// <summary>Raised by the corner button: dock when floating, close when docked.</summary>
    public event EventHandler? DockRequested;

    public event EventHandler? CloseRequested;

    private Control BuildContent()
    {
        var root = new StackPanel { Spacing = 8 };

        // Corner button. Floating shows the dock glyph; docked shows a close cross.
        var corner = new Button
        {
            Content = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty(IsDocked ? "dismiss" : "dock", 16),
                FontFamily = IconFont.Family,
                FontSize = 13,
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(4, 2),
            BorderThickness = default,
            Background = Brushes.Transparent,
        };
        ToolTip.SetTip(corner, IsDocked ? "Remove the calendar" : "Dock the peek");
        corner.Click += (_, _) =>
        {
            if (IsDocked) CloseRequested?.Invoke(this, EventArgs.Empty);
            else DockRequested?.Invoke(this, EventArgs.Empty);
        };
        root.Children.Add(corner);

        // Month navigator: < August 2026 >
        var nav = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var prev = ArrowButton("chevron-left", () => Shift(-1));
        Grid.SetColumn(prev, 0);
        nav.Children.Add(prev);

        _monthLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _monthLabel.VerticalAlignment = VerticalAlignment.Center;
        _monthLabel.FontWeight = FontWeight.SemiBold;
        Bind(_monthLabel, TextBlock.ForegroundProperty, "text.primary.brush");
        Grid.SetColumn(_monthLabel, 1);
        nav.Children.Add(_monthLabel);

        var next = ArrowButton("chevron-right", () => Shift(1));
        Grid.SetColumn(next, 2);
        nav.Children.Add(next);

        root.Children.Add(nav);
        root.Children.Add(_monthHost);

        var rule = new Border { Height = 1, Margin = new Thickness(0, 4) };
        Bind(rule, BackgroundProperty, "border.subtle.brush");
        root.Children.Add(rule);

        _agendaHost.Children.Add(BuildAgenda());
        root.Children.Add(_agendaHost);
        return root;
    }

    private Button ArrowButton(string icon, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty(icon, 16),
                FontFamily = IconFont.Family,
                FontSize = 12,
            },
            Padding = new Thickness(6, 2),
            BorderThickness = default,
            Background = Brushes.Transparent,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Shift(int months)
    {
        _month = _month.AddMonths(months);
        RenderMonth();
    }

    /// <summary>
    /// Six week-rows of seven days, starting on the day Options names, with leading and
    /// trailing days from the
    /// neighbouring months dimmed — the same grid the reference's date navigator draws.
    /// </summary>
    private void RenderMonth()
    {
        _monthLabel.Text = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

        // Options names the first day and whether week numbers show; both change the grid's
        // shape, so they are read here rather than cached.
        var firstDay = (DayOfWeek)(int)App.Settings.GetNumber(OptionsPages.Keys.FirstDayOfWeek, 0);
        var weekNumbers = App.Settings.GetBool(OptionsPages.Keys.ShowWeekNumbers);
        var dayColumns = weekNumbers ? 8 : 7;
        var firstDayColumn = weekNumbers ? 1 : 0;

        var grid = new Grid();
        for (var c = 0; c < dayColumns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (var r = 0; r < 7; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        string[] names = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
        for (var c = 0; c < 7; c++)
        {
            var head = new TextBlock
            {
                Text = names[((int)firstDay + c) % 7],
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3),
            };
            Bind(head, TextBlock.ForegroundProperty, "text.secondary.brush");
            Grid.SetColumn(head, c + firstDayColumn);
            Grid.SetRow(head, 0);
            grid.Children.Add(head);
        }

        var lead = ((int)_month.DayOfWeek - (int)firstDay + 7) % 7;
        var cursor = _month.AddDays(-lead);

        for (var week = 1; week <= 6; week++)
        {
            for (var day = 0; day < 7; day++)
            {
                if (weekNumbers && day == 0) grid.Children.Add(WeekNumberCell(cursor, week));

                grid.Children.Add(BuildDayCell(cursor, week, day + firstDayColumn));
                cursor = cursor.AddDays(1);
            }
        }

        _monthHost.Children.Clear();
        _monthHost.Children.Add(grid);
    }

    /// <summary>The ISO week number, shown down the left when Options asks for it.</summary>
    private Control WeekNumberCell(DateTime weekStart, int row)
    {
        var label = new TextBlock
        {
            Text = ISOWeek.GetWeekOfYear(weekStart).ToString(CultureInfo.CurrentCulture),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "text.disabled.brush");
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        return label;
    }

    private Control BuildDayCell(DateTime date, int row, int column)
    {
        var isToday = date.Date == _today.Date;
        var isSelected = date.Date == _selected.Date;
        var inMonth = date.Month == _month.Month;

        var label = new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.CurrentCulture),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (isToday) Bind(label, TextBlock.ForegroundProperty, "text.onaccent.brush");
        else if (inMonth) Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
        else Bind(label, TextBlock.ForegroundProperty, "text.disabled.brush");

        // Today keeps its filled marker whatever is selected; the selected day is outlined, so
        // the two read as different things rather than one overwriting the other.
        var cell = new Button
        {
            Height = 22,
            Content = label,
            Padding = default,
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(isSelected && !isToday ? 1 : 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = null,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        if (isToday) Bind(cell, BackgroundProperty, "accent.rest.brush");
        if (isSelected && !isToday) Bind(cell, BorderBrushProperty, "accent.rest.brush");

        var day = date;
        cell.Click += (_, _) =>
        {
            _selected = day;
            if (day.Month != _month.Month) _month = new DateTime(day.Year, day.Month, 1);
            RenderMonth();
            RenderAgenda();
        };

        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        return cell;
    }

    private void RenderAgenda()
    {
        _agendaHost.Children.Clear();
        _agendaHost.Children.Add(BuildAgenda());
    }

    /// <summary>
    /// The day's appointments. Sample data in Phase 0; Phase 11 replaces this with the store.
    /// </summary>
    private Control BuildAgenda()
    {
        var stack = new StackPanel { Spacing = 6 };

        var heading = new TextBlock
        {
            Text = _selected.ToString("dddd", CultureInfo.CurrentCulture),
            FontWeight = FontWeight.SemiBold,
        };
        Bind(heading, TextBlock.ForegroundProperty, "text.primary.brush");
        stack.Children.Add(heading);

        stack.Children.Add(BuildAppointment("5:00 PM", "Design review",
            "https://example.com/meet/design-review"));

        return stack;
    }

    private Control BuildAppointment(string time, string subject, string detail)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,3,*") };

        var when = new TextBlock
        {
            Text = time,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Bind(when, TextBlock.ForegroundProperty, "text.secondary.brush");
        Grid.SetColumn(when, 0);
        grid.Children.Add(when);

        // The category colour bar down the left of an appointment.
        var bar = new Border { Width = 3 };
        Bind(bar, BackgroundProperty, "accent.rest.brush");
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        var title = new TextBlock { Text = subject, FontSize = 11.5, FontWeight = FontWeight.SemiBold };
        Bind(title, TextBlock.ForegroundProperty, "text.primary.brush");
        var sub = new TextBlock
        {
            Text = detail,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Bind(sub, TextBlock.ForegroundProperty, "text.secondary.brush");
        text.Children.Add(title);
        text.Children.Add(sub);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        return grid;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
