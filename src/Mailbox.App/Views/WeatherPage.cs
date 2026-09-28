using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mailbox.App.Weather;
using Mailbox.Core.Localization;
using Mailbox.Core.Weather;
using Mailbox.Protocols;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>
/// One place's weather, as a page of cards: the conditions now, any warnings, the next two days
/// hour by hour, the next sixteen day by day, the details, and — where the Weather Service
/// forecasts — what its forecasters wrote.
/// </summary>
/// <remarks>
/// Built from what the receiver holds and rebuilt when it changes; nothing here fetches. Every
/// colour is a token: the page, the cards and their ink are the theme's own <c>weather.*</c>
/// family, the controls take the shell's, so the page is at home in whichever theme the reader
/// runs — light in Dark Gray, as content is there, and dark only in Black.
/// <para>
/// A scroll viewer held rather than inherited from: a control's template is found by its own
/// type, and a subclass of the scroll viewer finds none — it shows its content unscrolled,
/// clipped at the window's edge, which is exactly how this page first shipped to the harness.
/// </para>
/// </remarks>
internal sealed class WeatherPage : Border
{
    private readonly StackPanel _column = new() { Spacing = 16, Margin = new Thickness(24, 20, 24, 28), MaxWidth = 1040 };

    /// <summary>The page's expanders, so a harness run can photograph them open.</summary>
    private readonly List<Expander> _expanders = [];
    private readonly ScrollViewer _scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public WeatherPage()
    {
        _scroller.Content = _column;
        Child = _scroller;
    }

    /// <summary>Opens every expander on the page, for a harness run.</summary>
    public int ExpandAll()
    {
        foreach (var expander in _expanders) expander.IsExpanded = true;
        return _expanders.Count;
    }

    /// <summary>Scrolls the page, and says where it landed out of how far it goes.</summary>
    public string ScrollTo(double offset)
    {
        _scroller.Offset = new Vector(0, offset);
        return $"scrolled to {_scroller.Offset.Y:0} of {Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height):0}";
    }

    /// <summary>Add Location, pressed on the empty page.</summary>
    public event EventHandler? AddRequested;

    /// <summary>Try Again, pressed on a page whose forecast could not be fetched.</summary>
    public event EventHandler? RetryRequested;

    /// <summary>A link followed: the attribution's, or a warning's.</summary>
    public event EventHandler<string>? LinkRequested;

    /// <summary>The page with no places yet: what the module is for, and the one button that starts it.</summary>
    public void ShowEmpty()
    {
        _column.Children.Clear();
        var picture = Picture(WeatherArt.Placeholder, 128);
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.Margin = new Thickness(0, 60, 0, 8);

        var add = Accent(Strings.T("Add Location"));
        add.HorizontalAlignment = HorizontalAlignment.Center;
        add.Margin = new Thickness(0, 12, 0, 0);
        add.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);

        _column.Children.Add(picture);
        _column.Children.Add(Centred(Text(Strings.T("Add a place to see its weather"), 22, TokenKeys.Weather.CardText, FontWeight.SemiBold)));
        _column.Children.Add(Centred(Text(
            Strings.T("Search by city or by zip code. The first place you add is home: its weather shows on the rail."),
            13, TokenKeys.Weather.CardTextDim)));
        _column.Children.Add(add);
    }

    public void Show(WeatherPlace place, PlaceWeather weather, WeatherUnits units, DateTimeOffset now, bool offline)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(weather);
        _column.Children.Clear();
        _expanders.Clear();
        _column.Children.Add(Header(place, weather, now, offline));

        if (weather.Forecast is not { } forecast)
        {
            _column.Children.Add(Waiting(weather, offline));
            return;
        }

        var local = forecast.LocalTime(now);
        _column.Children.Add(Hero(forecast, local, units));

        // One card per kind of warning: offices reissue and neighbours overlap, and three cards
        // saying the same thing read as three things. The first of a kind is the most serious and
        // soonest, which is the one to read; the card says how many more there are.
        foreach (var kind in weather.Alerts.GroupBy(a => a.Event, StringComparer.OrdinalIgnoreCase))
        {
            _column.Children.Add(Alert(kind.First(), kind.Count() - 1, local, forecast));
        }
        _column.Children.Add(Hourly(forecast, local, units));
        _column.Children.Add(Daily(forecast, local, units));
        _column.Children.Add(Details(forecast, local, units));
        if (weather.Discussion is { Sections.Count: > 0 } discussion) _column.Children.Add(Discussion(discussion));
        _column.Children.Add(Attribution(place));
    }

    // ---- The parts of the page ------------------------------------------------------------------

    private static Control Header(WeatherPlace place, PlaceWeather weather, DateTimeOffset now, bool offline)
    {
        var names = new StackPanel { Spacing = 2 };
        names.Children.Add(Text(place.Label, 24, TokenKeys.Weather.CardText, FontWeight.SemiBold));
        names.Children.Add(Text(place.Country.Length > 0 ? place.Country : place.Region, 13, TokenKeys.Weather.CardTextDim));

        var status = Text(State(weather, now, offline), 12, TokenKeys.Weather.CardTextDim);
        status.VerticalAlignment = VerticalAlignment.Bottom;
        status.TextAlignment = TextAlignment.Right;
        status.MaxWidth = 380;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(names);
        Grid.SetColumn(status, 1);
        row.Children.Add(status);
        return row;
    }

    /// <summary>What the page is showing and how fresh it is, in the corner of the header.</summary>
    private static string State(PlaceWeather weather, DateTimeOffset now, bool offline)
    {
        var culture = CultureInfo.CurrentCulture;
        if (weather.Updating) return Strings.T("Updating…");

        var when = weather.Forecast is { } forecast
            ? string.Format(culture, Strings.T("Updated {0}"), forecast.Fetched.ToLocalTime().ToString("t", culture))
            : string.Empty;

        if (offline) return when.Length > 0 ? $"{Strings.T("Working offline")} · {when}" : Strings.T("Working offline");
        if (weather.Error.Length > 0 && weather.Forecast is not null)
        {
            return string.Format(culture, Strings.T("Could not update ({0}) · {1}"), weather.Error, when);
        }

        return when;
    }

    private Control Waiting(PlaceWeather weather, bool offline)
    {
        var body = new StackPanel { Spacing = 10 };
        if (offline)
        {
            body.Children.Add(Text(Strings.T("Working offline: the weather is fetched when you go back online."), 14, TokenKeys.Weather.CardText));
        }
        else if (weather.Error.Length > 0)
        {
            body.Children.Add(Text(Strings.T("The weather could not be fetched."), 15, TokenKeys.Weather.CardText, FontWeight.SemiBold));
            body.Children.Add(Text(
                string.Format(CultureInfo.CurrentCulture, Strings.T("{0} It will be tried again on its own."), weather.Error),
                13, TokenKeys.Weather.CardTextDim));
            var retry = Accent(Strings.T("Try Again"));
            retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
            body.Children.Add(retry);
        }
        else
        {
            body.Children.Add(Text(Strings.T("Fetching the weather…"), 14, TokenKeys.Weather.CardText));
        }

        return Card(null, body);
    }

    private static Control Hero(Forecast forecast, DateTime local, WeatherUnits units)
    {
        var now = forecast.Current;
        var today = forecast.Today(local);
        var condition = now.Condition;

        var picture = Picture(condition.Icon, 132);
        picture.VerticalAlignment = VerticalAlignment.Center;

        var reading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 24, 0) };
        reading.Children.Add(Text(units.FormatTemperature(now.Temperature), 64, TokenKeys.Weather.HeroText, FontWeight.Light));
        reading.Children.Add(Text(condition.Description, 19, TokenKeys.Weather.HeroText, FontWeight.SemiBold));
        var range = today is null
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, Strings.T("H {0}  L {1}"), units.FormatTemperature(today.High), units.FormatTemperature(today.Low));
        var feels = string.Format(CultureInfo.CurrentCulture, Strings.T("Feels like {0}"), units.FormatTemperature(now.FeelsLike ?? now.Temperature));
        reading.Children.Add(Text(range.Length > 0 ? $"{feels}   ·   {range}" : feels, 13, TokenKeys.Weather.HeroText));

        var facts = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var wind = now.WindSpeed is { } speed ? $"{units.FormatSpeed(speed)} {WeatherReadings.Compass(now.WindDirection ?? 0)}" : "—";
        var humidity = now.Humidity is { } h ? $"{Math.Round(h)}%" : "—";
        var uv = now.UvIndex is { } index ? $"{Math.Round(index)} · {WeatherReadings.UvName(WeatherReadings.Uv(index))}" : "—";
        var (sunLabel, sunTime) = NextSunEvent(forecast, local);
        AddFact(facts, 0, 0, Strings.T("Wind"), wind);
        AddFact(facts, 0, 1, Strings.T("Humidity"), humidity);
        AddFact(facts, 1, 0, Strings.T("UV index"), uv);
        AddFact(facts, 1, 1, sunLabel, sunTime);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        top.Children.Add(picture);
        Grid.SetColumn(reading, 1);
        top.Children.Add(reading);
        Grid.SetColumn(facts, 2);
        top.Children.Add(facts);

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(top);
        var likeliest = forecast.HoursFrom(local, 2).Select(h => h.PrecipitationChance ?? 0).DefaultIfEmpty(0).Max();
        if (WeatherReadings.NextTwoHours(forecast.NextTwoHours, local, now.Temperature, likeliest: likeliest) is { } soon)
        {
            stack.Children.Add(Text(soon, 13, TokenKeys.Weather.HeroText, FontWeight.SemiBold));
        }

        var hero = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(24, 20) , Child = stack };
        Bind(hero, Border.BackgroundProperty, TokenKeys.Weather.Hero);
        return hero;
    }

    private static void AddFact(Grid grid, int row, int column, string label, string value)
    {
        var fact = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 28, row == 0 ? 0 : 12, 0, 0), MinWidth = 96 };
        var caption = Text(label, 11, TokenKeys.Weather.HeroText);
        caption.Opacity = 0.8;
        fact.Children.Add(caption);
        fact.Children.Add(Text(value, 15, TokenKeys.Weather.HeroText, FontWeight.SemiBold));
        Grid.SetRow(fact, row);
        Grid.SetColumn(fact, column);
        grid.Children.Add(fact);
    }

    /// <summary>The next of sunrise or sunset, which is the one a reader is waiting for.</summary>
    private static (string Label, string Time) NextSunEvent(Forecast forecast, DateTime local)
    {
        var culture = CultureInfo.CurrentCulture;
        foreach (var day in forecast.DaysFrom(local, 2))
        {
            if (day.Sunrise is { } rise && rise > local) return (Strings.T("Sunrise"), rise.ToString("t", culture));
            if (day.Sunset is { } set && set > local) return (Strings.T("Sunset"), set.ToString("t", culture));
        }

        return (Strings.T("Sunset"), "—");
    }

    private Control Alert(WeatherAlert alert, int more, DateTime local, Forecast forecast)
    {
        var culture = CultureInfo.CurrentCulture;
        var token = alert.Severity switch
        {
            AlertSeverity.Extreme or AlertSeverity.Severe => TokenKeys.Weather.AlertSevere,
            AlertSeverity.Moderate => TokenKeys.Weather.AlertModerate,
            _ => TokenKeys.Weather.AlertMinor,
        };

        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(Text(alert.Event, 15, TokenKeys.Weather.CardText, FontWeight.SemiBold));
        var until = alert.Ends is { } ends
            ? string.Format(culture, Strings.T("Until {0}"), DayAndTime(forecast.LocalTime(ends)))
            : string.Empty;
        var from = alert.Onset is { } onset && forecast.LocalTime(onset) > local
            ? string.Format(culture, Strings.T("From {0}"), DayAndTime(forecast.LocalTime(onset)))
            : string.Empty;
        var others = more > 0 ? Strings.Counted("and {0} more like it", "and {0} more like it", more) : string.Empty;
        heading.Children.Add(Text(string.Join("  ·  ", new[] { from, until, alert.Sender, others }.Where(s => s.Length > 0)), 12, TokenKeys.Weather.CardTextDim));

        var text = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        if (alert.Headline.Length > 0) text.Children.Add(Text(alert.Headline, 13, TokenKeys.Weather.CardText, FontWeight.SemiBold));
        if (alert.Description.Length > 0) text.Children.Add(Text(Reflow(alert.Description), 13, TokenKeys.Weather.CardText));
        if (alert.Instruction.Length > 0) text.Children.Add(Text(Reflow(alert.Instruction), 13, TokenKeys.Weather.CardText, FontWeight.SemiBold));

        var details = new Expander { Header = Strings.T("Details"), Content = text, Margin = new Thickness(0, 6, 0, 0) };
        _expanders.Add(details);

        var body = new StackPanel();
        body.Children.Add(heading);
        body.Children.Add(details);

        var icon = WeatherConditions.AlertIcon(alert.Event) is { } key ? Picture(key, 44) : (Control)Glyph("warning", 28, TokenKeys.Weather.CardText);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 0, 14, 0);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(icon);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        return new AlertCard(token) { Child = row };
    }

    /// <summary>
    /// "Tue 6:00 AM": the day and the reader's own time pattern, written separately — inside a
    /// custom format a lone "t" is the first letter of AM/PM, not the time.
    /// </summary>
    private static string DayAndTime(DateTime moment)
    {
        var culture = CultureInfo.CurrentCulture;
        return $"{moment.ToString("ddd", culture)} {moment.ToString("t", culture)}";
    }

    /// <summary>
    /// The Weather Service's text arrives wrapped at seventy columns; paragraphs are joined back
    /// up. A product can open with its wire identifier — "ESFGJT" — which is addressing for the
    /// wire, not words for a reader, and is left off.
    /// </summary>
    private static string Reflow(string text)
    {
        var body = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart();
        var first = body.IndexOf('\n');
        if (first > 0 && System.Text.RegularExpressions.Regex.IsMatch(body[..first].Trim(), "^[A-Z0-9]{6,9}$")) body = body[first..];
        return string.Join("\n\n", body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => string.Join(' ', p.Split(['\n', ' '], StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Length > 0));
    }

    private static Control Hourly(Forecast forecast, DateTime local, WeatherUnits units)
    {
        var strip = new HourlyStrip();
        strip.Show(forecast.HoursFrom(local, 48), units);
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = strip,
            Padding = new Thickness(0, 0, 0, 10),
        };
        return Card(Strings.T("Hourly forecast"), scroller);
    }

    private static Control Daily(Forecast forecast, DateTime local, WeatherUnits units)
    {
        var culture = CultureInfo.CurrentCulture;
        var days = forecast.DaysFrom(local, OpenMeteo.ForecastDays);
        var rows = new StackPanel();
        if (days.Count == 0) return Card(Strings.T("Daily forecast"), rows);

        var floor = days.Min(d => d.Low);
        var ceiling = days.Max(d => d.High);
        var today = DateOnly.FromDateTime(local);

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("110,44,56,52,*,52"),
                Height = 46,
                Background = Brushes.Transparent,
            };

            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Text(day.Date == today ? Strings.T("Today") : day.Date.ToString("dddd", culture), 13, TokenKeys.Weather.CardText, FontWeight.SemiBold));
            name.Children.Add(Text(day.Date.ToString("m", culture), 11, TokenKeys.Weather.CardTextDim));
            row.Children.Add(name);

            var condition = forecast.ConditionFor(day);
            var picture = Picture(condition.Icon, 38);
            Grid.SetColumn(picture, 1);
            row.Children.Add(picture);

            var chance = Text(day.PrecipitationChance is >= 10 and var c ? $"{Math.Round(c)}%" : string.Empty, 12, TokenKeys.Weather.CardTextDim, FontWeight.SemiBold);
            chance.VerticalAlignment = VerticalAlignment.Center;
            chance.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(chance, 2);
            row.Children.Add(chance);

            var low = Text(units.FormatTemperature(day.Low), 14, TokenKeys.Weather.CardTextDim);
            low.VerticalAlignment = VerticalAlignment.Center;
            low.HorizontalAlignment = HorizontalAlignment.Right;
            low.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(low, 3);
            row.Children.Add(low);

            var bar = new DayRangeBar { VerticalAlignment = VerticalAlignment.Center };
            bar.Show(floor, ceiling, day.Low, day.High, day.Date == today ? forecast.Current.Temperature : null);
            Grid.SetColumn(bar, 4);
            row.Children.Add(bar);

            var high = Text(units.FormatTemperature(day.High), 14, TokenKeys.Weather.CardText, FontWeight.SemiBold);
            high.VerticalAlignment = VerticalAlignment.Center;
            high.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(high, 5);
            row.Children.Add(high);

            ToolTip.SetTip(row, DayTip(day, condition, units));

            if (i > 0)
            {
                var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 0) };
                Bind(rule, Border.BackgroundProperty, TokenKeys.Weather.ChartGrid);
                rows.Children.Add(rule);
            }

            rows.Children.Add(row);
        }

        return Card(string.Format(CultureInfo.CurrentCulture, Strings.T("{0}-day forecast"), days.Count), rows);
    }

    private static string DayTip(DailyWeather day, WeatherCondition condition, WeatherUnits units)
    {
        var culture = CultureInfo.CurrentCulture;
        var lines = new List<string>
        {
            $"{day.Date.ToString("D", culture)} — {condition.Description}",
            string.Format(culture, Strings.T("High {0}, low {1}"), units.FormatTemperature(day.High), units.FormatTemperature(day.Low)),
        };
        if (day.PrecipitationChance is { } chance) lines.Add(string.Format(culture, Strings.T("Chance of precipitation {0}%"), Math.Round(chance)));
        if (day.Precipitation is > 0 and var amount) lines.Add(string.Format(culture, Strings.T("Precipitation {0}"), units.FormatPrecipitation(amount)));
        if (day.WindMax is { } wind) lines.Add(string.Format(culture, Strings.T("Wind up to {0}, gusts {1}"), units.FormatSpeed(wind), units.FormatSpeed(day.GustMax ?? wind)));
        if (day.UvIndexMax is { } uv) lines.Add(string.Format(culture, Strings.T("UV index up to {0}"), Math.Round(uv)));
        if (day.Sunrise is { } rise && day.Sunset is { } set) lines.Add(string.Format(culture, Strings.T("Sunrise {0}, sunset {1}"), rise.ToString("t", culture), set.ToString("t", culture)));
        return string.Join('\n', lines);
    }

    private static Control Details(Forecast forecast, DateTime local, WeatherUnits units)
    {
        var now = forecast.Current;

        // As many columns as fit a tile of 220 or more, four at most, and every tile the same
        // width, so the grid ends where the cards above it end. The tiles carry the gutter on
        // their right and bottom, and the grid gives the last column's back.
        const double MinimumTile = 220;
        const double Gutter = 12;
        var tiles = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -Gutter, -Gutter) };
        tiles.SizeChanged += (_, e) =>
        {
            var columns = Math.Clamp((int)((e.NewSize.Width) / (MinimumTile + Gutter)), 1, 4);
            if (tiles.Columns != columns) tiles.Columns = columns;
        };

        tiles.Children.Add(Tile(Strings.T("Feels like"), units.FormatTemperature(now.FeelsLike ?? now.Temperature),
            WeatherReadings.FeelsLike(now.Temperature, now.FeelsLike, now.WindSpeed)));

        if (now.Humidity is { } humidity)
        {
            var dew = now.DewPoint is { } point
                ? string.Format(CultureInfo.CurrentCulture, Strings.T("The dew point is {0}. {1}"), units.FormatTemperature(point), WeatherReadings.DewPoint(point))
                : string.Empty;
            tiles.Children.Add(Tile(Strings.T("Humidity"), $"{Math.Round(humidity)}%", dew));
        }

        if (now.WindSpeed is { } wind)
        {
            var compass = new WindCompass();
            compass.Show(now.WindDirection);
            var gusts = string.Format(CultureInfo.CurrentCulture, Strings.T("Gusts {0}, from the {1}."),
                units.FormatSpeed(now.WindGusts ?? wind), WeatherReadings.Compass(now.WindDirection ?? 0));
            tiles.Children.Add(Tile(Strings.T("Wind"), units.FormatSpeed(wind), gusts, compass, beside: true));
        }

        if (now.UvIndex is { } uv)
        {
            var level = WeatherReadings.Uv(uv);
            var meter = new UvMeter { Margin = new Thickness(0, 6, 0, 0) };
            meter.Show(uv);
            tiles.Children.Add(Tile(Strings.T("UV index"), $"{Math.Round(uv)}  {WeatherReadings.UvName(level)}", WeatherReadings.UvAdvice(level), meter));
        }

        if (forecast.Today(local) is { Sunrise: { } rise, Sunset: { } set } today)
        {
            var arc = new SunArc { Margin = new Thickness(0, 4, 0, 0) };
            arc.Show((local - rise).TotalMinutes / Math.Max(1, (set - rise).TotalMinutes));
            var culture = CultureInfo.CurrentCulture;
            var (next, at) = NextSunEvent(forecast, local);
            var note = new List<string>();
            if (forecast.DaysFrom(local, 2).SelectMany(d => new[] { d.Sunrise, d.Sunset }).FirstOrDefault(t => t > local) is { } coming)
            {
                var wait = coming - local;
                note.Add(string.Format(culture, Strings.T("{0} in {1} h {2} min."), next, (int)wait.TotalHours, wait.Minutes));
            }

            if (today.Daylight is { } span)
            {
                note.Add(string.Format(culture, Strings.T("{0} h {1} min of daylight."), (int)span.TotalHours, span.Minutes));
            }

            tiles.Children.Add(Tile(Strings.T("Sunrise and sunset"), $"{rise.ToString("t", culture)} – {set.ToString("t", culture)}", string.Join(' ', note), arc));
        }

        if (now.Visibility is { } visibility)
        {
            tiles.Children.Add(Tile(Strings.T("Visibility"), units.FormatDistance(visibility), WeatherReadings.Visibility(visibility)));
        }

        if (now.Pressure is { } pressure)
        {
            var trend = WeatherReadings.Trend(forecast.Hourly, local);
            tiles.Children.Add(Tile(Strings.T("Pressure"), units.FormatPressure(pressure),
                string.Format(CultureInfo.CurrentCulture, Strings.T("{0} over the next three hours."), WeatherReadings.TrendName(trend))));
        }

        var nextDay = forecast.HoursFrom(local, 24);
        if (nextDay.Count > 0)
        {
            var total = nextDay.Sum(h => h.Precipitation ?? 0);
            var snow = nextDay.Sum(h => h.Snowfall ?? 0);
            var wetHours = nextDay.Count(h => h.Precipitation >= 0.1);
            var said = total < 0.1
                ? Strings.T("None expected.")
                : string.Join(' ', new[]
                {
                    Strings.Counted("Over {0} hour.", "Over {0} hours.", wetHours),
                    snow >= 0.1 ? string.Format(CultureInfo.CurrentCulture, Strings.T("Including {0} cm of snow."), Math.Round(snow, 1)) : string.Empty,
                }.Where(s => s.Length > 0));
            tiles.Children.Add(Tile(Strings.T("Next 24 hours"), units.FormatPrecipitation(total), said));
        }

        return tiles;
    }

    private static Control Tile(string title, string value, string note, Control? visual = null, bool beside = false)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Text(title, 12, TokenKeys.Weather.CardTextDim, FontWeight.SemiBold));

        var valueText = Text(value, 24, TokenKeys.Weather.CardText);
        if (visual is not null && beside)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(valueText);
            Grid.SetColumn(visual, 1);
            row.Children.Add(visual);
            stack.Children.Add(row);
        }
        else
        {
            stack.Children.Add(valueText);
            if (visual is not null) stack.Children.Add(visual);
        }

        if (note.Length > 0) stack.Children.Add(Text(note, 12, TokenKeys.Weather.CardTextDim));

        var card = Card(null, stack);
        card.MinHeight = 132;
        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }

    private Control Discussion(ForecastDiscussion discussion)
    {
        var culture = CultureInfo.CurrentCulture;
        var sections = new StackPanel { Spacing = 14, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var section in discussion.Sections)
        {
            var block = new StackPanel { Spacing = 6 };
            block.Children.Add(Text(section.Title, 14, TokenKeys.Weather.CardText, FontWeight.SemiBold));
            if (section.Issued.Length > 0) block.Children.Add(Text(section.Issued, 11, TokenKeys.Weather.CardTextDim));
            foreach (var part in section.Blocks)
            {
                block.Children.Add(part.Kind switch
                {
                    DiscussionBlockKind.Bullet => Bullet(part.Text),
                    DiscussionBlockKind.Table => Table(part.Text),
                    _ => Text(part.Text, 13, TokenKeys.Weather.CardText),
                });
            }

            sections.Children.Add(block);
        }

        var issued = discussion.Issued is { } at ? at.ToLocalTime().ToString("f", culture) : string.Empty;
        var byline = string.Join(" · ", new[] { discussion.Issuer, issued }.Where(s => s.Length > 0));

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(Text(byline, 12, TokenKeys.Weather.CardTextDim));
        var wrote = new Expander
        {
            Header = Strings.T("What the forecasters wrote"),
            Content = sections,
            IsExpanded = false,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _expanders.Add(wrote);
        body.Children.Add(wrote);
        return Card(Strings.T("Forecast discussion"), body);
    }

    private static Control Bullet(string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*") };
        row.Children.Add(Text("•", 13, TokenKeys.Weather.CardText));
        var body = Text(text, 13, TokenKeys.Weather.CardText);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    private static Control Table(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        Bind(block, TextBlock.FontFamilyProperty, "mono.fontfamily", brush: false);
        Bind(block, TextBlock.ForegroundProperty, TokenKeys.Weather.CardText);
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = block,
        };
    }

    /// <summary>
    /// The credit the data's licence asks for, and the Weather Service's where it spoke — linked,
    /// as the licence wants, and small, as a credit should be.
    /// </summary>
    private Control Attribution(WeatherPlace place)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(Text(Strings.T("Weather data by"), 11, TokenKeys.Weather.CardTextDim));
        row.Children.Add(Link("Open-Meteo.com", OpenMeteo.Home));
        row.Children.Add(Link("CC BY 4.0", OpenMeteo.Licence));
        if (place.HasWeatherService)
        {
            row.Children.Add(Text(Strings.T("· Warnings and discussion from the"), 11, TokenKeys.Weather.CardTextDim));
            row.Children.Add(Link(Strings.T("National Weather Service"), "https://www.weather.gov/"));
        }

        return row;
    }

    // ---- Small helpers ---------------------------------------------------------------------------

    private Button Link(string text, string url)
    {
        var label = new TextBlock { Text = text, FontSize = 11, TextDecorations = TextDecorations.Underline };
        Bind(label, TextBlock.ForegroundProperty, "text.link");
        var link = new Button { Content = label, Classes = { "flat" }, Padding = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center };
        link.Click += (_, _) => LinkRequested?.Invoke(this, url);
        ToolTip.SetTip(link, url);
        return link;
    }

    internal static Border Card(string? title, Control content)
    {
        var stack = new StackPanel { Spacing = 10 };
        if (title is not null) stack.Children.Add(Text(title, 15, TokenKeys.Weather.CardText, FontWeight.SemiBold));
        stack.Children.Add(content);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14),
            Child = stack,
        };
        Bind(card, Border.BackgroundProperty, TokenKeys.Weather.Card);
        Bind(card, Border.BorderBrushProperty, TokenKeys.Weather.CardBorder);
        return card;
    }

    internal static TextBlock Text(string text, double size, string token, FontWeight weight = FontWeight.Normal)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        Bind(block, TextBlock.ForegroundProperty, token);
        return block;
    }

    private static Control Centred(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        text.MaxWidth = 520;
        return text;
    }

    internal static Image Picture(string key, double size)
    {
        var image = new Image { Width = size, Height = size };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        if (WeatherArt.Ready(key) is { } ready) image.Source = ready;
        else _ = Fill(image, key);
        return image;
    }

    private static async Task Fill(Image image, string key)
    {
        if (await WeatherArt.LoadAsync(key) is Bitmap bitmap) image.Source = bitmap;
    }

    private static TextBlock Glyph(string name, double size, string token)
    {
        var glyph = new TextBlock
        {
            Text = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty(name, 24),
            FontFamily = Mailbox.Theming.Icons.IconFont.Family,
            FontSize = size,
        };
        Bind(glyph, TextBlock.ForegroundProperty, token);
        return glyph;
    }

    internal static Button Accent(string text)
    {
        var button = new Button { Content = text, Classes = { "weatheraccent" }, Padding = new Thickness(16, 7), MinWidth = 120 };
        return button;
    }

    /// <summary>Binds to a token's brush, or — for a resource that is not a colour — to the key itself.</summary>
    internal static void Bind(AvaloniaObject target, AvaloniaProperty property, string token, bool brush = true)
        => target[!property] = new DynamicResourceExtension(brush ? token + ".brush" : token);
}

/// <summary>
/// A warning, tinted by its severity: the severity's colour down the left edge, and a ground mixed
/// from that colour toward the card by the theme's tint.
/// </summary>
/// <remarks>
/// The tint is worked out from the theme's own resources and worked out again when they move, so
/// a warning read in Black is a dark red, not the light pink it is in White.
/// </remarks>
internal sealed class AlertCard : Border
{
    private readonly string _token;

    public AlertCard(string token)
    {
        _token = token;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(4, 0, 0, 0);
        Padding = new Thickness(16, 14);
        this[!BorderBrushProperty] = new DynamicResourceExtension(token + ".brush");
        ResourcesChanged += (_, _) => Tint();
        AttachedToVisualTree += (_, _) => Tint();
    }

    private void Tint()
    {
        if (!this.TryFindResource(_token + ".color", out var severity) || severity is not Color colour) return;
        if (!this.TryFindResource(TokenKeys.Weather.Card + ".color", out var ground) || ground is not Color card) return;
        var amount = this.TryFindResource(TokenKeys.Weather.AlertTint + ".value", out var tint) && tint is double d ? d : 0.9;
        Background = new SolidColorBrush(Blend.Toward(colour, card, amount));
    }
}
