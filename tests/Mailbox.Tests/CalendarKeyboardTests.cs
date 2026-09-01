using Avalonia.Input;
using Avalonia.Interactivity;
using Mailbox.Controls.Calendar;
using Mailbox.Scheduling;

namespace Mailbox.Tests;

/// <summary>
/// Moving around the time grid with the keyboard, which before this was possible only with a
/// pointer: the arrows carry a caret, the caret selects what it lands inside, and Enter opens it.
/// </summary>
/// <remarks>
/// Driven through the real key path — the events are raised on the control rather than the private
/// helpers being called — because the thing worth protecting is that pressing a key does this, not
/// that a method computes it. The grid needs no window for that; it needs no layout either, so the
/// scroll-into-view arithmetic is the one part that sits out (its guard returns on a zero height,
/// which is exactly what an unlaid-out control has).
/// </remarks>
public class CalendarKeyboardTests
{
    private static readonly DateOnly Monday = new(2026, 8, 17);

    /// <summary>A grid anchored on a known week, in UTC so a wall time is the time authored.</summary>
    private static TimeGridView Grid()
        => new()
        {
            Anchor = Monday,
            Today = Monday,
            Span = TimeGridSpan.Week,
            FirstDayOfWeek = DayOfWeek.Sunday,
            ViewZone = TimeZoneInfo.Utc,
            SlotMinutes = 30,
            WorkDayStart = new TimeOnly(8, 0),
        };

    private static void Press(TimeGridView grid, Key key)
        => grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    private static CalendarEntry Entry(DateTime start, DateTime end, string summary = "Review")
    {
        var appointment = new CalendarEvent
        {
            Uid = summary + "@keyboard",
            Summary = summary,
            Start = EventTime.At(start, "UTC"),
            End = EventTime.At(end, "UTC"),
        };

        var occurrence = Recurrence.Expand(
            [appointment],
            appointment.Start.ToUtc().AddDays(-1),
            appointment.End.ToUtc().AddDays(1)).First();

        // The entry's clock has to be the grid's, as CalendarSource sets it from the view zone.
        // Left at its Local default the caret and the chip would read different times, which is a
        // wrong test rather than a wrong grid.
        return new CalendarEntry
        {
            Occurrence = occurrence,
            CollectionId = 1,
            ItemId = 1,
            Zone = TimeZoneInfo.Utc,
        };
    }

    /// <summary>
    /// The first press has to land somewhere, and midnight is the wrong somewhere: it is eight
    /// hours above everything the day holds.
    /// </summary>
    [Fact]
    public void TheFirstArrowLandsAtTheTopOfTheWorkingDay()
    {
        var grid = Grid();
        Assert.Null(grid.Caret);

        Press(grid, Key.Down);

        Assert.Equal(new TimeOnly(8, 0), grid.Caret);
        Assert.Equal(Monday, grid.Selected);
    }

    [Fact]
    public void UpAndDownMoveOneSlotAtTheGridsOwnScale()
    {
        var grid = Grid();
        Press(grid, Key.Down);

        Press(grid, Key.Down);
        Assert.Equal(new TimeOnly(8, 30), grid.Caret);

        Press(grid, Key.Up);
        Assert.Equal(new TimeOnly(8, 0), grid.Caret);

        grid.SlotMinutes = 15;
        Press(grid, Key.Down);
        Assert.Equal(new TimeOnly(8, 15), grid.Caret);
    }

    /// <summary>
    /// Off the bottom of a day is the next day, not a stop. The pointer gets there by scrolling and
    /// clicking; the keyboard should not be the one input that hits a wall at midnight.
    /// </summary>
    [Fact]
    public void PastMidnightRollsIntoTheDayEitherSide()
    {
        var grid = Grid();
        Press(grid, Key.End);
        Assert.Equal(new TimeOnly(23, 30), grid.Caret);

        Press(grid, Key.Down);
        Assert.Equal(Monday.AddDays(1), grid.Selected);
        Assert.Equal(new TimeOnly(0, 0), grid.Caret);

        Press(grid, Key.Up);
        Assert.Equal(Monday, grid.Selected);
        Assert.Equal(new TimeOnly(23, 30), grid.Caret);
    }

    [Fact]
    public void HomeAndEndTakeTheDaysEnds()
    {
        var grid = Grid();
        Press(grid, Key.Down);

        Press(grid, Key.Home);
        Assert.Equal(new TimeOnly(0, 0), grid.Caret);

        Press(grid, Key.End);
        Assert.Equal(new TimeOnly(23, 30), grid.Caret);
    }

    /// <summary>
    /// The run follows the caret rather than fencing it: arrowing off the end of the week shows the
    /// next one. Without this the arrows are a way to look around one week, not a way to travel.
    /// </summary>
    [Fact]
    public void ArrowingOffTheEndOfTheRunPagesTheView()
    {
        var grid = Grid();
        Press(grid, Key.Down);

        // Sunday-first, so the anchor's week runs 16–22 August; from Monday the 17th five presses
        // reach the 22nd, which is still the same run.
        for (var i = 0; i < 5; i++) Press(grid, Key.Right);
        Assert.Equal(new DateOnly(2026, 8, 22), grid.Selected);
        Assert.Contains(new DateOnly(2026, 8, 22), grid.Days());

        Press(grid, Key.Right);
        Assert.Equal(new DateOnly(2026, 8, 23), grid.Selected);
        Assert.Contains(new DateOnly(2026, 8, 23), grid.Days());
    }

    [Fact]
    public void PageDownMovesAWholeWeekAndADayInTheDayView()
    {
        var grid = Grid();
        Press(grid, Key.Down);

        Press(grid, Key.PageDown);
        Assert.Equal(Monday.AddDays(7), grid.Selected);

        Press(grid, Key.PageUp);
        Assert.Equal(Monday, grid.Selected);

        grid.Span = TimeGridSpan.Day;
        Press(grid, Key.PageDown);
        Assert.Equal(Monday.AddDays(1), grid.Selected);
    }

    /// <summary>
    /// The caret carrying the selection is what puts the move on the accessibility bus: the
    /// SelectedEntry setter raises SpokenSelectionChanged, so a reader hears an appointment the
    /// moment the arrows reach it.
    /// </summary>
    [Fact]
    public void ArrowingIntoAnAppointmentSelectsItAndSaysSo()
    {
        var grid = Grid();
        grid.Entries = [Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0))];

        var spoke = 0;
        grid.SpokenSelectionChanged += (_, _) => spoke++;

        Press(grid, Key.Down);              // 08:00, outside it
        Assert.Null(grid.SelectedEntry);
        Assert.Equal(0, spoke);

        Press(grid, Key.Down);              // 08:30
        Press(grid, Key.Down);              // 09:00, inside it
        Assert.NotNull(grid.SelectedEntry);
        Assert.Equal("Review", grid.SelectedEntry!.Summary);
        Assert.Equal(1, spoke);

        // And off it again: the caret keeps a real position, and the selection lets go.
        Press(grid, Key.Down);              // 09:30, still inside
        Assert.NotNull(grid.SelectedEntry);
        Press(grid, Key.Down);              // 10:00, past the end
        Assert.Null(grid.SelectedEntry);
        Assert.Equal(new TimeOnly(10, 0), grid.Caret);
    }

    /// <summary>
    /// An appointment ends at its end: the half-open interval is what stops a 09:00–10:00 meeting
    /// and a 10:00–11:00 one both claiming ten o'clock.
    /// </summary>
    [Fact]
    public void TheEndOfAnAppointmentBelongsToTheNextOne()
    {
        var grid = Grid();
        grid.Entries =
        [
            Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0), "First"),
            Entry(new DateTime(2026, 8, 17, 10, 0, 0), new DateTime(2026, 8, 17, 11, 0, 0), "Second"),
        ];

        Press(grid, Key.Down);                                // places at 08:00
        for (var i = 0; i < 4; i++) Press(grid, Key.Down);    // 08:00 → 10:00

        Assert.Equal(new TimeOnly(10, 0), grid.Caret);
        Assert.Equal("Second", grid.SelectedEntry!.Summary);
    }

    /// <summary>
    /// A short meeting inside a long block is the one somebody arrowing to that time means, and the
    /// enclosing block is still reachable at every other minute of itself.
    /// </summary>
    [Fact]
    public void TheShorterAppointmentWinsAnOverlap()
    {
        var grid = Grid();
        grid.Entries =
        [
            Entry(new DateTime(2026, 8, 17, 8, 0, 0), new DateTime(2026, 8, 17, 12, 0, 0), "Offsite"),
            Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 9, 30, 0), "Standup"),
        ];

        Press(grid, Key.Down);                      // 08:00 — only the block covers it
        Assert.Equal("Offsite", grid.SelectedEntry!.Summary);

        Press(grid, Key.Down);
        Press(grid, Key.Down);                      // 09:00 — both cover it
        Assert.Equal("Standup", grid.SelectedEntry!.Summary);
    }

    [Fact]
    public void EnterOpensTheAppointmentTheCaretIsInside()
    {
        var grid = Grid();
        grid.Entries = [Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0))];

        CalendarEntry? activated = null;
        grid.EntryActivated += (_, entry) => activated = entry;

        Press(grid, Key.Down);
        Press(grid, Key.Down);
        Press(grid, Key.Down);                      // 09:00
        Press(grid, Key.Enter);

        Assert.NotNull(activated);
        Assert.Equal("Review", activated!.Summary);
    }

    /// <summary>Enter on empty time is the keyboard's double click: a new appointment, there.</summary>
    [Fact]
    public void EnterOnEmptyTimeAsksForANewAppointmentAtTheCaret()
    {
        var grid = Grid();

        (DateOnly Day, TimeOnly At)? asked = null;
        grid.SlotActivated += (_, slot) => asked = slot;

        Press(grid, Key.Down);
        Press(grid, Key.Down);                      // 08:30
        Press(grid, Key.Enter);

        Assert.Equal((Monday, new TimeOnly(8, 30)), asked);
    }

    /// <summary>
    /// A key the grid does not use goes back to the shell, or the calendar would swallow the
    /// application's own shortcuts while it happens to hold focus.
    /// </summary>
    [Fact]
    public void AKeyTheGridDoesNotUseIsLeftAlone()
    {
        var grid = Grid();
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A };

        grid.RaiseEvent(args);

        Assert.False(args.Handled);
        Assert.Null(grid.Caret);
    }
}
