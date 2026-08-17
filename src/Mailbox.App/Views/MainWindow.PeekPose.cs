using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Calendar;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// Pressing the calendar peek's own buttons, because the harness cannot click.
/// </summary>
/// <remarks>
/// The same method the drag pose uses: real pointer events at coordinates the view really drew
/// at, raised at the view and left to its own hit testing — so what a pose proves is that a day
/// cell, an arrow, the corner button and an agenda entry do what they say, rather than that the
/// shell can be told to do it.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Presses one thing in whichever peek is open: <c>day:2026-08-19</c>, <c>next</c>,
    /// <c>previous</c>, <c>corner</c> — which docks a floating peek and closes a docked one — or
    /// <c>entry:0</c> for the first appointment in the day's list.
    /// </summary>
    internal void PoseCalendarPeekPress(ShellViewModel shell, string spec)
    {
        var peek = _floatingPeek
            ?? this.FindControl<ContentControl>("DockHost")?.Content as PeekView;
        if (peek is null)
        {
            Log.Info("Harness: no peek is open — pose MAILBOX_PEEK=calendar or =docked as well.");
            return;
        }

        peek.UpdateLayout();
        var text = spec.Trim();

        if (text.StartsWith("day:", StringComparison.OrdinalIgnoreCase))
        {
            var when = text["day:".Length..].Trim();
            if (!DateOnly.TryParseExact(when, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                Log.Info($"Harness: “{when}” is not a date — say day:yyyy-MM-dd.");
                return;
            }

            if (peek.BoxOf(day) is not { } cell)
            {
                Log.Info($"Harness: {day:yyyy-MM-dd} is not on the peek's grid.");
                return;
            }

            Press(peek, cell.Center);
            Log.Info($"Harness: peek day {peek.Selected:yyyy-MM-dd} selected, month {peek.Anchor:yyyy-MM}.");
            return;
        }

        if (text.StartsWith("entry:", StringComparison.OrdinalIgnoreCase))
        {
            var index = int.TryParse(text["entry:".Length..].Trim(), CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (peek.BoxOf(index) is not { } box)
            {
                Log.Info($"Harness: the peek's agenda has no entry {index} — it holds {peek.Agenda.Count}.");
                return;
            }

            Log.Info($"Harness: opening the peek's “{peek.Agenda[index].Subject}”.");
            CaptureNextWindow();
            Press(peek, box.Center);
            return;
        }

        if (text.StartsWith("scroll:", StringComparison.OrdinalIgnoreCase))
        {
            // Down the agenda, in wheel notches: the peek is a fixed height and a busy day has
            // more on it than fits.
            var notches = int.TryParse(text["scroll:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
            var where = new Point(peek.Bounds.Width / 2, peek.Bounds.Height - 40);
            Wheel(peek, where, -notches);
            Log.Info($"Harness: the peek's agenda is {peek.Scroll:0} down, holding {peek.Agenda.Count}.");
            return;
        }

        switch (text.ToLowerInvariant())
        {
            case "next":
            case "previous":
                Press(peek, (text.Equals("next", StringComparison.OrdinalIgnoreCase) ? peek.NextBox : peek.PreviousBox).Center);
                Log.Info($"Harness: the peek is showing {peek.Anchor:yyyy-MM}.");
                break;

            case "corner":
                Press(peek, peek.CornerBox.Center);
                Log.Info($"Harness: the calendar is {(shell.IsCalendarDocked ? "docked" : "not docked")}.");
                break;

            default:
                Log.Info($"Harness: “{spec}” is not a peek press — say day:yyyy-MM-dd, next, previous, corner or entry:0.");
                break;
        }
    }

    /// <summary>
    /// One click: press and release at a point the view drew at, in the window's coordinates —
    /// which is what a pointer event states, and what handing it the control's own would get
    /// wrong by however far the control is from the window's corner.
    /// </summary>
    private static void Press(Control view, Point point)
    {
        var root = TopLevel.GetTopLevel(view) as Visual ?? view;
        var at = view.TranslatePoint(point, root) ?? point;

        var pointer = new Pointer(2, PointerType.Mouse, isPrimary: true);
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);

        view.RaiseEvent(new PointerPressedEventArgs(view, pointer, root, at, 0, down, KeyModifiers.None));
        view.RaiseEvent(new PointerReleasedEventArgs(view, pointer, root, at, 1, up, KeyModifiers.None, MouseButton.Left));
    }

    /// <summary>One turn of the wheel over a point the view drew at.</summary>
    private static void Wheel(Control view, Point point, int notches)
    {
        var root = TopLevel.GetTopLevel(view) as Visual ?? view;
        var at = view.TranslatePoint(point, root) ?? point;
        var pointer = new Pointer(3, PointerType.Mouse, isPrimary: true);

        view.RaiseEvent(new PointerWheelEventArgs(
            view, pointer, root, at, 0, new PointerPointProperties(), KeyModifiers.None, new Vector(0, notches)));
    }
}
