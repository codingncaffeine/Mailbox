namespace Mailbox.Core.Weather;

/// <summary>
/// The weather now, as the forecast's latest fifteen-minute step has it.
/// </summary>
/// <remarks>
/// Every value is in the service's own units — degrees Celsius, km/h, millimetres, hectopascals,
/// metres — and is converted by <see cref="WeatherUnits"/> on the way to the screen. Anything but
/// the time, the temperature and the code can be missing: a model that has no visibility for a
/// place says nothing rather than zero, and zero visibility would be a very different claim.
/// </remarks>
public sealed record CurrentWeather
{
    /// <summary>The wall-clock time at the place, not the reader's.</summary>
    public required DateTime Time { get; init; }
    public required double Temperature { get; init; }
    public required int Code { get; init; }
    public required bool IsDay { get; init; }
    public double? FeelsLike { get; init; }
    public double? Humidity { get; init; }
    public double? DewPoint { get; init; }
    public double? Precipitation { get; init; }
    public double? CloudCover { get; init; }
    public double? Pressure { get; init; }
    public double? WindSpeed { get; init; }
    public double? WindDirection { get; init; }
    public double? WindGusts { get; init; }
    public double? Visibility { get; init; }
    public double? UvIndex { get; init; }

    public WeatherCondition Condition => WeatherConditions.Classify(Code, IsDay, WindSpeed, WindGusts, Visibility);
}

/// <summary>One hour of the forecast.</summary>
public sealed record HourlyWeather
{
    /// <summary>The start of the hour, on the place's wall clock.</summary>
    public required DateTime Time { get; init; }
    public required double Temperature { get; init; }
    public required int Code { get; init; }
    public required bool IsDay { get; init; }
    public double? FeelsLike { get; init; }
    public double? Humidity { get; init; }
    public double? DewPoint { get; init; }
    /// <summary>The chance of any precipitation in the hour, in percent.</summary>
    public double? PrecipitationChance { get; init; }
    public double? Precipitation { get; init; }
    /// <summary>Snowfall in centimetres, which is how the service reports it.</summary>
    public double? Snowfall { get; init; }
    public double? CloudCover { get; init; }
    public double? Visibility { get; init; }
    public double? Pressure { get; init; }
    public double? WindSpeed { get; init; }
    public double? WindDirection { get; init; }
    public double? WindGusts { get; init; }
    public double? UvIndex { get; init; }

    public WeatherCondition Condition => WeatherConditions.Classify(Code, IsDay, WindSpeed, WindGusts, Visibility);
}

/// <summary>One day of the forecast.</summary>
public sealed record DailyWeather
{
    public required DateOnly Date { get; init; }
    public required int Code { get; init; }
    public required double High { get; init; }
    public required double Low { get; init; }
    public double? FeelsHigh { get; init; }
    public double? FeelsLow { get; init; }
    public DateTime? Sunrise { get; init; }
    public DateTime? Sunset { get; init; }
    public TimeSpan? Daylight { get; init; }
    public double? UvIndexMax { get; init; }
    public double? Precipitation { get; init; }
    /// <summary>Snowfall in centimetres, which is how the service reports it.</summary>
    public double? Snowfall { get; init; }
    public double? PrecipitationHours { get; init; }
    public double? PrecipitationChance { get; init; }
    public double? WindMax { get; init; }
    public double? GustMax { get; init; }
    public double? WindDirection { get; init; }

    /// <summary>A day is shown by its daytime picture, whatever its night brings.</summary>
    public WeatherCondition Condition => WeatherConditions.Classify(Code, isDay: true, WindMax, GustMax);
}

/// <summary>A fifteen-minute step of the next two hours: how much falls in it.</summary>
public sealed record PrecipitationStep(DateTime Time, double Amount);

/// <summary>
/// Everything one request brings back for one place.
/// </summary>
/// <remarks>
/// Times are the place's own wall-clock times, which is what a forecast is read in: a reader
/// looking at tomorrow in Oslo wants Oslo's morning, not their own. The zone travels with the
/// forecast so "now" can be worked out there too.
/// </remarks>
public sealed record Forecast
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }

    /// <summary>The IANA zone the service placed the location in: "America/Los_Angeles".</summary>
    public required string TimeZone { get; init; }

    /// <summary>The zone's offset when the forecast was made, for when the zone cannot be found.</summary>
    public required TimeSpan UtcOffset { get; init; }

    public double? Elevation { get; init; }

    /// <summary>When the response arrived, which is what its age is measured from.</summary>
    public required DateTimeOffset Fetched { get; init; }

    public required CurrentWeather Current { get; init; }
    public required IReadOnlyList<HourlyWeather> Hourly { get; init; }
    public required IReadOnlyList<DailyWeather> Daily { get; init; }
    public required IReadOnlyList<PrecipitationStep> NextTwoHours { get; init; }

    /// <summary>
    /// An instant on the place's wall clock.
    /// </summary>
    /// <remarks>
    /// By the zone when this machine knows it, so the answer is right across a change of clocks
    /// inside the sixteen days; by the offset the service gave otherwise, which is right until
    /// the next change.
    /// </remarks>
    public DateTime LocalTime(DateTimeOffset instant)
    {
        if (TimeZone.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone))
        {
            return TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        }

        return instant.ToOffset(UtcOffset).DateTime;
    }

    /// <summary>
    /// The hours from the one now under way, up to <paramref name="count"/> of them. The hourly
    /// list starts at the place's midnight, so most of the morning is already behind a reader.
    /// </summary>
    public IReadOnlyList<HourlyWeather> HoursFrom(DateTime localNow, int count)
    {
        var hour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0, DateTimeKind.Unspecified);
        return [.. Hourly.Where(h => h.Time >= hour).Take(count)];
    }

    /// <summary>The day under way at the place, or null when the forecast no longer reaches it.</summary>
    public DailyWeather? Today(DateTime localNow)
    {
        var today = DateOnly.FromDateTime(localNow);
        return Daily.FirstOrDefault(d => d.Date == today);
    }

    /// <summary>The days from today on, up to <paramref name="count"/> of them.</summary>
    public IReadOnlyList<DailyWeather> DaysFrom(DateTime localNow, int count)
    {
        var today = DateOnly.FromDateTime(localNow);
        return [.. Daily.Where(d => d.Date >= today).Take(count)];
    }
}
