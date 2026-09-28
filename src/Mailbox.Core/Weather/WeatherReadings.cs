using System.Globalization;
using Mailbox.Core.Localization;

namespace Mailbox.Core.Weather;

/// <summary>The WHO's five steps of the UV index.</summary>
public enum UvLevel
{
    Low,
    Moderate,
    High,
    VeryHigh,
    Extreme,
}

/// <summary>Which way the air pressure is going over the next few hours.</summary>
public enum PressureTrend
{
    Steady,
    Rising,
    Falling,
}

/// <summary>
/// What the numbers mean: the words a forecast puts beside a reading.
/// </summary>
/// <remarks>
/// A reading on its own asks the reader to know that a UV index of 8 is dangerous or that a dew
/// point of 22° is oppressive. Every weather service writes the meaning next to the number, with
/// the published scales behind it — the WHO's for UV, the meteorologists' usual bands for dew
/// point and visibility — and so does this.
/// </remarks>
public static class WeatherReadings
{
    private static readonly string[] Points =
        ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    /// <summary>
    /// The sixteen-point compass direction the wind blows <em>from</em>, which is how wind is
    /// named: a north wind comes out of the north.
    /// </summary>
    public static string Compass(double degrees)
    {
        var normal = ((degrees % 360) + 360) % 360;
        return Points[(int)Math.Round(normal / 22.5, MidpointRounding.AwayFromZero) % 16];
    }

    public static UvLevel Uv(double index) => index switch
    {
        < 2.5 => UvLevel.Low,
        < 5.5 => UvLevel.Moderate,
        < 7.5 => UvLevel.High,
        < 10.5 => UvLevel.VeryHigh,
        _ => UvLevel.Extreme,
    };

    public static string UvName(UvLevel level) => level switch
    {
        UvLevel.Low => Strings.T("Low"),
        UvLevel.Moderate => Strings.T("Moderate"),
        UvLevel.High => Strings.T("High"),
        UvLevel.VeryHigh => Strings.T("Very high"),
        _ => Strings.T("Extreme"),
    };

    /// <summary>The WHO's advice for each step, in the fewest words that still say what to do.</summary>
    public static string UvAdvice(UvLevel level) => level switch
    {
        UvLevel.Low => Strings.T("No protection needed."),
        UvLevel.Moderate => Strings.T("Sunscreen and a hat around midday."),
        UvLevel.High => Strings.T("Protection needed; seek shade at midday."),
        UvLevel.VeryHigh => Strings.T("Extra protection; avoid the midday sun."),
        _ => Strings.T("Take every precaution; stay out of the midday sun."),
    };

    /// <summary>How far a reader can see, in words: fog, mist and haze are what change it.</summary>
    public static string Visibility(double metres) => metres switch
    {
        < 1000 => Strings.T("Fog: very poor visibility."),
        < 4000 => Strings.T("Poor visibility."),
        < 10000 => Strings.T("Moderate visibility."),
        < 20000 => Strings.T("Good visibility."),
        _ => Strings.T("A perfectly clear view."),
    };

    /// <summary>How the air feels for its moisture, from the dew point in °C.</summary>
    public static string DewPoint(double celsius) => celsius switch
    {
        < 10 => Strings.T("The air is dry."),
        < 16 => Strings.T("Comfortable."),
        < 19 => Strings.T("Slightly humid."),
        < 22 => Strings.T("Humid and sticky."),
        _ => Strings.T("Oppressively humid."),
    };

    /// <summary>
    /// Why it feels the temperature it does: within two degrees it is the temperature; below it,
    /// the wind is taking heat away; above it, the humidity is keeping it in.
    /// </summary>
    public static string FeelsLike(double temperature, double? feelsLike, double? windKmh)
    {
        if (feelsLike is not { } feels || Math.Abs(feels - temperature) < 2) return Strings.T("Similar to the actual temperature.");
        if (feels < temperature)
        {
            return windKmh is > 10 ? Strings.T("The wind is making it feel colder.") : Strings.T("It feels colder than it is.");
        }

        return Strings.T("Humidity is making it feel warmer.");
    }

    /// <summary>
    /// Where the pressure is heading: the change over the next three hours, a hectopascal either
    /// way being the usual line between a change and a steady glass.
    /// </summary>
    public static PressureTrend Trend(IReadOnlyList<HourlyWeather> hours, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(hours);
        var hour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
        var now = hours.FirstOrDefault(h => h.Time == hour)?.Pressure;
        var later = hours.FirstOrDefault(h => h.Time == hour.AddHours(3))?.Pressure;
        if (now is not { } a || later is not { } b) return PressureTrend.Steady;
        return (b - a) switch
        {
            >= 1 => PressureTrend.Rising,
            <= -1 => PressureTrend.Falling,
            _ => PressureTrend.Steady,
        };
    }

    public static string TrendName(PressureTrend trend) => trend switch
    {
        PressureTrend.Rising => Strings.T("Rising"),
        PressureTrend.Falling => Strings.T("Falling"),
        _ => Strings.T("Steady"),
    };

    /// <summary>
    /// The next two hours in a sentence — "Rain starting around 7:30 PM", "Rain until about
    /// 8:15 PM" — from the fifteen-minute steps, or null when the service sent none.
    /// </summary>
    /// <remarks>
    /// A tenth of a millimetre in fifteen minutes is where precipitation starts to count: less is
    /// a model's noise, not something a reader would notice falling. Whether it falls as rain or
    /// snow is not in the steps, so the temperature decides, as it decides on the ground.
    /// </remarks>
    /// <param name="likeliest">
    /// The highest hourly chance of precipitation in the same two hours. The steps are amounts
    /// and can all be nothing while the hours say 60%; "no rain expected" beside a 60% column
    /// is a promise the chart breaks, so a chance of 30% or more is said instead.
    /// </param>
    public static string? NextTwoHours(IReadOnlyList<PrecipitationStep> steps, DateTime localNow, double temperature, IFormatProvider? format = null, double? likeliest = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var ahead = steps.Where(s => s.Time.AddMinutes(15) > localNow).ToList();
        if (ahead.Count == 0) return null;

        const double Counts = 0.1;
        var snow = temperature <= 0;
        var culture = format ?? CultureInfo.CurrentCulture;
        string When(DateTime t) => t.ToString("t", culture);

        var wetNow = ahead[0].Amount >= Counts;
        if (!wetNow)
        {
            var starts = ahead.FirstOrDefault(s => s.Amount >= Counts);
            if (starts is null)
            {
                if (likeliest is >= 30 and var chance)
                {
                    return string.Format(culture, snow ? Strings.T("A {0}% chance of snow in the next two hours.") : Strings.T("A {0}% chance of rain in the next two hours."), Math.Round(chance));
                }

                return snow ? Strings.T("No snow expected in the next two hours.") : Strings.T("No rain expected in the next two hours.");
            }
            return string.Format(culture, snow ? Strings.T("Snow starting around {0}.") : Strings.T("Rain starting around {0}."), When(starts.Time));
        }

        var stops = ahead.FirstOrDefault(s => s.Amount < Counts);
        if (stops is null) return snow ? Strings.T("Snow for the next two hours.") : Strings.T("Rain for the next two hours.");
        return string.Format(culture, snow ? Strings.T("Snow stopping around {0}.") : Strings.T("Rain stopping around {0}."), When(stops.Time));
    }
}
