using System.Globalization;
using Mailbox.Core.Weather;

namespace Mailbox.Tests;

public class WeatherMapLegendTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly MapLayer[] WithLegends =
        [MapLayers.RadarUnitedStates, MapLayers.RadarNorthAmerica, MapLayers.Temperature, MapLayers.Wind, MapLayers.Precipitation, MapLayers.Clouds];

    [Fact]
    public void EveryLegendRunsFromItsLowestValueUp()
    {
        foreach (var layer in WithLegends)
        {
            var legend = Assert.IsType<MapLegend>(layer.Legend);
            Assert.True(legend.Stops.Count >= 2, layer.Layer);
            Assert.All(legend.Stops.Zip(legend.Stops.Skip(1)), p => Assert.True(p.First.Value < p.Second.Value, $"{layer.Layer}: {p.First.Value} then {p.Second.Value}"));
        }

        Assert.Null(MapLayers.Satellite.Legend);
    }

    /// <summary>The legends are the colours of named styles, so a request must ask for those styles and not the service's default.</summary>
    [Theory]
    [InlineData("temperature", "TEMPERATURE-LINEAR")]
    [InlineData("wind", "WINDSPEEDKNOTS-LINEAR")]
    [InlineData("precipitation", "PRECIPPRTMMH-LINEAR")]
    [InlineData("clouds", "CLOUD")]
    public void GeoMetLayersAskForTheStyleTheirLegendWasReadFrom(string choice, string style)
        => Assert.Contains($"&styles={style}&", MapLayers.GetMapUrl(MapLayers.For(choice, inUnitedStates: false), 0, 0, 1, 1, 8, 8, null));

    [Fact]
    public void CanadasRadarAndTheIsobarsNameTheirStylesToo()
    {
        Assert.Contains("&styles=Radar-Rain_14colors&", MapLayers.GetMapUrl(MapLayers.RadarNorthAmerica, 0, 0, 1, 1, 8, 8, null));
        Assert.Contains("&styles=SeaLevelPressure_4mb&", MapLayers.GetMapUrl(MapLayers.Pressure, 0, 0, 1, 1, 8, 8, null));
    }

    [Fact]
    public void ASmoothScaleSpacesItsStopsEvenly()
    {
        var legend = new MapLegend(MapQuantity.Temperature, [new(-10, 0xFF0000FF), new(0, 0xFF00FF00), new(20, 0xFFFF0000)]);

        Assert.Equal(0, legend.Position(-40));
        Assert.Equal(0.25, legend.Position(-5), 6);
        Assert.Equal(0.5, legend.Position(0), 6);
        Assert.Equal(0.75, legend.Position(10), 6);
        Assert.Equal(1, legend.Position(99));
    }

    /// <summary>Three blocks, 1–2, 2–4 and 4 up; the last is given the width of the one below it, so 4–6.</summary>
    [Fact]
    public void ASteppedScaleIsARowOfEqualBlocks()
    {
        var legend = new MapLegend(MapQuantity.PrecipitationRate, [new(1, 0xFF0000FF), new(2, 0xFF00FF00), new(4, 0xFFFF0000)], Stepped: true);

        Assert.Equal(0, legend.Position(0.5));
        Assert.Equal(1 / 6.0, legend.Position(1.5), 6);
        Assert.Equal(0.5, legend.Position(3), 6);
        Assert.Equal(5 / 6.0, legend.Position(5), 6);
        Assert.Equal(1, legend.Position(100));
    }

    [Fact]
    public void AColourOnTheMapReadsBackAsItsValue()
    {
        var legend = MapLayers.Temperature.Legend!;
        foreach (var stop in legend.Stops)
        {
            Assert.Equal(stop.Value, legend.ValueOf((byte)(stop.Colour >> 16), (byte)(stop.Colour >> 8), (byte)stop.Colour)!.Value, tolerance: 0.01);
        }

        // Halfway between two stops' colours is halfway between their values.
        var (a, b) = (legend.Stops[8], legend.Stops[9]);
        byte Mid(int shift) => (byte)((((a.Colour >> shift) & 0xFF) + ((b.Colour >> shift) & 0xFF)) / 2);
        Assert.Equal((a.Value + b.Value) / 2, legend.ValueOf(Mid(16), Mid(8), Mid(0))!.Value, tolerance: 0.1);
    }

    [Fact]
    public void TemperatureIsLabelledInRoundNumbersOfTheReadersUnit()
    {
        var legend = MapLayers.Temperature.Legend!;

        var celsius = legend.Labels(WeatherUnits.Metric, Invariant);
        Assert.Equal(["-40°", "-20°", "0°", "20°", "40°"], celsius.Select(l => l.Text));
        Assert.Equal([0, 0.25, 0.5, 0.75, 1], celsius.Select(l => Math.Round(l.At, 6)));
        Assert.Equal("°C", legend.Unit(WeatherUnits.Metric));

        // 50 °F is 10 °C, ten of the sixteen five-degree steps from −40 °C.
        var fahrenheit = legend.Labels(WeatherUnits.UnitedStates, Invariant);
        Assert.Equal(["-25°", "0°", "25°", "50°", "75°", "100°"], fahrenheit.Select(l => l.Text));
        Assert.Equal(0.625, fahrenheit.Single(l => l.Text == "50°").At, 6);
        Assert.Equal("°F", legend.Unit(WeatherUnits.UnitedStates));
    }

    /// <summary>The scale runs to 55 knots, which is 63 miles an hour.</summary>
    [Fact]
    public void WindIsLabelledInTheReadersSpeed()
    {
        var legend = MapLayers.Wind.Legend!;
        var labels = legend.Labels(WeatherUnits.UnitedStates, Invariant);

        Assert.Equal(["0", "20", "40", "60"], labels.Select(l => l.Text));
        Assert.Equal(60 * 1.609344 / 1.852 / 55, labels[^1].At, 3);
        Assert.Equal("mph", legend.Unit(WeatherUnits.UnitedStates));
    }

    [Fact]
    public void RainAndRadarSayHowHardItFallsInWords()
    {
        foreach (var legend in new[] { MapLayers.RadarUnitedStates.Legend!, MapLayers.RadarNorthAmerica.Legend!, MapLayers.Precipitation.Legend! })
        {
            var labels = legend.Labels(WeatherUnits.Metric, Invariant);
            Assert.Equal(["Light", "Moderate", "Heavy"], labels.Select(l => l.Text));
            Assert.True(labels[0].At > 0 && labels[0].At < labels[1].At && labels[1].At < labels[2].At && labels[2].At < 1, string.Join(", ", labels));
            Assert.Equal(string.Empty, legend.Unit(WeatherUnits.Metric));
        }

        // Moderate rain begins at 2.5 mm an hour, which a radar sees as about 29 dBZ.
        Assert.Equal(29.4, MapLegend.ReflectivityOf(MapLegend.ModerateRate), 1);
    }

    [Theory]
    [InlineData(80, 20)]
    [InlineData(144, 25)]
    [InlineData(63.3, 20)]
    [InlineData(100, 20)]
    [InlineData(28.3, 5)]
    public void LabelStepsAreOneTwoTwoAndAHalfOrFiveTimesAPowerOfTen(double span, double step)
        => Assert.Equal(step, MapLegend.NiceStep(span, 6));

    [Fact]
    public void AGriddedLayerIsAskedForAtAPixelToACell()
    {
        Assert.Equal(1.5, MapLayers.RequestScale(MapLayers.Warnings, 2, 1000));
        Assert.Equal(1 / 15.0, MapLayers.RequestScale(MapLayers.Temperature, 1, 1000), 6);
        Assert.Equal(1, MapLayers.RequestScale(MapLayers.Temperature, 1, 20000));
        Assert.Equal(1.5, MapLayers.RequestScale(MapLayers.RadarUnitedStates, 2, 5000));
    }

    // ── The looks ──────────────────────────────────────────────────────────────────────────

    /// <summary>Pixels in the decoder's order — blue, green, red, alpha — premultiplied.</summary>
    private static byte[] Picture(int width, int height, Func<int, int, uint> argb)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = argb(x, y);
                var a = c >> 24;
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(((c & 0xFF) * a + 127) / 255);
                pixels[i + 1] = (byte)((((c >> 8) & 0xFF) * a + 127) / 255);
                pixels[i + 2] = (byte)((((c >> 16) & 0xFF) * a + 127) / 255);
                pixels[i + 3] = (byte)a;
            }
        }

        return pixels;
    }

    private static byte Alpha(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];

    /// <summary>
    /// A red warning with a blue one beside it, both running off the top of the picture and the
    /// red one off its left too. Down the red runs a dark zone border, the way the Weather Service
    /// draws one between two zones of one warning, and a faint seam; below them a stray pixel too
    /// faint to belong to anything.
    /// </summary>
    [Fact]
    public void AnOutlineFollowsWhereOneWarningMeetsAnotherAndNotTheServicesZoneBorders()
    {
        const int Width = 30;
        var pixels = Picture(Width, 16, (x, y) => (x, y) switch
        {
            (5, 13) => 0x40FF0000,
            (_, >= 10) => 0u,
            (7, _) => 0xFF000000,
            (8, _) => 0xFF800000,
            (12, _) => 0xC0FF0000,
            (< 20, _) => 0xFFFF0000,
            _ => 0xFF0000FF,
        });

        MapPixels.Outline(pixels, Width, 16, radius: 2, wash: 0.2);

        const byte Washed = 51;
        byte[] Pixel(int x, int y) => pixels[((y * Width + x) * 4)..((y * Width + x) * 4 + 4)];

        // Inside: washed, including the picture's border, the zone border and the seam.
        Assert.Equal(new byte[] { 0, 0, Washed, Washed }, Pixel(3, 5));
        Assert.Equal(Washed, Alpha(pixels, Width, 0, 5));
        Assert.Equal(Washed, Alpha(pixels, Width, 3, 0));
        Assert.Equal(new byte[] { 0, 0, Washed, Washed }, Pixel(7, 5));
        Assert.Equal(new byte[] { 0, 0, Washed, Washed }, Pixel(8, 5));
        Assert.Equal(Washed, Alpha(pixels, Width, 12, 5));
        Assert.Equal(Washed, Alpha(pixels, Width, 3, 7));

        // The edges: two pixels in from the outside, and two either side of where red meets blue.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(3, 8));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(3, 9));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(7, 9));
        Assert.Equal(Washed, Alpha(pixels, Width, 17, 5));
        Assert.Equal(255, Alpha(pixels, Width, 18, 5));
        Assert.Equal(255, Alpha(pixels, Width, 19, 5));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(20, 5));
        Assert.Equal(255, Alpha(pixels, Width, 21, 5));
        Assert.Equal(Washed, Alpha(pixels, Width, 22, 5));
        Assert.Equal(Washed, Alpha(pixels, Width, 29, 5));

        // Outside stays empty, and the stray faint pixel is dropped.
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(5, 13));
        Assert.Equal(0, Alpha(pixels, Width, 5, 12));
    }

    [Fact]
    public void InkTurnsBlackToTheMapsInkAndWhiteToItsHalo()
    {
        var pixels = Picture(4, 1, (x, _) => x switch { 0 => 0xFF000000, 1 => 0xFFFFFFFF, 2 => 0x80000000, _ => 0 });

        MapPixels.Ink(pixels, ink: 0xFFD8D8D8, halo: 0xFF141414);

        Assert.Equal(new byte[] { 0xD8, 0xD8, 0xD8, 255 }, pixels[0..4]);
        Assert.Equal(new byte[] { 0x14, 0x14, 0x14, 255 }, pixels[4..8]);
        Assert.Equal(new byte[] { 0x6C, 0x6C, 0x6C, 128 }, pixels[8..12]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels[12..16]);
    }

    [Fact]
    public void CloudCoverBecomesTheThemesCloudByHowMuchThereIs()
    {
        var legend = MapLayers.Clouds.Legend!;
        var pixels = Picture(3, 1, (x, _) => x switch { 0 => 0xFFFEFEFE, 1 => 0xFF868686, _ => 0xFF202020 });

        MapPixels.Tint(pixels, legend, tint: 0xD98A949F);

        Assert.Equal(217, pixels[3]);
        Assert.InRange(pixels[7], 105, 112);
        Assert.Equal(0, pixels[11]);
        Assert.Equal((byte)Math.Round(0x8A * 217 / 255.0), pixels[2]);
    }

    [Fact]
    public void SlightRatesAreFadedAndRealRainIsNot()
    {
        var legend = MapLayers.Precipitation.Legend!;
        var pixels = Picture(3, 1, (x, _) => x switch { 0 => 0xFF98FD65, 1 => 0xFF0078FC, _ => 0xFF00007F });

        MapPixels.Fade(pixels, legend, full: 1, floor: 0.25);

        Assert.Equal(255, pixels[3]);
        Assert.InRange(pixels[7], 155, 163);
        Assert.InRange(pixels[11], 62, 66);
    }
}
