using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Mailbox.App.Weather;

/// <summary>
/// Where the map is looking: a centre in Web Mercator world units on [0, 1), a zoom — the world
/// is 256 × 2^zoom pixels across — and the size of the window onto it.
/// </summary>
internal readonly record struct MapCamera(double CenterX, double CenterY, double Zoom, double Width, double Height)
{
    public const double MinimumZoom = 2;
    public const double MaximumZoom = 11;

    public double WorldPixels => 256 * Math.Pow(2, Zoom);

    public Point ToScreen(double x, double y) => new((x - CenterX) * WorldPixels + Width / 2, (y - CenterY) * WorldPixels + Height / 2);

    public (double X, double Y) ToWorld(Point screen)
        => ((screen.X - Width / 2) / WorldPixels + CenterX, (screen.Y - Height / 2) / WorldPixels + CenterY);

    /// <summary>The world rectangle on screen, as left, top, right, bottom.</summary>
    public (double Left, double Top, double Right, double Bottom) Visible
    {
        get
        {
            var (l, t) = ToWorld(new Point(0, 0));
            var (r, b) = ToWorld(new Point(Width, Height));
            return (l, t, r, b);
        }
    }

    /// <summary>Dragged by a number of pixels; the centre stays on the world.</summary>
    public MapCamera PanBy(double dx, double dy)
        => this with
        {
            CenterX = Math.Clamp(CenterX - dx / WorldPixels, 0, 1),
            CenterY = Math.Clamp(CenterY - dy / WorldPixels, 0, 1),
        };

    /// <summary>Zoomed by <paramref name="by"/> steps, keeping the world point under <paramref name="anchor"/> where it is.</summary>
    public MapCamera ZoomAround(Point anchor, double by)
    {
        var zoom = Math.Clamp(Zoom + by, MinimumZoom, MaximumZoom);
        var (wx, wy) = ToWorld(anchor);
        var world = 256 * Math.Pow(2, zoom);
        return this with
        {
            Zoom = zoom,
            CenterX = Math.Clamp(wx - (anchor.X - Width / 2) / world, 0, 1),
            CenterY = Math.Clamp(wy - (anchor.Y - Height / 2) / world, 0, 1),
        };
    }

    public static (double X, double Y) Project(double latitude, double longitude)
    {
        var s = Math.Sin(Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180);
        return ((longitude + 180) / 360, 0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI));
    }

    public static (double Latitude, double Longitude) Unproject(double x, double y)
        => (Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y))) * 180 / Math.PI, x * 360 - 180);
}

/// <summary>The base map's colours, read from the theme on the interface's thread and carried to the painter.</summary>
internal readonly record struct MapPalette(SKColor Water, SKColor Land, SKColor Border, SKColor State, SKColor County, SKColor Road);

/// <summary>What the painter made for one camera: the fills under the weather and the lines over it.</summary>
internal sealed record MapFrame(MapCamera Camera, Bitmap Fills, Bitmap Lines) : IDisposable
{
    public void Dispose()
    {
        Fills.Dispose();
        Lines.Dispose();
    }
}

/// <summary>
/// Paints the base map for a camera into two bitmaps at device resolution, off the interface's
/// thread: land and lakes, which the weather is drawn over, and the borders, states, counties and
/// highways, which are drawn over the weather so it never hides where it is.
/// </summary>
internal static class MapPainter
{
    public static MapFrame Paint(BaseMap map, MapCamera camera, double scale, MapPalette palette)
    {
        var (fills, lines) = PaintBitmaps(map, camera, scale, palette);
        using (fills)
        using (lines)
        {
            return new MapFrame(camera, ToAvalonia(fills, scale), ToAvalonia(lines, scale));
        }
    }

    /// <summary>The two layers as Skia bitmaps, which the caller owns — the half of a paint that needs no windowing platform.</summary>
    internal static (SKBitmap Fills, SKBitmap Lines) PaintBitmaps(BaseMap map, MapCamera camera, double scale, MapPalette palette)
    {
        var width = Math.Max(1, (int)Math.Ceiling(camera.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(camera.Height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var perWorld = camera.WorldPixels * scale;
        var originX = camera.CenterX * perWorld - width / 2.0;
        var originY = camera.CenterY * perWorld - height / 2.0;
        var (left, top, right, bottom) = camera.Visible;
        var bounds = (
            MinX: (int)Math.Floor(left * BaseMap.Quantum), MinY: (int)Math.Floor(top * BaseMap.Quantum),
            MaxX: (int)Math.Ceiling(right * BaseMap.Quantum), MaxY: (int)Math.Ceiling(bottom * BaseMap.Quantum));

        float X(int q) => (float)(q / BaseMap.Quantum * perWorld - originX);
        float Y(int q) => (float)(q / BaseMap.Quantum * perWorld - originY);

        SKPath? Outline(string layer, bool close)
        {
            if (!map.Layers.TryGetValue(layer, out var found) || found.For(camera.Zoom) is not { } detail) return null;
            var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var shape in detail.Shapes)
            {
                if (shape.MaxX < bounds.MinX || shape.MinX > bounds.MaxX || shape.MaxY < bounds.MinY || shape.MinY > bounds.MaxY) continue;
                for (var part = 0; part < shape.PartStarts.Length; part++)
                {
                    var start = shape.PartStarts[part];
                    var length = shape.PartLength(part);
                    path.MoveTo(X(shape.Points[start]), Y(shape.Points[start + 1]));
                    for (var i = 1; i < length; i++) path.LineTo(X(shape.Points[start + 2 * i]), Y(shape.Points[start + 2 * i + 1]));
                    if (close) path.Close();
                }
            }

            return path;
        }

        var fills = new SKBitmap(info);
        using (var canvas = new SKCanvas(fills))
        {
            canvas.Clear(palette.Water);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using (var land = Outline("land", close: true))
            {
                paint.Color = palette.Land;
                if (land is not null) canvas.DrawPath(land, paint);
            }

            using (var lakes = Outline("lakes", close: true))
            {
                paint.Color = palette.Water;
                if (lakes is not null) canvas.DrawPath(lakes, paint);
            }
        }

        var lines = new SKBitmap(info);
        using (var canvas = new SKCanvas(lines))
        {
            canvas.Clear(SKColors.Transparent);
            using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            var weight = (float)(scale * Math.Clamp(0.75 + (camera.Zoom - 4) * 0.12, 0.75, 1.5));

            void Stroke(string layer, SKColor colour, float width)
            {
                using var path = Outline(layer, close: false);
                if (path is null) return;
                pen.Color = colour;
                pen.StrokeWidth = width;
                canvas.DrawPath(path, pen);
            }

            Stroke("counties", palette.County, 0.6f * weight);
            Stroke("roads", palette.Road, 1.1f * weight);
            Stroke("states", palette.State, 0.9f * weight);
            Stroke("borders", palette.Border, 1.3f * weight);
        }

        return (fills, lines);
    }

    private static Bitmap ToAvalonia(SKBitmap bitmap, double scale)
        => new(PixelFormat.Bgra8888, AlphaFormat.Premul, bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height),
            new Vector(96 * scale, 96 * scale), bitmap.RowBytes);
}
