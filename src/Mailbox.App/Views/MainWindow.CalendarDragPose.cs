using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Calendar;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// Pressing a drag into a calendar view, because the harness cannot click.
/// </summary>
/// <remarks>
/// Real pointer events at coordinates the view really drew at (<c>BoxOf</c>, <c>PointAt</c>),
/// raised at the view and left to its own handlers — so what a pose proves is the hit-testing,
/// the threshold, the snapping and the write, rather than an <c>EntryMove</c> the pose made up.
/// A synthesized event object would have proved only that the shell can save an appointment,
/// which was never the part in doubt.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// How a pose states a drag: <c>move:+2d</c>, <c>end:+30m</c>, <c>start:-1h</c>, and
    /// <c>move:band</c> for the one gesture no offset can say — into the all-day band, which is
    /// what turns a timed appointment into an all-day one. <c>move:+1r</c> is Schedule View's
    /// own axis: a row down, which is the next calendar.
    /// </summary>
    private static readonly Regex DragSpec = new(
        @"^(?<grip>move|start|end)\s*:\s*(?:(?<sign>[+-])(?<count>\d+)(?<unit>[dhmr])|(?<band>band))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Drags one appointment and says what the store holds afterwards.
    /// </summary>
    /// <remarks>
    /// The appointment is the one <c>MAILBOX_SELECT</c> names, or the first the view is showing
    /// that is not on a read-only calendar.
    /// </remarks>
    internal void PoseCalendarDrag(ShellViewModel shell, string spec)
    {
        var match = DragSpec.Match(spec.Trim());
        if (!match.Success)
        {
            Log.Info($"Harness: “{spec}” is not a drag — say move:+2d, end:+30m or start:-1h.");
            return;
        }

        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        calendar.UpdateLayout();

        var wanted = Environment.GetEnvironmentVariable("MAILBOX_SELECT");
        var entry = calendar.Entries.FirstOrDefault(e => !e.IsReadOnly
            && (wanted is not { Length: > 0 } || e.Summary.Contains(wanted, StringComparison.OrdinalIgnoreCase)));
        if (entry is null)
        {
            Log.Info("Harness: the calendar is showing nothing that can be dragged.");
            return;
        }

        var grip = match.Groups["grip"].Value.ToLowerInvariant();
        var band = match.Groups["band"].Success;
        var count = band ? 0 : int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture)
                    * (match.Groups["sign"].Value == "-" ? -1 : 1);
        var unit = band ? 'b' : char.ToLowerInvariant(match.Groups["unit"].Value[0]);

        var pressed = calendar.Month is { } month
            ? MonthDrag(month, entry, grip, count, unit)
            : calendar.TimeGrid is { } grid
                ? TimeGridDrag(grid, entry, grip, count, unit)
                : calendar.Schedule is { } schedule
                    ? ScheduleDrag(schedule, entry, grip, count, unit)
                    : null;

        if (pressed is not { } drag)
        {
            Log.Info($"Harness: “{entry.Summary}” could not be dragged {grip} {count}{unit} in this view.");
            return;
        }

        Log.Info($"Harness: dragging “{entry.Summary}” from ({drag.View.X:0},{drag.View.Y:0}) to ({drag.To.X:0},{drag.To.Y:0}).");
        Drag(drag.Control, drag.View, drag.To);

        // The row, read back out of the store — the claim is what the calendar holds, not what
        // the handler was called with.
        if (App.Pim.Item(entry.ItemId) is { } row)
        {
            Log.Info($"Harness: item {row.Id} now {row.StartsLocal}–{row.EndsLocal}{(row.AllDay ? " (all day)" : string.Empty)}, sync {row.SyncState}.");
        }

        foreach (var after in calendar.Entries.Where(e => e.Occurrence.Event.Uid == entry.Occurrence.Event.Uid))
        {
            Log.Info($"Harness: “{after.Summary}” is now {after.StartWall:yyyy-MM-dd HH:mm}–{after.EndWall:yyyy-MM-dd HH:mm}"
                + $" on {after.CollectionName}.");
        }
    }

    /// <summary>
    /// Schedule View's drag: time along the row, and rows are calendars — so <c>r</c> is the unit
    /// this view has and the others do not.
    /// </summary>
    /// <remarks>
    /// The destination row is taken from what the view actually drew rather than from the
    /// collection list, because a hidden calendar has no row to drop onto and the pose has to
    /// fail the way the pointer would.
    /// </remarks>
    private static (Control Control, Point View, Point To)? ScheduleDrag(ScheduleView schedule, CalendarEntry entry, string grip, int count, char unit)
    {
        if (schedule.BoxOf(entry) is not { } box) return null;

        var from = grip switch
        {
            "start" => new Point(box.X + 2, box.Center.Y),
            "end" => new Point(box.Right - 2, box.Center.Y),
            _ => box.Center,
        };

        if (unit == 'r')
        {
            if (grip != "move") return null;

            var rows = schedule.DrawnRows;
            var at = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].CollectionId == entry.CollectionId) at = i;
            }

            var to = at + count;
            if (at < 0 || to < 0 || to >= rows.Count) return null;
            if (schedule.LaneOf(rows[to].CollectionId) is not { } lane) return null;

            // Straight down the row: the same time, a different calendar, which is the whole of
            // what this axis means.
            return (schedule, from, new Point(from.X, lane.Center.Y));
        }

        var shift = unit switch
        {
            'h' => TimeSpan.FromHours(count),
            'm' => TimeSpan.FromMinutes(count),
            _ => TimeSpan.Zero,
        };

        if (shift == TimeSpan.Zero) return null;

        var edge = grip == "end" ? entry.EndWall : entry.StartWall;
        var wanted = edge.TimeOfDay + shift;
        if (schedule.PointAt(wanted, entry.CollectionId) is not { } target) return null;

        return (schedule, from, new Point(target.X, from.Y));
    }

    private static (Control Control, Point View, Point To)? MonthDrag(MonthView month, CalendarEntry entry, string grip, int count, char unit)
    {
        if (unit != 'd') return null;
        if (month.BoxOf(entry) is not { } box) return null;

        var (first, last) = entry.Days();
        var from = grip switch
        {
            "start" => new Point(box.X + 2, box.Center.Y),
            "end" => new Point(box.Right - 2, box.Center.Y),

            // A bar spanning several days is held over its first one, for the reason the band's
            // own move says: the view keeps the grab's offset, so a grab in the middle and a drop
            // one day back moves it by however far into the bar the grab was.
            _ => new Point(box.X + PastTheGrip, box.Center.Y),
        };

        // The target cell's own point, both ways: a day three on from Thursday is in the next
        // week's row, and keeping the row the grab was in aims at the same weekday a week early.
        var anchor = grip == "end" ? last : first;
        if (month.PointAt(anchor.AddDays(count)) is not { } target) return null;
        return (month, from, target);
    }

    private static (Control Control, Point View, Point To)? TimeGridDrag(TimeGridView grid, CalendarEntry entry, string grip, int count, char unit)
    {
        if (grid.BoxOf(entry) is not { } box) return null;

        // A bar in the all-day band runs sideways, so its edges are its left and right — and the
        // view reads a grip on one the same way. Grabbing the bottom of a horizontal bar, which is
        // what the timed grips below do, lands in its middle and turns every resize into a move:
        // a pose that asked to lengthen a three-day event by a day slid the whole thing forward
        // instead, and said it had resized it.
        if (entry.IsMultiDay)
        {
            if (unit != 'd') return null;

            var (opens, closes) = entry.Days();
            var edge = grip switch
            {
                "start" => new Point(box.X + 2, box.Center.Y),
                "end" => new Point(box.Right - 2, box.Center.Y),

                // Held over its first day rather than its middle, and past the edge zone so it is
                // a move and not a resize. The view keeps whatever offset the grab had inside the
                // bar, so grabbing the middle of three days and dropping on the day before puts
                // the bar two days back — which is right for a pointer and wrong for a pose that
                // says "one day earlier".
                _ => new Point(box.X + PastTheGrip, box.Center.Y),
            };

            var anchor = (grip == "end" ? closes : opens).AddDays(count);
            if (grid.PointAt(anchor, TimeSpan.Zero, allDay: true) is not { } landing) return null;
            return (grid, edge, landing);
        }

        var from = grip switch
        {
            "start" => new Point(box.Center.X, box.Y + 2),
            "end" => new Point(box.Center.X, box.Bottom - 2),
            _ => box.Center,
        };

        var own = DateOnly.FromDateTime(entry.StartWall);

        if (unit == 'b')
        {
            // Straight up into the all-day band, on the appointment's own day.
            if (grid.PointAt(own, TimeSpan.Zero, allDay: true) is not { } band) return null;
            return (grid, from, band);
        }

        if (unit == 'd')
        {
            // Sideways by whole columns, which is what the two centres are the distance between.
            var here = grid.PointAt(own, entry.StartWall.TimeOfDay, entry.AllDay);
            var there = grid.PointAt(own.AddDays(count), entry.StartWall.TimeOfDay, entry.AllDay);
            if (here is not { } a || there is not { } b) return null;
            return (grid, from, new Point(from.X + (b.X - a.X), from.Y));
        }

        var minutes = unit == 'h' ? count * 60 : count;
        return (grid, from, new Point(from.X, from.Y + (minutes * grid.SlotHeight / grid.SlotMinutes)));
    }

    /// <summary>
    /// Press, move past the threshold, move to the target, release — the four events a pointer
    /// would have sent, raised at the view so every handler between sees them.
    /// </summary>
    /// <remarks>
    /// A pointer event states its position in the window's own coordinates, not the control's,
    /// and asking it for a position in the control is what subtracts the difference. Handing one
    /// coordinates that were already the control's is how a drag lands hundreds of pixels up and
    /// to the left of the thing it was aimed at — off the grid entirely, hitting nothing.
    /// </remarks>
    private static void Drag(Control view, Point from, Point to)
    {
        var root = TopLevel.GetTopLevel(view) as Visual ?? view;
        var start = view.TranslatePoint(from, root) ?? from;
        var end = view.TranslatePoint(to, root) ?? to;

        var pointer = new Pointer(1, PointerType.Mouse, isPrimary: true);
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);

        view.RaiseEvent(new PointerPressedEventArgs(view, pointer, root, start, 0, down, KeyModifiers.None));

        // Two moves: the first only has to pass the threshold, the second says where it lands.
        // One move that did both would leave a drag that had never been under way at the moment
        // it was asked where it was going.
        var nudge = new Point(
            start.X + (Math.Sign(end.X - start.X) * (ChipNudge + 1)),
            start.Y + (Math.Sign(end.Y - start.Y) * (ChipNudge + 1)));
        view.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, view, pointer, root, nudge, 1, down, KeyModifiers.None));
        view.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, view, pointer, root, end, 2, down, KeyModifiers.None));

        view.RaiseEvent(new PointerReleasedEventArgs(view, pointer, root, end, 3, up, KeyModifiers.None, MouseButton.Left));
    }

    /// <summary>The drag threshold, as far as a pose needs to know it: enough to pass it.</summary>
    private const double ChipNudge = 5;

    /// <summary>Far enough inside a bar's first day to be a move rather than a grab at its edge.</summary>
    private const double PastTheGrip = 10;
}
