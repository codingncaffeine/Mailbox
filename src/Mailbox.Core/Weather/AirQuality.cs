using Mailbox.Core.Localization;

namespace Mailbox.Core.Weather;

/// <summary>The index an air quality reading is told on.</summary>
public enum AirQualityScale
{
    /// <summary>The United States Environmental Protection Agency's Air Quality Index, 0 to 500.</summary>
    UnitedStates,

    /// <summary>The European Environment Agency's European Air Quality Index, from 0 to beyond 100.</summary>
    European,
}

/// <summary>A pollutant an index is worked out from.</summary>
public enum AirPollutant
{
    FineParticles,
    CoarseParticles,
    Ozone,
    NitrogenDioxide,
    SulfurDioxide,
    CarbonMonoxide,
}

/// <summary>A reading on an index, and the pollutant that set it — the one whose own index is highest.</summary>
public readonly record struct AirIndex(int Value, AirPollutant? Leading);

/// <summary>The air at a place, as the service reads it for the current hour.</summary>
public sealed record AirQuality
{
    public required DateTimeOffset Fetched { get; init; }

    /// <summary>The hour the reading is for, on the place's wall clock.</summary>
    public DateTime Time { get; init; }

    public AirIndex? UnitedStates { get; init; }
    public AirIndex? European { get; init; }

    public AirIndex? On(AirQualityScale scale) => scale == AirQualityScale.European ? European : UnitedStates;
}

/// <summary>One of an index's six bands: its highest reading, its name, what to do, and its colour as 0xAARRGGBB.</summary>
public sealed record AirQualityBand(int Upper, string Name, string Advice, uint Colour);

/// <summary>
/// The two air quality indices a forecast can be told on, with the bands, words and colours their
/// authorities give them.
/// </summary>
/// <remarks>
/// A reader reads air quality on the index of the place they are looking at, as people there do:
/// the European index for a place in Europe, the United States' everywhere else, which is the
/// index most of the world's air quality services report. Each keeps its authority's own colours —
/// the Environmental Protection Agency's green to maroon, the Environment Agency's turquoise to
/// violet — because they are part of the index as its public learns it; like the map's legends,
/// they are data rather than the theme's to choose. The band edges are those the service computes
/// its indices against; the European advice is the Environment Agency's own, and the American
/// follows the Protection Agency's health messages.
/// </remarks>
public static class AirQualityIndex
{
    /// <summary>
    /// The countries on the European index: the European Environment Agency's members and
    /// cooperating countries, and the rest of Europe with its dependencies.
    /// </summary>
    private static readonly HashSet<string> Europe = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IS", "IE", "IT",
        "LV", "LI", "LT", "LU", "MT", "NL", "NO", "PL", "PT", "RO", "SK", "SI", "ES", "SE", "CH", "TR",
        "AL", "BA", "ME", "MK", "RS", "XK",
        "GB", "AD", "MC", "SM", "VA", "UA", "MD", "BY", "RU",
        "FO", "GI", "IM", "JE", "GG", "AX", "SJ",
    };

    private static readonly int[] UnitedStatesUppers = [50, 100, 150, 200, 300, 500];
    private static readonly int[] EuropeanUppers = [20, 40, 60, 80, 100, int.MaxValue];

    /// <summary>The Environmental Protection Agency's colours: green, yellow, orange, red, purple, maroon.</summary>
    private static readonly uint[] UnitedStatesColours = [0xFF00E400, 0xFFFFFF00, 0xFFFF7E00, 0xFFFF0000, 0xFF8F3F97, 0xFF7E0023];

    /// <summary>The European Environment Agency's colours, from its own index's pages.</summary>
    private static readonly uint[] EuropeanColours = [0xFF50F0E6, 0xFF50CCAA, 0xFFF0E641, 0xFFFF5050, 0xFF960032, 0xFF7D2181];

    public static AirQualityScale For(string countryCode)
        => Europe.Contains(countryCode ?? string.Empty) ? AirQualityScale.European : AirQualityScale.UnitedStates;

    /// <summary>What the index is called beside a reading.</summary>
    public static string Name(AirQualityScale scale) => scale == AirQualityScale.European
        ? Strings.T("European AQI")
        : Strings.T("US AQI");

    public static IReadOnlyList<AirQualityBand> Bands(AirQualityScale scale)
    {
        var (uppers, colours) = scale == AirQualityScale.European ? (EuropeanUppers, EuropeanColours) : (UnitedStatesUppers, UnitedStatesColours);
        return [.. Enumerable.Range(0, 6).Select(i => new AirQualityBand(uppers[i], BandName(scale, i), Advice(scale, i), colours[i]))];
    }

    /// <summary>Which of the index's six bands a reading is in, from 0, the cleanest.</summary>
    public static int Level(AirQualityScale scale, int value)
    {
        var uppers = scale == AirQualityScale.European ? EuropeanUppers : UnitedStatesUppers;
        for (var i = 0; i < uppers.Length - 1; i++)
        {
            if (value <= uppers[i]) return i;
        }

        return uppers.Length - 1;
    }

    public static AirQualityBand Band(AirQualityScale scale, int value) => Bands(scale)[Level(scale, value)];

    /// <summary>The index's colours, cleanest first, for drawing its bands.</summary>
    public static IReadOnlyList<uint> Colours(AirQualityScale scale) => scale == AirQualityScale.European ? EuropeanColours : UnitedStatesColours;

    /// <summary>
    /// Where a reading falls along a bar of the index's six bands drawn equally wide, from 0 to 1.
    /// A band with no top — the European index's last — is given the width of the one below it.
    /// </summary>
    public static double Position(AirQualityScale scale, int value)
    {
        var uppers = scale == AirQualityScale.European ? EuropeanUppers : UnitedStatesUppers;
        var level = Level(scale, value);
        var lower = level == 0 ? 0 : uppers[level - 1];
        var upper = uppers[level] != int.MaxValue ? uppers[level] : lower + (lower - uppers[level - 2]);
        var within = Math.Clamp((double)(value - lower) / (upper - lower), 0, 1);
        return (level + within) / uppers.Length;
    }

    /// <summary>A pollutant as a reader knows it.</summary>
    public static string Name(AirPollutant pollutant) => pollutant switch
    {
        AirPollutant.FineParticles => Strings.T("fine particles (PM2.5)"),
        AirPollutant.CoarseParticles => Strings.T("coarse particles (PM10)"),
        AirPollutant.Ozone => Strings.T("ozone"),
        AirPollutant.NitrogenDioxide => Strings.T("nitrogen dioxide"),
        AirPollutant.SulfurDioxide => Strings.T("sulfur dioxide"),
        _ => Strings.T("carbon monoxide"),
    };

    private static string BandName(AirQualityScale scale, int level) => scale == AirQualityScale.European
        ? level switch
        {
            0 => Strings.T("Good"),
            1 => Strings.T("Fair"),
            2 => Strings.T("Moderate"),
            3 => Strings.T("Poor"),
            4 => Strings.T("Very poor"),
            _ => Strings.T("Extremely poor"),
        }
        : level switch
        {
            0 => Strings.T("Good"),
            1 => Strings.T("Moderate"),
            2 => Strings.T("Unhealthy for sensitive groups"),
            3 => Strings.T("Unhealthy"),
            4 => Strings.T("Very unhealthy"),
            _ => Strings.T("Hazardous"),
        };

    private static string Advice(AirQualityScale scale, int level) => scale == AirQualityScale.European
        ? level switch
        {
            0 => Strings.T("The air quality is good. Enjoy your usual outdoor activities."),
            1 => Strings.T("Enjoy your usual outdoor activities."),
            2 => Strings.T("Enjoy your usual outdoor activities. Sensitive people should consider reducing intense activities outdoors if they have symptoms."),
            3 => Strings.T("Consider reducing intense activities outdoors if you have symptoms such as sore eyes, a cough or a sore throat."),
            4 => Strings.T("Consider reducing physical activities outdoors if you have symptoms such as sore eyes, a cough or a sore throat."),
            _ => Strings.T("Reduce physical activities outdoors."),
        }
        : level switch
        {
            0 => Strings.T("Air quality is satisfactory, and air pollution poses little or no risk."),
            1 => Strings.T("Air quality is acceptable, though people unusually sensitive to air pollution may be at some risk."),
            2 => Strings.T("People with heart or lung disease, older adults and children may feel its effects; others are less likely to."),
            3 => Strings.T("Anyone may begin to feel its effects, and sensitive groups more seriously. Take it easier outdoors."),
            4 => Strings.T("A health alert: the risk of health effects is raised for everyone. Keep activity indoors."),
            _ => Strings.T("A health warning of emergency conditions: everyone is likely to be affected. Stay indoors."),
        };
}
