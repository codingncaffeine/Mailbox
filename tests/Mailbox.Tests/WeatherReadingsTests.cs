using System.Globalization;
using Mailbox.Core.Weather;

namespace Mailbox.Tests;

public class WeatherReadingsTests
{
    private static readonly CultureInfo American = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(0, "N")]
    [InlineData(11.24, "N")]
    [InlineData(11.25, "NNE")]
    [InlineData(237, "WSW")]
    [InlineData(225, "SW")]
    [InlineData(359, "N")]
    [InlineData(-90, "W")]
    [InlineData(720, "N")]
    public void TheWindIsNamedForWhereItComesFrom(double degrees, string point)
    {
        Assert.Equal(point, WeatherReadings.Compass(degrees));
    }

    [Theory]
    [InlineData(0, UvLevel.Low)]
    [InlineData(2.4, UvLevel.Low)]
    [InlineData(3, UvLevel.Moderate)]
    [InlineData(6, UvLevel.High)]
    [InlineData(8, UvLevel.VeryHigh)]
    [InlineData(11, UvLevel.Extreme)]
    public void TheUvIndexFollowsTheWhosSteps(double index, UvLevel level)
    {
        Assert.Equal(level, WeatherReadings.Uv(index));
    }

    [Fact]
    public void FeelingColderOrWarmerSaysWhy()
    {
        Assert.Equal("Similar to the actual temperature.", WeatherReadings.FeelsLike(20, 21, 5));
        Assert.Equal("The wind is making it feel colder.", WeatherReadings.FeelsLike(2, -4, 30));
        Assert.Equal("Humidity is making it feel warmer.", WeatherReadings.FeelsLike(30, 35, 5));
    }

    [Fact]
    public void ThePressureTrendReadsThreeHoursAhead()
    {
        var hours = WeatherFixtures.BeverlyHills().Hourly;
        var at = new DateTime(2026, 9, 27, 19, 20, 0);
        var now = hours.First(h => h.Time == new DateTime(2026, 9, 27, 19, 0, 0)).Pressure!.Value;
        var later = hours.First(h => h.Time == new DateTime(2026, 9, 27, 22, 0, 0)).Pressure!.Value;

        var expected = (later - now) switch { >= 1 => PressureTrend.Rising, <= -1 => PressureTrend.Falling, _ => PressureTrend.Steady };
        Assert.Equal(expected, WeatherReadings.Trend(hours, at));
        Assert.Equal(PressureTrend.Steady, WeatherReadings.Trend([], at));
    }

    /// <summary>
    /// A time as the culture writes it. Not a literal: current ICU puts a narrow no-break space
    /// before "PM", and a test with an ordinary space there fails on a string that looks the same.
    /// </summary>
    private static string At(int hour, int minute) => new DateTime(2026, 9, 27, hour, minute, 0).ToString("t", American);

    private static List<PrecipitationStep> Steps(params double[] amounts)
        => [.. amounts.Select((a, i) => new PrecipitationStep(new DateTime(2026, 9, 27, 19, 0, 0).AddMinutes(15 * i), a))];

    [Fact]
    public void TheNextTwoHoursAreOneSentence()
    {
        var now = new DateTime(2026, 9, 27, 19, 5, 0);

        Assert.Equal("No rain expected in the next two hours.", WeatherReadings.NextTwoHours(Steps(0, 0, 0, 0, 0, 0, 0, 0), now, 15, American));
        Assert.Equal($"Rain starting around {At(19, 30)}.", WeatherReadings.NextTwoHours(Steps(0, 0.05, 0.4, 0.8, 0, 0, 0, 0), now, 15, American));
        Assert.Equal($"Rain stopping around {At(19, 45)}.", WeatherReadings.NextTwoHours(Steps(0.6, 0.5, 0.2, 0, 0, 0, 0, 0), now, 15, American));
        Assert.Equal("Rain for the next two hours.", WeatherReadings.NextTwoHours(Steps(1, 1, 1, 1, 1, 1, 1, 1), now, 15, American));
        Assert.Equal($"Snow starting around {At(19, 15)}.", WeatherReadings.NextTwoHours(Steps(0, 0.3, 0, 0, 0, 0, 0, 0), now, -2, American));
        Assert.Null(WeatherReadings.NextTwoHours([], now, 15, American));
    }

    /// <summary>A step already over is not the next two hours: at 7:20 the 7:00 step has gone.</summary>
    [Fact]
    public void StepsAlreadyOverAreLeftBehind()
    {
        var steps = Steps(2, 0, 0, 0, 0, 0, 0, 0);

        Assert.Equal($"Rain stopping around {At(19, 15)}.", WeatherReadings.NextTwoHours(steps, new DateTime(2026, 9, 27, 19, 10, 0), 15, American));
        Assert.Equal("No rain expected in the next two hours.", WeatherReadings.NextTwoHours(steps, new DateTime(2026, 9, 27, 19, 20, 0), 15, American));
    }
}

public class WeatherNextTwoHoursChanceTests
{
    private static readonly System.Globalization.CultureInfo American = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    private static List<PrecipitationStep> Dry()
        => [.. Enumerable.Range(0, 8).Select(i => new PrecipitationStep(new DateTime(2026, 9, 27, 19, 0, 0).AddMinutes(15 * i), 0))];

    /// <summary>Dry steps beside a likely hour say the chance rather than promising nothing.</summary>
    [Fact]
    public void ALikelyHourIsSaidEvenWhenTheStepsAreDry()
    {
        var now = new DateTime(2026, 9, 27, 19, 5, 0);

        Assert.Equal("A 56% chance of rain in the next two hours.", WeatherReadings.NextTwoHours(Dry(), now, 15, American, likeliest: 56));
        Assert.Equal("No rain expected in the next two hours.", WeatherReadings.NextTwoHours(Dry(), now, 15, American, likeliest: 20));
        Assert.Equal("A 40% chance of snow in the next two hours.", WeatherReadings.NextTwoHours(Dry(), now, -3, American, likeliest: 40));
    }
}
