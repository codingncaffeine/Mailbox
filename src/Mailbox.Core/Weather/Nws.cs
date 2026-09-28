using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mailbox.Core.Weather;

/// <summary>What the Weather Service knows a point by: its office, grid square and radar.</summary>
/// <param name="Office">The forecast office's three-letter id: "LOX".</param>
/// <param name="RadarStation">The nearest radar: "KSOX".</param>
/// <param name="ForecastZone">The zone warnings are written for: "CAZ368".</param>
/// <param name="County">The county code: "CAC037".</param>
/// <param name="City">The nearest named place the office uses.</param>
/// <param name="State">Its state: "CA".</param>
public sealed record NwsPoint(
    string Office,
    int GridX,
    int GridY,
    string RadarStation,
    string ForecastZone,
    string County,
    string City,
    string State);

/// <summary>How serious a warning is, in the Common Alerting Protocol's own four steps.</summary>
public enum AlertSeverity
{
    Unknown,
    Minor,
    Moderate,
    Severe,
    Extreme,
}

/// <summary>One watch, warning, advisory or statement in force for a point.</summary>
public sealed record WeatherAlert
{
    public required string Id { get; init; }

    /// <summary>What it is: "Flood Watch", "Tornado Warning".</summary>
    public required string Event { get; init; }
    public required AlertSeverity Severity { get; init; }
    public string Headline { get; init; } = string.Empty;
    public string Urgency { get; init; } = string.Empty;
    public string Certainty { get; init; } = string.Empty;
    public DateTimeOffset? Onset { get; init; }

    /// <summary>When it ends, or when this issue of it expires when no end is known.</summary>
    public DateTimeOffset? Ends { get; init; }
    public string Sender { get; init; } = string.Empty;
    public string Area { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Instruction { get; init; } = string.Empty;
}

/// <summary>What a block of a discussion is, which decides how it is set.</summary>
public enum DiscussionBlockKind
{
    Paragraph,
    Bullet,

    /// <summary>Columns lined up with spaces, which only read in a fixed-width face.</summary>
    Table,
}

public sealed record DiscussionBlock(DiscussionBlockKind Kind, string Text);

/// <summary>One section of a discussion: "Key Messages", "Short Term (Through Monday)".</summary>
public sealed record DiscussionSection(string Title, string Issued, IReadOnlyList<DiscussionBlock> Blocks);

/// <summary>
/// An office's Area Forecast Discussion: the forecasters writing, a few times a day, about what
/// they expect and how sure they are. It is the nearest thing to local weather news there is.
/// </summary>
public sealed record ForecastDiscussion(
    string Office,
    string Issuer,
    DateTimeOffset? Issued,
    IReadOnlyList<DiscussionSection> Sections);

/// <summary>
/// The National Weather Service's API: free, keyless, and for the United States and its
/// territories only.
/// </summary>
/// <remarks>
/// It asks every client to name itself in its User-Agent, and it says in its own documentation
/// that requests made directly from clients are unlikely to reach its limits — which is how this
/// application makes them. Everything it adds is an extra over the forecast: when it fails, the
/// module goes on without warnings and discussion rather than failing with it.
/// </remarks>
public static partial class Nws
{
    public const string Endpoint = "https://api.weather.gov";

    /// <summary>The Weather Service's radar pages, which serve each station's latest loop.</summary>
    public const string RadarEndpoint = "https://radar.weather.gov/ridge/standard";

    /// <summary>
    /// The point lookup. Four decimal places, because the service answers a finer point with a
    /// redirect to the rounded one.
    /// </summary>
    public static string PointUrl(double latitude, double longitude)
        => $"{Endpoint}/points/{OpenMeteo.Coordinate(Math.Round(latitude, 4))},{OpenMeteo.Coordinate(Math.Round(longitude, 4))}";

    public static string AlertsUrl(double latitude, double longitude)
        => $"{Endpoint}/alerts/active?point={OpenMeteo.Coordinate(Math.Round(latitude, 4))},{OpenMeteo.Coordinate(Math.Round(longitude, 4))}";

    public static string DiscussionUrl(string office)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(office);
        return $"{Endpoint}/products/types/AFD/locations/{Uri.EscapeDataString(office.ToUpperInvariant())}/latest";
    }

    /// <summary>A station's latest loop: ten frames as an animated picture, the map drawn in.</summary>
    public static string RadarLoopUrl(string station)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(station);
        return $"{RadarEndpoint}/{Uri.EscapeDataString(station.ToUpperInvariant())}_loop.gif";
    }

    /// <summary>
    /// Whether a response says the point is outside what the service covers — its answer for
    /// anywhere outside the United States — rather than that something went wrong.
    /// </summary>
    public static bool IsOutsideCoverage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && Text(document.RootElement, "type") is { } type
                   && type.EndsWith("/InvalidPoint", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <exception cref="FormatException">The text is not a point.</exception>
    public static NwsPoint ParsePoint(string json)
    {
        using var document = Parse(json);
        if (!document.RootElement.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The point has no properties.");
        }

        var office = Text(p, "cwa") ?? Text(p, "gridId") ?? throw new FormatException("The point has no office.");
        var city = string.Empty;
        var state = string.Empty;
        if (p.TryGetProperty("relativeLocation", out var near) && near.TryGetProperty("properties", out var nearby))
        {
            city = Text(nearby, "city") ?? string.Empty;
            state = Text(nearby, "state") ?? string.Empty;
        }

        return new NwsPoint(
            office,
            Whole(p, "gridX"),
            Whole(p, "gridY"),
            Text(p, "radarStation") ?? string.Empty,
            LastSegment(Text(p, "forecastZone")),
            LastSegment(Text(p, "county")),
            city,
            state);
    }

    /// <summary>
    /// The alerts in force, the most serious first and, among equals, the soonest. Tests and
    /// exercises, which the service also carries, are left out: a reader acts on what they see.
    /// </summary>
    /// <exception cref="FormatException">The text is not an alert collection.</exception>
    public static IReadOnlyList<WeatherAlert> ParseAlerts(string json)
    {
        using var document = Parse(json);
        if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The alerts have no features.");
        }

        var alerts = new List<WeatherAlert>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            if (Text(p, "status") is { } status && !string.Equals(status, "Actual", StringComparison.Ordinal)) continue;
            if (Text(p, "event") is not { Length: > 0 } name) continue;

            alerts.Add(new WeatherAlert
            {
                Id = Text(p, "id") ?? Text(feature, "id") ?? name,
                Event = name,
                Severity = Text(p, "severity") switch
                {
                    "Extreme" => AlertSeverity.Extreme,
                    "Severe" => AlertSeverity.Severe,
                    "Moderate" => AlertSeverity.Moderate,
                    "Minor" => AlertSeverity.Minor,
                    _ => AlertSeverity.Unknown,
                },
                Headline = Text(p, "headline") ?? string.Empty,
                Urgency = Text(p, "urgency") ?? string.Empty,
                Certainty = Text(p, "certainty") ?? string.Empty,
                Onset = Moment(p, "onset") ?? Moment(p, "effective"),
                Ends = Moment(p, "ends") ?? Moment(p, "expires"),
                Sender = Text(p, "senderName") ?? string.Empty,
                Area = Text(p, "areaDesc") ?? string.Empty,
                Description = Text(p, "description") ?? string.Empty,
                Instruction = Text(p, "instruction") ?? string.Empty,
            });
        }

        return
        [
            .. alerts
                .OrderByDescending(a => a.Severity)
                .ThenBy(a => a.Onset ?? DateTimeOffset.MaxValue),
        ];
    }

    /// <summary>
    /// Reads an office's latest discussion out of its product: the heading lines, then each
    /// <c>.TITLE...</c> section up to its <c>&amp;&amp;</c>, stopping at the <c>$$</c> that ends
    /// the product and leaves only the forecasters' initials after it.
    /// </summary>
    /// <remarks>
    /// The text arrives hard-wrapped at seventy columns for the teletypes it was once written
    /// for. Paragraphs are joined back up so they reflow to whatever width they are shown at;
    /// a bulleted list keeps its bullets; and a block whose columns are lined up with spaces is
    /// kept exactly as written, because reflowing a table destroys it.
    /// </remarks>
    /// <exception cref="FormatException">The text is not a product.</exception>
    public static ForecastDiscussion ParseDiscussion(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var text = (Text(root, "productText") ?? throw new FormatException("The product has no text.")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var office = Text(root, "issuingOffice") ?? string.Empty;
        if (office.Length == 4 && office[0] == 'K') office = office[1..];

        var lines = text.Split('\n');
        var issuer = lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("National Weather Service", StringComparison.Ordinal)) ?? string.Empty;

        var sections = new List<DiscussionSection>();
        string? title = null;
        var issued = string.Empty;
        var body = new List<string>();

        void Close()
        {
            if (title is not null)
            {
                var blocks = Blocks(body);
                if (blocks.Count > 0) sections.Add(new DiscussionSection(title, issued, blocks));
            }

            title = null;
            issued = string.Empty;
            body.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Trim() == "$$") break;
            if (line.Trim() == "&&")
            {
                Close();
                continue;
            }

            if (SectionHeading().Match(line) is { Success: true } heading)
            {
                Close();
                title = Title(heading.Groups["title"].Value);
                var rest = heading.Groups["rest"].Value.Trim();

                // ".SYNOPSIS...27/530 PM." is a time stamp; anything else is the first words.
                if (rest.Length > 0 && !StampAfterHeading().IsMatch(rest)) body.Add(rest);
                continue;
            }

            if (title is null) continue;

            if (issued.Length == 0 && body.All(string.IsNullOrWhiteSpace)
                && line.TrimStart().StartsWith("Issued at ", StringComparison.Ordinal))
            {
                issued = line.Trim();
                continue;
            }

            body.Add(line);
        }

        Close();
        return new ForecastDiscussion(office, issuer, Moment(root, "issuanceTime"), sections);
    }

    /// <summary>A section heading: a line starting with a dot, a capitalised title, and three dots.</summary>
    [GeneratedRegex(@"^\.(?<title>[A-Z0-9][A-Z0-9 /&()'.,-]*?)\.\.\.(?<rest>.*)$")]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^\d{1,2}/\d{3,4} ?(AM|PM)\.?$")]
    private static partial Regex StampAfterHeading();

    /// <summary>A run of three spaces inside a line, which is how a table lines its columns up.</summary>
    [GeneratedRegex(@"\S {3,}\S")]
    private static partial Regex ColumnGap();

    [GeneratedRegex(@"^\s*([-*•])\s+")]
    private static partial Regex BulletMark();

    private static List<DiscussionBlock> Blocks(List<string> body)
    {
        var blocks = new List<DiscussionBlock>();
        var chunk = new List<string>();

        void Flush()
        {
            if (chunk.Count == 0) return;

            var tableRows = chunk.Count(l => ColumnGap().IsMatch(l.Trim()));
            if (chunk.Count >= 2 && tableRows * 2 >= chunk.Count)
            {
                blocks.Add(new DiscussionBlock(DiscussionBlockKind.Table, string.Join('\n', chunk)));
            }
            else
            {
                var current = new StringBuilder();
                var kind = DiscussionBlockKind.Paragraph;
                foreach (var line in chunk)
                {
                    if (BulletMark().Match(line) is { Success: true } mark)
                    {
                        if (current.Length > 0) blocks.Add(new DiscussionBlock(kind, Squash(current.ToString())));
                        current.Clear().Append(line[mark.Length..]);
                        kind = DiscussionBlockKind.Bullet;
                        continue;
                    }

                    current.Append(' ').Append(line.Trim());
                }

                if (current.Length > 0) blocks.Add(new DiscussionBlock(kind, Squash(current.ToString())));
            }

            chunk.Clear();
        }

        foreach (var line in body)
        {
            if (string.IsNullOrWhiteSpace(line)) Flush();
            else chunk.Add(line);
        }

        Flush();
        return blocks;
    }

    private static string Squash(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// "SHORT TERM /THROUGH MONDAY/" to "Short Term (Through Monday)", and the office's own
    /// "PUB WATCHES/WARNINGS/ADVISORIES" to "Watches/Warnings/Advisories" — the reader knows
    /// which office they are reading.
    /// </summary>
    public static string Title(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        var text = heading.Trim();

        if (Regex.Match(text, "^[A-Z]{3} (WATCHES/WARNINGS/ADVISORIES)$") is { Success: true } own)
        {
            text = own.Groups[1].Value;
        }

        var qualifier = string.Empty;
        if (Regex.Match(text, @"^(?<main>.*?)\s*/(?<when>[^/]+)/\s*$") is { Success: true } split)
        {
            text = split.Groups["main"].Value;
            qualifier = split.Groups["when"].Value;
        }

        var titled = TitleCase(text);
        return qualifier.Length > 0 ? $"{titled} ({TitleCase(qualifier)})" : titled;
    }

    /// <summary>Terms of the trade that stay as they are written: "00Z TAFs", not "00z Tafs".</summary>
    private static readonly Dictionary<string, string> KeptAsWritten = new(StringComparer.Ordinal)
    {
        ["TAF"] = "TAF", ["TAFS"] = "TAFs", ["VFR"] = "VFR", ["MVFR"] = "MVFR", ["IFR"] = "IFR",
        ["LIFR"] = "LIFR", ["UTC"] = "UTC", ["NWS"] = "NWS", ["QPF"] = "QPF", ["PWAT"] = "PWAT",
    };

    private static readonly HashSet<string> SmallWords =
        new(["and", "or", "of", "the", "to", "for", "in", "on", "at", "through", "with"], StringComparer.Ordinal);

    private static string TitleCase(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = string.Join('/', words[i].Split('/').Select((part, j) =>
            {
                if (KeptAsWritten.TryGetValue(part, out var kept)) return kept;
                if (part.Any(char.IsDigit)) return part;
                var lower = part.ToLower(CultureInfo.InvariantCulture);
                if ((i > 0 || j > 0) && SmallWords.Contains(lower)) return lower;
                return lower.Length > 0 ? char.ToUpper(lower[0], CultureInfo.InvariantCulture) + lower[1..] : lower;
            }));
        }

        return string.Join(' ', words);
    }

    private static JsonDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw new FormatException("The response is not an object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("The response is not JSON.", ex);
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Whole(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;

    private static DateTimeOffset? Moment(JsonElement element, string name)
        => Text(element, name) is { } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? moment
            : null;

    private static string LastSegment(string? url)
        => url is { Length: > 0 } ? url[(url.LastIndexOf('/') + 1)..] : string.Empty;
}
