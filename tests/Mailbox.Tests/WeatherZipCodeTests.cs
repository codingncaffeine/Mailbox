using System.IO.Compression;
using System.Text;
using Mailbox.Core.Weather;
using Mailbox.Protocols;

namespace Mailbox.Tests;

public class WeatherZipCodeTests
{
    private static StreamReader List()
    {
        var stream = typeof(WeatherReceiver).Assembly.GetManifestResourceStream("Mailbox.Protocols.us-zip.tsv.gz");
        Assert.NotNull(stream);
        return new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
    }

    [Theory]
    [InlineData("85083", "85083")]
    [InlineData(" 85083 ", "85083")]
    [InlineData("85083-1234", "85083")]
    [InlineData("00501", "00501")]
    [InlineData("8508", null)]
    [InlineData("850831", null)]
    [InlineData("85083-12", null)]
    [InlineData("Phoenix", null)]
    [InlineData("SW1A", null)]
    [InlineData("M5V 2T6", null)]
    public void OnlyFiveDigitsOrZipPlusFourReadAsAZipCode(string typed, string? zip)
        => Assert.Equal(zip, ZipCodes.Parse(typed));

    /// <summary>Every state and the District of Columbia, every line readable, in order, and nothing outside the country.</summary>
    [Fact]
    public void TheListTheBuildCarriesIsWholeAndInOrder()
    {
        using var list = List();
        var codes = new List<string>();
        var states = new HashSet<string>(StringComparer.Ordinal);
        while (list.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) continue;
            var field = line.Split('\t');
            Assert.True(field.Length == 5, line);
            Assert.Matches("^[0-9]{5}$", field[0]);
            Assert.InRange(double.Parse(field[3], System.Globalization.CultureInfo.InvariantCulture), 18, 72);
            // West of the Atlantic coast, or past the date line: Wake Island, filed under Hawaii.
            var longitude = double.Parse(field[4], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(longitude is >= -180 and <= -65 or >= 160 and <= 180, line);
            codes.Add(field[0]);
            states.Add(field[2]);
        }

        Assert.InRange(codes.Count, 40_000, 45_000);
        Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(51, states.Count);
        Assert.Contains("District of Columbia", states);
    }

    [Theory]
    [InlineData("00501", "Holtsville", "New York")]
    [InlineData("85083", "Phoenix", "Arizona")]
    [InlineData("90210", "Beverly Hills", "California")]
    [InlineData("99950", "Ketchikan", "Alaska")]
    public void ACodeIsFoundAtItsOwnPlace(string zip, string name, string state)
    {
        using var list = List();

        var place = ZipCodes.Find(list, zip);

        Assert.NotNull(place);
        Assert.Equal((name, state, zip), (place.Name, place.Region, place.Postcode));
    }

    /// <summary>A code the list does not have — below its first, between two, past its last, or a military one — finds nothing.</summary>
    [Theory]
    [InlineData("00000")]
    [InlineData("85084")]
    [InlineData("99999")]
    [InlineData("09001")]
    public void ACodeTheListLacksFindsNothing(string zip)
    {
        using var list = List();

        Assert.Null(ZipCodes.Find(list, zip));
    }
}
