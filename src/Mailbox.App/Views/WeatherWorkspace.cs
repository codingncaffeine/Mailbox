using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Mailbox.App.Weather;
using Mailbox.Core.Localization;
using Mailbox.Core.Weather;
using Mailbox.Protocols;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>
/// The Weather module in the window: the reader's places down the side, and the chosen one's
/// weather beside them.
/// </summary>
/// <remarks>
/// The same shape the other modules have — a navigation pane the shell's own toggle hides, and a
/// page filling the rest. The places are the list; the first is home, marked as such, and a
/// place's row carries its conditions now so the list is worth glancing at on its own.
/// <para>
/// Nothing here fetches. The receiver does, on the pool, and says when a place changed; the
/// workspace redraws once per burst of those, on the interface's thread, from what the receiver
/// then holds.
/// </para>
/// </remarks>
public sealed class WeatherWorkspace : Border
{
    private readonly WeatherPlaces _places;
    private readonly WeatherReceiver _weather;
    private readonly Func<WeatherUnits> _units;
    private readonly Func<bool> _offline;
    private readonly Border _pane = new();
    private readonly ListBox _list = new();
    private readonly WeatherPage _page = new();
    private bool _redrawQueued;
    private bool _reloading;

    public WeatherWorkspace(WeatherPlaces places, WeatherReceiver weather, Func<WeatherUnits> units, Func<bool> offline)
    {
        _places = places ?? throw new ArgumentNullException(nameof(places));
        _weather = weather ?? throw new ArgumentNullException(nameof(weather));
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _offline = offline ?? throw new ArgumentNullException(nameof(offline));

        this[!MarginProperty] = new DynamicResourceExtension("workspace.inset.rightmargin");
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        this[!BackgroundProperty] = new DynamicResourceExtension(TokenKeys.Weather.Background + ".brush");
        Styles.Add(AccentButtonStyles());

        BuildPane();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_pane);
        Grid.SetColumn(_page, 1);
        grid.Children.Add(_page);
        Child = grid;

        _page.AddRequested += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);
        _page.RetryRequested += (_, _) => { if (Selected is { } place) UpdateRequested?.Invoke(this, place); };
        _page.LinkRequested += (_, url) => LinkRequested?.Invoke(this, url);

        _places.Changed += (_, _) => Dispatcher.UIThread.Post(Reload);
        _weather.Changed += (_, _) => QueueRedraw();

        Reload();
    }

    /// <summary>The place whose weather is showing, or null with no places kept.</summary>
    public WeatherPlace? Selected => _list.SelectedItem is PlaceRow row ? row.Place : null;

    /// <summary>Whether the navigation pane is showing, which the shell's own toggle drives.</summary>
    public bool IsNavVisible
    {
        get => _pane.IsVisible;
        set => _pane.IsVisible = value;
    }

    /// <summary>What the status bar says: how many places are kept, as the other modules count their items.</summary>
    public string Status => string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.T("Locations: {0}"), _places.All.Count);

    public event EventHandler? Changed;

    public event EventHandler? AddRequested;

    /// <summary>Update Now, or Try Again: fetch this place whether or not it is due.</summary>
    public event EventHandler<WeatherPlace>? UpdateRequested;

    /// <summary>A place came on screen: fetch whatever of it is due, and nothing that is not.</summary>
    public event EventHandler<WeatherPlace>? PlaceShown;

    public event EventHandler<WeatherPlace>? RemoveRequested;

    public event EventHandler<WeatherPlace>? MakeHomeRequested;

    public event EventHandler<(WeatherPlace Place, int By)>? MoveRequested;

    public event EventHandler<string>? LinkRequested;

    /// <summary>Puts the keyboard on the list of places, so the arrow keys move through them at once.</summary>
    public bool FocusSurface() => _list.Focus();

    /// <summary>Shows a place, as a click on its row does.</summary>
    public void Select(string placeId)
    {
        foreach (var item in _list.Items)
        {
            if (item is PlaceRow row && row.Place.Id == placeId)
            {
                _list.SelectedItem = row;
                return;
            }
        }
    }

    /// <summary>Scrolls the page, for a harness run photographing what is below the fold.</summary>
    public string PoseScroll(double offset) => _page.ScrollTo(offset);

    /// <summary>Scrolls to the map and waits for its first sharp frame, for a harness run.</summary>
    public async Task<string> PoseMapAsync(string? layer, string? overlays)
    {
        var said = _page.ScrollToMap();
        var weather = _page.Map.Layers;
        if (layer is { Length: > 0 }) weather.Choose(layer == "none" ? null : layer);
        foreach (var overlay in (overlays ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!weather.OverlaysOn.Contains(overlay)) weather.Toggle(overlay);
        }

        for (var i = 0; i < 60 && !_page.Map.Surface.HasFrame; i++) await Task.Delay(100);
        await Task.Delay(500);
        for (var i = 0; i < 300 && !weather.Settled; i++) await Task.Delay(100);
        return $"{said}; map {(_page.Map.Surface.HasFrame ? "painted" : "not painted")} at zoom {_page.Map.Surface.Camera.Zoom:0.#}; "
               + $"layer {weather.Choice ?? "none"} ({weather.Layer?.Layer ?? "-"}), {weather.Loaded}/{weather.Frames.Count} frame(s), "
               + $"overlays [{string.Join(",", weather.OverlaysOn)}]{(weather.Note.Length > 0 ? $", note “{weather.Note}”" : string.Empty)}";
    }

    /// <summary>Opens the page's expanders, for a harness run photographing them.</summary>
    public int PoseExpand() => _page.ExpandAll();

    /// <summary>Selects the place whose name carries the words, for a harness run.</summary>
    public string PoseSelect(string named)
    {
        foreach (var item in _list.Items)
        {
            if (item is PlaceRow row && row.Place.Label.Contains(named, StringComparison.OrdinalIgnoreCase))
            {
                _list.SelectedItem = row;
                return $"“{row.Place.Label}”";
            }
        }

        return $"no place matches “{named}” ({_places.All.Count} kept)";
    }

    /// <summary>Rebuilds the list from the places kept, keeping the selection where it was.</summary>
    public void Reload()
    {
        var keep = Selected?.Id;
        _reloading = true;
        try
        {
            _list.Items.Clear();
            for (var i = 0; i < _places.All.Count; i++) _list.Items.Add(new PlaceRow(_places.All[i], home: i == 0, this));

            var target = _list.Items.OfType<PlaceRow>().FirstOrDefault(r => r.Place.Id == keep)
                         ?? _list.Items.OfType<PlaceRow>().FirstOrDefault();
            _list.SelectedItem = target;
        }
        finally
        {
            _reloading = false;
        }

        Redraw();
    }

    private void QueueRedraw()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            Redraw();
        }, DispatcherPriority.Background);
    }

    /// <summary>Draws the rows' conditions and the chosen place's page from what the receiver holds now.</summary>
    private void Redraw()
    {
        var units = _units();
        foreach (var row in _list.Items.OfType<PlaceRow>()) row.Refresh(_weather.Get(row.Place.Id), units);

        if (Selected is { } place)
        {
            _page.Show(place, _weather.Get(place.Id), units, Mailbox.Core.PosedClock.UtcNow, _offline());
        }
        else
        {
            _page.ShowEmpty();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void BuildPane()
    {
        _pane.Width = this.TryFindResource("nav.width.value", out var width) && width is double w and > 0 ? w : 235;
        _pane[!BackgroundProperty] = new DynamicResourceExtension("nav.background.brush");

        var collapse = new Button
        {
            Classes = { "flat" },
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 4, 0),
            FontFamily = IconFont.Family,
            FontSize = 12,
            Content = IconGlyphs.GetOrEmpty("collapse-left", 16),
        };
        ToolTip.SetTip(collapse, Strings.T("Collapse the Folder Pane"));
        collapse.Click += (_, _) => IsNavVisible = false;

        var heading = new TextBlock { Text = Strings.T("Locations"), FontSize = 15, Margin = new Thickness(12, 4, 0, 6) };
        heading[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Margin = new Thickness(4, 0, 4, 0);
        Avalonia.Automation.AutomationProperties.SetName(_list, Strings.T("Locations"));
        _list.SelectionChanged += (_, _) =>
        {
            if (_reloading) return;
            Redraw();
            if (Selected is { } place) PlaceShown?.Invoke(this, place);
        };

        var add = new Button { Classes = { "flat" }, Margin = new Thickness(8, 6, 8, 8), HorizontalAlignment = HorizontalAlignment.Left };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var plus = new TextBlock { Text = IconGlyphs.GetOrEmpty("add", 16), FontFamily = IconFont.Family, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        plus[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        var addText = new TextBlock { Text = Strings.T("Add Location"), VerticalAlignment = VerticalAlignment.Center };
        addText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        addRow.Children.Add(plus);
        addRow.Children.Add(addText);
        add.Content = addRow;
        add.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);

        var top = new StackPanel();
        top.Children.Add(collapse);
        top.Children.Add(heading);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(add, Dock.Bottom);

        var dock = new DockPanel();
        dock.Children.Add(top);
        dock.Children.Add(add);
        dock.Children.Add(new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        _pane.Child = dock;
    }

    /// <summary>
    /// The page's call-to-action: the accent's own fill and ink, as a default button is drawn in
    /// the reference's dialogs, with its hover and pressed steps.
    /// </summary>
    private static Styles AccentButtonStyles()
    {
        static Setter Resource(AvaloniaProperty property, string key) => new(property, new DynamicResourceExtension(key));

        return
        [
            new Style(x => x.OfType<Button>().Class("weatheraccent"))
            {
                Setters =
                {
                    Resource(BackgroundProperty, "accent.rest.brush"),
                    Resource(Button.ForegroundProperty, "text.onaccent.brush"),
                    new Setter(Button.CornerRadiusProperty, new CornerRadius(6)),
                    new Setter(Button.BorderThicknessProperty, new Thickness(0)),
                    new Setter(Button.FontWeightProperty, FontWeight.SemiBold),
                    new Setter(Button.CursorProperty, new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)),
                },
            },
            new Style(x => x.OfType<Button>().Class("weatheraccent").Class(":pointerover"))
            {
                Setters = { Resource(BackgroundProperty, "accent.hover.brush") },
            },
            new Style(x => x.OfType<Button>().Class("weatheraccent").Class(":pressed"))
            {
                Setters = { Resource(BackgroundProperty, "accent.pressed.brush") },
            },

            // The map's layer chips: a card's ground at rest, the accent when chosen.
            new Style(x => x.OfType<Button>().Class("weatherchip"))
            {
                Setters =
                {
                    Resource(BackgroundProperty, "weather.card.brush"),
                    Resource(Button.BorderBrushProperty, "weather.card.border.brush"),
                    Resource(Button.ForegroundProperty, "weather.card.text.brush"),
                    new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                    new Setter(Button.CornerRadiusProperty, new CornerRadius(14)),
                    new Setter(Button.PaddingProperty, new Thickness(11, 4)),
                    new Setter(Button.FontSizeProperty, 12.0),
                    new Setter(Button.MinHeightProperty, 0.0),
                    new Setter(Button.CursorProperty, new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)),
                },
            },
            new Style(x => x.OfType<Button>().Class("weatherchip").Class(":pointerover"))
            {
                Setters = { Resource(BackgroundProperty, "state.hover.brush") },
            },
            new Style(x => x.OfType<Button>().Class("weatherchip").Class("selected"))
            {
                Setters =
                {
                    Resource(BackgroundProperty, "accent.rest.brush"),
                    Resource(Button.BorderBrushProperty, "accent.rest.brush"),
                    Resource(Button.ForegroundProperty, "text.onaccent.brush"),
                },
            },
            new Style(x => x.OfType<Button>().Class("weatherchip").Class("selected").Class(":pointerover"))
            {
                Setters = { Resource(BackgroundProperty, "accent.hover.brush") },
            },
        ];
    }

    /// <summary>A place in the list: its picture now, its name, what the sky is doing, and the temperature.</summary>
    private sealed class PlaceRow : Grid
    {
        private readonly Image _picture = new() { Width = 32, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock _name = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _sky = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _temperature = new() { FontSize = 18, FontWeight = FontWeight.Light, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
        private readonly bool _home;

        public PlaceRow(WeatherPlace place, bool home, WeatherWorkspace owner)
        {
            Place = place;
            _home = home;
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
            Height = 44;
            RenderOptions.SetBitmapInterpolationMode(_picture, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

            _name.Text = place.Name;
            _name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
            // The pane's own ink, quietened: the content's secondary grey is dark, and the pane is
            // dark in Dark Gray, where that grey would all but vanish.
            _sky[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
            _sky.Opacity = 0.72;
            _temperature[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(_name);
            names.Children.Add(_sky);

            Children.Add(_picture);
            Grid.SetColumn(names, 1);
            Children.Add(names);
            Grid.SetColumn(_temperature, 2);
            Children.Add(_temperature);

            Avalonia.Automation.AutomationProperties.SetName(this, place.FullName);
            ToolTip.SetTip(this, place.FullName);

            var menu = new ContextMenu();
            menu.Items.Add(Item(Strings.T("Update Now"), () => owner.UpdateRequested?.Invoke(owner, Place)));
            menu.Items.Add(Item(Strings.T("Set as Home"), () => owner.MakeHomeRequested?.Invoke(owner, Place), enabled: !home));
            menu.Items.Add(Item(Strings.T("Move Up"), () => owner.MoveRequested?.Invoke(owner, (Place, -1)), enabled: !home));
            menu.Items.Add(Item(Strings.T("Move Down"), () => owner.MoveRequested?.Invoke(owner, (Place, 1))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Strings.T("Remove Location"), () => owner.RemoveRequested?.Invoke(owner, Place)));
            ContextMenu = menu;
        }

        public WeatherPlace Place { get; }

        private static MenuItem Item(string header, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => run();
            return item;
        }

        public void Refresh(PlaceWeather weather, WeatherUnits units)
        {
            var key = weather.Forecast?.Current.Condition.Icon ?? WeatherArt.Placeholder;
            if (WeatherArt.Ready(key) is { } ready) _picture.Source = ready;
            else _ = Fill(key);

            var sky = weather.Forecast?.Current.Condition.Description ?? (weather.Updating ? Strings.T("Updating…") : string.Empty);
            _sky.Text = _home ? (sky.Length > 0 ? $"{Strings.T("Home")} · {sky}" : Strings.T("Home")) : sky;
            _temperature.Text = weather.Forecast is { } forecast ? units.FormatTemperature(forecast.Current.Temperature) : string.Empty;
        }

        private async Task Fill(string key)
        {
            if (await WeatherArt.LoadAsync(key) is { } bitmap) _picture.Source = bitmap;
        }
    }
}
