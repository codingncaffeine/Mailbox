using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Mailbox.App.Weather;
using Mailbox.Core.Weather;

namespace Mailbox.HeadlessTests;

/// <summary>The weather on the calendar's toolbar: what it shows, what pressing it asks for, and that it wears the theme.</summary>
public class CalendarWeatherBarTests
{
    private static readonly WeatherPlace BeverlyHills = new()
    {
        Id = "geonames:5328041", Name = "Beverly Hills", Region = "California", CountryCode = "US",
        Latitude = 34.07362, Longitude = -118.40036,
    };

    private static readonly WeatherPlace Oslo = new()
    {
        Id = "geonames:3143244", Name = "Oslo", Region = "Oslo", CountryCode = "NO",
        Latitude = 59.91273, Longitude = 10.74609,
    };

    private static Forecast Recorded()
    {
        var text = File.ReadAllText(Path.Combine(Mailbox.App.StringsExport.RepoRoot()!, "tests", "fixtures", "weather", "open-meteo-forecast-beverly-hills.json"));
        return OpenMeteo.ParseForecast(text, DateTimeOffset.UnixEpoch);
    }

    /// <summary>The moment the recorded forecast was made, as the instant a reader would be looking at it.</summary>
    private static DateTimeOffset Then(Forecast forecast) => new(forecast.Current.Time, forecast.UtcOffset);

    private static IReadOnlyList<string> Words(Control control)
        => [.. control.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).Where(t => t.Length > 0)];

    [Fact]
    public void ThePlaceComesFirstAndThenItsNextThreeDays()
    {
        var forecast = Recorded();
        var days = forecast.DaysFrom(forecast.LocalTime(Then(forecast)), 3);
        var units = WeatherUnits.UnitedStates;

        var shown = HeadlessApp.OnUiThread(() =>
        {
            var bar = new CalendarWeatherBar();
            bar.Show(BeverlyHills, forecast, [BeverlyHills, Oslo], units, Then(forecast));
            return (Count: bar.Children.Count, Place: Words(bar.Children[0]), Days: bar.Children.Skip(1).Select(Words).ToList());
        });

        Assert.Equal(4, shown.Count);
        Assert.Equal("Beverly Hills, California", shown.Place[0]);
        Assert.Equal(["Today", "Tomorrow", days[2].Date.ToString("dddd", CultureInfo.CurrentCulture)], shown.Days.Select(d => d[0]));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal($"{units.FormatTemperature(days[i].High)}F/{units.FormatTemperature(days[i].Low)}F", shown.Days[i][1]);
        }
    }

    [Fact]
    public void TheTemperaturesAreInTheReadersOwnUnit()
    {
        var forecast = Recorded();

        var range = HeadlessApp.OnUiThread(() =>
        {
            var bar = new CalendarWeatherBar();
            bar.Show(BeverlyHills, forecast, [BeverlyHills], WeatherUnits.Metric, Then(forecast));
            return Words(bar.Children[1])[1];
        });

        Assert.Matches(@"^-?\d+°C/-?\d+°C$", range);
    }

    /// <summary>Until the place's forecast arrives there is the place alone, so the list of places can still be reached.</summary>
    [Fact]
    public void WithoutAForecastThereIsThePlaceAlone()
    {
        var count = HeadlessApp.OnUiThread(() =>
        {
            var bar = new CalendarWeatherBar();
            bar.Show(BeverlyHills, null, [BeverlyHills], WeatherUnits.UnitedStates, DateTimeOffset.UtcNow);
            return bar.Children.Count;
        });

        Assert.Equal(1, count);
    }

    /// <summary>The list under the place: every place with the shown one marked, then Add Location and Open Weather — and choosing says which.</summary>
    [Fact]
    public void ThePlaceListMarksTheShownPlaceAndSaysWhichIsChosen()
    {
        var forecast = Recorded();

        var (headers, marked, chosen, opened, added) = HeadlessApp.OnUiThread(() =>
        {
            var bar = new CalendarWeatherBar();
            string? picked = null;
            string? open = null;
            var add = false;
            bar.PlaceChosen += (_, id) => picked = id;
            bar.WeatherRequested += (_, id) => open = id;
            bar.AddRequested += (_, _) => add = true;
            bar.Show(BeverlyHills, forecast, [BeverlyHills, Oslo], WeatherUnits.UnitedStates, Then(forecast));

            var menu = (MenuFlyout)((Button)bar.Children[0]).Flyout!;
            var items = menu.Items.OfType<MenuItem>().ToList();
            items.Single(i => (string?)i.Header == "Oslo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            items.Single(i => (string?)i.Header == "Open Weather").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            items.Single(i => (string?)i.Header == "Add Location…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return (
                items.Select(i => (string?)i.Header).ToList(),
                items.Where(i => i.IsChecked).Select(i => (string?)i.Header).ToList(),
                picked, open, add);
        });

        Assert.Equal(["Beverly Hills, California", "Oslo", "Add Location…", "Open Weather"], headers);
        Assert.Equal(["Beverly Hills, California"], marked);
        Assert.Equal(Oslo.Id, chosen);
        Assert.Equal(BeverlyHills.Id, opened);
        Assert.True(added);
    }

    [Fact]
    public void ADayOpensItsPlaceInTheWeatherModule()
    {
        var forecast = Recorded();

        var opened = HeadlessApp.OnUiThread(() =>
        {
            var bar = new CalendarWeatherBar();
            string? open = null;
            bar.WeatherRequested += (_, id) => open = id;
            bar.Show(Oslo, forecast, [BeverlyHills, Oslo], WeatherUnits.UnitedStates, Then(forecast));
            ((Button)bar.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return open;
        });

        Assert.Equal(Oslo.Id, opened);
    }

    /// <summary>
    /// The words are drawn in the toolbar's own colour, taken live from the theme: changed while
    /// the bar is on screen, as switching themes changes it, every word follows at once.
    /// </summary>
    [Fact]
    public void TheBarWearsTheThemeAndFollowsAChangeOfIt()
    {
        var forecast = Recorded();

        var (before, after) = HeadlessApp.OnUiThread(() =>
        {
            var bar = new CalendarWeatherBar();
            var window = new Window { Content = bar };
            window.Resources["calendar.toolbar.text.brush"] = new SolidColorBrush(Color.Parse("#112233"));
            window.Show();
            bar.Show(BeverlyHills, forecast, [BeverlyHills], WeatherUnits.UnitedStates, Then(forecast));

            IReadOnlyList<Color?> Inks() => [.. bar.GetLogicalDescendants().OfType<TextBlock>().Select(t => (t.Foreground as ISolidColorBrush)?.Color)];
            var first = Inks();
            window.Resources["calendar.toolbar.text.brush"] = new SolidColorBrush(Color.Parse("#F0E0D0"));
            var second = Inks();
            window.Close();
            return (first, second);
        });

        Assert.NotEmpty(before);
        Assert.All(before, c => Assert.Equal(Color.Parse("#112233"), c));
        Assert.All(after, c => Assert.Equal(Color.Parse("#F0E0D0"), c));
    }
}
