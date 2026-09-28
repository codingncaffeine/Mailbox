using System.Globalization;
using System.Xml.Linq;
using Mailbox.Core.Localization;

namespace Mailbox.Core.Weather;

/// <summary>What a map layer shows, which decides how its time runs.</summary>
public enum MapLayerKind
{
    /// <summary>What was measured: radar, satellite — the recent past, played up to now.</summary>
    Observation,

    /// <summary>What a model expects: temperature, wind — from now into the coming days.</summary>
    Forecast,

    /// <summary>Drawn over the others at their latest: warnings, lightning, isobars.</summary>
    Overlay,
}

/// <summary>Where a layer is drawn from: a Web Map Service endpoint and the layer's name there.</summary>
public sealed record MapLayer
{
    public required string Id { get; init; }
    public required MapLayerKind Kind { get; init; }
    public required string Endpoint { get; init; }
    public required string Layer { get; init; }
    public string Style { get; init; } = string.Empty;

    /// <summary>Where the layer's capabilities — its times — are asked for.</summary>
    public required string Capabilities { get; init; }

    /// <summary>How opaque the layer is drawn over the map.</summary>
    public double Opacity { get; init; } = 0.85;

    /// <summary>How its pictures are reworked before they are drawn.</summary>
    public MapLook Look { get; init; } = MapLook.AsServed;

    /// <summary>
    /// The size of the grid the values come on, in metres on the ground, or 0 for line work drawn
    /// at the view's own resolution. A picture is asked for at about a pixel to a grid cell — one
    /// sample of each — and drawn smoothly up to the view, so a model's fifteen-kilometre cells
    /// read as a field rather than as tiles, and cost the service a picture a fraction of the size.
    /// </summary>
    public double GridMetres { get; init; }

    /// <summary>The colour scale the map draws the legend from, or null for a layer without one.</summary>
    public MapLegend? Legend { get; init; }

    /// <summary>Who provides it, for the credit under the map.</summary>
    public required string Credit { get; init; }
}

/// <summary>
/// The map's weather layers, from two public services that need no key: NOAA's nowCOAST (radar
/// for the United States and its territories, satellite for the whole world, lightning, the
/// Weather Service's warnings) and Environment and Climate Change Canada's GeoMet (radar for
/// North America, and the global model's temperature, wind, precipitation, cloud and pressure).
/// </summary>
/// <remarks>
/// One weather layer at a time, as weather maps show them — a radar and a temperature field drawn
/// over each other are two colour scales fighting — with the overlays, which are lines and
/// outlines rather than fields, over whichever it is.
/// <para>
/// Every picture is asked for as one image of the view, in Web Mercator, rather than as tiles: a
/// reader's view costs one request per frame, and an animation a dozen, which keeps each reader
/// far inside what the services are built to serve.
/// </para>
/// <para>
/// Each GeoMet layer names its style rather than taking the service's default, because the map
/// draws its legends itself, from the colours of those styles (see <see cref="MapLegend"/>).
/// </para>
/// </remarks>
public static class MapLayers
{
    public const string NowCoast = "https://nowcoast.noaa.gov/geoserver/ows";
    public const string GeoMet = "https://geo.weather.gc.ca/geomet";

    private const string NowCoastCredit = "NOAA nowCOAST";
    private const string GeoMetCredit = "Environment and Climate Change Canada";

    /// <summary>
    /// nowCOAST's base reflectivity, from its own legend's colour map — a ramp from 1 to 80 dBZ,
    /// read here every 2.5 dBZ from 5 to 75. The faint grey of the lowest returns is the service's.
    /// </summary>
    private static readonly MapLegend ReflectivityLegend = new(MapQuantity.Reflectivity,
    [
        new(5, 0x66777777), new(7.5, 0xFF43AAAA), new(10, 0xFF00ECEC), new(12.5, 0xFF00CBF0), new(15, 0xFF01A0F6),
        new(17.5, 0xFF005AF6), new(20, 0xFF0000F6), new(22.5, 0xFF006F8A), new(25, 0xFF00FF00), new(27.5, 0xFF00E700),
        new(30, 0xFF00C800), new(32.5, 0xFF00B000), new(35, 0xFF009000), new(37.5, 0xFF6FC000), new(40, 0xFFFFFF00),
        new(42.5, 0xFFF5E300), new(45, 0xFFE7C000), new(47.5, 0xFFF2AB00), new(50, 0xFFFF9000), new(52.5, 0xFFFF5100),
        new(55, 0xFFFF0000), new(57.5, 0xFFEF0000), new(60, 0xFFDC0000), new(62.5, 0xFFD00000), new(65, 0xFFC00000),
        new(67.5, 0xFFDB006F), new(70, 0xFFFF00FF), new(72.5, 0xFFD225E7), new(75, 0xFF9955C9),
    ]);

    /// <summary>GeoMet's <c>Radar-Rain_14colors</c>: fourteen blocks of rain rate, from 0.1 mm an hour.</summary>
    private static readonly MapLegend RainRateLegend = new(MapQuantity.PrecipitationRate,
    [
        new(0.1, 0xFF53B4FE), new(1, 0xFF00D4A5), new(2, 0xFF00DC22), new(4, 0xFF00A700), new(8, 0xFF007000),
        new(12, 0xFFE9F100), new(16, 0xFFFECB00), new(24, 0xFFFE9800), new(32, 0xFFFE5D00), new(50, 0xFFFE0019),
        new(64, 0xFFE010A7), new(100, 0xFF8520B8), new(125, 0xFF500079), new(200, 0xFF350050),
    ], Stepped: true);

    /// <summary>GeoMet's <c>TEMPERATURE-LINEAR</c>, −40 to 40 °C; it blends near enough straight between these.</summary>
    private static readonly MapLegend TemperatureLegend = new(MapQuantity.Temperature,
    [
        new(-40, 0xFF000081), new(-35, 0xFF0000B4), new(-30, 0xFF0000EC), new(-25, 0xFF003CFC), new(-20, 0xFF008FFD),
        new(-15, 0xFF00E3FD), new(-10, 0xFF33FC86), new(-5, 0xFF77FC86), new(0, 0xFFB7FD46), new(5, 0xFFFBFE03),
        new(10, 0xFFFDBD00), new(15, 0xFFFD7900), new(20, 0xFFFD3200), new(25, 0xFFEE0000), new(30, 0xFFB60000),
        new(35, 0xFF7F0000), new(40, 0xFF580000),
    ]);

    /// <summary>GeoMet's <c>WINDSPEEDKNOTS-LINEAR</c>, 0 to 55 knots.</summary>
    private static readonly MapLegend WindLegend = new(MapQuantity.WindSpeed,
    [
        new(0, 0xFF000082), new(2.5, 0xFF0000A4), new(5, 0xFF0002CC), new(7.5, 0xFF0017E8), new(10, 0xFF0036FD),
        new(12.5, 0xFF0074FD), new(15, 0xFF03AFFA), new(17.5, 0xFF0AE8F3), new(20, 0xFF2CFCD1), new(22.5, 0xFF5EFC9F),
        new(25, 0xFF90FC6D), new(27.5, 0xFFC2FD3B), new(30, 0xFFE2EB1B), new(32.5, 0xFFFDD400), new(35, 0xFFFD9F00),
        new(37.5, 0xFFFD7100), new(40, 0xFFFD3B00), new(42.5, 0xFFF21900), new(45, 0xFFD50700), new(47.5, 0xFFB10000),
        new(50, 0xFF850000), new(52.5, 0xFF6D0000), new(55, 0xFF560000),
    ]);

    /// <summary>GeoMet's <c>PRECIPPRTMMH-LINEAR</c>: 0.01 to 50 mm an hour, each class blending straight to the next.</summary>
    private static readonly MapLegend PrecipitationLegend = new(MapQuantity.PrecipitationRate,
    [
        new(0.01, 0xFF00007F), new(0.05, 0xFF0000E3), new(0.1, 0xFF0078FC), new(0.5, 0xFF19FCE4), new(1, 0xFF98FD65),
        new(5, 0xFFFDE400), new(10, 0xFFFD6500), new(20, 0xFFE70000), new(50, 0xFF7F0000),
    ]);

    /// <summary>GeoMet's <c>CLOUD</c>, grey from 0 to 100 %; the map redraws it as cloud in the theme's colour.</summary>
    private static readonly MapLegend CloudLegend = new(MapQuantity.CloudCover,
    [
        new(0, 0xFF202020), new(10, 0xFF292929), new(20, 0xFF454545), new(30, 0xFF575757), new(40, 0xFF737373),
        new(50, 0xFF868686), new(60, 0xFFA1A1A1), new(70, 0xFFBDBDBD), new(80, 0xFFD0D0D0), new(90, 0xFFEBEBEB),
        new(100, 0xFFFEFEFE),
    ]);

    public static readonly MapLayer RadarUnitedStates = new()
    {
        Id = "radar", Kind = MapLayerKind.Observation, Endpoint = NowCoast,
        Layer = "weather_radar:base_reflectivity_mosaic", Style = "weather_radar_base_reflectivity",
        Capabilities = "https://nowcoast.noaa.gov/geoserver/weather_radar/base_reflectivity_mosaic/ows?service=WMS&version=1.3.0&request=GetCapabilities",
        GridMetres = 1000, Legend = ReflectivityLegend,
        Credit = NowCoastCredit,
    };

    public static readonly MapLayer RadarNorthAmerica = new()
    {
        Id = "radar", Kind = MapLayerKind.Observation, Endpoint = GeoMet, Layer = "RADAR_1KM_RRAI", Style = "Radar-Rain_14colors",
        Capabilities = $"{GeoMet}?service=WMS&version=1.3.0&request=GetCapabilities&layer=RADAR_1KM_RRAI",
        GridMetres = 1000, Legend = RainRateLegend,
        Credit = GeoMetCredit,
    };

    public static readonly MapLayer Satellite = new()
    {
        Id = "satellite", Kind = MapLayerKind.Observation, Endpoint = NowCoast,
        Layer = "satellite:global_longwave_imagery_mosaic", Opacity = 0.75,
        Capabilities = "https://nowcoast.noaa.gov/geoserver/satellite/global_longwave_imagery_mosaic/ows?service=WMS&version=1.3.0&request=GetCapabilities",
        GridMetres = 2000,
        Credit = NowCoastCredit,
    };

    public static readonly MapLayer Temperature = Model("temperature", "GDPS_15km_AirTemp_2m", "TEMPERATURE-LINEAR", 0.7, TemperatureLegend);
    public static readonly MapLayer Wind = Model("wind", "GDPS_15km_WindSpeed_10m", "WINDSPEEDKNOTS-LINEAR", 0.7, WindLegend);
    public static readonly MapLayer Precipitation = Model("precipitation", "GDPS_15km_PrecipRate", "PRECIPPRTMMH-LINEAR", 0.85, PrecipitationLegend) with { Look = MapLook.Faded };
    public static readonly MapLayer Clouds = Model("clouds", "GDPS_15km_TotalCloudCover", "CLOUD", 1, CloudLegend) with { Look = MapLook.Tinted };

    /// <summary>
    /// The Weather Service's warnings, watches and advisories. The service fills each area solid in
    /// its hazard's colour; the map draws the outline and a light wash, so the radar under a watch
    /// the size of a state still shows.
    /// </summary>
    public static readonly MapLayer Warnings = new()
    {
        Id = "warnings", Kind = MapLayerKind.Overlay, Endpoint = NowCoast, Layer = "alerts:watches_warnings_advisories",
        Opacity = 0.95, Look = MapLook.Outlined,
        Capabilities = string.Empty,
        Credit = NowCoastCredit,
    };

    public static readonly MapLayer Lightning = new()
    {
        Id = "lightning", Kind = MapLayerKind.Overlay, Endpoint = NowCoast, Layer = "lightning_detection:ldn_lightning_strike_density",
        Opacity = 0.9,
        Capabilities = "https://nowcoast.noaa.gov/geoserver/lightning_detection/ldn_lightning_strike_density/ows?service=WMS&version=1.3.0&request=GetCapabilities",
        Credit = NowCoastCredit,
    };

    /// <summary>Isobars every 4 hPa, as surface analyses draw them, in the map's own ink.</summary>
    public static readonly MapLayer Pressure = Model("pressure", "GDPS_15km_Pressure_MSL-Contour", "SeaLevelPressure_4mb", 0.8, null)
        with { Kind = MapLayerKind.Overlay, Look = MapLook.Inked, GridMetres = 0 };

    /// <summary>The weather a reader chooses between, one at a time.</summary>
    public static IReadOnlyList<string> Choices { get; } = ["radar", "satellite", "temperature", "wind", "precipitation", "clouds"];

    /// <summary>The overlays a reader turns on and off over it.</summary>
    public static IReadOnlyList<MapLayer> Overlays { get; } = [Warnings, Lightning, Pressure];

    /// <summary>A field of the Global Deterministic Prediction System, GeoMet's worldwide model, on its 15 km grid.</summary>
    private static MapLayer Model(string id, string layer, string style, double opacity, MapLegend? legend) => new()
    {
        Id = id, Kind = MapLayerKind.Forecast, Endpoint = GeoMet, Layer = layer, Style = style, Opacity = opacity,
        Capabilities = $"{GeoMet}?service=WMS&version=1.3.0&request=GetCapabilities&layer={layer}",
        GridMetres = 15000, Legend = legend,
        Credit = GeoMetCredit,
    };

    /// <summary>
    /// The layer for a choice where the reader is looking. Radar is the Weather Service's mosaic
    /// over the United States and its territories, and Canada's North American composite anywhere
    /// else — which covers Canada, and nothing beyond North America, where no free radar exists.
    /// </summary>
    public static MapLayer For(string choice, bool inUnitedStates) => choice switch
    {
        "radar" => inUnitedStates ? RadarUnitedStates : RadarNorthAmerica,
        "satellite" => Satellite,
        "temperature" => Temperature,
        "wind" => Wind,
        "precipitation" => Precipitation,
        "clouds" => Clouds,
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Not a map layer."),
    };

    /// <summary>What a choice is called on the map's buttons.</summary>
    public static string Name(string id) => id switch
    {
        "radar" => Strings.T("Radar"),
        "satellite" => Strings.T("Satellite"),
        "temperature" => Strings.T("Temperature"),
        "wind" => Strings.T("Wind"),
        "precipitation" => Strings.T("Precipitation"),
        "clouds" => Strings.T("Clouds"),
        "warnings" => Strings.T("Warnings"),
        "lightning" => Strings.T("Lightning"),
        "pressure" => Strings.T("Pressure"),
        _ => id,
    };

    /// <summary>Radar covers North America and no further; elsewhere the map says so rather than showing an empty sky.</summary>
    public static bool RadarCovers(double latitude, double longitude)
        => latitude is > 13 and < 72 && longitude is > -170 and < -50 || (latitude is > 12 and < 23 && longitude is > -161 and < -154) || (latitude is > 13 and < 14 && longitude is > 144 and < 146);

    /// <summary>The half-width of the Web Mercator world in metres, which EPSG:3857 is measured in.</summary>
    public const double MercatorHalfWorld = 20037508.342789244;

    /// <summary>
    /// A GetMap request for one frame: the view's rectangle in world units on [0, 1), drawn at a
    /// pixel size, at a time when the layer has one.
    /// </summary>
    public static string GetMapUrl(MapLayer layer, double left, double top, double right, double bottom, int width, int height, DateTimeOffset? time)
    {
        ArgumentNullException.ThrowIfNull(layer);
        static string M(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var minX = (left - 0.5) * 2 * MercatorHalfWorld;
        var maxX = (right - 0.5) * 2 * MercatorHalfWorld;
        var maxY = (0.5 - top) * 2 * MercatorHalfWorld;
        var minY = (0.5 - bottom) * 2 * MercatorHalfWorld;
        var url = $"{layer.Endpoint}?service=WMS&version=1.3.0&request=GetMap&layers={Uri.EscapeDataString(layer.Layer)}"
                  + $"&styles={Uri.EscapeDataString(layer.Style)}&crs=EPSG:3857&bbox={M(minX)},{M(minY)},{M(maxX)},{M(maxY)}"
                  + $"&width={width}&height={height}&format=image/png&transparent=true";
        if (time is { } at) url += "&time=" + at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return url;
    }

    /// <summary>
    /// How many pixels to ask for across one pixel of the view: the screen's scale, up to one and a
    /// half — a sharper picture costs the service more than it shows — and for a gridded layer no
    /// more than one to a grid cell. More would draw each cell as a block of equal pixels, which
    /// smoothing only rounds at the corners; one sample a cell is blended into its neighbours.
    /// </summary>
    /// <param name="layer">The layer asked for.</param>
    /// <param name="screenScale">The screen's pixels to one of the view's.</param>
    /// <param name="metresPerPixel">Ground metres across one pixel of the view, where it is looking.</param>
    public static double RequestScale(MapLayer layer, double screenScale, double metresPerPixel)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var scale = Math.Min(screenScale, 1.5);
        if (layer.GridMetres <= 0 || metresPerPixel <= 0) return scale;
        var cellPixels = layer.GridMetres / metresPerPixel;
        return Math.Min(scale, 1 / cellPixels);
    }

    /// <summary>
    /// The times a layer has, from its capabilities: the <c>time</c> dimension of the named layer,
    /// as a list or as the interval form <c>start/end/period</c>. Empty when the layer has no time
    /// or is not in the document.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Times(string capabilities, string layer)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(layer);
        var bare = layer.Contains(':', StringComparison.Ordinal) ? layer[(layer.IndexOf(':', StringComparison.Ordinal) + 1)..] : layer;

        XDocument document;
        try
        {
            document = XDocument.Parse(capabilities);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "Layer"))
        {
            var name = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value.Trim();
            if (name != layer && name != bare) continue;

            // A layer group — nowCOAST's radar mosaic is one, of the lower 48, Alaska, Hawaii, the
            // Caribbean and Guam — has no time of its own; its members do, and the service matches
            // each member to the nearest of its own times, so the first member's times serve.
            bool IsTime(XElement e) => e.Name.LocalName == "Dimension" && (string?)e.Attribute("name") == "time";
            var dimension = element.Elements().FirstOrDefault(IsTime) ?? element.Descendants().FirstOrDefault(IsTime);
            return dimension is null ? [] : ParseTimes(dimension.Value);
        }

        return [];
    }

    /// <summary>Reads a WMS time value: a comma-separated list, intervals <c>start/end/period</c>, or a mix.</summary>
    public static IReadOnlyList<DateTimeOffset> ParseTimes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var times = new List<DateTimeOffset>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('/');
            if (pieces.Length == 3 && Moment(pieces[0]) is { } start && Moment(pieces[1]) is { } end && Period(pieces[2]) is { } step && step > TimeSpan.Zero)
            {
                for (var at = start; at <= end && times.Count < 5000; at += step) times.Add(at);
            }
            else if (Moment(part) is { } single)
            {
                times.Add(single);
            }
        }

        return [.. times.Distinct().Order()];
    }

    /// <summary>
    /// The frames to play. Observations: the last two hours, no closer together than ten minutes,
    /// ending at the latest. Forecasts: from the hour under way, every three hours for two days.
    /// Overlays: their latest time alone.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Frames(MapLayerKind kind, IReadOnlyList<DateTimeOffset> times, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.Count == 0) return [];

        switch (kind)
        {
            case MapLayerKind.Overlay:
                return [times.LastOrDefault(t => t <= now + TimeSpan.FromMinutes(5), times[0])];

            case MapLayerKind.Observation:
            {
                var recent = times.Where(t => t <= now + TimeSpan.FromMinutes(5) && t >= now - TimeSpan.FromHours(2)).ToList();
                if (recent.Count == 0) recent = [times[^1]];
                var picked = new List<DateTimeOffset> { recent[^1] };
                for (var i = recent.Count - 2; i >= 0; i--)
                {
                    if (picked[^1] - recent[i] >= TimeSpan.FromMinutes(10)) picked.Add(recent[i]);
                }

                picked.Reverse();
                return picked;
            }

            default:
            {
                var hour = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
                var start = times.FirstOrDefault(t => t >= hour, times[^1]);
                var frames = new List<DateTimeOffset>();
                foreach (var t in times)
                {
                    if (t < start || t > start + TimeSpan.FromHours(48)) continue;
                    if (frames.Count == 0 || t - frames[^1] >= TimeSpan.FromHours(3)) frames.Add(t);
                }

                return frames;
            }
        }
    }

    private static DateTimeOffset? Moment(string text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    /// <summary>An ISO 8601 duration of days, hours, minutes and seconds: P1D, PT6H, PT6M, PT1H30M.</summary>
    private static TimeSpan? Period(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!match.Success) return null;
        int Part(int i) => match.Groups[i].Success ? int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return new TimeSpan(Part(1), Part(2), Part(3), Part(4));
    }
}
