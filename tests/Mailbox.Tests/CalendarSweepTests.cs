using Avalonia.Input;
using Avalonia.Interactivity;
using Mailbox.Controls.Calendar;

namespace Mailbox.Tests;

/// <summary>
/// Asking for an appointment of a chosen length, rather than the fixed half hour that was the
/// only thing empty time could produce.
/// </summary>
/// <remarks>
/// Two drivers, one range. The keyboard extends the caret with <c>Shift</c> and the pointer sweeps
/// across the grid, and both end up raising the same <see cref="SlotRange"/> — so these press the
/// real keys rather than calling the helpers, and the thing being held is that a press does this.
/// <para>
/// The keyboard half is here and the pointer half is not: a sweep turns a point into a slot off
/// the geometry the last render laid out, and an unlaid-out control has none — so the pointer is
/// proved by a posed run against a real window instead. What these hold is the range model both
/// drivers share, which is where the arithmetic lives.
/// </para>
/// </remarks>
public class CalendarSweepTests
{
    private static readonly DateOnly Monday = new(2026, 8, 17);

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

    private static ScheduleView Schedule()
        => new()
        {
            Day = Monday,
            Today = Monday,
            WorkDayStart = new TimeOnly(8, 0),
            WorkDayEnd = new TimeOnly(17, 0),
            Rows = [new ScheduleRow(1, "Calendar", null, false)],
        };

    private static void Press(InputElement view, Key key, KeyModifiers modifiers = KeyModifiers.None)
        => view.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });

    // ---- The time grid ----------------------------------------------------------------------

    /// <summary>
    /// Shift and an arrow grow the range; Enter asks for exactly that stretch. Before this the
    /// only appointment empty time could make was thirty minutes long, however much of the day
    /// the reader had in mind.
    /// </summary>
    [Fact]
    public void ShiftAndAnArrowAskForALongerAppointment()
    {
        var grid = Grid();

        SlotRange? asked = null;
        grid.SlotActivated += (_, range) => asked = range;

        Press(grid, Key.Down);                                  // places the caret at 08:00
        Press(grid, Key.Down, KeyModifiers.Shift);              // 08:30
        Press(grid, Key.Down, KeyModifiers.Shift);              // 09:00
        Press(grid, Key.Enter);

        Assert.Equal(new SlotRange(Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)), asked);
        Assert.Equal(90, asked!.Value.Minutes);
    }

    /// <summary>Extending upwards is the same range: the anchor is one end, not the earlier one.</summary>
    [Fact]
    public void ExtendingUpwardsAsksForTheSameStretch()
    {
        var grid = Grid();

        SlotRange? asked = null;
        grid.SlotActivated += (_, range) => asked = range;

        Press(grid, Key.Down);                                  // 08:00
        Press(grid, Key.Up, KeyModifiers.Shift);                // 07:30
        Press(grid, Key.Up, KeyModifiers.Shift);                // 07:00
        Press(grid, Key.Enter);

        Assert.Equal(new SlotRange(Monday, new TimeOnly(7, 0), new TimeOnly(8, 30)), asked);
    }

    /// <summary>
    /// Shrinking back past the anchor turns the range around rather than refusing — what a text
    /// caret does, and what the pointer does when a sweep is dragged back over its own start.
    /// </summary>
    [Fact]
    public void ShrinkingPastTheAnchorTurnsTheRangeRound()
    {
        var grid = Grid();

        SlotRange? asked = null;
        grid.SlotActivated += (_, range) => asked = range;

        Press(grid, Key.Down);                                  // 08:00
        Press(grid, Key.Down, KeyModifiers.Shift);              // 08:30
        Press(grid, Key.Up, KeyModifiers.Shift);                // 08:00
        Press(grid, Key.Up, KeyModifiers.Shift);                // 07:30
        Press(grid, Key.Enter);

        Assert.Equal(new SlotRange(Monday, new TimeOnly(7, 30), new TimeOnly(8, 30)), asked);
    }

    /// <summary>A plain arrow ends the range: an unshifted press must not go on growing one.</summary>
    [Fact]
    public void APlainArrowEndsTheRange()
    {
        var grid = Grid();

        SlotRange? asked = null;
        grid.SlotActivated += (_, range) => asked = range;

        Press(grid, Key.Down);                                  // 08:00
        Press(grid, Key.Down, KeyModifiers.Shift);              // range 08:00–09:00
        Press(grid, Key.Down);                                  // plain: caret 09:00, range gone
        Press(grid, Key.Enter);

        Assert.Equal(new SlotRange(Monday, new TimeOnly(9, 0), new TimeOnly(9, 30)), asked);
    }

    /// <summary>Escape lets go of the range and leaves the caret where the reader was looking.</summary>
    [Fact]
    public void EscapeLetsGoOfTheRangeAndKeepsTheCaret()
    {
        var grid = Grid();

        SlotRange? asked = null;
        grid.SlotActivated += (_, range) => asked = range;

        Press(grid, Key.Down);                                  // 08:00
        Press(grid, Key.Down, KeyModifiers.Shift);              // 08:30
        Press(grid, Key.Escape);
        Press(grid, Key.Enter);

        Assert.Equal(new TimeOnly(8, 30), grid.Caret);
        Assert.Equal(new SlotRange(Monday, new TimeOnly(8, 30), new TimeOnly(9, 0)), asked);
    }

    /// <summary>
    /// A range stops at the day's ends rather than paging. An extend that walked into tomorrow
    /// would be describing an appointment on a day nobody was on.
    /// </summary>
    [Fact]
    public void ARangeDoesNotRunPastTheEndOfTheDay()
    {
        var grid = Grid();

        SlotRange? asked = null;
        grid.SlotActivated += (_, range) => asked = range;

        Press(grid, Key.Down);                                  // 08:00
        Press(grid, Key.End, KeyModifiers.Shift);               // 23:30
        Press(grid, Key.Down, KeyModifiers.Shift);              // refused: nothing below it
        Press(grid, Key.Enter);

        Assert.Equal(new TimeOnly(23, 30), grid.Caret);
        Assert.Equal(Monday, asked!.Value.Day);
        Assert.Equal(new DateTime(2026, 8, 18, 0, 0, 0), asked!.Value.End);
    }

    /// <summary>
    /// Shift and Home take the range to the top of the day, which is the fastest way to say
    /// "this morning".
    /// </summary>
    [Fact]
    public void ShiftAndHomeReachTheTopOfTheDay()
    {
        var grid = Grid();

        SlotRange? asked = null;
        grid.SlotActivated += (_, range) => asked = range;

        Press(grid, Key.Down);                                  // 08:00
        Press(grid, Key.Home, KeyModifiers.Shift);
        Press(grid, Key.Enter);

        Assert.Equal(new SlotRange(Monday, new TimeOnly(0, 0), new TimeOnly(8, 30)), asked);
    }

    /// <summary>
    /// An extended range opens a new appointment even where one sits under an end: the reader
    /// asked for a stretch of time, not for whatever happens to be in it.
    /// </summary>
    [Fact]
    public void AnExtendedRangeOverAnAppointmentStillAsksForANewOne()
    {
        var grid = Grid();
        grid.Entries = [CalendarKeyboardTests.Entry(
            new DateTime(2026, 8, 17, 9, 0, 0), new DateTime(2026, 8, 17, 10, 0, 0))];

        CalendarEntry? activated = null;
        SlotRange? asked = null;
        grid.EntryActivated += (_, entry) => activated = entry;
        grid.SlotActivated += (_, range) => asked = range;

        Press(grid, Key.Down);                                  // 08:00
        Press(grid, Key.Down, KeyModifiers.Shift);              // 08:30
        Press(grid, Key.Down, KeyModifiers.Shift);              // 09:00 — inside the appointment
        Press(grid, Key.Enter);

        Assert.Null(activated);
        Assert.Equal(new SlotRange(Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)), asked);
    }

    // ---- Schedule View ----------------------------------------------------------------------

    /// <summary>
    /// Time runs sideways here, so the range grows sideways — and raises the same shape, which is
    /// the point of there being one.
    /// </summary>
    [Fact]
    public void TheScheduleExtendsAlongTheDay()
    {
        var schedule = Schedule();

        SlotRange? asked = null;
        schedule.SlotActivated += (_, range) => asked = range;

        Press(schedule, Key.Right);                             // places the caret at 08:00
        Press(schedule, Key.Right, KeyModifiers.Shift);         // 08:30
        Press(schedule, Key.Right, KeyModifiers.Shift);         // 09:00
        Press(schedule, Key.Enter);

        Assert.Equal(new SlotRange(Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)), asked);
    }

    /// <summary>Up and down change calendar rather than time, so they end the range like any plain move.</summary>
    [Fact]
    public void ChangingRowEndsTheScheduleRange()
    {
        var schedule = Schedule();
        schedule.Rows = [new ScheduleRow(1, "Mine", null, false), new ScheduleRow(2, "Theirs", null, false)];

        SlotRange? asked = null;
        schedule.SlotActivated += (_, range) => asked = range;

        Press(schedule, Key.Right);                             // 08:00
        Press(schedule, Key.Right, KeyModifiers.Shift);         // range 08:00–09:00
        Press(schedule, Key.Down);                              // second row, range gone
        Press(schedule, Key.Enter);

        Assert.Equal(new SlotRange(Monday, new TimeOnly(8, 30), new TimeOnly(9, 0)), asked);
    }

    /// <summary>Escape lets go of the schedule's range too.</summary>
    [Fact]
    public void EscapeLetsGoOfTheScheduleRange()
    {
        var schedule = Schedule();

        SlotRange? asked = null;
        schedule.SlotActivated += (_, range) => asked = range;

        Press(schedule, Key.Right);
        Press(schedule, Key.Right, KeyModifiers.Shift);
        Press(schedule, Key.Escape);
        Press(schedule, Key.Enter);

        Assert.Equal(new SlotRange(Monday, new TimeOnly(8, 30), new TimeOnly(9, 0)), asked);
    }
}
