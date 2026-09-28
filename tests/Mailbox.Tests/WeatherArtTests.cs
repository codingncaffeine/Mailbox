using System.Text.RegularExpressions;
using Mailbox.Core.Weather;

namespace Mailbox.Tests;

/// <summary>
/// Every weather picture the code names is one that ships, and every one that ships is the shape
/// the interface draws.
/// </summary>
/// <remarks>
/// A picture is named by a string — <c>"night/partly-cloudy-night"</c> — and a string with a typo
/// in it compiles, runs, and draws nothing. So the names are read out of the source itself and
/// held against the files, the way the translation template is held against the calls.
/// </remarks>
public partial class WeatherArtTests
{
    [GeneratedRegex("\"(day|night)/([a-z0-9-]+)\"")]
    private static partial Regex PictureName();

    private static string Root() => Path.GetFullPath(Path.Combine(WeatherFixtures.Directory(), "..", "..", ".."));

    [Fact]
    public void EveryPictureTheCodeNamesShips()
    {
        var art = Path.Combine(Root(), "assets", "weather");
        var named = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(p => PictureName().Matches(File.ReadAllText(p)).Select(m => $"{m.Groups[1].Value}/{m.Groups[2].Value}"))
            .Distinct()
            .ToList();

        Assert.NotEmpty(named);
        var missing = named.Where(n => !File.Exists(Path.Combine(art, n + ".png"))).ToList();
        Assert.True(missing.Count == 0, "Named but not shipped: " + string.Join(", ", missing));
    }

    /// <summary>The table's answers, checked directly as well as through the source scan.</summary>
    [Fact]
    public void EveryConditionAndWarningResolvesToAFile()
    {
        var art = Path.Combine(Root(), "assets", "weather");
        var keys = WeatherConditions.KnownCodes
            .SelectMany(code => new[] { true, false }.SelectMany(day => new[]
            {
                WeatherConditions.Sky(code, day).Icon,
                WeatherConditions.Classify(code, day, windKmh: 50).Icon,
                WeatherConditions.Classify(code, day, windKmh: 70, gustKmh: 80, visibilityMetres: 100).Icon,
            }))
            .Concat(new[] { "Tornado Warning", "Hurricane Watch", "Tropical Storm Warning", "Tsunami Warning",
                "Earthquake Warning", "Ashfall Advisory", "Dust Storm Warning", "Dense Smoke Advisory", "Dense Fog Advisory",
                "Severe Thunderstorm Warning", "Blizzard Warning", "Ice Storm Warning", "Winter Weather Advisory",
                "Coastal Flood Warning", "Flood Watch", "Extreme Heat Warning", "Extreme Cold Warning", "High Wind Warning" }
                .Select(e => WeatherConditions.AlertIcon(e) ?? throw new InvalidOperationException(e)))
            .Distinct();

        Assert.All(keys, key => Assert.True(File.Exists(Path.Combine(art, key + ".png")), key));
    }

    /// <summary>
    /// Square, and one size per set: each set is drawn into the same box, so a picture off the
    /// set's size would draw larger or smaller than its neighbours.
    /// </summary>
    [Theory]
    [InlineData("day", 120)]
    [InlineData("night", 146)]
    public void EachSetIsOneSquareSize(string set, int side)
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "assets", "weather", set), "*.png");

        Assert.NotEmpty(files);
        Assert.All(files, file =>
        {
            using var png = File.OpenRead(file);
            Span<byte> header = stackalloc byte[24];
            png.ReadExactly(header);
            var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            Assert.True(width == side && height == side, $"{Path.GetFileName(file)} is {width}x{height}");
        });
    }
}
