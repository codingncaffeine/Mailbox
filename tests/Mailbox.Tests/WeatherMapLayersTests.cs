using Mailbox.Core.Weather;

namespace Mailbox.Tests;

public class WeatherMapLayersTests
{
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void TimesAreReadAsListsAndAsIntervals()
    {
        Assert.Equal([At("2026-09-28T00:00Z"), At("2026-09-28T00:06Z"), At("2026-09-28T00:12Z")],
            MapLayers.ParseTimes("2026-09-28T00:00:00Z/2026-09-28T00:12:00Z/PT6M"));
        Assert.Equal([At("2026-09-27T21:00:18Z"), At("2026-09-27T21:04:09Z")],
            MapLayers.ParseTimes("2026-09-27T21:04:09.000Z,2026-09-27T21:00:18.000Z"));
        Assert.Equal(25, MapLayers.ParseTimes("2026-09-28T00:00:00Z/2026-09-29T00:00:00Z/PT1H").Count);
        Assert.Empty(MapLayers.ParseTimes("not a time"));
    }

    /// <summary>Each recorded capabilities document yields its layer's own times, whichever of the two forms it uses.</summary>
    [Theory]
    [InlineData("wms-nowcoast-radar.xml", "weather_radar:base_reflectivity_mosaic", "2026-09-27T21:00:18Z", 4)]
    [InlineData("wms-geomet-radar.xml", "RADAR_1KM_RRAI", "2026-09-28T00:30:00Z", 6)]
    [InlineData("wms-geomet-temperature.xml", "GDPS_15km_AirTemp_2m", "2026-09-27T12:00:00Z", 60)]
    public void TheCapabilitiesYieldTheLayersTimes(string fixture, string layer, string first, int apartMinutes)
    {
        var times = MapLayers.Times(WeatherFixtures.Read(fixture), layer);

        Assert.True(times.Count > 10, $"{times.Count} times");
        Assert.Equal(At(first), times[0]);
        Assert.InRange((times[1] - times[0]).TotalMinutes, apartMinutes - 1, apartMinutes + 1);
        Assert.True(times.Zip(times.Skip(1)).All(p => p.First < p.Second));
    }

    [Fact]
    public void ALayerTheDocumentDoesNotHaveHasNoTimes()
    {
        Assert.Empty(MapLayers.Times(WeatherFixtures.Read("wms-geomet-temperature.xml"), "GDPS_15km_Nothing"));
        Assert.Empty(MapLayers.Times("<not xml", "anything"));
    }

    /// <summary>An observation plays the last two hours, ten minutes apart at the closest, ending at the latest.</summary>
    [Fact]
    public void ObservationsPlayTheLastTwoHours()
    {
        var times = MapLayers.Times(WeatherFixtures.Read("wms-nowcoast-radar.xml"), "weather_radar:base_reflectivity_mosaic");
        var now = times[^1] + TimeSpan.FromMinutes(1);

        var frames = MapLayers.Frames(MapLayerKind.Observation, times, now);

        Assert.Equal(times[^1], frames[^1]);
        Assert.True(frames[0] >= now - TimeSpan.FromHours(2));
        Assert.All(frames.Zip(frames.Skip(1)), p => Assert.True(p.Second - p.First >= TimeSpan.FromMinutes(10)));
        Assert.InRange(frames.Count, 8, 13);
    }

    /// <summary>A forecast plays from the hour under way, every three hours for two days.</summary>
    [Fact]
    public void ForecastsPlayTheNextTwoDays()
    {
        var times = MapLayers.ParseTimes("2026-09-27T12:00:00Z/2026-10-07T12:00:00Z/PT1H");
        var now = At("2026-09-28T02:20:00Z");

        var frames = MapLayers.Frames(MapLayerKind.Forecast, times, now);

        Assert.Equal(At("2026-09-28T02:00:00Z"), frames[0]);
        Assert.Equal(At("2026-09-30T02:00:00Z"), frames[^1]);
        Assert.Equal(17, frames.Count);
    }

    [Fact]
    public void AnOverlayShowsItsLatest()
    {
        var times = MapLayers.ParseTimes("2026-09-28T00:00:00Z/2026-09-28T02:00:00Z/PT15M");

        Assert.Equal([At("2026-09-28T02:00:00Z")], MapLayers.Frames(MapLayerKind.Overlay, times, At("2026-09-28T02:07:00Z")));
    }

    /// <summary>The whole world is ±20,037,508 metres in EPSG:3857, and a request asks for exactly the view.</summary>
    [Fact]
    public void AGetMapAsksForTheViewInWebMercatorMetres()
    {
        var url = MapLayers.GetMapUrl(MapLayers.Temperature, 0, 0, 1, 1, 800, 800, At("2026-09-28T03:00:00Z"));

        Assert.StartsWith("https://geo.weather.gc.ca/geomet?service=WMS&version=1.3.0&request=GetMap&layers=GDPS_15km_AirTemp_2m&", url);
        Assert.Contains("&crs=EPSG:3857&bbox=-20037508.343,-20037508.343,20037508.343,20037508.343&width=800&height=800", url);
        Assert.EndsWith("&format=image/png&transparent=true&time=2026-09-28T03:00:00Z", url);
        Assert.DoesNotContain("&time=", MapLayers.GetMapUrl(MapLayers.Warnings, 0.2, 0.3, 0.25, 0.35, 400, 300, null));
    }

    [Fact]
    public void RadarIsTheWeatherServicesInTheUnitedStatesAndCanadasElsewhere()
    {
        Assert.Same(MapLayers.RadarUnitedStates, MapLayers.For("radar", inUnitedStates: true));
        Assert.Same(MapLayers.RadarNorthAmerica, MapLayers.For("radar", inUnitedStates: false));
        Assert.True(MapLayers.RadarCovers(34.07, -118.4));
        Assert.True(MapLayers.RadarCovers(45.5, -73.6));
        Assert.False(MapLayers.RadarCovers(59.9, 10.7));
    }
}
