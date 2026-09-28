using System.Globalization;
using System.Text.Json;

namespace Mailbox.Core.Weather;

/// <summary>
/// Open-Meteo: the forecast and the place search, both free for a non-commercial client and
/// neither needing a key or an account.
/// </summary>
/// <remarks>
/// There is no key because the service counts calls by the address they come from. Every copy of
/// Mailbox asks from its own reader's connection, so the allowance — ten thousand calls a day — is
/// each reader's own; nothing here passes through a server of ours, and nothing should, because a
/// server in the middle would put every reader on one address and one allowance.
/// <para>
/// What is asked for is fixed and deliberate. The service counts a request for more than ten
/// variables, or for more than two weeks, as several calls; this one is a few dozen variables
/// over sixteen days, which it counts as a handful, and it is made at most every half hour per
/// place — a few hundred calls a day even for a reader who keeps many places.
/// </para>
/// </remarks>
public static class OpenMeteo
{
    public const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";
    public const string SearchEndpoint = "https://geocoding-api.open-meteo.com/v1/search";
    public const string AirQualityEndpoint = "https://air-quality-api.open-meteo.com/v1/air-quality";

    /// <summary>The page the attribution the licence asks for links to.</summary>
    public const string Home = "https://open-meteo.com/";

    /// <summary>The data is CC BY 4.0: free to use, with credit shown.</summary>
    public const string Licence = "https://creativecommons.org/licenses/by/4.0/";

    /// <summary>The fifteen-minute reading the service calls current.</summary>
    public const string CurrentVariables =
        "temperature_2m,relative_humidity_2m,apparent_temperature,dew_point_2m,is_day,precipitation,"
        + "weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m,wind_gusts_10m,"
        + "visibility,uv_index";

    public const string HourlyVariables =
        "temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,precipitation_probability,"
        + "precipitation,snowfall,weather_code,cloud_cover,visibility,pressure_msl,wind_speed_10m,"
        + "wind_direction_10m,wind_gusts_10m,uv_index,is_day";

    public const string DailyVariables =
        "weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,"
        + "apparent_temperature_min,sunrise,sunset,daylight_duration,uv_index_max,precipitation_sum,"
        + "snowfall_sum,precipitation_hours,precipitation_probability_max,wind_speed_10m_max,"
        + "wind_gusts_10m_max,wind_direction_10m_dominant";

    /// <summary>How many days are asked for: the most the service forecasts.</summary>
    public const int ForecastDays = 16;

    /// <summary>
    /// An index and each pollutant's own index on the same scale, the highest of which is the
    /// pollutant to name. Only the place's own index is asked for, which keeps a request within
    /// the ten variables the service counts as one call.
    /// </summary>
    public const string UnitedStatesAirVariables =
        "us_aqi,us_aqi_pm2_5,us_aqi_pm10,us_aqi_ozone,us_aqi_nitrogen_dioxide,us_aqi_sulphur_dioxide,us_aqi_carbon_monoxide";

    public const string EuropeanAirVariables =
        "european_aqi,european_aqi_pm2_5,european_aqi_pm10,european_aqi_ozone,european_aqi_nitrogen_dioxide,european_aqi_sulphur_dioxide";

    /// <summary>Who the air quality comes from, which the service asks to be credited beside itself.</summary>
    public const string AirQualitySource = "https://atmosphere.copernicus.eu/";

    /// <summary>
    /// The whole forecast for a point, in the service's metric units and in the point's own
    /// time zone.
    /// </summary>
    public static string ForecastUrl(double latitude, double longitude)
        => $"{ForecastEndpoint}?latitude={Coordinate(latitude)}&longitude={Coordinate(longitude)}"
           + $"&current={CurrentVariables}&minutely_15=precipitation&hourly={HourlyVariables}"
           + $"&daily={DailyVariables}&timezone=auto&forecast_days={ForecastDays}&forecast_minutely_15=8";

    /// <summary>
    /// A search by name or postal code, answered in the reader's language where the service has
    /// the names in it.
    /// </summary>
    /// <summary>The air at a point this hour, on one index, from the Copernicus models the service relays.</summary>
    public static string AirQualityUrl(double latitude, double longitude, AirQualityScale scale)
        => $"{AirQualityEndpoint}?latitude={Coordinate(latitude)}&longitude={Coordinate(longitude)}"
           + $"&current={(scale == AirQualityScale.European ? EuropeanAirVariables : UnitedStatesAirVariables)}&timezone=auto";

    public static string SearchUrl(string query, string language)
    {
        ArgumentNullException.ThrowIfNull(query);
        var lang = language is { Length: 2 } ? language.ToLowerInvariant() : "en";
        return $"{SearchEndpoint}?name={Uri.EscapeDataString(query.Trim())}&count=10&language={lang}&format=json";
    }

    /// <summary>Four decimal places is eleven metres, which is finer than any forecast grid.</summary>
    public static string Coordinate(double degrees) => degrees.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// The reason the service gave for refusing a request — "Latitude must be in range…" — or
    /// null when the text is not one of its errors.
    /// </summary>
    public static string? ErrorReason(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True
                && root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                return reason.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON at all — a proxy's error page, say. Not one of the service's errors.
        }

        return null;
    }

    /// <summary>
    /// Reads a forecast response.
    /// </summary>
    /// <remarks>
    /// Rows the model has nothing for are dropped rather than shown as zeros. The service pads a
    /// place whose models stop short of sixteen days with empty hours to the end, and an hour
    /// with no temperature is not an hour at zero degrees. A row needs its time, a temperature
    /// and a weather code to be kept; anything else may be missing and is shown as missing.
    /// </remarks>
    /// <exception cref="FormatException">The text is not a forecast.</exception>
    public static Forecast ParseForecast(string json, DateTimeOffset fetched)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The forecast is not JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The forecast is not an object.");
            if (ErrorReason(json) is { } reason) throw new FormatException(reason);

            if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The forecast has no current conditions.");
            }

            var now = new CurrentWeather
            {
                Time = Time(current, "time") ?? throw new FormatException("The current conditions have no time."),
                Temperature = Number(current, "temperature_2m") ?? throw new FormatException("The current conditions have no temperature."),
                Code = (int)(Number(current, "weather_code") ?? throw new FormatException("The current conditions have no weather code.")),
                IsDay = Number(current, "is_day") is not 0,
                FeelsLike = Number(current, "apparent_temperature"),
                Humidity = Number(current, "relative_humidity_2m"),
                DewPoint = Number(current, "dew_point_2m"),
                Precipitation = Number(current, "precipitation"),
                CloudCover = Number(current, "cloud_cover"),
                Pressure = Number(current, "pressure_msl"),
                WindSpeed = Number(current, "wind_speed_10m"),
                WindDirection = Number(current, "wind_direction_10m"),
                WindGusts = Number(current, "wind_gusts_10m"),
                Visibility = Number(current, "visibility"),
                UvIndex = Number(current, "uv_index"),
            };

            return new Forecast
            {
                Latitude = Number(root, "latitude") ?? 0,
                Longitude = Number(root, "longitude") ?? 0,
                TimeZone = root.TryGetProperty("timezone", out var zone) && zone.ValueKind == JsonValueKind.String
                    ? zone.GetString() ?? string.Empty
                    : string.Empty,
                UtcOffset = TimeSpan.FromSeconds(Number(root, "utc_offset_seconds") ?? 0),
                Elevation = Number(root, "elevation"),
                Fetched = fetched,
                Current = now,
                Hourly = Hours(root),
                Daily = Days(root),
                NextTwoHours = Steps(root),
            };
        }
    }

    /// <summary>
    /// Reads an air quality response: whichever indices it carries, each with the pollutant whose
    /// own index is highest. An index the models had nothing for is missing, not zero.
    /// </summary>
    /// <exception cref="FormatException">The text is not an air quality response.</exception>
    public static AirQuality ParseAirQuality(string json, DateTimeOffset fetched)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The air quality is not JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The air quality is not an object.");
            if (ErrorReason(json) is { } reason) throw new FormatException(reason);
            if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The air quality has no current reading.");
            }

            AirIndex? Index(string prefix, params (string Suffix, AirPollutant Pollutant)[] parts)
            {
                if (Number(current, prefix) is not { } value) return null;
                AirPollutant? leading = null;
                var highest = double.MinValue;
                foreach (var (suffix, pollutant) in parts)
                {
                    if (Number(current, $"{prefix}_{suffix}") is not { } part || part <= highest) continue;
                    (highest, leading) = (part, pollutant);
                }

                return new AirIndex((int)Math.Round(value), leading);
            }

            (string, AirPollutant)[] particlesAndGases =
            [
                ("pm2_5", AirPollutant.FineParticles), ("pm10", AirPollutant.CoarseParticles), ("ozone", AirPollutant.Ozone),
                ("nitrogen_dioxide", AirPollutant.NitrogenDioxide), ("sulphur_dioxide", AirPollutant.SulfurDioxide),
            ];

            return new AirQuality
            {
                Fetched = fetched,
                Time = Time(current, "time") ?? throw new FormatException("The air quality reading has no time."),
                UnitedStates = Index("us_aqi", [.. particlesAndGases, ("carbon_monoxide", AirPollutant.CarbonMonoxide)]),
                European = Index("european_aqi", particlesAndGases),
            };
        }
    }

    /// <summary>
    /// Reads a place search. No match is an empty list: the service leaves the results out
    /// altogether rather than sending an empty one.
    /// </summary>
    /// <param name="json">The response.</param>
    /// <param name="typed">What the reader searched for, kept on a place found by its postal code.</param>
    /// <exception cref="FormatException">The text is not a search response.</exception>
    public static IReadOnlyList<WeatherPlace> ParsePlaces(string json, string typed = "")
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The search response is not an object.");
            if (ErrorReason(json) is { } reason) throw new FormatException(reason);
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];

            var places = new List<WeatherPlace>();
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object) continue;
                if (Text(result, "name") is not { Length: > 0 } name) continue;
                if (Number(result, "latitude") is not { } latitude || Number(result, "longitude") is not { } longitude) continue;

                var postcode = string.Empty;
                if (typed.Trim() is { Length: > 0 } wanted
                    && result.TryGetProperty("postcodes", out var codes) && codes.ValueKind == JsonValueKind.Array
                    && codes.EnumerateArray().Any(c => string.Equals(c.GetString(), wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    postcode = wanted;
                }

                places.Add(new WeatherPlace
                {
                    Id = Number(result, "id") is { } id
                        ? "geonames:" + ((long)id).ToString(CultureInfo.InvariantCulture)
                        : $"point:{Coordinate(latitude)},{Coordinate(longitude)}",
                    Name = name,
                    Latitude = latitude,
                    Longitude = longitude,
                    Region = Text(result, "admin1") ?? string.Empty,
                    Country = Text(result, "country") ?? string.Empty,
                    CountryCode = Text(result, "country_code") ?? string.Empty,
                    TimeZone = Text(result, "timezone") ?? string.Empty,
                    Postcode = postcode,
                });
            }

            return places;
        }
        catch (JsonException ex)
        {
            throw new FormatException("The search response is not JSON.", ex);
        }
    }

    private static List<HourlyWeather> Hours(JsonElement root)
    {
        if (!root.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Object) return [];

        var times = Times(hourly, "time");
        var temperature = Numbers(hourly, "temperature_2m", times.Count);
        var code = Numbers(hourly, "weather_code", times.Count);
        var isDay = Numbers(hourly, "is_day", times.Count);
        var feels = Numbers(hourly, "apparent_temperature", times.Count);
        var humidity = Numbers(hourly, "relative_humidity_2m", times.Count);
        var dewPoint = Numbers(hourly, "dew_point_2m", times.Count);
        var chance = Numbers(hourly, "precipitation_probability", times.Count);
        var amount = Numbers(hourly, "precipitation", times.Count);
        var snow = Numbers(hourly, "snowfall", times.Count);
        var cloud = Numbers(hourly, "cloud_cover", times.Count);
        var visibility = Numbers(hourly, "visibility", times.Count);
        var pressure = Numbers(hourly, "pressure_msl", times.Count);
        var wind = Numbers(hourly, "wind_speed_10m", times.Count);
        var direction = Numbers(hourly, "wind_direction_10m", times.Count);
        var gusts = Numbers(hourly, "wind_gusts_10m", times.Count);
        var uv = Numbers(hourly, "uv_index", times.Count);

        var hours = new List<HourlyWeather>(times.Count);
        for (var i = 0; i < times.Count; i++)
        {
            if (times[i] is not { } time || temperature[i] is not { } degrees || code[i] is not { } weather) continue;

            hours.Add(new HourlyWeather
            {
                Time = time,
                Temperature = degrees,
                Code = (int)weather,
                IsDay = isDay[i] is not 0,
                FeelsLike = feels[i],
                Humidity = humidity[i],
                DewPoint = dewPoint[i],
                PrecipitationChance = chance[i],
                Precipitation = amount[i],
                Snowfall = snow[i],
                CloudCover = cloud[i],
                Visibility = visibility[i],
                Pressure = pressure[i],
                WindSpeed = wind[i],
                WindDirection = direction[i],
                WindGusts = gusts[i],
                UvIndex = uv[i],
            });
        }

        return hours;
    }

    private static List<DailyWeather> Days(JsonElement root)
    {
        if (!root.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object) return [];

        var dates = Dates(daily, "time");
        var code = Numbers(daily, "weather_code", dates.Count);
        var high = Numbers(daily, "temperature_2m_max", dates.Count);
        var low = Numbers(daily, "temperature_2m_min", dates.Count);
        var feelsHigh = Numbers(daily, "apparent_temperature_max", dates.Count);
        var feelsLow = Numbers(daily, "apparent_temperature_min", dates.Count);
        var sunrise = Times(daily, "sunrise", dates.Count);
        var sunset = Times(daily, "sunset", dates.Count);
        var daylight = Numbers(daily, "daylight_duration", dates.Count);
        var uv = Numbers(daily, "uv_index_max", dates.Count);
        var amount = Numbers(daily, "precipitation_sum", dates.Count);
        var snow = Numbers(daily, "snowfall_sum", dates.Count);
        var hours = Numbers(daily, "precipitation_hours", dates.Count);
        var chance = Numbers(daily, "precipitation_probability_max", dates.Count);
        var wind = Numbers(daily, "wind_speed_10m_max", dates.Count);
        var gusts = Numbers(daily, "wind_gusts_10m_max", dates.Count);
        var direction = Numbers(daily, "wind_direction_10m_dominant", dates.Count);

        var days = new List<DailyWeather>(dates.Count);
        for (var i = 0; i < dates.Count; i++)
        {
            if (dates[i] is not { } date || code[i] is not { } weather || high[i] is not { } max || low[i] is not { } min) continue;

            days.Add(new DailyWeather
            {
                Date = date,
                Code = (int)weather,
                High = max,
                Low = min,
                FeelsHigh = feelsHigh[i],
                FeelsLow = feelsLow[i],
                Sunrise = sunrise[i],
                Sunset = sunset[i],
                Daylight = daylight[i] is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                UvIndexMax = uv[i],
                Precipitation = amount[i],
                Snowfall = snow[i],
                PrecipitationHours = hours[i],
                PrecipitationChance = chance[i],
                WindMax = wind[i],
                GustMax = gusts[i],
                WindDirection = direction[i],
            });
        }

        return days;
    }

    private static List<PrecipitationStep> Steps(JsonElement root)
    {
        if (!root.TryGetProperty("minutely_15", out var steps) || steps.ValueKind != JsonValueKind.Object) return [];

        var times = Times(steps, "time");
        var amount = Numbers(steps, "precipitation", times.Count);

        var list = new List<PrecipitationStep>(times.Count);
        for (var i = 0; i < times.Count; i++)
        {
            if (times[i] is { } time && amount[i] is { } mm) list.Add(new PrecipitationStep(time, mm));
        }

        return list;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? Number(value) : null;

    private static double? Number(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>
    /// A column of numbers, padded with nothing to the length of the time column — a variable the
    /// service left out entirely reads as missing throughout, not as a shorter list.
    /// </summary>
    private static double?[] Numbers(JsonElement section, string name, int length)
    {
        var column = new double?[length];
        if (!section.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return column;

        var i = 0;
        foreach (var value in array.EnumerateArray())
        {
            if (i >= length) break;
            column[i++] = Number(value);
        }

        return column;
    }

    private static DateTime? Time(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Time(value.GetString()) : null;

    /// <summary>The service's local times: "2026-09-27T19:00", on the place's wall clock.</summary>
    private static DateTime? Time(string? text)
        => DateTime.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)
            ? DateTime.SpecifyKind(time, DateTimeKind.Unspecified)
            : null;

    private static List<DateTime?> Times(JsonElement section, string name)
    {
        if (!section.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return [.. array.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? Time(v.GetString()) : null)];
    }

    private static DateTime?[] Times(JsonElement section, string name, int length)
    {
        var column = new DateTime?[length];
        var times = Times(section, name);
        for (var i = 0; i < length && i < times.Count; i++) column[i] = times[i];
        return column;
    }

    private static List<DateOnly?> Dates(JsonElement section, string name)
    {
        if (!section.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return
        [
            .. array.EnumerateArray().Select(v =>
                v.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date
                    : (DateOnly?)null),
        ];
    }
}
