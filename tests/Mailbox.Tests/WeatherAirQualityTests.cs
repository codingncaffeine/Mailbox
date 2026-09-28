using Mailbox.Core.Weather;

namespace Mailbox.Tests;

public class WeatherAirQualityTests
{
    private static readonly DateTimeOffset Fetched = new(2026, 9, 28, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AnAmericanReadingCarriesItsIndexAndThePollutantThatSetIt()
    {
        var air = OpenMeteo.ParseAirQuality(WeatherFixtures.Read("open-meteo-air-beverly-hills.json"), Fetched);

        Assert.Equal(new AirIndex(76, AirPollutant.FineParticles), air.UnitedStates);
        Assert.Null(air.European);
        Assert.Equal(new DateTime(2026, 9, 27, 21, 0, 0), air.Time);
        Assert.Equal(Fetched, air.Fetched);
    }

    /// <summary>Oslo's fine particles and ozone both read 21: a tie goes to the pollutant listed first, the particles.</summary>
    [Fact]
    public void AEuropeanReadingIsReadOnItsOwnIndex()
    {
        var air = OpenMeteo.ParseAirQuality(WeatherFixtures.Read("open-meteo-air-oslo.json"), Fetched);

        Assert.Equal(new AirIndex(21, AirPollutant.FineParticles), air.European);
        Assert.Null(air.UnitedStates);
        Assert.Equal(air.European, air.On(AirQualityScale.European));
    }

    [Fact]
    public void AnIndexTheModelsHadNothingForIsMissingNotZero()
    {
        var air = OpenMeteo.ParseAirQuality("""{"current":{"time":"2026-09-27T21:00","us_aqi":null,"us_aqi_pm2_5":null}}""", Fetched);

        Assert.Null(air.UnitedStates);
    }

    [Fact]
    public void AnIndexWithoutItsPartsNamesNoPollutant()
    {
        var air = OpenMeteo.ParseAirQuality("""{"current":{"time":"2026-09-27T21:00","us_aqi":12}}""", Fetched);

        Assert.Equal(new AirIndex(12, null), air.UnitedStates);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"latitude":1}""")]
    [InlineData("""{"error":true,"reason":"Latitude must be in range of -90 to 90°. Given: 91.0."}""")]
    public void AnythingElseIsNotAirQuality(string text)
        => Assert.Throws<FormatException>(() => OpenMeteo.ParseAirQuality(text, Fetched));

    /// <summary>The service counts more than ten variables as two calls; either index is asked for alone and within that.</summary>
    [Theory]
    [InlineData(AirQualityScale.UnitedStates, "us_aqi", "european_aqi")]
    [InlineData(AirQualityScale.European, "european_aqi", "us_aqi")]
    public void ARequestAsksForOneIndexAsOneCall(AirQualityScale scale, string wanted, string unwanted)
    {
        var url = OpenMeteo.AirQualityUrl(59.9127, 10.7461, scale);
        var current = url.Split("current=")[1].Split('&')[0].Split(',');

        Assert.StartsWith(OpenMeteo.AirQualityEndpoint + "?latitude=59.9127&longitude=10.7461&", url);
        Assert.InRange(current.Length, 2, 10);
        Assert.All(current, v => Assert.StartsWith(wanted, v));
        Assert.DoesNotContain(current, v => v.StartsWith(unwanted, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NO", AirQualityScale.European)]
    [InlineData("gb", AirQualityScale.European)]
    [InlineData("TR", AirQualityScale.European)]
    [InlineData("US", AirQualityScale.UnitedStates)]
    [InlineData("CA", AirQualityScale.UnitedStates)]
    [InlineData("JP", AirQualityScale.UnitedStates)]
    [InlineData("", AirQualityScale.UnitedStates)]
    public void APlaceIsReadOnItsOwnRegionsIndex(string country, AirQualityScale scale)
        => Assert.Equal(scale, AirQualityIndex.For(country));

    /// <summary>The bands' edges as the service computes them: each band includes its top.</summary>
    [Theory]
    [InlineData(AirQualityScale.UnitedStates, 0, 0)]
    [InlineData(AirQualityScale.UnitedStates, 50, 0)]
    [InlineData(AirQualityScale.UnitedStates, 51, 1)]
    [InlineData(AirQualityScale.UnitedStates, 100, 1)]
    [InlineData(AirQualityScale.UnitedStates, 101, 2)]
    [InlineData(AirQualityScale.UnitedStates, 151, 3)]
    [InlineData(AirQualityScale.UnitedStates, 201, 4)]
    [InlineData(AirQualityScale.UnitedStates, 300, 4)]
    [InlineData(AirQualityScale.UnitedStates, 301, 5)]
    [InlineData(AirQualityScale.UnitedStates, 640, 5)]
    [InlineData(AirQualityScale.European, 20, 0)]
    [InlineData(AirQualityScale.European, 21, 1)]
    [InlineData(AirQualityScale.European, 60, 2)]
    [InlineData(AirQualityScale.European, 80, 3)]
    [InlineData(AirQualityScale.European, 100, 4)]
    [InlineData(AirQualityScale.European, 101, 5)]
    [InlineData(AirQualityScale.European, 400, 5)]
    public void AReadingFallsInItsBand(AirQualityScale scale, int value, int level)
        => Assert.Equal(level, AirQualityIndex.Level(scale, value));

    [Fact]
    public void EachIndexKeepsItsAuthoritysSixColoursAndNames()
    {
        var american = AirQualityIndex.Bands(AirQualityScale.UnitedStates);
        var european = AirQualityIndex.Bands(AirQualityScale.European);

        Assert.Equal([0xFF00E400u, 0xFFFFFF00, 0xFFFF7E00, 0xFFFF0000, 0xFF8F3F97, 0xFF7E0023], american.Select(b => b.Colour));
        Assert.Equal([0xFF50F0E6u, 0xFF50CCAA, 0xFFF0E641, 0xFFFF5050, 0xFF960032, 0xFF7D2181], european.Select(b => b.Colour));
        Assert.Equal("Unhealthy for sensitive groups", american[2].Name);
        Assert.Equal("Fair", european[1].Name);
        Assert.All(american.Concat(european), b => Assert.NotEmpty(b.Advice));
    }

    /// <summary>Six equal bands: a reading sits within its own, and the European top band is as wide as the one below it.</summary>
    [Theory]
    [InlineData(AirQualityScale.UnitedStates, 0, 0)]
    [InlineData(AirQualityScale.UnitedStates, 50, 1 / 6.0)]
    [InlineData(AirQualityScale.UnitedStates, 75, 1.5 / 6)]
    [InlineData(AirQualityScale.UnitedStates, 400, 5.5 / 6)]
    [InlineData(AirQualityScale.UnitedStates, 900, 1)]
    [InlineData(AirQualityScale.European, 30, 1.5 / 6)]
    [InlineData(AirQualityScale.European, 110, 5.5 / 6)]
    [InlineData(AirQualityScale.European, 300, 1)]
    public void AReadingIsPlacedAlongItsBand(AirQualityScale scale, int value, double at)
        => Assert.Equal(at, AirQualityIndex.Position(scale, value), 6);
}
