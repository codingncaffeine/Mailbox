using Mailbox.Core.Localization;

namespace Mailbox.Core.Weather;

/// <summary>What the sky is doing, in words and as a picture.</summary>
/// <param name="Description">What a forecast would say: "Partly cloudy", "Heavy rain".</param>
/// <param name="Icon">The picture's key, <c>day/…</c> or <c>night/…</c>, which is its file under the weather art.</param>
public sealed record WeatherCondition(string Description, string Icon);

/// <summary>
/// The World Meteorological Organization's weather codes — what every forecast model reports
/// the sky as — turned into what a reader is shown.
/// </summary>
/// <remarks>
/// The code alone says nothing about wind, and a clear sky with a gale blowing across it is not
/// the day a sun on its own promises. So wind is read alongside it, with the National Weather
/// Service's own words for the thresholds: sustained winds of 25 mph make a day "windy", 40 mph
/// "very windy". Precipitation always wins over wind — rain is what decides whether a reader
/// takes a coat — except where the two together have a name of their own, which snow does.
/// <para>
/// Night has its own pictures because the day ones draw a sun: at night a clear sky is a moon,
/// and a partly cloudy one is a moon behind a cloud. Where the night art has no exact match —
/// there is no night drizzle — the nearest one stands in, as the light rain it looks like.
/// </para>
/// </remarks>
public static class WeatherConditions
{
    /// <summary>Sustained wind, in km/h, at which the Weather Service starts calling a day windy (25 mph).</summary>
    public const double WindyKmh = 40.2;

    /// <summary>Sustained wind, in km/h, of a very windy day (40 mph).</summary>
    public const double VeryWindyKmh = 64.4;

    /// <summary>Wind or gusts, in km/h, a blizzard needs (35 mph).</summary>
    public const double BlizzardWindKmh = 56.3;

    /// <summary>How little a reader can see, in metres, before blowing snow is a blizzard (a quarter mile).</summary>
    public const double BlizzardVisibilityMetres = 402;

    /// <summary>
    /// What to show for one reading.
    /// </summary>
    /// <param name="code">The WMO weather code.</param>
    /// <param name="isDay">Whether the sun is up where the reading is.</param>
    /// <param name="windKmh">Sustained wind, when known.</param>
    /// <param name="gustKmh">Gusts, when known.</param>
    /// <param name="visibilityMetres">How far a reader can see, when known.</param>
    public static WeatherCondition Classify(
        int code,
        bool isDay,
        double? windKmh = null,
        double? gustKmh = null,
        double? visibilityMetres = null)
    {
        var wind = windKmh ?? 0;
        var strongest = Math.Max(wind, gustKmh ?? 0);

        if (IsSnow(code))
        {
            if (strongest >= BlizzardWindKmh && visibilityMetres is < BlizzardVisibilityMetres)
            {
                return new(Strings.T("Blizzard"), isDay ? "day/blizzard" : "night/blizzard-night");
            }

            if (wind >= WindyKmh)
            {
                return new(Strings.T("Blowing snow"), isDay ? "day/windy-with-snow" : "night/blowing-snow-night");
            }
        }

        if (IsRain(code) && wind >= WindyKmh && isDay)
        {
            // Only by day: the night art has no rain driven sideways, and a night rain cloud
            // says rain, which is the part of this that matters.
            return new(Strings.T("Rain and wind"), "day/windy-with-rain");
        }

        if (code is >= 0 and <= 3 && wind >= WindyKmh)
        {
            return wind >= VeryWindyKmh
                ? new(Strings.T("Very windy"), isDay ? "day/very-windy" : "night/very-windy-night")
                : new(Strings.T("Windy"), isDay ? "day/windy" : "night/windy-night");
        }

        return Sky(code, isDay);
    }

    /// <summary>The code on its own, with no wind to weigh against it.</summary>
    public static WeatherCondition Sky(int code, bool isDay) => code switch
    {
        0 => isDay ? new(Strings.T("Sunny"), "day/sunny-day") : new(Strings.T("Clear"), "night/clear-night"),
        1 => isDay ? new(Strings.T("Mostly sunny"), "day/mostly-sunny") : new(Strings.T("Mostly clear"), "night/fair-night"),
        2 => new(Strings.T("Partly cloudy"), isDay ? "day/partly-cloudy" : "night/partly-cloudy-night"),
        3 => new(Strings.T("Cloudy"), isDay ? "day/cloudy" : "night/cloudy-night"),
        45 => new(Strings.T("Fog"), isDay ? "day/fog" : "night/fog-night"),
        48 => new(Strings.T("Freezing fog"), isDay ? "day/fog" : "night/fog-night"),
        51 => new(Strings.T("Light drizzle"), isDay ? "day/drizzle" : "night/light-rain-night"),
        53 => new(Strings.T("Drizzle"), isDay ? "day/drizzle" : "night/light-rain-night"),
        55 => new(Strings.T("Heavy drizzle"), isDay ? "day/drizzle" : "night/light-rain-night"),
        56 => new(Strings.T("Light freezing drizzle"), isDay ? "day/freezing-rain" : "night/freezing-rain-night"),
        57 => new(Strings.T("Freezing drizzle"), isDay ? "day/freezing-rain" : "night/freezing-rain-night"),
        61 => new(Strings.T("Light rain"), isDay ? "day/light-rain" : "night/light-rain-night"),
        63 => new(Strings.T("Rain"), isDay ? "day/moderate-rain" : "night/moderate-rain-night"),
        65 => new(Strings.T("Heavy rain"), isDay ? "day/heavy-rain" : "night/heavy-rain-night"),
        66 => new(Strings.T("Light freezing rain"), isDay ? "day/freezing-rain" : "night/freezing-rain-night"),
        67 => new(Strings.T("Freezing rain"), isDay ? "day/freezing-rain" : "night/ice-storm-night"),
        71 => new(Strings.T("Light snow"), isDay ? "day/light-snow" : "night/snow-night"),
        73 => new(Strings.T("Snow"), isDay ? "day/moderate-snow" : "night/snow-night"),
        75 => new(Strings.T("Heavy snow"), isDay ? "day/heavy-snow" : "night/heavy-snow-night"),
        77 => new(Strings.T("Snow grains"), isDay ? "day/ice-pellets" : "night/ice-pellets-night"),
        80 => new(Strings.T("Light showers"), isDay ? "day/showers" : "night/showers-night"),
        81 => new(Strings.T("Showers"), isDay ? "day/showers" : "night/showers-night"),
        82 => new(Strings.T("Heavy showers"), isDay ? "day/heavy-rain" : "night/heavy-rain-night"),
        85 => new(Strings.T("Snow showers"), isDay ? "day/snow-showers" : "night/snow-night"),
        86 => new(Strings.T("Heavy snow showers"), isDay ? "day/heavy-snow" : "night/heavy-snow-night"),
        95 => new(Strings.T("Thunderstorms"), isDay ? "day/thunderstorm" : "night/thunderstorm-night"),
        96 => new(Strings.T("Thunderstorms with hail"), isDay ? "day/heavy-thunderstorm" : "night/severe-thunderstorm-night"),
        99 => new(Strings.T("Severe thunderstorms with hail"), isDay ? "day/heavy-thunderstorm" : "night/severe-thunderstorm-night"),

        // A code the table does not know is one a model has started sending since; a cloud is
        // the least wrong thing to show for it, and says nothing a reader could act on wrongly.
        _ => new(Strings.T("Cloudy"), isDay ? "day/cloudy" : "night/cloudy-night"),
    };

    /// <summary>Every code the WMO table defines for present weather in a forecast.</summary>
    public static IReadOnlyList<int> KnownCodes { get; } =
        [0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67, 71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99];

    public static bool IsSnow(int code) => code is 71 or 73 or 75 or 77 or 85 or 86;

    public static bool IsRain(int code) => code is >= 51 and <= 67 or >= 80 and <= 82;

    /// <summary>
    /// The picture for a warning, by the event's name — "Tornado Warning", "Flood Watch" — or
    /// null when nothing in the art says it better than a plain warning mark.
    /// </summary>
    /// <remarks>
    /// Matched on words in the name rather than a fixed list, because the Weather Service has
    /// well over a hundred event types and renames them now and then; what the word "Tornado"
    /// means does not change.
    /// </remarks>
    public static string? AlertIcon(string eventName)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        bool Says(string word) => eventName.Contains(word, StringComparison.OrdinalIgnoreCase);

        if (Says("Tornado")) return "day/tornado";
        if (Says("Hurricane") || Says("Typhoon")) return "day/hurricane";
        if (Says("Tropical")) return "day/tropical-storm";
        if (Says("Tsunami")) return "day/tsunami";
        if (Says("Earthquake")) return "day/earthquake";
        if (Says("Ashfall") || Says("Volcan")) return "day/volcanic-ash";
        if (Says("Dust")) return "day/dust-sand";
        if (Says("Smoke") || Says("Air Quality") || Says("Air Stagnation")) return "day/smoke";
        if (Says("Fog")) return "day/fog";
        if (Says("Thunderstorm")) return "day/thunderstorm";
        if (Says("Blizzard") || Says("Winter Storm") || Says("Snow Squall")) return "day/blizzard";
        if (Says("Ice") || Says("Freezing")) return "day/icy-conditions";
        if (Says("Snow") || Says("Winter")) return "day/heavy-snow";
        if (Says("Coastal") || Says("Surge") || Says("Surf") || Says("Rip Current") || Says("Beach") || Says("Lakeshore")) return "day/tsunami";
        if (Says("Flood")) return "day/heavy-rain";
        if (Says("Heat")) return "day/very-hot";
        if (Says("Cold") || Says("Freeze") || Says("Frost") || Says("Wind Chill")) return "day/very-cold";
        if (Says("Wind") || Says("Gale") || Says("Storm")) return "day/very-windy";
        return null;
    }
}
