using System.Globalization;
using Mailbox.Scheduling;

namespace Mailbox.Tests;

/// <summary>
/// A time typed into an appointment's Start or End box.
/// </summary>
/// <remarks>
/// The list offers every half hour, so anything else has to be typed — and what gets typed is
/// whatever the reader is used to writing. These hold the shapes, and the refusals: a box that
/// quietly turned a typo into midnight would write an appointment nobody asked for.
/// <para>
/// Run under the invariant culture, so a machine set to a 24-hour locale and one set to a 12-hour
/// locale agree about what these say. The culture's own shapes are tried first in the real thing
/// and are not what could rot here.
/// </para>
/// </remarks>
public class TypedTimeTests : IDisposable
{
    private readonly CultureInfo _was = CultureInfo.CurrentCulture;

    public TypedTimeTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData("9:15", 9, 15)]
    [InlineData("09:15", 9, 15)]
    [InlineData("9.15", 9, 15)]
    [InlineData("21:45", 21, 45)]
    [InlineData("07:42", 7, 42)]
    [InlineData("23:59", 23, 59)]
    [InlineData("0:00", 0, 0)]
    public void AClockTimeReadsAsItself(string typed, int hour, int minute)
        => Assert.Equal(new TimeOnly(hour, minute), TypedTime.Read(typed));

    /// <summary>
    /// A bare number is the commonest thing anybody types into a time box, and the framework's own
    /// parser refuses it outright. Four digits or fewer, read the way a 24-hour clock is written.
    /// </summary>
    [Theory]
    [InlineData("9", 9, 0)]
    [InlineData("09", 9, 0)]
    [InlineData("14", 14, 0)]
    [InlineData("915", 9, 15)]
    [InlineData("0915", 9, 15)]
    [InlineData("2145", 21, 45)]
    [InlineData("0000", 0, 0)]
    public void ABareNumberIsAClockTime(string typed, int hour, int minute)
        => Assert.Equal(new TimeOnly(hour, minute), TypedTime.Read(typed));

    [Theory]
    [InlineData("9:15 AM", 9, 15)]
    [InlineData("9:15AM", 9, 15)]
    [InlineData("9:15 pm", 21, 15)]
    [InlineData("9 pm", 21, 0)]
    [InlineData("9pm", 21, 0)]
    [InlineData("12 am", 0, 0)]
    [InlineData("12 pm", 12, 0)]
    public void AnAmPmTimeReadsAsItself(string typed, int hour, int minute)
        => Assert.Equal(new TimeOnly(hour, minute), TypedTime.Read(typed));

    /// <summary>Extra space is not a different time.</summary>
    [Fact]
    public void SurroundingSpaceIsIgnored()
    {
        Assert.Equal(new TimeOnly(9, 15), TypedTime.Read("  9:15  "));
        Assert.Equal(new TimeOnly(21, 15), TypedTime.Read("9:15   pm"));
    }

    /// <summary>
    /// Null rather than a guess. The box puts back the time that was there, which is the only
    /// safe answer to something that was not understood.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("lunchtime")]
    [InlineData("25:00")]
    [InlineData("9:75")]
    [InlineData("2575")]
    [InlineData("99999")]
    [InlineData("-1")]
    [InlineData("9:15:20:30")]
    public void WhatIsNotATimeIsRefused(string? typed) => Assert.Null(TypedTime.Read(typed));

    /// <summary>What is written back is what the list offers, so the box does not disagree with itself.</summary>
    [Fact]
    public void WhatIsWrittenBackParsesBack()
    {
        foreach (var minutes in new[] { 0, 15, 545, 720, 1305, 1439 })
        {
            var time = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes));
            Assert.Equal(time, TypedTime.Read(TypedTime.Write(time)));
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        CultureInfo.CurrentCulture = _was;
    }
}
