using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.Views;
using Mailbox.Core.Localization;
using Mailbox.Core.Weather;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Weather;

/// <summary>
/// The weather on the calendar's toolbar, where the reference draws it beside the date: the place,
/// with a list of the reader's other weather places under it, then that place's next three days —
/// each a picture beside the day's name and its high and low.
/// </summary>
/// <remarks>
/// A view and nothing more: the window hands it what to show and hears what was pressed. The days
/// are the place's own next three, whatever stretch of the calendar is on screen, as the
/// reference's are — the weather is about the days ahead, not the month being looked at. Every
/// colour is the toolbar's own, taken live from the theme, so the bar reads as part of whichever
/// theme is on and follows a change of it without being drawn again.
/// </remarks>
internal sealed class CalendarWeatherBar : StackPanel
{
    private const string InkKey = "calendar.toolbar.text.brush";

    /// <summary>What the bar shows, in words, for a harness run to read back.</summary>
    private readonly List<string> _said = [];

    public CalendarWeatherBar()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 10;
        Height = 38;
        VerticalAlignment = VerticalAlignment.Top;

        // Centred on the date's line: the date sits 22 down in 18px type, so its middle is at 34.
        Margin = new Thickness(26, 15, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(this, Strings.T("Weather"));
    }

    /// <summary>Another of the reader's places was chosen for the calendar. The argument is its id.</summary>
    public event EventHandler<string>? PlaceChosen;

    /// <summary>A day, or Open Weather, was pressed: the place's forecast is wanted. The argument is its id.</summary>
    public event EventHandler<string>? WeatherRequested;

    /// <summary>Add Location was chosen from the place's list.</summary>
    public event EventHandler? AddRequested;

    /// <summary>The place and the days shown, in words: "Phoenix, Arizona · Today day/sunny-day 81°F/68°F · …".</summary>
    public string Said => string.Join(" · ", _said);

    /// <summary>
    /// Shows a place and its next three days — or the place alone while its forecast has not
    /// arrived, so the list of places can still be reached.
    /// </summary>
    public void Show(WeatherPlace place, Forecast? forecast, IReadOnlyList<WeatherPlace> places, WeatherUnits units, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(units);

        Children.Clear();
        _said.Clear();
        Children.Add(PlaceButton(place, places));
        _said.Add(place.Label);
        if (forecast is null) return;

        var local = forecast.LocalTime(now);
        var today = DateOnly.FromDateTime(local);
        foreach (var day in forecast.DaysFrom(local, 3))
        {
            Children.Add(DayButton(place, forecast, day, today, units));
        }
    }

    private Button PlaceButton(WeatherPlace place, IReadOnlyList<WeatherPlace> places)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Ink(new TextBlock { Text = place.Label, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }));
        row.Children.Add(Ink(new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
            FontFamily = IconFont.Family,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        }));

        var button = Flat(row);
        button.Padding = new Thickness(6, 0);
        ToolTip.SetTip(button, Strings.T("Choose the place the calendar shows the weather for"));
        Avalonia.Automation.AutomationProperties.SetName(button, place.FullName);

        // Left-aligned under the button, as the calendar's other dropdown is: the reader's places,
        // the shown one marked, then the two ways out to the Weather module.
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var other in places)
        {
            var id = other.Id;
            var item = new MenuItem
            {
                Header = other.Label,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "calendar-weather-place",
                IsChecked = other.Id == place.Id,
            };
            item.Click += (_, _) => PlaceChosen?.Invoke(this, id);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = Strings.T("Add Location…") };
        add.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(add);
        var open = new MenuItem { Header = Strings.T("Open Weather") };
        var shown = place.Id;
        open.Click += (_, _) => WeatherRequested?.Invoke(this, shown);
        menu.Items.Add(open);

        button.Flyout = menu;
        return button;
    }

    private Button DayButton(WeatherPlace place, Forecast forecast, DailyWeather day, DateOnly today, WeatherUnits units)
    {
        var condition = forecast.ConditionFor(day);
        var name = day.Date == today ? Strings.T("Today")
            : day.Date == today.AddDays(1) ? Strings.T("Tomorrow")
            : day.Date.ToString("dddd", CultureInfo.CurrentCulture);

        // The high and the low with the unit's letter, as the reference writes them: "63°F/47°F".
        var letter = units.TemperatureSymbol[1..];
        var range = $"{units.FormatTemperature(day.High)}{letter}/{units.FormatTemperature(day.Low)}{letter}";

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Ink(new TextBlock { Text = name, FontSize = 12, Opacity = 0.75 }));
        words.Children.Add(Ink(new TextBlock { Text = range, FontSize = 13, FontWeight = FontWeight.SemiBold }));

        // The pictures fill about three fifths of their square, so 36 draws a sun about as tall as
        // the two lines beside it, as the reference's is; the square's own margin is the gap.
        var picture = WeatherPage.Picture(condition.Icon, 36);
        picture.VerticalAlignment = VerticalAlignment.Center;
        picture.Margin = new Thickness(-4, 0, 0, 0);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        row.Children.Add(picture);
        row.Children.Add(words);

        var button = Flat(row);
        button.Padding = new Thickness(4, 1);
        ToolTip.SetTip(button, WeatherPage.DayTip(day, condition, units));
        Avalonia.Automation.AutomationProperties.SetName(button, $"{name}: {condition.Description}, {range}");
        var id = place.Id;
        button.Click += (_, _) => WeatherRequested?.Invoke(this, id);

        _said.Add($"{name} {condition.Icon} {range}");
        return button;
    }

    private static Button Flat(Control content) => new()
    {
        Classes = { "flat" },
        Content = content,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Left,
    };

    private static TextBlock Ink(TextBlock block)
    {
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(InkKey);
        return block;
    }
}
