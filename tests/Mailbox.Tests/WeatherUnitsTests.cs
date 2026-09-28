using System.Globalization;
using Mailbox.Core.Settings;
using Mailbox.Core.Weather;

namespace Mailbox.Tests;

public class WeatherUnitsTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void TemperaturesReadAsAThermometerWould()
    {
        Assert.Equal("72°", WeatherUnits.UnitedStates.FormatTemperature(22, Invariant));
        Assert.Equal("22°", WeatherUnits.Metric.FormatTemperature(22, Invariant));
        Assert.Equal("-18°", WeatherUnits.Metric.FormatTemperature(-17.6, Invariant));

        // Half a degree rounds away from zero, and a reading just below zero is not "-0".
        Assert.Equal("3°", WeatherUnits.Metric.FormatTemperature(2.5, Invariant));
        Assert.Equal("0°", WeatherUnits.Metric.FormatTemperature(-0.4, Invariant));
        Assert.Equal("32°", WeatherUnits.UnitedStates.FormatTemperature(0, Invariant));
    }

    [Fact]
    public void WindIsGivenInEachOfTheFourUnitsPeopleUse()
    {
        Assert.Equal("5 mph", WeatherUnits.UnitedStates.FormatSpeed(7.3, Invariant));
        Assert.Equal("7 km/h", WeatherUnits.Metric.FormatSpeed(7.3, Invariant));
        Assert.Equal("2 m/s", (WeatherUnits.Metric with { Speed = SpeedUnit.MetresPerSecond }).FormatSpeed(7.3, Invariant));
        Assert.Equal("4 kn", (WeatherUnits.Metric with { Speed = SpeedUnit.Knots }).FormatSpeed(7.3, Invariant));
    }

    [Fact]
    public void RainPressureAndDistanceUseTheirOwnPrecision()
    {
        Assert.Equal("0.12 in", WeatherUnits.UnitedStates.FormatPrecipitation(3.1, Invariant));
        Assert.Equal("3.1 mm", WeatherUnits.Metric.FormatPrecipitation(3.1, Invariant));
        Assert.Equal("0 mm", WeatherUnits.Metric.FormatPrecipitation(0, Invariant));
        Assert.Equal("0.00 in", WeatherUnits.UnitedStates.FormatPrecipitation(0, Invariant));

        Assert.Equal("29.84 inHg", WeatherUnits.UnitedStates.FormatPressure(1010.4, Invariant));
        Assert.Equal("1010 hPa", WeatherUnits.Metric.FormatPressure(1010.4, Invariant));

        Assert.Equal("9.9 mi", WeatherUnits.UnitedStates.FormatDistance(16000, Invariant));
        Assert.Equal("16 km", WeatherUnits.Metric.FormatDistance(16000, Invariant));
        Assert.Equal("0.4 km", WeatherUnits.Metric.FormatDistance(400, Invariant));
    }

    [Fact]
    public void TheDecimalPointIsTheReadersOwn()
    {
        Assert.Equal("3,1 mm", WeatherUnits.Metric.FormatPrecipitation(3.1, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Theory]
    [InlineData("en-US", TemperatureUnit.Fahrenheit, SpeedUnit.MilesPerHour, DistanceUnit.Miles)]
    [InlineData("es-PR", TemperatureUnit.Fahrenheit, SpeedUnit.MilesPerHour, DistanceUnit.Miles)]
    [InlineData("en-GB", TemperatureUnit.Celsius, SpeedUnit.MilesPerHour, DistanceUnit.Miles)]
    [InlineData("de-DE", TemperatureUnit.Celsius, SpeedUnit.KilometresPerHour, DistanceUnit.Kilometres)]
    [InlineData("en", TemperatureUnit.Celsius, SpeedUnit.KilometresPerHour, DistanceUnit.Kilometres)]
    [InlineData("", TemperatureUnit.Celsius, SpeedUnit.KilometresPerHour, DistanceUnit.Kilometres)]
    public void TheDefaultsAreTheReadersRegions(string culture, TemperatureUnit temperature, SpeedUnit speed, DistanceUnit distance)
    {
        var units = WeatherUnits.ForCulture(culture.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture));

        Assert.Equal(temperature, units.Temperature);
        Assert.Equal(speed, units.Speed);
        Assert.Equal(distance, units.Distance);
    }

    /// <summary>
    /// A choice once made stays made: saved, it wins over the region, and a unit never chosen still
    /// follows the region on its own.
    /// </summary>
    [Fact]
    public void AChoiceOutlastsTheRegionAndAnUnmadeOneFollowsIt()
    {
        var settings = SettingsStore.Transient();
        var german = CultureInfo.GetCultureInfo("de-DE");

        Assert.Equal(WeatherUnits.Metric, WeatherUnits.Load(settings, german));

        (WeatherUnits.Metric with { Speed = SpeedUnit.Knots }).Save(settings);
        Assert.Equal(SpeedUnit.Knots, WeatherUnits.Load(settings, CultureInfo.GetCultureInfo("en-US")).Speed);

        settings.Remove(WeatherUnits.TemperatureKey);
        var american = WeatherUnits.Load(settings, CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(TemperatureUnit.Fahrenheit, american.Temperature);
        Assert.Equal(SpeedUnit.Knots, american.Speed);
    }
}

public class WeatherConditionsTests
{
    /// <summary>
    /// Every code a model can send has a picture for day and one for night, and the night one
    /// never draws a sun.
    /// </summary>
    [Fact]
    public void EveryCodeHasADayAndANightPicture()
    {
        foreach (var code in WeatherConditions.KnownCodes)
        {
            var day = WeatherConditions.Sky(code, isDay: true);
            var night = WeatherConditions.Sky(code, isDay: false);

            Assert.False(string.IsNullOrWhiteSpace(day.Description));
            Assert.StartsWith("day/", day.Icon);
            Assert.StartsWith("night/", night.Icon);
            Assert.DoesNotContain("sun", night.Icon);
        }
    }

    [Fact]
    public void AClearSkyIsTheSunByDayAndTheMoonByNight()
    {
        Assert.Equal(new WeatherCondition("Sunny", "day/sunny-day"), WeatherConditions.Sky(0, isDay: true));
        Assert.Equal(new WeatherCondition("Clear", "night/clear-night"), WeatherConditions.Sky(0, isDay: false));
        Assert.Equal("night/partly-cloudy-night", WeatherConditions.Sky(2, isDay: false).Icon);
    }

    [Fact]
    public void WindTurnsAFairDayWindy()
    {
        Assert.Equal("Sunny", WeatherConditions.Classify(0, isDay: true, windKmh: 30).Description);
        Assert.Equal(new WeatherCondition("Windy", "day/windy"), WeatherConditions.Classify(0, isDay: true, windKmh: 45));
        Assert.Equal(new WeatherCondition("Very windy", "night/very-windy-night"), WeatherConditions.Classify(3, isDay: false, windKmh: 70));
    }

    /// <summary>Rain is what decides whether a reader takes a coat, so it wins over wind.</summary>
    [Fact]
    public void PrecipitationWinsOverWind()
    {
        Assert.Equal("Heavy rain", WeatherConditions.Classify(65, isDay: false, windKmh: 50).Description);
        Assert.Equal(new WeatherCondition("Rain and wind", "day/windy-with-rain"), WeatherConditions.Classify(63, isDay: true, windKmh: 50));
        Assert.Equal("Thunderstorms", WeatherConditions.Classify(95, isDay: true, windKmh: 80).Description);
    }

    /// <summary>
    /// Snow with wind has names of its own: blowing snow, and a blizzard when the wind is strong
    /// enough and the snow thick enough to see no more than a quarter mile.
    /// </summary>
    [Fact]
    public void SnowAndWindTogetherAreBlowingSnowOrABlizzard()
    {
        Assert.Equal("Snow", WeatherConditions.Classify(73, isDay: true, windKmh: 20).Description);
        Assert.Equal(new WeatherCondition("Blowing snow", "day/windy-with-snow"), WeatherConditions.Classify(73, isDay: true, windKmh: 45));
        Assert.Equal("Blowing snow", WeatherConditions.Classify(75, isDay: true, windKmh: 45, gustKmh: 70, visibilityMetres: 2000).Description);
        Assert.Equal(new WeatherCondition("Blizzard", "night/blizzard-night"),
            WeatherConditions.Classify(75, isDay: false, windKmh: 45, gustKmh: 70, visibilityMetres: 300));
    }

    [Fact]
    public void ACodeNobodyKnowsIsACloud()
    {
        Assert.Equal(new WeatherCondition("Cloudy", "day/cloudy"), WeatherConditions.Sky(42, isDay: true));
    }

    [Theory]
    [InlineData("Tornado Warning", "day/tornado")]
    [InlineData("Hurricane Watch", "day/hurricane")]
    [InlineData("Tropical Storm Warning", "day/tropical-storm")]
    [InlineData("Flood Watch", "day/heavy-rain")]
    [InlineData("Flash Flood Warning", "day/heavy-rain")]
    [InlineData("Coastal Flood Advisory", "day/tsunami")]
    [InlineData("Rip Current Statement", "day/tsunami")]
    [InlineData("Dust Storm Warning", "day/dust-sand")]
    [InlineData("Winter Storm Warning", "day/blizzard")]
    [InlineData("Freezing Fog Advisory", "day/fog")]
    [InlineData("Freeze Warning", "day/very-cold")]
    [InlineData("Extreme Heat Warning", "day/very-hot")]
    [InlineData("High Wind Warning", "day/very-windy")]
    [InlineData("Air Quality Alert", "day/smoke")]
    [InlineData("Severe Thunderstorm Watch", "day/thunderstorm")]
    public void AWarningIsDrawnByWhatItIsAbout(string name, string icon)
    {
        Assert.Equal(icon, WeatherConditions.AlertIcon(name));
    }

    [Theory]
    [InlineData("Red Flag Warning")]
    [InlineData("Special Weather Statement")]
    [InlineData("Avalanche Warning")]
    public void AWarningTheArtHasNothingForGetsNoPicture(string name)
    {
        Assert.Null(WeatherConditions.AlertIcon(name));
    }
}

public class WeatherPlacesTests
{
    private static WeatherPlace Place(string id, string name, double lat, double lon, string country = "US")
        => new() { Id = id, Name = name, Latitude = lat, Longitude = lon, CountryCode = country, Region = "Somewhere" };

    [Fact]
    public void ThereIsNoHomeUntilAPlaceIsAdded()
    {
        var places = new WeatherPlaces(SettingsStore.Transient());

        Assert.Empty(places.All);
        Assert.Null(places.Home);
    }

    [Fact]
    public void PlacesAreKeptInOrderAndTheFirstIsHome()
    {
        var settings = SettingsStore.Transient();
        var places = new WeatherPlaces(settings);
        places.Add(Place("a", "Beverly Hills", 34.07, -118.40));
        places.Add(Place("b", "Oslo", 59.91, 10.75, "NO"));

        var reread = new WeatherPlaces(settings);

        Assert.Equal(["a", "b"], reread.All.Select(p => p.Id));
        Assert.Equal("Beverly Hills", reread.Home?.Name);
        Assert.Equal("NO", reread.All[1].CountryCode);
    }

    /// <summary>The same town found once by name and once by zip code is one place.</summary>
    [Fact]
    public void ThePlaceAddedTwiceIsKeptOnce()
    {
        var places = new WeatherPlaces(SettingsStore.Transient());
        var first = places.Add(Place("geonames:1", "Beverly Hills", 34.07362, -118.40036));

        var second = places.Add(Place("geonames:2", "Beverly Hills", 34.0801, -118.4050));

        Assert.Same(first, second);
        Assert.Single(places.All);
    }

    [Fact]
    public void MakingAPlaceHomeMovesItToTheTop()
    {
        var places = new WeatherPlaces(SettingsStore.Transient());
        places.Add(Place("a", "A", 10, 10));
        places.Add(Place("b", "B", 20, 20));
        places.Add(Place("c", "C", 30, 30));

        Assert.True(places.MakeHome("c"));
        Assert.Equal(["c", "a", "b"], places.All.Select(p => p.Id));

        Assert.True(places.Move("c", 99));
        Assert.Equal(["a", "b", "c"], places.All.Select(p => p.Id));

        Assert.True(places.Remove("a"));
        Assert.False(places.Remove("a"));
        Assert.Equal("b", places.Home?.Id);
    }

    /// <summary>The Options dialog's Cancel puts the store back, and the list goes back with it.</summary>
    [Fact]
    public void PuttingTheStoreBackPutsThePlacesBack()
    {
        var settings = SettingsStore.Transient();
        var places = new WeatherPlaces(settings);
        places.Add(Place("a", "A", 10, 10));
        var before = settings.Snapshot();
        var raised = 0;
        places.Changed += (_, _) => raised++;

        places.Add(Place("b", "B", 20, 20));
        settings.Revert(before);

        Assert.Equal(["a"], places.All.Select(p => p.Id));
        Assert.True(raised >= 2);
    }

    [Fact]
    public void AnEntryThatCannotBeReadCostsOnlyThatPlace()
    {
        var settings = SettingsStore.Transient();
        settings.Set(WeatherPlaces.Key,
            """[{"id":"a","name":"A","latitude":10,"longitude":20},{"id":"b","name":"B","latitude":"north"},{"id":"c","name":"C","latitude":95,"longitude":0}]""");

        var places = new WeatherPlaces(settings);

        Assert.Equal(["a"], places.All.Select(p => p.Id));
    }
}
