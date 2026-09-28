using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Localization;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Tokens;
using SkiaSharp;

namespace Mailbox.App.Weather;

/// <summary>
/// The map: the base map in the theme's colours, dragged and zoomed by the reader, with the place
/// marked and the nearby towns named.
/// </summary>
/// <remarks>
/// The drawing is split the way the interface thread needs it split. The polygons and lines —
/// hundreds of thousands of points at a regional zoom — are painted by <see cref="MapPainter"/>
/// on the pool into bitmaps at device resolution; while a new paint is under way the last one is
/// moved and scaled with the camera, so a drag follows the pointer at once and sharpens a moment
/// later. The town names, a few dozen at most, are set here as text, in the theme's own face,
/// above whatever the weather layers draw.
/// </remarks>
internal sealed class WeatherMap : Panel
{
    private readonly MapSurface _surface = new();

    public WeatherMap()
    {
        ClipToBounds = true;
        Children.Add(_surface);

        var controls = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10),
        };
        controls.Children.Add(MapButton("zoom-in", Strings.T("Zoom in"), () => _surface.ZoomBy(1)));
        controls.Children.Add(MapButton("zoom-out", Strings.T("Zoom out"), () => _surface.ZoomBy(-1)));
        controls.Children.Add(MapButton("location", Strings.T("Back to the place"), () => _surface.Recenter()));
        Children.Add(controls);

        var credit = new TextBlock
        {
            Text = Strings.T("Map: Natural Earth · US Census via us-atlas"),
            FontSize = 10,
            Margin = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0.8,
        };
        credit[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(TokenKeys.Weather.MapLabel + ".brush");
        Children.Add(credit);
    }

    /// <summary>Centres the map on a place and marks it.</summary>
    public void Show(double latitude, double longitude, double zoom = 7) => _surface.CenterOn(latitude, longitude, zoom);

    internal MapSurface Surface => _surface;

    private static Control MapButton(string glyph, string tip, Action press)
    {
        var button = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty(glyph, 20),
                FontFamily = IconFont.Family,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        button[!BackgroundProperty] = new DynamicResourceExtension(TokenKeys.Weather.Card + ".brush");
        button[!Button.BorderBrushProperty] = new DynamicResourceExtension(TokenKeys.Weather.CardBorder + ".brush");
        button[!Button.ForegroundProperty] = new DynamicResourceExtension(TokenKeys.Weather.CardText + ".brush");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => press();
        return button;
    }
}

/// <summary>The map's drawing surface and its input: drag to pan, wheel or double-click to zoom, keys for both.</summary>
internal sealed class MapSurface : Control
{
    private MapCamera _camera = new(0.5, 0.5, 3, 1, 1);
    private MapFrame? _frame;
    private BaseMap? _map;
    private bool _painting;
    private bool _again;
    private (double X, double Y)? _pin;
    private double _pinZoom = 7;
    private Point? _dragFrom;
    private readonly Dictionary<string, FormattedText> _labels = new(StringComparer.Ordinal);

    public MapSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        ResourcesChanged += (_, _) =>
        {
            _labels.Clear();
            Repaint();
        };
        _ = LoadAsync();
    }

    public MapCamera Camera => _camera;

    /// <summary>Raised when the view moves, for the weather layers to follow.</summary>
    public event EventHandler? CameraChanged;

    /// <summary>Raised after a paint lands, so a harness shot can wait for a sharp frame.</summary>
    public event EventHandler? Painted;

    public bool HasFrame => _frame is not null && _frame.Camera == _camera;

    private async Task LoadAsync()
    {
        _map = await BaseMap.LoadAsync();
        Repaint();
    }

    public void CenterOn(double latitude, double longitude, double zoom)
    {
        var (x, y) = MapCamera.Project(latitude, longitude);
        _pin = (x, y);
        _pinZoom = zoom;
        Move(_camera with { CenterX = x, CenterY = y, Zoom = Math.Clamp(zoom, MapCamera.MinimumZoom, MapCamera.MaximumZoom) });
    }

    public void Recenter()
    {
        if (_pin is { } pin) Move(_camera with { CenterX = pin.X, CenterY = pin.Y, Zoom = _pinZoom });
    }

    public void ZoomBy(double steps) => Move(_camera.ZoomAround(new Point(_camera.Width / 2, _camera.Height / 2), steps));

    private void Move(MapCamera camera)
    {
        if (camera == _camera) return;
        _camera = camera;
        InvalidateVisual();
        CameraChanged?.Invoke(this, EventArgs.Empty);
        Repaint();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Move(_camera with { Width = Math.Max(1, e.NewSize.Width), Height = Math.Max(1, e.NewSize.Height) });
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(this);
        Move(_camera.PanBy(now.X - from.X, now.Y - from.Y));
        _dragFrom = now;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // The wheel zooms the map rather than scrolling the page under it, as every map does.
        Move(_camera.ZoomAround(e.GetPosition(this), Math.Clamp(e.Delta.Y, -2, 2) * 0.5));
        e.Handled = true;
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        Move(_camera.ZoomAround(e.GetPosition(this), 1));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        const double Step = 80;
        MapCamera? moved = e.Key switch
        {
            Key.Add or Key.OemPlus => _camera.ZoomAround(new Point(_camera.Width / 2, _camera.Height / 2), 1),
            Key.Subtract or Key.OemMinus => _camera.ZoomAround(new Point(_camera.Width / 2, _camera.Height / 2), -1),
            Key.Left => _camera.PanBy(Step, 0),
            Key.Right => _camera.PanBy(-Step, 0),
            Key.Up => _camera.PanBy(0, Step),
            Key.Down => _camera.PanBy(0, -Step),
            _ => null,
        };
        if (moved is not { } camera) return;
        Move(camera);
        e.Handled = true;
    }

    /// <summary>Paints the camera on the pool; one paint at a time, the latest camera next.</summary>
    private void Repaint()
    {
        if (_map is not { } map || _camera.Width < 2 || _camera.Height < 2) return;
        if (_painting)
        {
            _again = true;
            return;
        }

        _painting = true;
        var camera = _camera;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var palette = Palette();
        _ = Task.Run(() => MapPainter.Paint(map, camera, scale, palette)).ContinueWith(done => Dispatcher.UIThread.Post(() =>
        {
            _painting = false;
            if (done.IsCompletedSuccessfully)
            {
                var old = _frame;
                _frame = done.Result;
                old?.Dispose();
                InvalidateVisual();
                Painted?.Invoke(this, EventArgs.Empty);
            }
            else if (done.Exception is { } failure)
            {
                Log.Warn("The weather map could not be painted.", failure);
            }

            if (_again)
            {
                _again = false;
                Repaint();
            }
        }), TaskScheduler.Default);
    }

    private MapPalette Palette()
    {
        SKColor Of(string token)
        {
            var colour = this.TryFindResource(token + ".color", out var found) && found is Color c ? c : Colors.Magenta;
            return new SKColor(colour.R, colour.G, colour.B, colour.A);
        }

        return new MapPalette(
            Of(TokenKeys.Weather.MapWater), Of(TokenKeys.Weather.MapLand), Of(TokenKeys.Weather.MapBorder),
            Of(TokenKeys.Weather.MapState), Of(TokenKeys.Weather.MapCounty), Of(TokenKeys.Weather.MapRoad));
    }

    private Color Colour(string token) => this.TryFindResource(token + ".color", out var found) && found is Color c ? c : Colors.Magenta;

    /// <summary>Where a frame painted for another camera lands on this one.</summary>
    private Rect Placed(MapFrame frame)
    {
        var (left, top) = frame.Camera.ToWorld(new Point(0, 0));
        var corner = _camera.ToScreen(left, top);
        var scale = Math.Pow(2, _camera.Zoom - frame.Camera.Zoom);
        return new Rect(corner.X, corner.Y, frame.Camera.Width * scale, frame.Camera.Height * scale);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Colour(TokenKeys.Weather.MapWater)), bounds);

        if (_frame is { } frame)
        {
            var place = Placed(frame);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality }))
            {
                context.DrawImage(frame.Fills, new Rect(frame.Fills.Size), place);
                DrawOverlays?.Invoke(context, _camera);
                context.DrawImage(frame.Lines, new Rect(frame.Lines.Size), place);
            }
        }

        DrawPlaces(context);
        DrawPin(context);
    }

    /// <summary>What the weather layers draw between the fills and the lines.</summary>
    public Action<DrawingContext, MapCamera>? DrawOverlays { get; set; }

    private void DrawPin(DrawingContext context)
    {
        if (_pin is not { } pin) return;
        var at = _camera.ToScreen(pin.X, pin.Y);
        context.DrawEllipse(new SolidColorBrush(Colour(TokenKeys.Weather.Card)), null, at, 9, 9);
        context.DrawEllipse(new SolidColorBrush(Colour(TokenKeys.Accent.Rest)), null, at, 6, 6);
    }

    /// <summary>
    /// The towns worth naming at this zoom, most important first, each set where it does not
    /// collide with one already set — the name to the right of its dot, on a halo of the land's
    /// colour so it reads over any weather.
    /// </summary>
    private void DrawPlaces(DrawingContext context)
    {
        if (_map is not { } map) return;
        var ink = new SolidColorBrush(Colour(TokenKeys.Weather.MapLabel));
        var halo = new SolidColorBrush(Colour(TokenKeys.Weather.MapHalo));
        var placed = new List<Rect>();
        if (_pin is { } pin) placed.Add(new Rect(_camera.ToScreen(pin.X, pin.Y) - new Point(10, 10), new Size(20, 20)));

        foreach (var place in map.Places)
        {
            if (place.MinZoom > _camera.Zoom + 0.6) break;
            var at = _camera.ToScreen(place.X / BaseMap.Quantum, place.Y / BaseMap.Quantum);
            if (at.X < -40 || at.Y < -20 || at.X > _camera.Width + 10 || at.Y > _camera.Height + 20) continue;

            if (!_labels.TryGetValue(place.Name, out var text))
            {
                var face = this.TryFindResource("ui.fontfamily", out var family) && family is FontFamily ui ? new Typeface(ui) : Typeface.Default;
                text = new FormattedText(place.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 11, ink);
                _labels[place.Name] = text;
            }

            var box = new Rect(at.X + 5, at.Y - text.Height / 2, text.Width, text.Height).Inflate(3);
            if (placed.Any(r => r.Intersects(box))) continue;
            placed.Add(box);

            context.DrawEllipse(halo, null, at, 3.2, 3.2);
            context.DrawEllipse(ink, null, at, 2, 2);
            var origin = new Point(at.X + 6, at.Y - text.Height / 2);
            text.SetForegroundBrush(halo);
            foreach (var (dx, dy) in new[] { (-1.0, 0.0), (1.0, 0.0), (0.0, -1.0), (0.0, 1.0) }) context.DrawText(text, origin + new Point(dx, dy));
            text.SetForegroundBrush(ink);
            context.DrawText(text, origin);
            if (placed.Count > 80) break;
        }
    }
}
