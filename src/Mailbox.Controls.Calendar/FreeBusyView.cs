using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Controls.Common;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>One row of the Scheduling Assistant: somebody asked, and what is known of their day.</summary>
/// <param name="Busy">
/// The blocks they are not free in. Empty with <paramref name="Known"/> false means nothing is
/// known, which is a different thing from being free all day and is drawn differently.
/// </param>
public sealed record FreeBusyRow(string Name, string Address, bool IsOrganizer, bool Known, IReadOnlyList<(DateTime Start, DateTime End)> Busy);

/// <summary>
/// The Scheduling Assistant's grid: everybody asked down the left, the day across the top, and
/// what is known of each person's time in between.
/// </summary>
/// <remarks>
/// <b>No capture exists for this view</b>, the reference's own being of an empty calendar; the
/// geometry is authored from the time grid's, which it shares — the same 28px header band, the
/// same hairlines, the same chip colours for a busy block.
/// <para>
/// <b>What is honestly knowable.</b> The reference reads free/busy out of Exchange for everybody
/// in the organization. There is no such service here, so this shows what this machine really
/// knows: the organizer's own calendar, in full, and a row per attendee saying that nothing is
/// known of theirs — which is exactly what the reference itself shows for somebody outside the
/// organization. A grid that drew invented free time would be worse than one that says it does
/// not know (rule 4).
/// </para>
/// </remarks>
public sealed class FreeBusyView : CalendarSurface
{
    /// <summary>The names column, wide enough for "A. Person" and a mail address beneath it.</summary>
    private const double NamesWidth = 190;

    private const double HeaderHeight = 28;
    private const double RowHeight = 34;
    private const double NameTextSize = 14;
    private const double AddressTextSize = 11;
    private const double HourTextSize = 12;

    private IReadOnlyList<FreeBusyRow> _rows = [];
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Today);
    private TimeOnly _from = new(8, 0);
    private TimeOnly _to = new(18, 0);
    private DateTime _start;
    private DateTime _end;

    public IReadOnlyList<FreeBusyRow> Rows
    {
        get => _rows;
        set
        {
            _rows = value ?? [];
            InvalidateVisual();
        }
    }

    /// <summary>The day on show.</summary>
    public DateOnly Day
    {
        get => _day;
        set
        {
            _day = value;
            InvalidateVisual();
        }
    }

    /// <summary>The hours the grid covers — the working day, widened to hold the meeting.</summary>
    public TimeOnly From
    {
        get => _from;
        set
        {
            _from = value;
            InvalidateVisual();
        }
    }

    public TimeOnly To
    {
        get => _to;
        set
        {
            _to = value;
            InvalidateVisual();
        }
    }

    /// <summary>The meeting itself, drawn over the grid as the reference's green bars do.</summary>
    public DateTime MeetingStart
    {
        get => _start;
        set
        {
            _start = value;
            InvalidateVisual();
        }
    }

    public DateTime MeetingEnd
    {
        get => _end;
        set
        {
            _end = value;
            InvalidateVisual();
        }
    }

    /// <summary>The meeting's edges dragged, in the grid's own wall times.</summary>
    public event EventHandler<(DateTime Start, DateTime End)>? MeetingMoved;

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < NamesWidth + 60 || height < HeaderHeight + RowHeight) return;

        var line = Palette.Colour(TokenKeys.Calendar.GridLine);
        Fill(context, new Rect(0, 0, width, height), Palette.Colour(TokenKeys.Calendar.Background));
        Fill(context, new Rect(0, 0, width, HeaderHeight), Palette.Colour(TokenKeys.Calendar.HeaderBackground));
        Fill(context, new Rect(0, HeaderHeight, width, 1), line);
        Fill(context, new Rect(NamesWidth, 0, 1, height), line);

        var grid = new Rect(NamesWidth + 1, HeaderHeight + 1, width - NamesWidth - 1, height - HeaderHeight - 1);
        DrawHours(context, grid, line);

        var y = grid.Y;
        foreach (var row in _rows)
        {
            if (y + RowHeight > Bounds.Height) break;
            DrawRow(context, row, new Rect(0, y, width, RowHeight), grid, line);
            y += RowHeight;
        }

        DrawMeeting(context, grid);
    }

    /// <summary>The hour marks across the top, one per hour the grid covers.</summary>
    private void DrawHours(DrawingContext context, Rect grid, Color line)
    {
        var ink = Palette.Colour(TokenKeys.Calendar.HourText);
        var hours = Math.Max(1, _to.Hour - _from.Hour);
        var step = grid.Width / hours;

        for (var i = 0; i <= hours; i++)
        {
            var x = grid.X + (i * step);
            Fill(context, new Rect(Math.Round(x), 0, 1, grid.Bottom), line);
            if (i == hours) break;

            var when = _from.AddHours(i);
            DrawAt(context, Ink(when.ToString("%h", Culture) + (when.Hour < 12 ? "am" : "pm"), HourTextSize, ink), x + 4, 19);
        }
    }

    private void DrawRow(DrawingContext context, FreeBusyRow row, Rect band, Rect grid, Color line)
    {
        var ink = Palette.Colour(TokenKeys.Calendar.DayText);
        var subtle = Palette.Colour(TokenKeys.Calendar.PastText);

        DrawAt(context, Ink(row.Name, NameTextSize, ink, row.IsOrganizer ? SemiBoldFace : Face), 8, band.Y + 15);
        if (row.Address.Length > 0)
        {
            DrawAt(context, Ink(Ellipsize(row.Address, NamesWidth - 16, AddressTextSize), AddressTextSize, subtle), 8, band.Y + 28);
        }

        Fill(context, new Rect(0, Math.Round(band.Bottom) - 1, band.Width, 1), line);

        if (!row.Known)
        {
            // Nothing known is said in words rather than drawn as free time: an empty row would
            // read as somebody with nothing on, which is a different and much worse claim.
            DrawAt(
                context,
                Ink("No free/busy information available", AddressTextSize, subtle),
                grid.X + 8,
                band.Y + 21);
            return;
        }

        foreach (var (start, end) in row.Busy)
        {
            var box = Slot(grid, start, end);
            if (box is not { } rect) continue;
            var paint = Palette.Chip(null, BusyStatus.Busy);
            Fill(context, new Rect(rect.X, band.Y + 6, rect.Width, RowHeight - 14), paint.Bar);
        }
    }

    /// <summary>The meeting itself: the two edges the reference draws in green over everybody.</summary>
    private void DrawMeeting(DrawingContext context, Rect grid)
    {
        if (_end <= _start) return;
        if (Slot(grid, _start, _end) is not { } box) return;

        var edge = Palette.Colour(TokenKeys.Calendar.CurrentTimeIndicator);
        Fill(context, new Rect(box.X, grid.Y, 2, grid.Height), edge);
        Fill(context, new Rect(box.Right - 2, grid.Y, 2, grid.Height), edge);
    }

    /// <summary>Where a span of the day falls on the grid, or null when it is off it.</summary>
    private Rect? Slot(Rect grid, DateTime start, DateTime end)
    {
        var dayStart = _day.ToDateTime(_from);
        var dayEnd = _day.ToDateTime(_to);
        if (end <= dayStart || start >= dayEnd) return null;

        var span = (dayEnd - dayStart).TotalMinutes;
        if (span <= 0) return null;

        var from = Math.Max(0, (start - dayStart).TotalMinutes);
        var to = Math.Min(span, (end - dayStart).TotalMinutes);
        var x = grid.X + (from / span * grid.Width);
        var width = Math.Max(2, (to - from) / span * grid.Width);
        return new Rect(x, grid.Y, width, grid.Height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Clicking the grid moves the meeting to that half hour, keeping its length — the
        // reference's own way of picking a time out of everybody's day.
        var point = e.GetPosition(this);
        if (point.X <= NamesWidth || point.Y <= HeaderHeight || _end <= _start) return;

        var grid = new Rect(NamesWidth + 1, HeaderHeight + 1, Bounds.Width - NamesWidth - 1, Bounds.Height - HeaderHeight - 1);
        var span = (_to - _from).TotalMinutes;
        var minutes = (point.X - grid.X) / grid.Width * span;
        var snapped = Math.Round(minutes / 30) * 30;

        var start = _day.ToDateTime(_from).AddMinutes(snapped);
        MeetingMoved?.Invoke(this, (start, start + (_end - _start)));
        e.Handled = true;
    }
}
