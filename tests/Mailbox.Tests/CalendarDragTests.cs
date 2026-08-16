using Avalonia;
using Mailbox.Controls.Calendar;
using Mailbox.Scheduling;

namespace Mailbox.Tests;

/// <summary>
/// The parts of a calendar drag that are arithmetic rather than pointer handling: which part of
/// a chip a grab is on, and what may be dragged at all.
/// </summary>
/// <remarks>
/// The rest of a drag — hit-testing, the threshold, the snapping and the write — is pressed
/// through the running application with <c>MAILBOX_DRAG</c> and read back out of the store,
/// because a drag is a gesture and a gesture is not a method call.
/// </remarks>
public class CalendarDragTests
{
    private static CalendarEntry Entry(bool readOnly = false)
    {
        var appointment = new CalendarEvent
        {
            Uid = "drag@test",
            Summary = "Review",
            Start = EventTime.At(new DateTime(2026, 8, 18, 9, 0, 0), "UTC"),
            End = EventTime.At(new DateTime(2026, 8, 18, 10, 0, 0), "UTC"),
        };

        var occurrence = Recurrence.Expand(
            [appointment],
            appointment.Start.ToUtc().AddDays(-1),
            appointment.End.ToUtc().AddDays(1)).First();

        return new CalendarEntry { Occurrence = occurrence, CollectionId = 1, ItemId = 1, IsReadOnly = readOnly };
    }

    [Fact]
    public void AGrabNearTheTopOfATallChipTakesItsStart()
    {
        var box = new Rect(0, 100, 180, 60);

        Assert.Equal(DragGrip.Start, ChipDrag.GripAt(box, new Point(90, 102), horizontal: false));
        Assert.Equal(DragGrip.End, ChipDrag.GripAt(box, new Point(90, 158), horizontal: false));
        Assert.Equal(DragGrip.Move, ChipDrag.GripAt(box, new Point(90, 130), horizontal: false));
    }

    [Fact]
    public void AGrabNearTheEndOfABarTakesItsEndWhenTheBarRunsSideways()
    {
        var box = new Rect(40, 10, 300, 18);

        Assert.Equal(DragGrip.Start, ChipDrag.GripAt(box, new Point(42, 19), horizontal: true));
        Assert.Equal(DragGrip.End, ChipDrag.GripAt(box, new Point(338, 19), horizontal: true));
        Assert.Equal(DragGrip.Move, ChipDrag.GripAt(box, new Point(200, 19), horizontal: true));
    }

    /// <summary>
    /// A chip with no middle has no edges either: on one two edges deep, every grab would be a
    /// resize and the appointment could never be moved at all.
    /// </summary>
    [Fact]
    public void AChipTooShortToHaveAMiddleIsAllBody()
    {
        var box = new Rect(0, 0, 200, 12);

        Assert.Equal(DragGrip.Move, ChipDrag.GripAt(box, new Point(100, 1), horizontal: false));
        Assert.Equal(DragGrip.Move, ChipDrag.GripAt(box, new Point(100, 11), horizontal: false));
    }

    [Fact]
    public void AnAppointmentOnAReadOnlyCalendarIsNotDragged()
    {
        Assert.True(ChipDrag.CanDrag(Entry()));
        Assert.False(ChipDrag.CanDrag(Entry(readOnly: true)));
    }

    [Fact]
    public void AMoveSaysWhetherItWasAResize()
    {
        var entry = Entry();
        var moved = new EntryMove(entry, entry.StartWall.AddHours(1), entry.EndWall.AddHours(1), AllDay: false);
        var resized = moved with { Resized = true };

        Assert.False(moved.Resized);
        Assert.True(resized.Resized);
    }
}
