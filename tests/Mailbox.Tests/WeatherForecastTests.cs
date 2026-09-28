using Mailbox.Core.Weather;

namespace Mailbox.Tests;

/// <summary>The recorded responses the weather tests read, from <c>tests/fixtures/weather</c>.</summary>
/// <remarks>
/// Each was fetched from the live service with the exact request the application makes, so a
/// parser that passes here passes on what the services really send — including the gaps: the
/// Oslo forecast runs out before sixteen days and pads the rest with nulls.
/// </remarks>
internal static class WeatherFixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(Directory(), name));

    public static string Directory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
        return Path.Combine(root, "tests", "fixtures", "weather");
    }

    public static readonly DateTimeOffset Fetched = new(2026, 9, 28, 2, 5, 0, TimeSpan.Zero);

    public static Forecast BeverlyHills() => OpenMeteo.ParseForecast(Read("open-meteo-forecast-beverly-hills.json"), Fetched);

    public static Forecast Oslo() => OpenMeteo.ParseForecast(Read("open-meteo-forecast-oslo.json"), Fetched);
}

public class WeatherForecastTests
{
    [Fact]
    public void TheCurrentConditionsAreReadAsTheServiceSentThem()
    {
        var forecast = WeatherFixtures.BeverlyHills();
        var now = forecast.Current;

        Assert.Equal(new DateTime(2026, 9, 27, 19, 0, 0), now.Time);
        Assert.Equal(22, now.Temperature);
        Assert.Equal(24.4, now.FeelsLike);
        Assert.Equal(83, now.Humidity);
        Assert.Equal(3, now.Code);
        Assert.False(now.IsDay);
        Assert.Equal(7.3, now.WindSpeed);
        Assert.Equal(237, now.WindDirection);
        Assert.Equal(16000, now.Visibility);
        Assert.Equal("Cloudy", now.Condition.Description);
        Assert.Equal("night/cloudy-night", now.Condition.Icon);
    }

    [Fact]
    public void TheZoneAndEveryRowComeThrough()
    {
        var forecast = WeatherFixtures.BeverlyHills();

        Assert.Equal("America/Los_Angeles", forecast.TimeZone);
        Assert.Equal(TimeSpan.FromHours(-7), forecast.UtcOffset);
        Assert.Equal(384, forecast.Hourly.Count);
        Assert.Equal(new DateTime(2026, 9, 27, 0, 0, 0), forecast.Hourly[0].Time);
        Assert.Equal(16, forecast.Daily.Count);
        Assert.Equal(8, forecast.NextTwoHours.Count);
        Assert.All(forecast.NextTwoHours, step => Assert.Equal(0, step.Amount));
    }

    [Fact]
    public void ADayCarriesItsSunAndItsRange()
    {
        var today = WeatherFixtures.BeverlyHills().Daily[0];

        Assert.Equal(new DateOnly(2026, 9, 27), today.Date);
        Assert.Equal(30.2, today.High);
        Assert.Equal(19.9, today.Low);
        Assert.Equal(new DateTime(2026, 9, 27, 6, 45, 0), today.Sunrise);
        Assert.Equal(new DateTime(2026, 9, 27, 18, 43, 0), today.Sunset);
        Assert.Equal("Fog", today.Condition.Description);
        Assert.Equal("day/fog", today.Condition.Icon);
    }

    /// <summary>
    /// Oslo's models stop short of sixteen days and the service pads the rest with nulls. Those
    /// hours are not hours at zero degrees: they are dropped, and the forecast simply ends sooner.
    /// </summary>
    [Fact]
    public void AForecastThatEndsEarlyEndsRatherThanFillingWithZeros()
    {
        var forecast = WeatherFixtures.Oslo();

        Assert.Equal(351, forecast.Hourly.Count);
        Assert.Equal(new DateTime(2026, 10, 12, 14, 0, 0), forecast.Hourly[^1].Time);
        Assert.Equal(14, forecast.Daily.Count);
        Assert.Equal(new DateOnly(2026, 10, 11), forecast.Daily[^1].Date);
        Assert.Equal("Europe/Oslo", forecast.TimeZone);
        Assert.Equal(14.2, forecast.Current.Temperature);
    }

    /// <summary>
    /// The request and the parser are two lists of the same variables, and a variable added to one
    /// and not the other is a column that silently never fills. The recorded response was made
    /// with this very request, so its columns are the request's.
    /// </summary>
    [Fact]
    public void TheRequestAsksForExactlyWhatTheResponseCarries()
    {
        using var recorded = System.Text.Json.JsonDocument.Parse(WeatherFixtures.Read("open-meteo-forecast-beverly-hills.json"));

        foreach (var (section, asked) in new[]
                 {
                     ("current", OpenMeteo.CurrentVariables),
                     ("hourly", OpenMeteo.HourlyVariables),
                     ("daily", OpenMeteo.DailyVariables),
                 })
        {
            var columns = recorded.RootElement.GetProperty(section).EnumerateObject()
                .Select(p => p.Name)
                .Where(n => n is not ("time" or "interval"))
                .Order(StringComparer.Ordinal);
            Assert.Equal(asked.Split(',').Order(StringComparer.Ordinal), columns);
        }

        var url = OpenMeteo.ForecastUrl(34.07362, -118.40036);
        Assert.StartsWith("https://api.open-meteo.com/v1/forecast?latitude=34.0736&longitude=-118.4004&", url);
        Assert.Contains("&timezone=auto&forecast_days=16&", url);
    }

    [Fact]
    public void AnErrorIsReadAsTheReasonTheServiceGave()
    {
        var json = WeatherFixtures.Read("open-meteo-error-latitude.json");

        Assert.Equal("Latitude must be in range of -90 to 90°. Given: 999.0.", OpenMeteo.ErrorReason(json));
        var thrown = Assert.Throws<FormatException>(() => OpenMeteo.ParseForecast(json, WeatherFixtures.Fetched));
        Assert.Equal("Latitude must be in range of -90 to 90°. Given: 999.0.", thrown.Message);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void TextThatIsNotAForecastIsRefused(string text)
    {
        Assert.Throws<FormatException>(() => OpenMeteo.ParseForecast(text, WeatherFixtures.Fetched));
        Assert.Null(OpenMeteo.ErrorReason(text));
    }

    [Fact]
    public void TheHoursShownStartWithTheOneUnderWay()
    {
        var forecast = WeatherFixtures.BeverlyHills();

        var hours = forecast.HoursFrom(new DateTime(2026, 9, 27, 19, 25, 0), 24);

        Assert.Equal(24, hours.Count);
        Assert.Equal(new DateTime(2026, 9, 27, 19, 0, 0), hours[0].Time);
        Assert.Equal(new DateTime(2026, 9, 28, 18, 0, 0), hours[^1].Time);
    }

    [Fact]
    public void NowIsWorkedOutOnThePlacesClock()
    {
        var forecast = WeatherFixtures.BeverlyHills();

        // Two in the morning in London is seven the evening before in Los Angeles.
        Assert.Equal(new DateTime(2026, 9, 27, 19, 0, 0), forecast.LocalTime(new DateTimeOffset(2026, 9, 28, 2, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 9, 27), forecast.Today(new DateTime(2026, 9, 27, 19, 0, 0))?.Date);
        Assert.Equal(new DateOnly(2026, 9, 28), forecast.DaysFrom(new DateTime(2026, 9, 28, 1, 0, 0), 10)[0].Date);
    }

    /// <summary>
    /// A zone this machine does not have falls back to the offset the service sent, which is
    /// right until the next change of clocks.
    /// </summary>
    [Fact]
    public void AnUnknownZoneFallsBackToTheOffset()
    {
        var forecast = WeatherFixtures.BeverlyHills() with { TimeZone = "Nowhere/Imaginary" };

        Assert.Equal(new DateTime(2026, 9, 27, 19, 0, 0), forecast.LocalTime(new DateTimeOffset(2026, 9, 28, 2, 0, 0, TimeSpan.Zero)));
    }
}

public class WeatherPlaceSearchTests
{
    [Fact]
    public void AZipCodeFindsItsTownAndIsKeptOnIt()
    {
        var found = OpenMeteo.ParsePlaces(WeatherFixtures.Read("open-meteo-geocoding-90210.json"), "90210");

        var place = Assert.Single(found);
        Assert.Equal("Beverly Hills", place.Name);
        Assert.Equal("California", place.Region);
        Assert.Equal("US", place.CountryCode);
        Assert.Equal("90210", place.Postcode);
        Assert.Equal("geonames:5328041", place.Id);
        Assert.Equal("Beverly Hills, California", place.Label);
        Assert.Equal("America/Los_Angeles", place.TimeZone);
        Assert.True(place.HasWeatherService);
    }

    [Fact]
    public void ANameFindsSeveralPlacesTheLargestFirst()
    {
        var found = OpenMeteo.ParsePlaces(WeatherFixtures.Read("open-meteo-geocoding-london.json"), "London");

        Assert.Equal(10, found.Count);
        Assert.Equal("London, England, United Kingdom", found[0].FullName);
        Assert.Equal(string.Empty, found[0].Postcode);
        Assert.False(found[0].HasWeatherService);
    }

    /// <summary>The service leaves the results out altogether when nothing matches.</summary>
    [Fact]
    public void NoMatchIsAnEmptyList()
    {
        Assert.Empty(OpenMeteo.ParsePlaces(WeatherFixtures.Read("open-meteo-geocoding-zzqxv.json"), "zzqxv"));
    }

    [Fact]
    public void TheSearchIsEscapedAndAskedInTheReadersLanguage()
    {
        Assert.Equal(
            "https://geocoding-api.open-meteo.com/v1/search?name=S%C3%A3o%20Paulo&count=10&language=pt&format=json",
            OpenMeteo.SearchUrl("  São Paulo ", "PT"));
        Assert.EndsWith("&language=en&format=json", OpenMeteo.SearchUrl("Oslo", string.Empty));
    }
}
