using System.Globalization;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Weather;

/// <summary>How a temperature is shown.</summary>
public enum TemperatureUnit
{
    Fahrenheit,
    Celsius,
}

/// <summary>How a wind speed is shown.</summary>
public enum SpeedUnit
{
    MilesPerHour,
    KilometresPerHour,
    MetresPerSecond,
    Knots,
}

/// <summary>How an amount of rain or snow is shown.</summary>
public enum PrecipitationUnit
{
    Inches,
    Millimetres,
}

/// <summary>How air pressure is shown.</summary>
public enum PressureUnit
{
    InchesOfMercury,
    Hectopascals,
}

/// <summary>How a distance is shown — which, here, means how far a reader can see.</summary>
public enum DistanceUnit
{
    Miles,
    Kilometres,
}

/// <summary>
/// The units a reader sees the weather in, and the arithmetic from what the service sends.
/// </summary>
/// <remarks>
/// Every forecast is asked for in the service's own metric units and converted here, on the way
/// to the screen. A cached forecast is therefore the same whatever the reader has chosen, and
/// changing a unit redraws what is already held instead of asking the service again.
/// <para>
/// Five separate choices rather than one "metric or imperial" switch, because that is not how
/// people actually mix them: a reader in Britain wants Celsius and miles per hour, a pilot wants
/// knots whatever else they use. The defaults come from the reader's region, which is what every
/// weather application does; each one can be changed on its own.
/// </para>
/// </remarks>
public sealed record WeatherUnits(
    TemperatureUnit Temperature,
    SpeedUnit Speed,
    PrecipitationUnit Precipitation,
    PressureUnit Pressure,
    DistanceUnit Distance)
{
    public const string TemperatureKey = "weather.units.temperature";
    public const string SpeedKey = "weather.units.speed";
    public const string PrecipitationKey = "weather.units.precipitation";
    public const string PressureKey = "weather.units.pressure";
    public const string DistanceKey = "weather.units.distance";

    public static WeatherUnits Metric { get; } = new(
        TemperatureUnit.Celsius, SpeedUnit.KilometresPerHour, PrecipitationUnit.Millimetres,
        PressureUnit.Hectopascals, DistanceUnit.Kilometres);

    public static WeatherUnits UnitedStates { get; } = new(
        TemperatureUnit.Fahrenheit, SpeedUnit.MilesPerHour, PrecipitationUnit.Inches,
        PressureUnit.InchesOfMercury, DistanceUnit.Miles);

    /// <summary>Celsius for the temperature, miles for everything that moves or is far away.</summary>
    public static WeatherUnits UnitedKingdom { get; } = new(
        TemperatureUnit.Celsius, SpeedUnit.MilesPerHour, PrecipitationUnit.Millimetres,
        PressureUnit.Hectopascals, DistanceUnit.Miles);

    /// <summary>
    /// What a region reads its weather in: Fahrenheit where the forecasts on television are in
    /// Fahrenheit, the British mix in Britain and its islands, metric everywhere else.
    /// </summary>
    public static WeatherUnits ForRegion(string? region) => region?.ToUpperInvariant() switch
    {
        "US" or "AS" or "GU" or "MP" or "PR" or "UM" or "VI"
            or "BS" or "BZ" or "KY" or "PW" or "LR" or "FM" or "MH" => UnitedStates,
        "GB" or "IM" or "JE" or "GG" => UnitedKingdom,
        _ => Metric,
    };

    /// <summary>
    /// The region a culture names, or metric when it names none — the invariant culture a
    /// <c>C</c> locale gives has no region to go by.
    /// </summary>
    public static WeatherUnits ForCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (culture.Name.Length == 0) return Metric;

        try
        {
            return ForRegion(new RegionInfo(culture.Name).TwoLetterISORegionName);
        }
        catch (ArgumentException)
        {
            // A neutral culture ("en", not "en-US") has no region.
            return Metric;
        }
    }

    /// <summary>
    /// The reader's choices, each falling back to their region's own when it has not been made.
    /// </summary>
    public static WeatherUnits Load(SettingsStore settings, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var region = ForCulture(culture ?? CultureInfo.CurrentCulture);

        return new WeatherUnits(
            settings.GetString(TemperatureKey) switch
            {
                "fahrenheit" => TemperatureUnit.Fahrenheit,
                "celsius" => TemperatureUnit.Celsius,
                _ => region.Temperature,
            },
            settings.GetString(SpeedKey) switch
            {
                "mph" => SpeedUnit.MilesPerHour,
                "kmh" => SpeedUnit.KilometresPerHour,
                "ms" => SpeedUnit.MetresPerSecond,
                "kn" => SpeedUnit.Knots,
                _ => region.Speed,
            },
            settings.GetString(PrecipitationKey) switch
            {
                "in" => PrecipitationUnit.Inches,
                "mm" => PrecipitationUnit.Millimetres,
                _ => region.Precipitation,
            },
            settings.GetString(PressureKey) switch
            {
                "inhg" => PressureUnit.InchesOfMercury,
                "hpa" => PressureUnit.Hectopascals,
                _ => region.Pressure,
            },
            settings.GetString(DistanceKey) switch
            {
                "mi" => DistanceUnit.Miles,
                "km" => DistanceUnit.Kilometres,
                _ => region.Distance,
            });
    }

    /// <summary>Writes every choice, so a later change of region does not move them.</summary>
    public void Save(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Set(TemperatureKey, Temperature == TemperatureUnit.Fahrenheit ? "fahrenheit" : "celsius");
        settings.Set(SpeedKey, Speed switch
        {
            SpeedUnit.MilesPerHour => "mph",
            SpeedUnit.MetresPerSecond => "ms",
            SpeedUnit.Knots => "kn",
            _ => "kmh",
        });
        settings.Set(PrecipitationKey, Precipitation == PrecipitationUnit.Inches ? "in" : "mm");
        settings.Set(PressureKey, Pressure == PressureUnit.InchesOfMercury ? "inhg" : "hpa");
        settings.Set(DistanceKey, Distance == DistanceUnit.Miles ? "mi" : "km");
    }

    public double TemperatureFrom(double celsius)
        => Temperature == TemperatureUnit.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    public double SpeedFrom(double kilometresPerHour) => Speed switch
    {
        SpeedUnit.MilesPerHour => kilometresPerHour / 1.609344,
        SpeedUnit.MetresPerSecond => kilometresPerHour / 3.6,
        SpeedUnit.Knots => kilometresPerHour / 1.852,
        _ => kilometresPerHour,
    };

    public double PrecipitationFrom(double millimetres)
        => Precipitation == PrecipitationUnit.Inches ? millimetres / 25.4 : millimetres;

    public double PressureFrom(double hectopascals)
        => Pressure == PressureUnit.InchesOfMercury ? hectopascals / 33.8639 : hectopascals;

    public double DistanceFrom(double metres)
        => Distance == DistanceUnit.Miles ? metres / 1609.344 : metres / 1000;

    public string TemperatureSymbol => Temperature == TemperatureUnit.Fahrenheit ? "°F" : "°C";

    public string SpeedSymbol => Speed switch
    {
        SpeedUnit.MilesPerHour => "mph",
        SpeedUnit.MetresPerSecond => "m/s",
        SpeedUnit.Knots => "kn",
        _ => "km/h",
    };

    public string PrecipitationSymbol => Precipitation == PrecipitationUnit.Inches ? "in" : "mm";

    public string PressureSymbol => Pressure == PressureUnit.InchesOfMercury ? "inHg" : "hPa";

    public string DistanceSymbol => Distance == DistanceUnit.Miles ? "mi" : "km";

    /// <summary>A temperature as a forecast writes one: whole degrees and the ring, "72°".</summary>
    public string FormatTemperature(double celsius, IFormatProvider? format = null)
        => Whole(TemperatureFrom(celsius)).ToString(format ?? CultureInfo.CurrentCulture) + "°";

    /// <summary>A wind speed in whole units, "12 mph".</summary>
    public string FormatSpeed(double kilometresPerHour, IFormatProvider? format = null)
        => Whole(SpeedFrom(kilometresPerHour)).ToString(format ?? CultureInfo.CurrentCulture) + " " + SpeedSymbol;

    /// <summary>
    /// An amount of rain: hundredths of an inch or tenths of a millimetre, which is the precision
    /// each is reported to — "0.12 in", "3.1 mm", and "0 mm" rather than "0.0 mm".
    /// </summary>
    public string FormatPrecipitation(double millimetres, IFormatProvider? format = null)
    {
        var culture = format ?? CultureInfo.CurrentCulture;
        var amount = PrecipitationFrom(millimetres);
        var text = Precipitation == PrecipitationUnit.Inches
            ? amount.ToString("0.00", culture)
            : amount.ToString("0.#", culture);
        return text + " " + PrecipitationSymbol;
    }

    /// <summary>Pressure the way each unit is read off a barometer: "29.92 inHg", "1013 hPa".</summary>
    public string FormatPressure(double hectopascals, IFormatProvider? format = null)
    {
        var culture = format ?? CultureInfo.CurrentCulture;
        return Pressure == PressureUnit.InchesOfMercury
            ? PressureFrom(hectopascals).ToString("0.00", culture) + " inHg"
            : Whole(hectopascals).ToString(culture) + " hPa";
    }

    /// <summary>
    /// How far a reader can see: whole units from ten up, where a tenth means nothing, and one
    /// decimal below it, where the difference between 0.2 and 0.8 is the difference between fog
    /// and mist.
    /// </summary>
    public string FormatDistance(double metres, IFormatProvider? format = null)
    {
        var culture = format ?? CultureInfo.CurrentCulture;
        var distance = DistanceFrom(metres);
        var text = distance >= 10 ? Whole(distance).ToString(culture) : distance.ToString("0.#", culture);
        return text + " " + DistanceSymbol;
    }

    /// <summary>
    /// Rounded half away from zero, as a thermometer reading is, and never "-0": a minus sign in
    /// front of nothing reads as a colder zero than the other kind.
    /// </summary>
    public static long Whole(double value)
    {
        var rounded = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }
}
