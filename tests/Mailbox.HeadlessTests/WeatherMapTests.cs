using Avalonia;
using Mailbox.App.Weather;
using SkiaSharp;

namespace Mailbox.HeadlessTests;

/// <summary>The map's arithmetic and the base map it draws, held against the file that ships.</summary>
public class WeatherMapTests
{
    private static BaseMap Shipped()
        => BaseMap.Decode(File.ReadAllBytes(Path.Combine(Mailbox.App.StringsExport.RepoRoot()!, "assets", "weather", "map", "basemap.bin")));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(34.07362, -118.40036)]
    [InlineData(59.91273, 10.74609)]
    [InlineData(-33.8688, 151.2093)]
    [InlineData(84.9, 179.9)]
    [InlineData(-84.9, -179.9)]
    public void ProjectingAndBackIsTheSamePlace(double latitude, double longitude)
    {
        var (x, y) = MapCamera.Project(latitude, longitude);
        var (lat, lon) = MapCamera.Unproject(x, y);

        Assert.InRange(x, 0, 1);
        Assert.InRange(y, 0, 1);
        Assert.Equal(latitude, lat, 9);
        Assert.Equal(longitude, lon, 9);
    }

    [Fact]
    public void TheEquatorAndTheMeridianAreTheMiddleOfTheWorld()
    {
        Assert.Equal((0.5, 0.5), MapCamera.Project(0, 0));
    }

    /// <summary>Zooming with the wheel keeps the spot under the pointer under the pointer, as every map does.</summary>
    [Fact]
    public void ZoomingKeepsThePointUnderThePointerWhereItIs()
    {
        var camera = new MapCamera(0.17, 0.39, 6, 800, 500);
        var pointer = new Point(610, 120);
        var before = camera.ToWorld(pointer);

        var zoomed = camera.ZoomAround(pointer, 1.5);

        Assert.Equal(7.5, zoomed.Zoom, 9);
        var after = zoomed.ToWorld(pointer);
        Assert.Equal(before.X, after.X, 12);
        Assert.Equal(before.Y, after.Y, 12);
    }

    [Fact]
    public void ZoomStopsAtItsLimitsAndPanningAtTheEdgeOfTheWorld()
    {
        var camera = new MapCamera(0.5, 0.5, 3, 800, 500);

        Assert.Equal(MapCamera.MaximumZoom, camera.ZoomAround(new Point(400, 250), 50).Zoom);
        Assert.Equal(MapCamera.MinimumZoom, camera.ZoomAround(new Point(400, 250), -50).Zoom);
        var flung = camera.PanBy(-1_000_000, 1_000_000);
        Assert.Equal(1, flung.CenterX);
        Assert.Equal(0, flung.CenterY);
    }

    [Fact]
    public void TheShippedBaseMapCarriesEveryLayerAndEveryTown()
    {
        var map = Shipped();

        Assert.Equal(["borders", "counties", "lakes", "land", "roads", "states"], map.Layers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(7342, map.Places.Count);
        Assert.Contains(map.Places, p => p.Name == "Los Angeles");
        Assert.True(map.Places.Zip(map.Places.Skip(1)).All(pair => pair.First.MinZoom <= pair.Second.MinZoom), "the places come most important first");
    }

    /// <summary>A continent is drawn from the coarse data and a county from the fine; counties only when close enough to see.</summary>
    [Fact]
    public void EachZoomDrawsFromItsOwnLevelOfDetail()
    {
        var map = Shipped();

        Assert.Equal(0, map.Layers["land"].For(3)?.MinZoom);
        Assert.Equal(5, map.Layers["land"].For(6)?.MinZoom);
        Assert.Null(map.Layers["counties"].For(5));
        Assert.Equal(6, map.Layers["counties"].For(6.5)?.MinZoom);
        Assert.True(map.Layers["land"].For(6)!.Shapes.Length > map.Layers["land"].For(3)!.Shapes.Length);
    }

    [Fact]
    public void AFileThatIsNotABaseMapIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => BaseMap.Decode("NOTAMAP!!"u8));
    }

    /// <summary>
    /// A paint covers the view at the screen's own scale, and draws land where there is land: the
    /// middle of Los Angeles is the land colour, the sea off Santa Monica the water colour.
    /// </summary>
    [Fact]
    public void APaintIsTheViewAtTheScreensScaleWithLandWhereTheLandIs()
    {
        var map = Shipped();
        var (x, y) = MapCamera.Project(34.05, -118.25);
        var camera = new MapCamera(x, y, 8, 400, 300);
        var palette = new MapPalette(new SKColor(0, 0, 255), new SKColor(0, 255, 0), SKColors.Black, SKColors.Gray, SKColors.Silver, SKColors.Orange);

        var (fills, lines) = MapPainter.PaintBitmaps(map, camera, 2, palette);
        using (fills)
        using (lines)
        {
            var (sx, sy) = MapCamera.Project(33.95, -118.65);
            var offshore = camera.ToScreen(sx, sy);

            Assert.Equal(800, fills.Width);
            Assert.Equal(600, fills.Height);
            Assert.Equal(new SKColor(0, 255, 0), fills.GetPixel(fills.Width / 2, fills.Height / 2));
            Assert.Equal(new SKColor(0, 0, 255), fills.GetPixel((int)(offshore.X * 2), (int)(offshore.Y * 2)));

            // The lines layer is transparent but for its lines: LA has county and state lines and
            // highways, so something is drawn, and most of it is clear.
            var drawn = 0;
            for (var py = 0; py < lines.Height; py += 4)
            {
                for (var px = 0; px < lines.Width; px += 4)
                {
                    if (lines.GetPixel(px, py).Alpha > 0) drawn++;
                }
            }

            Assert.InRange(drawn, 1, lines.Width * lines.Height / 16 / 2);
        }
    }
}
