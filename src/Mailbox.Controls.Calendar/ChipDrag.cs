using Avalonia;

namespace Mailbox.Controls.Calendar;

/// <summary>Which part of a chip was taken hold of, and so what dragging it does.</summary>
public enum DragGrip
{
    /// <summary>The body: the appointment keeps its length and changes when it starts.</summary>
    Move,

    /// <summary>The leading edge: it starts later or earlier and still ends when it did.</summary>
    Start,

    /// <summary>The trailing edge: it still starts when it did and runs for longer or less.</summary>
    End,
}

/// <summary>Where a drag left an appointment: the wall times it should now keep.</summary>
/// <param name="AllDay">
/// What it should now be, not what it was — a timed appointment dropped in the all-day band
/// becomes an all-day one, which is the same gesture the reference reads that way.
/// </param>
public sealed record EntryMove(CalendarEntry Entry, DateTime Start, DateTime End, bool AllDay)
{
    /// <summary>True when only the length changed, which is worth saying differently.</summary>
    public bool Resized { get; init; }
}

/// <summary>
/// A drag over a calendar view, from the press that might become one to the release that
/// carries it out.
/// </summary>
/// <remarks>
/// A press is not yet a drag: the same press selects the appointment, opens it on the second
/// click, and moves it only once the pointer has gone far enough to mean it. Until then this
/// holds what would be dragged and nothing has happened.
/// </remarks>
internal sealed class ChipDrag
{
    /// <summary>How far the pointer goes before a press counts as a drag: the usual few pixels.</summary>
    public const double Threshold = 4;

    /// <summary>How close to an edge counts as taking hold of it rather than of the body.</summary>
    public const double EdgeReach = 5;

    public required CalendarEntry Entry { get; init; }

    public required DragGrip Grip { get; init; }

    /// <summary>Where the press landed, which a move is measured from.</summary>
    public required Point Origin { get; init; }

    /// <summary>The box the chip was drawn in, so the grab keeps its offset inside it.</summary>
    public required Rect Box { get; init; }

    /// <summary>Whether the pointer has passed the threshold: false while it is still a click.</summary>
    public bool Live { get; set; }

    /// <summary>Where the drag currently proposes to leave it.</summary>
    public DateTime Start { get; set; }

    public DateTime End { get; set; }

    /// <summary>Which side of the view it is over: the all-day band or the timed grid.</summary>
    public bool AllDay { get; set; }

    /// <summary>True once the proposal differs from where the appointment already is.</summary>
    public bool Moved => Start != Entry.StartWall || End != Entry.EndWall || AllDay != Entry.AllDay;

    /// <summary>
    /// Which part of a chip a point is on. An edge grip needs the chip to be big enough to have
    /// a middle as well: on a chip two edges deep every grab would be a resize.
    /// </summary>
    public static DragGrip GripAt(Rect box, Point point, bool horizontal)
    {
        var length = horizontal ? box.Width : box.Height;
        if (length < (EdgeReach * 3)) return DragGrip.Move;

        var from = horizontal ? point.X - box.X : point.Y - box.Y;
        if (from <= EdgeReach) return DragGrip.Start;
        if (from >= length - EdgeReach) return DragGrip.End;
        return DragGrip.Move;
    }

    /// <summary>An appointment on a calendar nobody may write to is not dragged anywhere.</summary>
    public static bool CanDrag(CalendarEntry entry) => !entry.IsReadOnly;
}
