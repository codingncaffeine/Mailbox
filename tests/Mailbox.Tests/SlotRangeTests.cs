using Mailbox.Controls.Calendar;

namespace Mailbox.Tests;

/// <summary>
/// The stretch of empty time a sweep or a shifted arrow asks for.
/// </summary>
/// <remarks>
/// One shape for both drivers, so these hold the arithmetic both of them lean on: which way round
/// the two ends were given, that the range covers the slot the caret is on rather than stopping at
/// its start, and that a range running to the bottom of the day ends at midnight rather than
/// reading as a negative length.
/// </remarks>
public class SlotRangeTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    [Fact]
    public void OneSlotIsTheSlotsOwnLength()
    {
        var range = SlotRange.Between(Day, new TimeOnly(9, 0), new TimeOnly(9, 0), 30);

        Assert.Equal(30, range.Minutes);
        Assert.True(range.IsSingle(30));
        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0), range.Start);
        Assert.Equal(new DateTime(2026, 9, 1, 9, 30, 0), range.End);
    }

    /// <summary>
    /// A sweep down three rows is ninety minutes, not sixty: the far end runs to the end of the
    /// slot the caret is on, because that slot is part of what was swept over.
    /// </summary>
    [Fact]
    public void TheRangeCoversTheSlotTheCaretIsOn()
    {
        var range = SlotRange.Between(Day, new TimeOnly(9, 0), new TimeOnly(10, 0), 30);

        Assert.Equal(90, range.Minutes);
        Assert.False(range.IsSingle(30));
        Assert.Equal(new DateTime(2026, 9, 1, 10, 30, 0), range.End);
    }

    /// <summary>Swept upwards is the same range held the other way round.</summary>
    [Fact]
    public void SweepingUpwardsIsTheSameRange()
    {
        var down = SlotRange.Between(Day, new TimeOnly(9, 0), new TimeOnly(11, 0), 30);
        var up = SlotRange.Between(Day, new TimeOnly(11, 0), new TimeOnly(9, 0), 30);

        Assert.Equal(down, up);
        Assert.Equal(150, up.Minutes);
    }

    /// <summary>
    /// A range swept to the bottom of the day ends at midnight — the next day's zero. Taken from
    /// the subtraction instead, that reads as a day-long negative and the appointment would go in
    /// backwards.
    /// </summary>
    [Fact]
    public void ARangeToTheEndOfTheDayEndsAtMidnight()
    {
        var range = SlotRange.Between(Day, new TimeOnly(23, 0), new TimeOnly(23, 30), 30);

        Assert.Equal(60, range.Minutes);
        Assert.Equal(new DateTime(2026, 9, 2, 0, 0, 0), range.End);
    }

    /// <summary>The whole day, which is what a shifted Home then End asks for.</summary>
    [Fact]
    public void TheWholeDayIsTwentyFourHours()
    {
        var range = SlotRange.Between(Day, new TimeOnly(0, 0), new TimeOnly(23, 30), 30);

        Assert.Equal(24 * 60, range.Minutes);
        Assert.Equal(new DateTime(2026, 9, 2, 0, 0, 0), range.End);
    }

    /// <summary>A finer time scale makes a finer single slot; the arithmetic is the scale's, not 30's.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void TheSlotLengthIsWhateverTheTimeScaleSays(int scale)
    {
        var single = SlotRange.Between(Day, new TimeOnly(9, 0), new TimeOnly(9, 0), scale);
        Assert.Equal(scale, single.Minutes);
        Assert.True(single.IsSingle(scale));

        var two = SlotRange.Between(Day, new TimeOnly(9, 0), new TimeOnly(9, 0).Add(TimeSpan.FromMinutes(scale)), scale);
        Assert.Equal(scale * 2, two.Minutes);
        Assert.False(two.IsSingle(scale));
    }
}
