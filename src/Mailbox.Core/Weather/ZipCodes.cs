using System.Globalization;
using Mailbox.Core.Localization;

namespace Mailbox.Core.Weather;

/// <summary>
/// The United States' ZIP codes, each at its own point, read from the list the application
/// carries: GeoNames' postal codes (CC BY 4.0), built by <c>tools/weather-zipcodes.mjs</c>.
/// </summary>
/// <remarks>
/// The forecast service's place search knows most codes only as the city they belong to, so every
/// code in Phoenix finds the middle of Phoenix, thirty kilometres from some of them, and a few it
/// does not know at all. A code found here is where the reader lives, found without a request.
/// </remarks>
public static class ZipCodes
{
    /// <summary>The five digits of a ZIP code as a reader types one — "85083", "85083-1234" — or null for anything else.</summary>
    public static string? Parse(string? query)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 10 && text[5] == '-' && text[6..].All(char.IsAsciiDigit)) text = text[..5];
        return text.Length == 5 && text.All(char.IsAsciiDigit) ? text : null;
    }

    /// <summary>
    /// The place a code stands for, read from the list's lines — code, place, state, latitude and
    /// longitude, separated by tabs and sorted by code — or null when the list has no such code.
    /// </summary>
    public static WeatherPlace? Find(TextReader list, string zip)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(zip);
        while (list.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) continue;
            var order = string.CompareOrdinal(line, 0, zip, 0, zip.Length);
            if (order < 0) continue;
            if (order > 0 || line.Length <= zip.Length || line[zip.Length] != '\t') return null;

            var field = line.Split('\t');
            if (field.Length < 5
                || !double.TryParse(field[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.TryParse(field[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                return null;
            }

            return new WeatherPlace
            {
                Id = "zip:US:" + zip,
                Name = field[1],
                Region = field[2],
                Country = Strings.T("United States"),
                CountryCode = "US",
                Latitude = latitude,
                Longitude = longitude,
                Postcode = zip,
            };
        }

        return null;
    }
}
