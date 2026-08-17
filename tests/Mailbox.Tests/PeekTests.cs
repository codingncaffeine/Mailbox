using System.Globalization;
using Mailbox.Controls.Calendar;
using Mailbox.Scheduling;

namespace Mailbox.Tests;

/// <summary>
/// The calendar peek: where it draws its parts, and what its agenda says.
/// </summary>
/// <remarks>
/// The numbers are the reference's own, read off the two captures at 100% — the floating popup
/// over the shell and the pane it docks into. They are asserted here rather than compared in a
/// screenshot because a layout that drifts should fail somewhere a person is looking.
/// </remarks>
public class PeekTests
{
    private static readonly CultureInfo Us = new("en-US");

    // ---- The popup's box -------------------------------------------------------------------

    [Fact]
    public void TheFloatingPopupIsTheSizeTheReferenceDrawsIt()
    {
        // 286×330 outside, of which 274×320 is content: a hairline, then a frame the desktop
        // draws wider than it is tall.
        Assert.Equal(286, PeekLayout.PopupWidth + (2 * (PeekLayout.FrameX + PeekLayout.Outline)));
        Assert.Equal(330, PeekLayout.PopupHeight + (2 * (PeekLayout.FrameY + PeekLayout.Outline)));
    }

    // ---- The month block -------------------------------------------------------------------

    [Fact]
    public void ThePopupsGridStartsThirtyOnePixelsInAndItsCellsAreTwentyEightByTwentyFour()
    {
        var layout = new PeekLayout(docked: false, PeekLayout.PopupWidth);

        Assert.Equal(31, layout.Grid.X);
        Assert.Equal(196, layout.Grid.Width);

        var cell = layout.DayCell(0, 0);
        Assert.Equal(31, cell.X);
        Assert.Equal(28, cell.Width);
        Assert.Equal(24, cell.Height);

        // The sixth column is where the reference's today marker sits — x=239 in the capture,
        // 68 of which is the popup's own left edge. That marker is what pinned the whole grid.
        Assert.Equal(239 - 68, layout.DayCell(0, 5).X);
    }

    [Fact]
    public void TheDockedPanesGridStartsTwentyThreePixelsIn()
    {
        var layout = new PeekLayout(docked: true, PeekLayout.DockedWidth);
        Assert.Equal(23, layout.Grid.X);
        Assert.Equal(219, layout.Grid.Right);
    }

    [Fact]
    public void TheBaselinesLandWhereTheReferencesInkDoes()
    {
        var layout = new PeekLayout(docked: false, PeekLayout.PopupWidth);

        // Content-relative: the month's name at 38, the weekday letters at 59, the first week's
        // numbers at 90, and the six rows 24 apart from there.
        Assert.Equal(38, layout.TitleBaseline);
        Assert.Equal(59, layout.WeekdayBaseline);
        Assert.Equal(66, layout.DayCell(0, 0).Y);
        Assert.Equal(210, layout.Grid.Bottom);
        Assert.Equal(24, layout.DayCell(1, 0).Y - layout.DayCell(0, 0).Y);
    }

    [Fact]
    public void TheDockedPaneHoldsEverythingSixPixelsLowerThanThePopup()
    {
        var popup = new PeekLayout(docked: false, PeekLayout.PopupWidth);
        var docked = new PeekLayout(docked: true, PeekLayout.DockedWidth);

        Assert.Equal(popup.TitleBaseline + 6, docked.TitleBaseline);
        Assert.Equal(popup.Grid.Y + 6, docked.Grid.Y);
    }

    [Fact]
    public void TheArrowsHangOffTheGridsEdgesRatherThanItsCentre()
    {
        foreach (var layout in new[]
        {
            new PeekLayout(docked: false, PeekLayout.PopupWidth),
            new PeekLayout(docked: true, PeekLayout.DockedWidth),
        })
        {
            Assert.Equal(layout.Grid.X - 4, layout.Previous.Center.X);
            Assert.Equal(layout.Grid.Right + 1, layout.Next.Center.X);
            Assert.Equal(layout.TitleCentre, layout.Grid.X + (layout.Grid.Width / 2));
        }
    }

    [Fact]
    public void AWeekNumberColumnWidensTheGridAndMovesTheDaysAlong()
    {
        var plain = new PeekLayout(docked: false, PeekLayout.PopupWidth);
        var numbered = new PeekLayout(docked: false, PeekLayout.PopupWidth, weekNumbers: true);

        Assert.Equal(0, plain.WeekNumberColumn);
        Assert.Equal(PeekLayout.CellWidth, numbered.WeekNumberColumn);
        Assert.Equal(plain.Grid.Width + PeekLayout.CellWidth, numbered.Grid.Width);

        // The days keep their own 28px columns; the numbers take the one before them.
        Assert.Equal(numbered.Grid.X, numbered.WeekCell(0).X);
        Assert.Equal(numbered.Grid.X + PeekLayout.CellWidth, numbered.DayCell(0, 0).X);
    }

    // ---- The rule and the agenda -----------------------------------------------------------

    [Fact]
    public void OnlyTheDockedPaneRulesALineUnderItsGrid()
    {
        var popup = new PeekLayout(docked: false, PeekLayout.PopupWidth);
        Assert.Equal(0, popup.Rule.Width);

        // 221 wide, 9 in from the pane's left edge and 24 short of its right.
        var docked = new PeekLayout(docked: true, PeekLayout.DockedWidth);
        Assert.Equal(9, docked.Rule.X);
        Assert.Equal(221, docked.Rule.Width);
        Assert.Equal(docked.Grid.Bottom + 14, docked.Rule.Y);
    }

    [Fact]
    public void TheAgendaFollowsTheGridInThePopupAndTheRuleInThePane()
    {
        var popup = new PeekLayout(docked: false, PeekLayout.PopupWidth);
        Assert.Equal(4, popup.AgendaLeft);
        Assert.Equal(popup.Grid.Bottom + 25, popup.HeadingBaseline);
        Assert.Equal(popup.HeadingBaseline + 9, popup.AgendaTop);

        var docked = new PeekLayout(docked: true, PeekLayout.DockedWidth);
        Assert.Equal(9, docked.AgendaLeft);
        Assert.Equal(docked.Rule.Bottom + 22, docked.HeadingBaseline);
    }

    [Fact]
    public void AnEntryOfTwoLinesIsThirtyPixelsTall()
    {
        Assert.Equal(30, PeekLayout.EntryHeight(2));
        Assert.Equal(14, PeekLayout.EntryHeight(1));
    }

    // ---- What the agenda says --------------------------------------------------------------

    private static CalendarEntry At(string summary, DateTime start, double hours, string location = "", BusyStatus busy = BusyStatus.Busy)
    {
        var end = start.AddHours(hours);
        var calendarEvent = new CalendarEvent
        {
            Uid = summary,
            Summary = summary,
            Location = location,
            Busy = busy,
            Start = EventTime.At(start, TimeZoneInfo.Utc.Id),
            End = EventTime.At(end, TimeZoneInfo.Utc.Id),
        };

        return new CalendarEntry
        {
            Occurrence = new Occurrence(
                calendarEvent, calendarEvent.Start, calendarEvent.End,
                new DateTimeOffset(start, TimeSpan.Zero), new DateTimeOffset(end, TimeSpan.Zero),
                IsPartOfSeries: false, RecurrenceId: null),
            Zone = TimeZoneInfo.Utc,
        };
    }

    private static CalendarEntry AllDay(string summary, DateOnly first, int days = 1)
    {
        var calendarEvent = new CalendarEvent
        {
            Uid = summary,
            Summary = summary,
            Start = EventTime.Date(first),
            End = EventTime.Date(first.AddDays(days)),
        };

        return new CalendarEntry
        {
            Occurrence = new Occurrence(
                calendarEvent, calendarEvent.Start, calendarEvent.End,
                new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                new DateTimeOffset(first.AddDays(days).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                IsPartOfSeries: false, RecurrenceId: null),
            Zone = TimeZoneInfo.Utc,
        };
    }

    private static readonly DateOnly Day = new(2026, 8, 16);

    [Fact]
    public void TheAgendaHoldsTheDaysOwnAppointmentsAndNoOthers()
    {
        var rows = PeekAgenda.For(
            [
                At("today", Day.ToDateTime(new TimeOnly(17, 0)), 1),
                At("tomorrow", Day.AddDays(1).ToDateTime(new TimeOnly(9, 0)), 1),
            ],
            Day,
            Us);

        Assert.Equal(["today"], rows.Select(r => r.Subject));
    }

    [Fact]
    public void ATimeReadsAsTheReferenceWritesIt()
    {
        var rows = PeekAgenda.For([At("x", Day.ToDateTime(new TimeOnly(17, 0)), 1)], Day, Us);
        Assert.Equal("5:00 PM", rows[0].Time);
    }

    [Fact]
    public void AnAllDayItemLeadsAndSaysSoInsteadOfATime()
    {
        var rows = PeekAgenda.For(
            [
                At("nine", Day.ToDateTime(new TimeOnly(9, 0)), 1),
                AllDay("holiday", Day),
            ],
            Day,
            Us);

        Assert.Equal(["holiday", "nine"], rows.Select(r => r.Subject));
        Assert.Equal(PeekAgenda.AllDayLabel, rows[0].Time);
    }

    [Fact]
    public void TimedItemsFollowTheClock()
    {
        var rows = PeekAgenda.For(
            [
                At("later", Day.ToDateTime(new TimeOnly(17, 0)), 1),
                At("earlier", Day.ToDateTime(new TimeOnly(9, 0)), 1),
            ],
            Day,
            Us);

        Assert.Equal(["earlier", "later"], rows.Select(r => r.Subject));
    }

    [Fact]
    public void AnEntryTakesASecondLineOnlyWhenItSaysWhereItIs()
    {
        var rows = PeekAgenda.For(
            [
                At("with", Day.ToDateTime(new TimeOnly(9, 0)), 1, "https://example.com/meet/weekly"),
                At("without", Day.ToDateTime(new TimeOnly(10, 0)), 1),
            ],
            Day,
            Us);

        Assert.Equal(2, rows[0].Lines);
        Assert.Equal("https://example.com/meet/weekly", rows[0].Detail);
        Assert.Equal(1, rows[1].Lines);
    }

    [Fact]
    public void ABandIsOnEveryDayItCovers()
    {
        var band = AllDay("conference", Day, days: 3);

        foreach (var day in new[] { Day, Day.AddDays(1), Day.AddDays(2) })
        {
            Assert.Equal(["conference"], PeekAgenda.For([band], day, Us).Select(r => r.Subject));
        }

        // Its end is exclusive, so the day after the last is not one of them.
        Assert.Empty(PeekAgenda.For([band], Day.AddDays(3), Us));
    }
}
