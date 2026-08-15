using Mailbox.Core;

namespace Mailbox.Tests;

/// <summary>The Snooze menu's presets, pinned against a clock so the arithmetic is checked, not the day.</summary>
public class SnoozePresetsTests
{
    [Fact]
    public void ThePresetsFallWhereTheReferencePutsThem()
    {
        // A Wednesday afternoon.
        var now = new DateTimeOffset(2026, 8, 12, 14, 30, 0, TimeSpan.Zero);
        var presets = SnoozePresets.For(now).ToDictionary(p => p.Header.Split(" (")[0], p => p.Until);

        Assert.Equal(now.AddHours(4), presets["Later Today"]);
        Assert.Equal(new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero), presets["Tomorrow"]);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.Zero), presets["This Weekend"]);
        Assert.Equal(new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero), presets["Next Week"]);
    }

    [Fact]
    public void OnASaturdayTheWeekendIsSundayAndOnAMondayNextWeekIsAWeekOn()
    {
        var saturday = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        var weekend = SnoozePresets.For(saturday).Single(p => p.Header.StartsWith("This Weekend")).Until;
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero), weekend);

        var monday = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var nextWeek = SnoozePresets.For(monday).Single(p => p.Header.StartsWith("Next Week")).Until;
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero), nextWeek);
    }

    [Fact]
    public void EveryPresetIsInTheFuture()
    {
        var now = new DateTimeOffset(2026, 8, 12, 23, 30, 0, TimeSpan.Zero);
        Assert.All(SnoozePresets.For(now), p => Assert.True(p.Until > now));
    }
}
