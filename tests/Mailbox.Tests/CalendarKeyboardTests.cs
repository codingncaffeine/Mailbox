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

    /// <summary>A press, handed back so a test can ask whether the view took it.</summary>
    private static KeyEventArgs Press(InputElement view, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        view.RaiseEvent(args);
        return args;
    }

    /// <summary>Shared with <see cref="CalendarSweepTests"/>, which needs the same zone-matched entry.</summary>
    internal static CalendarEntry Entry(DateTime start, DateTime end, string summary = "Review", long collectionId = 1)
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
            CollectionId = collectionId,
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

        SlotRange? asked = null;
        grid.SlotActivated += (_, slot) => asked = slot;

        Press(grid, Key.Down);
        Press(grid, Key.Down);                      // 08:30
        Press(grid, Key.Enter);

        // One slot, because nothing extended it: the half hour this has always made.
        Assert.Equal(new SlotRange(Monday, new TimeOnly(8, 30), new TimeOnly(9, 0)), asked);
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

    // ---- The month grid --------------------------------------------------------------------

    /// <summary>
    /// A month grid on the six weeks from Sunday 16 August, wired to its own paging the way the
    /// workspace wires it: the view asks through <c>Scrolled</c> and the host moves the weeks, so
    /// there is one path off the end of the grid rather than one for the wheel and one for a key.
    /// </summary>
    private static MonthView Month()
    {
        var view = new MonthView
        {
            FirstDay = new DateOnly(2026, 8, 16),
            Weeks = 6,
            Today = Monday,
        };

        view.Scrolled += (_, weeks) => view.FirstDay = view.FirstDay.AddDays(weeks * 7);
        return view;
    }

    [Fact]
    public void TheFirstArrowPlacesTheMonthCaretRatherThanMovingIt()
    {
        var month = Month();
        Assert.Null(month.Caret);

        Press(month, Key.Right);

        Assert.Equal(Monday, month.Caret);
        Assert.Equal(Monday, month.Selected);
    }

    [Fact]
    public void TheArrowsMoveAMonthCaretByDaysAndByWeeks()
    {
        var month = Month();
        Press(month, Key.Right);                        // places on Monday the 17th

        Press(month, Key.Right);
        Assert.Equal(new DateOnly(2026, 8, 18), month.Caret);

        Press(month, Key.Down);
        Assert.Equal(new DateOnly(2026, 8, 25), month.Caret);

        Press(month, Key.Up);
        Press(month, Key.Left);
        Assert.Equal(Monday, month.Caret);
    }

    /// <summary>A row of this grid is a week, so its ends are the week's.</summary>
    [Fact]
    public void HomeAndEndTakeTheWeeksEnds()
    {
        var month = Month();
        Press(month, Key.Right);
        Press(month, Key.Right);                        // Tuesday the 18th

        Press(month, Key.Home);
        Assert.Equal(new DateOnly(2026, 8, 16), month.Caret);

        Press(month, Key.End);
        Assert.Equal(new DateOnly(2026, 8, 22), month.Caret);
    }

    /// <summary>
    /// A page in a view of months is a month — and a short one keeps the caret inside itself
    /// rather than spilling into the next.
    /// </summary>
    [Fact]
    public void ThePageKeysMoveAWholeMonth()
    {
        var month = Month();
        Press(month, Key.Right);
        month.Caret = new DateOnly(2026, 8, 31);

        Press(month, Key.PageDown);
        Assert.Equal(new DateOnly(2026, 9, 30), month.Caret);

        Press(month, Key.PageUp);
        Assert.Equal(new DateOnly(2026, 8, 30), month.Caret);
    }

    /// <summary>
    /// The weeks on show follow the caret, or the arrows would be a way to look around six weeks
    /// rather than a way to travel — and the host is told, so it reads the store for them.
    /// </summary>
    [Fact]
    public void ArrowingOffTheGridMovesTheWeeksOnShow()
    {
        var month = Month();
        var asked = 0;
        month.Scrolled += (_, weeks) => asked += weeks;

        Press(month, Key.Right);
        Press(month, Key.Home);                         // Sunday the 16th, the first cell
        Press(month, Key.Up);

        Assert.Equal(new DateOnly(2026, 8, 9), month.Caret);
        Assert.Equal(new DateOnly(2026, 8, 9), month.FirstDay);
        Assert.Equal(-1, asked);
    }

    /// <summary>
    /// Tab reaches into a day that holds more than one appointment, and the press that runs off
    /// the end is left alone so the focus moves out of the calendar as it would anywhere else.
    /// </summary>
    [Fact]
    public void TabWalksTheDaysAppointmentsAndThenLetsThePressGo()
    {
        var month = Month();
        month.Entries =
        [
            Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0), "First"),
            Entry(new DateTime(2026, 8, 17, 14, 0, 0), new DateTime(2026, 8, 17, 15, 0, 0), "Second"),
        ];

        Press(month, Key.Right);                        // the 17th, no chip taken hold of
        Assert.Null(month.SelectedEntry);

        Assert.True(Press(month, Key.Tab).Handled);
        Assert.Equal("First", month.SelectedEntry!.Summary);

        Assert.True(Press(month, Key.Tab).Handled);
        Assert.Equal("Second", month.SelectedEntry!.Summary);

        // Past the last one the grid does not answer, which is what lets Tab leave.
        Assert.False(Press(month, Key.Tab).Handled);
        Assert.Equal("Second", month.SelectedEntry!.Summary);

        Assert.True(Press(month, Key.Tab, KeyModifiers.Shift).Handled);
        Assert.Equal("First", month.SelectedEntry!.Summary);

        // Back past the first is the day itself; the press after that leaves.
        Assert.True(Press(month, Key.Tab, KeyModifiers.Shift).Handled);
        Assert.Null(month.SelectedEntry);
        Assert.False(Press(month, Key.Tab, KeyModifiers.Shift).Handled);
    }

    /// <summary>
    /// The caret is a place and a chip is a thing: moving the caret lets go, which is also what
    /// puts the change on the accessibility bus.
    /// </summary>
    [Fact]
    public void ArrowingOntoAnotherDayLetsGoOfTheChip()
    {
        var month = Month();
        month.Entries = [Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0))];

        var spoke = 0;
        month.SpokenSelectionChanged += (_, _) => spoke++;

        Press(month, Key.Right);
        Press(month, Key.Tab);
        Assert.NotNull(month.SelectedEntry);

        Press(month, Key.Right);
        Assert.Null(month.SelectedEntry);
        Assert.Equal(2, spoke);
    }

    [Fact]
    public void EnterOpensTheChipTabTookHoldOf()
    {
        var month = Month();
        month.Entries = [Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0))];

        CalendarEntry? activated = null;
        month.EntryActivated += (_, entry) => activated = entry;

        Press(month, Key.Right);
        Press(month, Key.Tab);
        Press(month, Key.Enter);

        Assert.Equal("Review", activated!.Summary);
    }

    /// <summary>
    /// Enter on a day nothing is taken hold of asks for a new appointment there — including on a
    /// day that has appointments, which is why the caret does not select one by landing on it.
    /// </summary>
    [Fact]
    public void EnterOnADayAsksForANewAppointmentOnIt()
    {
        var month = Month();
        month.Entries = [Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0))];

        DateOnly? asked = null;
        month.DayActivated += (_, day) => asked = day;

        Press(month, Key.Right);
        Press(month, Key.Enter);

        Assert.Equal(Monday, asked);
    }

    [Fact]
    public void AKeyTheMonthGridDoesNotUseIsLeftAlone()
    {
        var month = Month();

        Assert.False(Press(month, Key.A).Handled);
        Assert.Null(month.Caret);
    }

    // ---- Schedule View ---------------------------------------------------------------------

    /// <summary>One day laid out sideways, on two calendars.</summary>
    private static ScheduleView Schedule()
        => new()
        {
            Day = Monday,
            Today = Monday,
            Rows = [new ScheduleRow(1, "Calendar", null), new ScheduleRow(2, "Team", null)],
            WorkDayStart = new TimeOnly(8, 0),
            StartHour = 7,
            HoursShown = 12,
        };

    [Fact]
    public void TheFirstArrowInTheScheduleLandsAtTheTopOfTheWorkingDay()
    {
        var schedule = Schedule();
        Assert.Null(schedule.Caret);

        Press(schedule, Key.Right);

        Assert.Equal(new TimeOnly(8, 0), schedule.Caret);
        Assert.Equal(0, schedule.CaretRow);
    }

    /// <summary>
    /// Time runs sideways here, so the two axes are the time grid's turned a quarter turn — and
    /// the day's ends are where the caret stops, because the day either side is a page away.
    /// </summary>
    [Fact]
    public void TheArrowsMoveThroughTheDayAndBetweenCalendars()
    {
        var schedule = Schedule();
        Press(schedule, Key.Right);

        Press(schedule, Key.Right);
        Assert.Equal(new TimeOnly(8, 30), schedule.Caret);

        Press(schedule, Key.Left);
        Assert.Equal(new TimeOnly(8, 0), schedule.Caret);

        Press(schedule, Key.Down);
        Assert.Equal(1, schedule.CaretRow);
        Press(schedule, Key.Down);
        Assert.Equal(1, schedule.CaretRow);
        Press(schedule, Key.Up);
        Assert.Equal(0, schedule.CaretRow);

        Press(schedule, Key.Home);
        Assert.Equal(new TimeOnly(0, 0), schedule.Caret);
        Press(schedule, Key.Left);
        Assert.Equal(new TimeOnly(0, 0), schedule.Caret);

        Press(schedule, Key.End);
        Assert.Equal(new TimeOnly(23, 30), schedule.Caret);
        Press(schedule, Key.Right);
        Assert.Equal(new TimeOnly(23, 30), schedule.Caret);
    }

    /// <summary>
    /// Nothing else moves this view sideways — the bar across its foot is drawn and nothing turns
    /// it — so the hours on show following the caret is the only way to reach the rest of the day.
    /// </summary>
    [Fact]
    public void TheHoursOnShowFollowTheScheduleCaret()
    {
        var schedule = Schedule();
        Press(schedule, Key.Right);
        Assert.Equal(7, schedule.StartHour);

        Press(schedule, Key.End);
        Assert.Equal(12, schedule.StartHour);

        Press(schedule, Key.Home);
        Assert.Equal(0, schedule.StartHour);
    }

    [Fact]
    public void ThePageKeysAskTheHostForTheDayEitherSide()
    {
        var schedule = Schedule();
        var stepped = 0;
        schedule.DayStepped += (_, direction) => stepped += direction;

        Press(schedule, Key.PageDown);
        Press(schedule, Key.PageDown);
        Press(schedule, Key.PageUp);

        Assert.Equal(1, stepped);
    }

    /// <summary>
    /// A row is a calendar, so the appointment the caret is inside is the one on that calendar —
    /// two meetings at the same hour on two calendars are two different things to land on.
    /// </summary>
    [Fact]
    public void TheCaretSelectsOnTheRowItIsOn()
    {
        var schedule = Schedule();
        schedule.Entries =
        [
            Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0), "Mine", collectionId: 1),
            Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0), "Theirs", collectionId: 2),
        ];

        var spoke = 0;
        schedule.SpokenSelectionChanged += (_, _) => spoke++;

        Press(schedule, Key.Right);                     // 08:00, outside both
        Assert.Null(schedule.SelectedEntry);

        Press(schedule, Key.Right);
        Press(schedule, Key.Right);                     // 09:00
        Assert.Equal("Mine", schedule.SelectedEntry!.Summary);

        Press(schedule, Key.Down);
        Assert.Equal("Theirs", schedule.SelectedEntry!.Summary);
        Assert.Equal(2, spoke);
    }

    [Fact]
    public void EnterInTheScheduleOpensWhatTheCaretIsInsideAndOtherwiseAsksForANewOne()
    {
        var schedule = Schedule();
        schedule.Entries = [Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0))];

        CalendarEntry? activated = null;
        SlotRange? asked = null;
        schedule.EntryActivated += (_, entry) => activated = entry;
        schedule.SlotActivated += (_, slot) => asked = slot;

        Press(schedule, Key.Right);                     // 08:00, empty
        Press(schedule, Key.Enter);
        Assert.Equal(new SlotRange(Monday, new TimeOnly(8, 0), new TimeOnly(8, 30)), asked);
        Assert.Null(activated);

        Press(schedule, Key.Right);
        Press(schedule, Key.Right);                     // 09:00
        Press(schedule, Key.Enter);
        Assert.Equal("Review", activated!.Summary);
    }

    [Fact]
    public void TabWalksTheCalendarsOwnAppointments()
    {
        var schedule = Schedule();
        schedule.Entries =
        [
            Entry(new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0), "First", collectionId: 1),
            Entry(new DateTime(2026, 8, 17, 14, 0, 0), new DateTime(2026, 8, 17, 15, 0, 0), "Second", collectionId: 1),
            Entry(new DateTime(2026, 8, 17, 11, 0, 0), new DateTime(2026, 8, 17, 12, 0, 0), "Elsewhere", collectionId: 2),
        ];

        Press(schedule, Key.Right);                     // 08:00 on the first calendar

        Assert.True(Press(schedule, Key.Tab).Handled);
        Assert.Equal("First", schedule.SelectedEntry!.Summary);
        Assert.Equal(new TimeOnly(9, 0), schedule.Caret);

        Assert.True(Press(schedule, Key.Tab).Handled);
        Assert.Equal("Second", schedule.SelectedEntry!.Summary);

        Assert.False(Press(schedule, Key.Tab).Handled);
    }

    /// <summary>A schedule with no calendars on it has nowhere to put a caret.</summary>
    [Fact]
    public void AScheduleWithNoRowsAnswersNothing()
    {
        var schedule = Schedule();
        schedule.Rows = [];

        Assert.False(Press(schedule, Key.Right).Handled);
        Assert.Null(schedule.Caret);
    }

    [Fact]
    public void AKeyTheScheduleDoesNotUseIsLeftAlone()
    {
        var schedule = Schedule();

        Assert.False(Press(schedule, Key.A).Handled);
        Assert.Null(schedule.Caret);
    }
}
