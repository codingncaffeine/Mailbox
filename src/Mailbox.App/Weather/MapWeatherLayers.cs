using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Localization;
using Mailbox.Core.Weather;
using SkiaSharp;

namespace Mailbox.App.Weather;

/// <summary>
/// A picture of a layer and the view it was asked for, at <paramref name="Scale"/> of the view's
/// pixels. <paramref name="Source"/> keeps what the service sent when the look is drawn in the
/// theme's colours, so a change of theme reworks the picture without asking again.
/// </summary>
internal sealed record MapPicture(MapCamera Camera, Bitmap Bitmap, MapLayer Layer, double Scale, byte[]? Source)
{
    /// <summary>Whether the picture is a grid drawn smooth up to the view rather than line work at its own size.</summary>
    public bool Smooth => Layer.GridMetres > 0;
}

/// <summary>
/// The weather drawn on the map: the one layer chosen, its frames in time, the overlays over it,
/// and the animation that plays the frames.
/// </summary>
/// <remarks>
/// Pictures are asked for once the view has settled — a drag in progress would otherwise ask for
/// a picture per pixel moved — and the frame on screen first, then the rest of the animation.
/// Until a new view's pictures arrive the old ones are drawn where they belong on it, so the
/// weather moves with the map rather than blinking out under the pointer. Decoding, and the
/// reworking a layer's look asks for (<see cref="MapPixels"/>), happen on the pool; nothing here
/// waits on the interface's thread.
/// </remarks>
internal sealed class MapWeatherLayers
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(350);

    private readonly DispatcherTimer _settle = new() { Interval = Settle };
    private readonly DispatcherTimer _play = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly Dictionary<DateTimeOffset, MapPicture> _frames = [];
    private readonly Dictionary<string, MapPicture> _overlays = new(StringComparer.Ordinal);
    private readonly HashSet<string> _overlaysOn = new(StringComparer.Ordinal);
    private CancellationTokenSource? _fetching;
    private MapCamera _camera;
    private double _scale = 1;
    private bool _inUnitedStates;
    private (double Latitude, double Longitude) _centre;
    private int _hold;
    private MapInks _inks = new(0xFFFF00FF, 0xFFFF00FF, 0xFFFF00FF);

    public MapWeatherLayers()
    {
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = RefreshAsync();
        };
        _play.Tick += (_, _) => Advance();
    }

    /// <summary>Something to draw changed: a picture arrived, a frame moved, a layer was chosen.</summary>
    public event EventHandler? Changed;

    /// <summary>The weather layer chosen, or null for the map alone.</summary>
    public string? Choice { get; private set; }

    public IReadOnlyCollection<string> OverlaysOn => _overlaysOn;

    /// <summary>The layer the choice stands for where the map is looking.</summary>
    public MapLayer? Layer => Choice is { } choice ? MapLayers.For(choice, _inUnitedStates) : null;

    public IReadOnlyList<DateTimeOffset> Frames { get; private set; } = [];

    public int FrameIndex { get; private set; }

    public bool Playing => _play.IsEnabled;

    /// <summary>What the map says instead of drawing, when the choice has nothing here: radar outside North America.</summary>
    public string Note { get; private set; } = string.Empty;

    /// <summary>How many frames of the animation have their pictures.</summary>
    public int Loaded => Frames.Count(_frames.ContainsKey);

    /// <summary>Whether everything asked for has arrived, for a harness shot to wait on.</summary>
    public bool Settled => _fetching is null && !_settle.IsEnabled;

    public void Choose(string? choice)
    {
        if (Choice == choice) return;
        Choice = choice;
        Pause();
        Frames = [];
        Clear(_frames);
        Changed?.Invoke(this, EventArgs.Empty);
        Soon();
    }

    public void Toggle(string overlay)
    {
        if (!_overlaysOn.Add(overlay))
        {
            _overlaysOn.Remove(overlay);
            if (_overlays.Remove(overlay, out var gone)) gone.Bitmap.Dispose();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Soon();
    }

    /// <summary>
    /// The theme's colours for the looks drawn in them. Pictures already held in the old colours
    /// are reworked from what the service sent, on the pool.
    /// </summary>
    public void Recolour(MapInks inks)
    {
        if (inks == _inks) return;
        _inks = inks;
        foreach (var (time, picture) in _frames.Where(p => p.Value.Source is not null).ToList()) _ = ReworkAsync(_frames, time, picture);
        foreach (var (id, picture) in _overlays.Where(p => p.Value.Source is not null).ToList()) _ = ReworkAsync(_overlays, id, picture);
    }

    private async Task ReworkAsync<TKey>(Dictionary<TKey, MapPicture> pictures, TKey key, MapPicture picture) where TKey : notnull
    {
        var inks = _inks;
        var redone = await Task.Run(() => Decode(picture.Source!, picture.Camera, picture.Layer, picture.Scale, inks));
        if (redone is null) return;
        if (!pictures.TryGetValue(key, out var held) || held != picture || inks != _inks)
        {
            redone.Bitmap.Dispose();
            return;
        }

        pictures[key] = redone;
        picture.Bitmap.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The view moved: its pictures are asked for once it has settled.</summary>
    public void ViewChanged(MapCamera camera, double scale, bool inUnitedStates)
    {
        _camera = camera;
        _scale = scale;
        _inUnitedStates = inUnitedStates;
        var (x, y) = (camera.CenterX, camera.CenterY);
        _centre = MapCamera.Unproject(x, y);
        Soon();
    }

    public void Play()
    {
        if (Frames.Count < 2) return;
        _play.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        _play.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Show(int index)
    {
        if (Frames.Count == 0) return;
        FrameIndex = Math.Clamp(index, 0, Frames.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The next frame that has its picture, holding a moment on the last before starting over.</summary>
    private void Advance()
    {
        if (Frames.Count < 2) return;
        if (FrameIndex == Frames.Count - 1 && _hold++ < 2) return;
        _hold = 0;
        for (var step = 1; step <= Frames.Count; step++)
        {
            var next = (FrameIndex + step) % Frames.Count;
            if (!_frames.ContainsKey(Frames[next])) continue;
            FrameIndex = next;
            break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Soon()
    {
        _settle.Stop();
        _settle.Start();
    }

    private static void Clear<TKey>(Dictionary<TKey, MapPicture> pictures) where TKey : notnull
    {
        foreach (var picture in pictures.Values) picture.Bitmap.Dispose();
        pictures.Clear();
    }

    private async Task RefreshAsync()
    {
        if (_camera.Width < 8 || _camera.Height < 8) return;
        if (_fetching is { } previous) await previous.CancelAsync();
        var fetching = new CancellationTokenSource();
        _fetching = fetching;
        var camera = _camera;
        var token = fetching.Token;
        var inks = _inks;
        var (left, top, right, bottom) = camera.Visible;

        // Ground metres across one of the view's pixels where it is looking, which decides how
        // finely a gridded layer is worth asking for.
        var metresPerPixel = 2 * MapLayers.MercatorHalfWorld / camera.WorldPixels * Math.Cos(_centre.Latitude * Math.PI / 180);
        (int Width, int Height, double Scale) Size(MapLayer layer)
        {
            var scale = MapLayers.RequestScale(layer, _scale, metresPerPixel);
            var width = (int)Math.Clamp(Math.Round(camera.Width * scale), 8, 2048);
            var height = (int)Math.Clamp(Math.Round(camera.Height * scale), 8, 2048);
            return (width, height, width / camera.Width);
        }

        try
        {
            if (Layer is { } layer)
            {
                if (Choice == "radar" && !MapLayers.RadarCovers(_centre.Latitude, _centre.Longitude))
                {
                    Note = Strings.T("Radar covers North America. Precipitation and Satellite show the rest of the world.");
                    Frames = [];
                    Clear(_frames);
                }
                else
                {
                    Note = string.Empty;
                    var times = await App.WeatherMap.TimesAsync(layer, token);
                    var frames = times.Count > 0 ? MapLayers.Frames(layer.Kind, times, DateTimeOffset.UtcNow) : [];
                    if (!frames.SequenceEqual(Frames))
                    {
                        Frames = frames;
                        FrameIndex = layer.Kind == MapLayerKind.Observation ? Math.Max(0, frames.Count - 1) : 0;
                        foreach (var stale in _frames.Keys.Where(k => !frames.Contains(k)).ToList())
                        {
                            _frames[stale].Bitmap.Dispose();
                            _frames.Remove(stale);
                        }
                    }

                    Changed?.Invoke(this, EventArgs.Empty);
                    var (width, height, scale) = Size(layer);
                    var order = Enumerable.Range(0, Frames.Count).OrderBy(i => i == FrameIndex ? 0 : 1).ThenBy(i => Math.Abs(i - FrameIndex)).ToList();
                    foreach (var i in order)
                    {
                        token.ThrowIfCancellationRequested();
                        var time = Frames[i];
                        if (_frames.TryGetValue(time, out var have) && have.Camera == camera && have.Layer == layer) continue;
                        if (await Fetch(MapLayers.GetMapUrl(layer, left, top, right, bottom, width, height, time), camera, layer, scale, inks, token) is { } picture)
                        {
                            if (_frames.Remove(time, out var old)) old.Bitmap.Dispose();
                            _frames[time] = picture;
                            Changed?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
            }

            foreach (var overlay in MapLayers.Overlays.Where(o => _overlaysOn.Contains(o.Id)).ToList())
            {
                token.ThrowIfCancellationRequested();
                var times = await App.WeatherMap.TimesAsync(overlay, token);
                var at = times.Count > 0 ? MapLayers.Frames(MapLayerKind.Overlay, times, DateTimeOffset.UtcNow)[0] : (DateTimeOffset?)null;
                var (width, height, scale) = Size(overlay);
                if (await Fetch(MapLayers.GetMapUrl(overlay, left, top, right, bottom, width, height, at), camera, overlay, scale, inks, token) is { } picture)
                {
                    if (_overlays.Remove(overlay.Id, out var old)) old.Bitmap.Dispose();
                    _overlays[overlay.Id] = picture;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The view moved on, or the layer changed; the newer refresh has it.
        }
        finally
        {
            if (_fetching == fetching) _fetching = null;
            fetching.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<MapPicture?> Fetch(string url, MapCamera camera, MapLayer layer, double scale, MapInks inks, CancellationToken token)
    {
        var bytes = await App.WeatherMap.ImageAsync(url, token);
        if (bytes is null) return null;
        return await Task.Run(() => Decode(bytes, camera, layer, scale, inks), token);
    }

    /// <summary>Decodes a picture to premultiplied pixels, reworks them by the layer's look, and hands them over as a bitmap.</summary>
    private static MapPicture? Decode(byte[] bytes, MapCamera camera, MapLayer layer, double scale, MapInks inks)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                Log.Warn($"A weather map picture of {layer.Layer} was not an image.");
                return null;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var pixels = new byte[info.BytesSize];
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var result = codec.GetPixels(info, pinned.AddrOfPinnedObject());
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    Log.Warn($"A weather map picture of {layer.Layer} could not be decoded: {result}.");
                    return null;
                }

                MapPixels.Apply(pixels, info.Width, info.Height, layer, inks, scale);
                var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pinned.AddrOfPinnedObject(),
                    new PixelSize(info.Width, info.Height), new Vector(96, 96), info.RowBytes);
                return new MapPicture(camera, bitmap, layer, scale, MapPixels.FollowsTheme(layer.Look) ? bytes : null);
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            Log.Warn("A weather map picture could not be decoded.", ex);
            return null;
        }
    }

    /// <summary>Draws the frame on screen and the overlays, each where its own view puts it on this one.</summary>
    public void Draw(DrawingContext context, MapCamera current)
    {
        if (Layer is { } layer && Frames.Count > 0 && _frames.TryGetValue(Frames[Math.Clamp(FrameIndex, 0, Frames.Count - 1)], out var frame))
        {
            Place(context, current, frame, layer.Opacity);
        }
        else if (Layer is { } shown && _frames.Count > 0)
        {
            // The frame on screen has not arrived yet: the nearest one that has stands in.
            Place(context, current, _frames.Values.Last(), shown.Opacity);
        }

        foreach (var overlay in MapLayers.Overlays)
        {
            if (_overlaysOn.Contains(overlay.Id) && _overlays.TryGetValue(overlay.Id, out var picture)) Place(context, current, picture, overlay.Opacity);
        }
    }

    private static void Place(DrawingContext context, MapCamera current, MapPicture picture, double opacity)
    {
        var (left, top) = picture.Camera.ToWorld(new Point(0, 0));
        var corner = current.ToScreen(left, top);
        var scale = Math.Pow(2, current.Zoom - picture.Camera.Zoom);
        var place = new Rect(corner.X, corner.Y, picture.Camera.Width * scale, picture.Camera.Height * scale);

        // A grid asked for at a pixel to a cell is drawn up to the view with the smoothest filter
        // there is, which turns its cells into a field.
        var filter = picture.Smooth ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.MediumQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = filter }))
        using (context.PushOpacity(opacity))
        {
            context.DrawImage(picture.Bitmap, new Rect(picture.Bitmap.Size), place);
        }
    }
}
