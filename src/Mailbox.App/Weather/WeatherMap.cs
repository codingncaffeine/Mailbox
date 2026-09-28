using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Localization;
using Mailbox.Core.Weather;
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
    private readonly MapWeatherLayers _layers = new();
    private readonly WrapPanel _chips = new() { Margin = new Thickness(10, 10, 60, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<string, Button> _chipButtons = new(StringComparer.Ordinal);
    private readonly TextBlock _note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, TextAlignment = TextAlignment.Center };
    private readonly Border _noteCard;
    private readonly Button _play;
    private readonly Avalonia.Controls.Shapes.Path _playGlyph = new() { Width = 12, Height = 12, Stretch = Stretch.Uniform };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 0, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _time = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 88 };
    private readonly TextBlock _credit = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.85 };
    private readonly Border _timeBar;
    private readonly MapLegendBar _legend = new();
    private readonly Border _legendCard;
    private Func<DateTimeOffset, DateTime> _local = moment => moment.LocalDateTime;
    private WeatherUnits _units = WeatherUnits.Metric;
    private bool _inUnitedStates;
    private bool _sliding;

    private static readonly Geometry PlayShape = Geometry.Parse("M 0,0 L 10,6 L 0,12 Z");
    private static readonly Geometry PauseShape = Geometry.Parse("M 0,0 H 3.5 V 12 H 0 Z M 6.5,0 H 10 V 12 H 6.5 Z");

    public WeatherMap()
    {
        ClipToBounds = true;
        Children.Add(_surface);
        _surface.DrawOverlays = _layers.Draw;
        _surface.CameraChanged += (_, _) => _layers.ViewChanged(_surface.Camera, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1, _inUnitedStates);
        _layers.Changed += (_, _) =>
        {
            _surface.InvalidateVisual();
            Refresh();
        };
        ResourcesChanged += (_, _) => _layers.Recolour(Inks());

        // The weather to show, one at a time, and the overlays over it.
        foreach (var choice in MapLayers.Choices) _chips.Children.Add(Chip(choice, () => _layers.Choose(_layers.Choice == choice ? null : choice)));
        _chips.Children.Add(new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0, 8, 4), [!Border.BackgroundProperty] = Brush(TokenKeys.Weather.CardBorder) });
        foreach (var overlay in MapLayers.Overlays) _chips.Children.Add(Chip(overlay.Id, () => _layers.Toggle(overlay.Id)));
        Children.Add(_chips);

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

        _note[!TextBlock.ForegroundProperty] = Brush(TokenKeys.Weather.CardText);
        _noteCard = Card(_note);
        _noteCard.HorizontalAlignment = HorizontalAlignment.Center;
        _noteCard.VerticalAlignment = VerticalAlignment.Center;
        _noteCard.IsVisible = false;
        Children.Add(_noteCard);

        _legendCard = Card(_legend);
        _legendCard.HorizontalAlignment = HorizontalAlignment.Left;
        _legendCard.VerticalAlignment = VerticalAlignment.Bottom;
        _legendCard.Margin = new Thickness(10, 0, 0, 60);
        _legendCard.Padding = new Thickness(12, 8);
        _legendCard.IsVisible = false;
        Children.Add(_legendCard);

        _playGlyph[!Avalonia.Controls.Shapes.Shape.FillProperty] = Brush(TokenKeys.Weather.CardText);
        _playGlyph.Data = PlayShape;
        _play = new Button { Width = 30, Height = 30, Padding = new Thickness(0), Classes = { "weatherchip" }, Content = _playGlyph, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_play, Strings.T("Play or pause"));
        _play.Click += (_, _) =>
        {
            if (_layers.Playing) _layers.Pause();
            else _layers.Play();
        };
        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _sliding) return;
            _layers.Pause();
            _layers.Show((int)Math.Round(_slider.Value));
        };
        _time[!TextBlock.ForegroundProperty] = Brush(TokenKeys.Weather.CardText);
        _credit[!TextBlock.ForegroundProperty] = Brush(TokenKeys.Weather.CardTextDim);

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        bar.Children.Add(_play);
        Grid.SetColumn(_slider, 1);
        bar.Children.Add(_slider);
        Grid.SetColumn(_time, 2);
        bar.Children.Add(_time);
        Grid.SetColumn(_credit, 3);
        bar.Children.Add(_credit);
        _timeBar = Card(bar);
        _timeBar.VerticalAlignment = VerticalAlignment.Bottom;
        _timeBar.Margin = new Thickness(10);
        _timeBar.Padding = new Thickness(8, 6);
        Children.Add(_timeBar);

        Refresh();
    }

    internal MapSurface Surface => _surface;

    internal MapWeatherLayers Layers => _layers;

    /// <summary>The reader's units, which the legend is labelled in.</summary>
    public WeatherUnits Units
    {
        get => _units;
        set
        {
            if (value == _units) return;
            _units = value;
            Refresh();
        }
    }

    /// <summary>
    /// Centres the map on a place and marks it, and chooses what to show there: radar where there
    /// is radar, precipitation where there is not, and the Weather Service's warnings in its own
    /// country.
    /// </summary>
    public void Show(double latitude, double longitude, bool inUnitedStates, Func<DateTimeOffset, DateTime> local, double zoom = 7)
    {
        _inUnitedStates = inUnitedStates;
        _local = local;
        _layers.Recolour(Inks());
        _surface.CenterOn(latitude, longitude, zoom);
        _layers.Choose(MapLayers.RadarCovers(latitude, longitude) ? "radar" : "precipitation");
        if (inUnitedStates != _layers.OverlaysOn.Contains("warnings")) _layers.Toggle("warnings");
        _layers.ViewChanged(_surface.Camera, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1, inUnitedStates);
    }

    private void Refresh()
    {
        foreach (var (id, button) in _chipButtons)
        {
            button.Classes.Set("selected", id == _layers.Choice || _layers.OverlaysOn.Contains(id));
        }

        _noteCard.IsVisible = _layers.Note.Length > 0;
        _note.Text = _layers.Note;

        var frames = _layers.Frames;
        _timeBar.IsVisible = _layers.Layer is not null && _layers.Note.Length == 0;
        _play.IsEnabled = frames.Count > 1;
        _playGlyph.Data = _layers.Playing ? PauseShape : PlayShape;
        _sliding = true;
        _slider.Maximum = Math.Max(0, frames.Count - 1);
        _slider.Value = _layers.FrameIndex;
        _slider.IsEnabled = frames.Count > 1;
        _sliding = false;

        if (_layers.Layer is { } layer && frames.Count > 0)
        {
            var at = _local(frames[Math.Clamp(_layers.FrameIndex, 0, frames.Count - 1)]);
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            _time.Text = layer.Kind == MapLayerKind.Forecast
                ? $"{at.ToString("ddd", culture)} {at.ToString("t", culture)}"
                : at.ToString("t", culture);
            var loading = _layers.Loaded < frames.Count ? $" · {Strings.T("loading")} {_layers.Loaded}/{frames.Count}" : string.Empty;
            _credit.Text = $"{MapLayers.Name(layer.Id)}: {layer.Credit}{loading} · {Strings.T("Map: Natural Earth, us-atlas")}";
        }
        else
        {
            _time.Text = string.Empty;
            _credit.Text = Strings.T("Map: Natural Earth, us-atlas");
        }

        _legendCard.IsVisible = _layers.Layer is { Legend: not null } && _layers.Note.Length == 0;
        if (_layers.Layer is { Legend: not null } legendFor) _legend.Show(legendFor, _units);
    }

    /// <summary>The theme's colours the layers' looks are drawn in, read here on the interface's thread.</summary>
    private MapInks Inks()
    {
        uint Of(string token) => this.TryFindResource(token + ".color", out var found) && found is Color c ? c.ToUInt32() : 0xFFFF00FF;
        return new MapInks(Of(TokenKeys.Weather.MapLabel), Of(TokenKeys.Weather.MapHalo), Of(TokenKeys.Weather.MapCloud));
    }

    private Button Chip(string id, Action press)
    {
        var button = new Button { Content = MapLayers.Name(id), Classes = { "weatherchip" }, Margin = new Thickness(0, 0, 6, 6) };
        Avalonia.Automation.AutomationProperties.SetName(button, MapLayers.Name(id));
        button.Click += (_, _) => press();
        _chipButtons[id] = button;
        return button;
    }

    private static Border Card(Control content)
    {
        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6), Child = content };
        card[!Border.BackgroundProperty] = Brush(TokenKeys.Weather.Card);
        card[!Border.BorderBrushProperty] = Brush(TokenKeys.Weather.CardBorder);
        return card;
    }

    private static DynamicResourceExtension Brush(string token) => new(token + ".brush");

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
        button[!BackgroundProperty] = Brush(TokenKeys.Weather.Card);
        button[!Button.BorderBrushProperty] = Brush(TokenKeys.Weather.CardBorder);
        button[!Button.ForegroundProperty] = Brush(TokenKeys.Weather.CardText);
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
