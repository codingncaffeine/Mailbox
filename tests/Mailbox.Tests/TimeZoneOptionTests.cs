using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// The second time zone the day and week views can draw beside their own: how it is written, and
/// what the Calendar page's rows come to.
/// </summary>
public class TimeZoneOptionTests
{
    private static CalendarOptions Options(out SettingsStore settings)
    {
        settings = SettingsStore.Transient();
        return new CalendarOptions(settings);
    }

    [Fact]
    public void NoSecondZoneUntilOneIsAskedFor()
    {
        var options = Options(out var settings);
        Assert.False(options.ShowSecondTimeZone);
        Assert.Null(options.SecondTimeZone);

        settings.Set(CalendarOptions.SecondTimeZoneIdKey, "America/New_York");
        Assert.Null(options.SecondTimeZone);

        settings.Set(CalendarOptions.SecondTimeZoneShownKey, true);
        Assert.Equal("America/New_York", options.SecondTimeZone?.Id);
    }

    /// <summary>
    /// A zone this machine has never heard of is no zone rather than a view that will not draw:
    /// the setting travels between machines, and the zone databases do not agree.
    /// </summary>
    [Fact]
    public void AZoneThisMachineDoesNotKnowIsSimplyNotShown()
    {
        var options = Options(out var settings);
        settings.Set(CalendarOptions.SecondTimeZoneShownKey, true);
        settings.Set(CalendarOptions.SecondTimeZoneIdKey, "Mars/Olympus_Mons");

        Assert.Null(options.SecondTimeZone);
    }

    [Fact]
    public void TheColumnsAreHeadedByTheirLabelsOrTheirOffsets()
    {
        var options = Options(out var settings);
        Assert.Equal(string.Empty, options.TimeZoneLabel);
        Assert.Equal(string.Empty, options.SecondTimeZoneLabel);

        settings.Set(CalendarOptions.TimeZoneLabelKey, "Home");
        settings.Set(CalendarOptions.SecondTimeZoneLabelKey, "Head office");

        Assert.Equal("Home", options.TimeZoneLabel);
        Assert.Equal("Head office", options.SecondTimeZoneLabel);
    }

    /// <summary>
    /// Half the world moves its clocks twice a year, so an offset is read at an instant and not
    /// once for all time.
    /// </summary>
    [Fact]
    public void AnOffsetIsWrittenAsItStandsOnTheDay()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var summer = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("UTC", TimeZoneChoices.ShortLabel(london, winter));
        Assert.Equal("UTC+1", TimeZoneChoices.ShortLabel(london, summer));
        Assert.Equal("UTC", TimeZoneChoices.ShortLabel(TimeZoneInfo.Utc, summer));
    }

    [Fact]
    public void AZoneOffAWholeHourKeepsItsMinutes()
    {
        var kolkata = TimeZoneChoices.Find("Asia/Kolkata");
        Assert.NotNull(kolkata);
        Assert.Equal("UTC+5:30", TimeZoneChoices.ShortLabel(kolkata, new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TheListRunsWestToEastAndWritesEachZoneAsTheReferenceDoes()
    {
        var all = TimeZoneChoices.All;

        Assert.NotEmpty(all);
        Assert.Equal(all.OrderBy(z => z.BaseUtcOffset).Select(z => z.BaseUtcOffset), all.Select(z => z.BaseUtcOffset));
        Assert.Contains(all, z => TimeZoneChoices.Describe(z).StartsWith("(UTC", StringComparison.Ordinal));
    }
}
