using Mailbox.Controls.Calendar;
using Mailbox.Core.Settings;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The appointment window's Tags group where it reaches the wire and the store: CLASS and
/// PRIORITY on a VEVENT, and the columns a list draws them from.
/// </summary>
/// <remarks>
/// Both halves, because a button that changes only what is in memory is a button that does
/// nothing. What is asserted is the text a server would receive and the columns a query would
/// read — not that a field was set on an object.
/// </remarks>
public class AppointmentTagTests
{
    private static readonly string Zone = TimeZoneInfo.Local.Id;

    private static CalendarEvent Sample(bool isPrivate = false, TaskUrgency urgency = TaskUrgency.Normal) => new()
    {
        Uid = "appt@mailbox",
        Summary = "Quarterly review",
        Start = EventTime.At(new DateTime(2026, 8, 16, 9, 0, 0), Zone),
        End = EventTime.At(new DateTime(2026, 8, 16, 10, 0, 0), Zone),
        IsPrivate = isPrivate,
        Urgency = urgency,
        Categories = ["Finance"],
    };

    [Fact]
    public void PrivateAndImportanceSurviveARoundTripThroughText()
    {
        var appointment = Sample(isPrivate: true, urgency: TaskUrgency.High);
        var back = ICalendarCodec.Parse(ICalendarCodec.Serialize(appointment)).Single();

        Assert.True(back.IsPrivate);
        Assert.Equal(TaskUrgency.High, back.Urgency);
        Assert.Equal(["Finance"], back.Categories);
    }

    [Fact]
    public void LowImportanceIsPriorityNine()
    {
        var text = ICalendarCodec.Serialize(Sample(urgency: TaskUrgency.Low));

        Assert.Contains("PRIORITY:9", text, StringComparison.Ordinal);
        Assert.Equal(TaskUrgency.Low, ICalendarCodec.Parse(text).Single().Urgency);
    }

    /// <summary>
    /// PUBLIC and 5 are the standard's own defaults, so writing them would put a property on
    /// every appointment in the file to say what its absence already says.
    /// </summary>
    [Fact]
    public void NeitherIsWrittenWhenItSaysNothing()
    {
        var text = ICalendarCodec.Serialize(Sample());

        Assert.DoesNotContain("CLASS:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIORITY:", text, StringComparison.Ordinal);
    }

    /// <summary>CONFIDENTIAL means the same thing to a reader of a shared calendar as PRIVATE.</summary>
    [Theory]
    [InlineData("PRIVATE", true)]
    [InlineData("CONFIDENTIAL", true)]
    [InlineData("PUBLIC", false)]
    public void ClassIsReadTheWayATasksIs(string klass, bool expected)
    {
        var text = ICalendarCodec.Serialize(Sample()).Replace(
            "END:VEVENT", $"CLASS:{klass}\r\nEND:VEVENT", StringComparison.Ordinal);

        Assert.Equal(expected, ICalendarCodec.Parse(text).Single().IsPrivate);
    }

    /// <summary>A list draws the mark from the column, so the column has to carry it.</summary>
    [Fact]
    public void TheColumnsMirrorWhatTheTextSays()
    {
        var row = PimEventCodec.ToItem(Sample(isPrivate: true, urgency: TaskUrgency.High), collectionId: 1, existing: null);

        Assert.True(row.IsPrivate);
        Assert.Equal(1, row.Priority);
        Assert.Equal("Finance", row.Categories);
    }

    /// <summary>And a damaged row rebuilt from its columns alone still knows both.</summary>
    [Fact]
    public void TheColumnsAloneStillDescribeBoth()
    {
        var row = PimEventCodec.ToItem(Sample(isPrivate: true, urgency: TaskUrgency.Low), collectionId: 1, existing: null);
        var back = PimEventCodec.FromColumns(row with { RawPayload = "not iCalendar" });

        Assert.True(back.IsPrivate);
        Assert.Equal(TaskUrgency.Low, back.Urgency);
    }

    /// <summary>
    /// A task and an appointment marked alike must sort alike: one mapping, not two.
    /// </summary>
    [Fact]
    public void ATaskAndAnAppointmentAgreeAboutWhatHighMeans()
    {
        var task = new TaskItem { Uid = "t@mailbox", Summary = "Do it", Urgency = TaskUrgency.High };

        Assert.Equal(task.PriorityNumber, Sample(urgency: TaskUrgency.High).PriorityNumber);
        Assert.Equal(TaskItem.PriorityFor(TaskUrgency.Low), Sample(urgency: TaskUrgency.Low).PriorityNumber);
    }

    // ---- The Daily Task List's columns ---------------------------------------------------------

    /// <summary>
    /// The band's columns are the grid's columns: whole pixels, the remainder to the earliest,
    /// summing back to the width. A band a rounding away from the grid puts tasks under the
    /// wrong day at the edges.
    /// </summary>
    [Theory]
    [InlineData(1352.0, 7)]
    [InlineData(1000.0, 5)]
    [InlineData(700.0, 1)]
    public void TheBandSlicesItsColumnsTheWayTheGridDoes(double width, int columns)
    {
        var slices = DailyTaskListView.Slice(width, columns);

        Assert.Equal(columns, slices.Count);
        Assert.Equal(width, slices.Sum());
        Assert.All(slices, s => Assert.Equal(s, Math.Floor(s)));
        Assert.True(slices[0] >= slices[^1]);
        Assert.True(slices[0] - slices[^1] <= 1);
    }

    [Fact]
    public void TheBandAndTheGridShareOneRulerWidth()
    {
        Assert.Equal(62, TimeGridView.RulerSpanFor(secondZone: false));
        Assert.Equal(124, TimeGridView.RulerSpanFor(secondZone: true));
    }

    /// <summary>
    /// One palette, so "Purple" chosen on the Options page and "Purple" chosen on the bar are
    /// the same purple.
    /// </summary>
    [Fact]
    public void TheCalendarPaletteIsOneTable()
    {
        Assert.Equal(8, CalendarOptions.Palette.Count);
        Assert.Equal("Blue", CalendarOptions.Palette[0].Name);
        Assert.Equal(string.Empty, CalendarOptions.Palette[0].Hex);
        Assert.Equal("#8764B8", CalendarOptions.Palette.Single(c => c.Name == "Purple").Hex);
    }
}
