using System.Globalization;
using Mailbox.Core.Localization;

namespace Mailbox.Core.Weather;

/// <summary>What a layer's colours measure, which decides how its legend is labelled.</summary>
public enum MapQuantity
{
    /// <summary>Air temperature, in degrees Celsius.</summary>
    Temperature,

    /// <summary>Wind speed, in knots.</summary>
    WindSpeed,

    /// <summary>How hard rain or snow is falling, in millimetres an hour.</summary>
    PrecipitationRate,

    /// <summary>What a radar sees, in dBZ.</summary>
    Reflectivity,

    /// <summary>How much of the sky is cloud, in percent.</summary>
    CloudCover,
}

/// <summary>One colour of a legend, as 0xAARRGGBB, and the value it stands for.</summary>
public readonly record struct MapLegendStop(double Value, uint Colour);

/// <summary>A label under a legend's bar: where along the bar, from 0 to 1, and what it says.</summary>
public readonly record struct MapLegendLabel(double At, string Text);

/// <summary>
/// A layer's colour scale, which the map draws as its own legend in the reader's units rather
/// than showing the service's picture of it.
/// </summary>
/// <remarks>
/// The stops are the service's own colours for the style the layer asks for by name, read off the
/// legend the service draws for that style, and spaced evenly along the bar as that legend spaces
/// them. A smooth scale blends from stop to stop; a stepped one is a row of blocks, each running
/// from its value up to the next one's.
/// </remarks>
public sealed record MapLegend(MapQuantity Quantity, IReadOnlyList<MapLegendStop> Stops, bool Stepped = false)
{
    /// <summary>The words for how hard rain or snow falls, and the rates where one gives way to the next.</summary>
    /// <remarks>
    /// The American Meteorological Society's classes: light up to 2.5 mm an hour, moderate to 7.6,
    /// heavy beyond. A radar's dBZ is turned into a rate by the Marshall–Palmer relation, Z = 200 R^1.6,
    /// which is the one the Weather Service's own rainfall estimates start from.
    /// </remarks>
    public const double ModerateRate = 2.5;

    public const double HeavyRate = 7.6;

    public static double ReflectivityOf(double millimetresPerHour) => 10 * Math.Log10(200 * Math.Pow(millimetresPerHour, 1.6));

    /// <summary>Where a value falls along the bar, from 0 at its left end to 1 at its right.</summary>
    public double Position(double value)
    {
        var n = Stops.Count;
        if (n < 2) return 0;
        var spans = Stepped ? n : n - 1;
        if (value <= Stops[0].Value) return 0;

        for (var i = 0; i < n - 1; i++)
        {
            var (low, high) = (Stops[i].Value, Stops[i + 1].Value);
            if (value < high) return (i + (value - low) / (high - low)) / spans;
        }

        if (!Stepped) return 1;

        // The last block runs on without an end; it is given the width of the one below it.
        var (below, top) = (Stops[n - 2].Value, Stops[n - 1].Value);
        return Math.Min(1, (n - 1 + (value - top) / (top - below)) / spans);
    }

    /// <summary>
    /// The value a colour on the map stands for: the nearest point on the scale, found along the
    /// straight blend between each pair of neighbouring stops. Null for a scale of fewer than two.
    /// </summary>
    public double? ValueOf(byte red, byte green, byte blue)
    {
        if (Stops.Count < 2) return null;
        var best = double.MaxValue;
        var value = Stops[0].Value;
        for (var i = 0; i < Stops.Count - 1; i++)
        {
            var (a, b) = (Stops[i], Stops[i + 1]);
            var (ar, ag, ab) = Channels(a.Colour);
            var (br, bg, bb) = Channels(b.Colour);
            var (dr, dg, db) = (br - ar, bg - ag, bb - ab);
            var length = dr * dr + dg * dg + db * db;
            var along = Stepped || length == 0 ? 0 : Math.Clamp(((red - ar) * dr + (green - ag) * dg + (blue - ab) * db) / length, 0, 1);
            var (pr, pg, pb) = (ar + dr * along - red, ag + dg * along - green, ab + db * along - blue);
            var distance = pr * pr + pg * pg + pb * pb;
            if (distance < best)
            {
                best = distance;
                value = a.Value + (b.Value - a.Value) * along;
            }
        }

        if (Stepped)
        {
            var (lr, lg, lb) = Channels(Stops[^1].Colour);
            if ((lr - red) * (lr - red) + (lg - green) * (lg - green) + (lb - blue) * (lb - blue) < best) value = Stops[^1].Value;
        }

        return value;
    }

    private static (double R, double G, double B) Channels(uint colour) => ((colour >> 16) & 0xFF, (colour >> 8) & 0xFF, colour & 0xFF);

    /// <summary>What the legend is headed.</summary>
    public string Title => Quantity switch
    {
        MapQuantity.Temperature => Strings.T("Temperature"),
        MapQuantity.WindSpeed => Strings.T("Wind speed"),
        MapQuantity.CloudCover => Strings.T("Cloud cover"),
        _ => Strings.T("Rain and snow"),
    };

    /// <summary>The unit beside the heading, in the reader's units; empty where the labels are words.</summary>
    public string Unit(WeatherUnits units)
    {
        ArgumentNullException.ThrowIfNull(units);
        return Quantity switch
        {
            MapQuantity.Temperature => units.TemperatureSymbol,
            MapQuantity.WindSpeed => units.SpeedSymbol,
            MapQuantity.CloudCover => "%",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The labels under the bar. Numbers where the reader has a unit for the quantity — round
    /// values in that unit, no more than six of them — and words where the numbers would mean
    /// little to most readers: nobody pictures 0.3 millimetres an hour, or 35 dBZ, but everyone
    /// knows light rain from heavy.
    /// </summary>
    public IReadOnlyList<MapLegendLabel> Labels(WeatherUnits units, IFormatProvider? format = null)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (Stops.Count < 2) return [];
        var culture = format ?? CultureInfo.CurrentCulture;

        switch (Quantity)
        {
            case MapQuantity.Temperature:
                return Numbers(units.TemperatureFrom, "°", culture);
            case MapQuantity.WindSpeed:
                return Numbers(knots => units.SpeedFrom(knots * 1.852), string.Empty, culture);
            case MapQuantity.CloudCover:
                return Numbers(percent => percent, string.Empty, culture);
            default:
            {
                var (moderate, heavy) = Quantity == MapQuantity.Reflectivity
                    ? (ReflectivityOf(ModerateRate), ReflectivityOf(HeavyRate))
                    : (ModerateRate, HeavyRate);
                var (from, to) = (Position(moderate), Position(heavy));
                return
                [
                    new(from / 2, Strings.T("precipitation", "Light")),
                    new((from + to) / 2, Strings.T("precipitation", "Moderate")),
                    new((to + 1) / 2, Strings.T("precipitation", "Heavy")),
                ];
            }
        }
    }

    /// <summary>
    /// Round numbers in the reader's unit across the scale's range. The conversion from the
    /// scale's own unit is a straight line — degrees, speeds — so it is undone from two points.
    /// </summary>
    private List<MapLegendLabel> Numbers(Func<double, double> toReader, string suffix, IFormatProvider culture)
    {
        var (first, last) = (Stops[0].Value, Stops[^1].Value);
        var (low, high) = (toReader(first), toReader(last));
        var slope = toReader(1) - toReader(0);
        var step = NiceStep(high - low, 6);
        var labels = new List<MapLegendLabel>();
        for (var reading = Math.Ceiling(low / step - 1e-9) * step; reading <= high + 1e-9; reading += step)
        {
            var native = (reading - toReader(0)) / slope;
            var text = WeatherUnits.Whole(reading).ToString(culture) + suffix;
            labels.Add(new MapLegendLabel(Position(native), text));
        }

        return labels;
    }

    /// <summary>The smallest of 1, 2, 2.5 and 5 times a power of ten that marks a span in no more than <paramref name="most"/> labels.</summary>
    public static double NiceStep(double span, int most)
    {
        if (span <= 0) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(span / most)));
        foreach (var factor in new[] { 1, 2, 2.5, 5, 10 })
        {
            var step = factor * power;
            if (Math.Floor(span / step) + 1 <= most) return step;
        }

        return 10 * power;
    }
}
