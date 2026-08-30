using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>One row of the schedule: a calendar, and what is on it that day.</summary>
/// <param name="IsReadOnly">
/// True for a subscription or a shared calendar nobody here may write to. The row is drawn like
/// any other and read like any other; what it is not is a place to drop an appointment.
/// </param>
public sealed record ScheduleRow(
    long CollectionId,
    string Name,
    Avalonia.Media.Color? Colour,
    bool IsReadOnly = false);

/// <summary>
/// Schedule View: one day laid out sideways, a row per calendar, so several calendars can be
/// read against each other at a glance.
/// </summary>
/// <remarks>
/// The reference's answer to "when is everyone free", minus the tenant free/busy that is out of
/// scope here — the rows are the calendars this machine has, local and subscribed alike.
/// <b>No capture exists for this view</b>; the geometry is authored from the reference's shape
/// and shares the time grid's own numbers so the two read as one family.
/// </remarks>
public sealed class ScheduleView : CalendarSurface
{
    /// <summary>The column of calendar names down the left, measured.</summary>
    private const double NameWidth = 144;

    /// <summary>The band naming the day, above the ruler and across the whole width.</summary>
    private const double DateBandHeight = 27;

    /// <summary>The hour ruler under it.</summary>
    private const double RulerHeight = 25;

    /// <summary>The all-day band between the ruler and the rows.</summary>
    private const double AllDayHeight = 34;

    private const double RulerBaseline = 18;
    private const double RulerTextSize = 13;

    /// <summary>The shortest a calendar's row is drawn; they share whatever is left over.</summary>
    private const double MinimumRowHeight = 44;

    /// <summary>The scroll bar across the foot, which is how the reference moves through the day.</summary>
    private const double ScrollBarHeight = 17;

    /// <summary>
    /// What a dragged time is rounded to. A quarter of an hour, because this view shows a whole
    /// day across a pane and a finer snap would be finer than the pixels under it.
    /// </summary>
    private const double SnapMinutes = 15;

    private readonly List<(Rect Box, CalendarEntry Entry)> _entryHits = [];

    /// <summary>Each calendar's band, so a drag can be asked which row it is over.</summary>
    private readonly List<(Rect Lane, ScheduleRow Row)> _rowHits = [];

    /// <summary>The lanes and the scale, kept from the last render so a pointer can be read.</summary>
    private Rect _lanes;
    private double _perHour = 1;
    private ChipDrag? _drag;

    private DateOnly _day = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private DateTime? _now;
    private IReadOnlyList<ScheduleRow> _rows = [];
    private IReadOnlyList<CalendarEntry> _entries = [];
    private CalendarEntry? _selectedEntry;
    private double _startHour = 7;
    private double _hoursShown = 12;
    private TimeOnly _workStart = new(8, 0);
    private TimeOnly _workEnd = new(17, 0);

    public ScheduleView()
    {
        Focusable = true;
    }

    public DateOnly Day
    {
        get => _day;
        set => Set(ref _day, value);
    }

    public DateOnly Today
    {
        get => _today;
        set => Set(ref _today, value);
    }

    public DateTime? Now
    {
        get => _now;
        set => Set(ref _now, value);
    }

    public IReadOnlyList<ScheduleRow> Rows
    {
        get => _rows;
        set
        {
            _rows = value ?? [];
            InvalidateVisual();
        }
    }

    public IReadOnlyList<CalendarEntry> Entries
    {
        get => _entries;
        set
        {
            _entries = value ?? [];
            InvalidateVisual();
        }
    }

    public CalendarEntry? SelectedEntry
    {
        get => _selectedEntry;
        set => Set(ref _selectedEntry, value);
    }

    /// <summary>The hour at the left edge, and how many hours the width covers.</summary>
    public double StartHour
    {
        get => _startHour;
        set => Set(ref _startHour, Math.Clamp(value, 0, 23));
    }

    public double HoursShown
    {
        get => _hoursShown;
        set => Set(ref _hoursShown, Math.Clamp(value, 2, 24));
    }

    public TimeOnly WorkDayStart
    {
        get => _workStart;
        set => Set(ref _workStart, value);
    }

    public TimeOnly WorkDayEnd
    {
        get => _workEnd;
        set => Set(ref _workEnd, value);
    }

    public event EventHandler<CalendarEntry>? EntrySelected;
    public event EventHandler<CalendarEntry>? EntryActivated;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        _entryHits.Clear();
        _rowHits.Clear();
        var header = DateBandHeight + RulerHeight + AllDayHeight + 4;
        if (Bounds.Width < NameWidth + 80 || Bounds.Height < header + MinimumRowHeight) return;

        var gridLine = Palette.Colour(TokenKeys.Calendar.GridLine);
        var headerFill = Palette.Colour(TokenKeys.Calendar.HeaderBackground);

        // The grid's own top border, the day, the hours, and the all-day band, in that order —
        // three stacked bands, as the reference draws them.
        Fill(context, new Rect(0, 0, Bounds.Width, 1), gridLine);

        var dateTop = 1;
        Fill(context, new Rect(0, dateTop, Bounds.Width, DateBandHeight), headerFill);
        DrawAt(
            context,
            Ink(Day.ToString("dddd, MMMM d, yyyy", Culture), RulerTextSize, Palette.Colour(TokenKeys.Calendar.HeaderText), SemiBoldFace),
            NameWidth + 8,
            dateTop + RulerBaseline);
        Fill(context, new Rect(0, dateTop + DateBandHeight, Bounds.Width, 1), Palette.Colour(TokenKeys.Calendar.HeaderLine));

        var rulerTop = dateTop + DateBandHeight + 1;
        Fill(context, new Rect(0, rulerTop, Bounds.Width, RulerHeight), headerFill);
        Fill(context, new Rect(0, rulerTop + RulerHeight, Bounds.Width, 1), gridLine);

        var bandTop = rulerTop + RulerHeight + 1;
        Fill(context, new Rect(0, bandTop, Bounds.Width, AllDayHeight), Palette.Colour(TokenKeys.Calendar.AllDayBandBackground));
        Fill(context, new Rect(0, bandTop + AllDayHeight, Bounds.Width, 1), gridLine);

        var rowsTop = bandTop + AllDayHeight + 1;
        var rowsHeight = Math.Max(0, Bounds.Height - rowsTop - ScrollBarHeight);
        var lanes = new Rect(NameWidth, rowsTop, Bounds.Width - NameWidth, rowsHeight);
        var perHour = lanes.Width / HoursShown;
        _lanes = lanes;
        _perHour = perHour;

        Fill(context, new Rect(0, rowsTop, NameWidth, rowsHeight), headerFill);

        var hourInk = Palette.Colour(TokenKeys.Calendar.HourText);
        for (var h = 0; h <= HoursShown; h++)
        {
            var hour = StartHour + h;
            if (hour > 24) break;
            var x = lanes.X + (h * perHour);
            var label = TimeOnly.FromTimeSpan(TimeSpan.FromHours(hour % 24)).ToString("h tt", Culture);
            DrawAt(context, Ink(label, RulerTextSize, hourInk), x + 4, rulerTop + RulerBaseline);
            Fill(context, new Rect(x, rulerTop, 1, Bounds.Height - rulerTop - ScrollBarHeight), gridLine);
        }

        // The rows share whatever height is left, which is why one calendar fills the pane in
        // the reference rather than sitting in a 44px strip at the top of it.
        var count = Math.Max(1, Rows.Count);
        var each = Math.Max(MinimumRowHeight, (rowsHeight - count) / count);

        for (var r = 0; r < Rows.Count; r++)
        {
            var top = rowsTop + (r * (each + 1));
            if (top >= lanes.Bottom) break;
            var height = Math.Min(each, lanes.Bottom - top);
            var row = new Rect(lanes.X, top, lanes.Width, height);
            Fill(context, row, Palette.Colour(TokenKeys.Calendar.NonWorkingFill));

            var workLeft = lanes.X + ((WorkDayStart.ToTimeSpan().TotalHours - StartHour) * perHour);
            var workRight = lanes.X + ((WorkDayEnd.ToTimeSpan().TotalHours - StartHour) * perHour);
            var clipped = new Rect(
                Math.Max(lanes.X, workLeft),
                top,
                Math.Max(0, Math.Min(lanes.Right, workRight) - Math.Max(lanes.X, workLeft)),
                height);
            if (clipped.Width > 0) Fill(context, clipped, Palette.Colour(TokenKeys.Calendar.WorkingHoursFill));

            DrawAt(
                context,
                Ink(Rows[r].Name, RulerTextSize, Palette.Colour(TokenKeys.Calendar.HeaderText), SemiBoldFace),
                8,
                top + 20);

            Fill(context, new Rect(0, top + height, Bounds.Width, 1), gridLine);
            _rowHits.Add((row, Rows[r]));
            DrawRowEntries(context, Rows[r], row, perHour);
        }

        DrawGhost(context);

        Fill(context, new Rect(NameWidth, 0, 1, Bounds.Height - ScrollBarHeight), gridLine);
        DrawNow(context, lanes, perHour);
        DrawScrollBar(context, perHour);
    }

    /// <summary>
    /// The bar across the foot: the day is wider than the pane, and this is how far along it the
    /// visible hours are.
    /// </summary>
    private void DrawScrollBar(DrawingContext context, double perHour)
    {
        var track = new Rect(NameWidth, Bounds.Height - ScrollBarHeight, Bounds.Width - NameWidth, ScrollBarHeight);
        if (track.Width < 40) return;
        Fill(context, track, Palette.Colour(TokenKeys.Nav.Background));

        var fraction = Math.Clamp(HoursShown / 24.0, 0.05, 1);
        var thumbWidth = Math.Max(30, track.Width * fraction);
        var offset = (track.Width - thumbWidth) * Math.Clamp(StartHour / Math.Max(1, 24 - HoursShown), 0, 1);
        Fill(
            context,
            new Rect(track.X + offset, track.Y + 1, thumbWidth, track.Height - 2),
            Palette.Colour(TokenKeys.Border.Strong));
    }

    private void DrawRowEntries(DrawingContext context, ScheduleRow row, Rect lane, double perHour)
    {
        var dayStart = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var mine = Entries
            .Where(e => e.CollectionId == row.CollectionId)
            .Where(e =>
            {
                var (first, last) = e.Days();
                return first <= Day && last >= Day;
            })
            .ToList();
        if (mine.Count == 0) return;

        var placed = EventLayout.Solve(
            mine,
            e => new DateTimeOffset(e.AllDay ? dayStart : e.StartWall, TimeSpan.Zero),
            e => new DateTimeOffset(e.AllDay ? dayStart.AddDays(1) : e.EndWall, TimeSpan.Zero));

        foreach (var box in placed)
        {
            var startHours = (box.Start.DateTime - dayStart).TotalHours;
            var endHours = (box.End.DateTime - dayStart).TotalHours;
            var left = lane.X + ((startHours - StartHour) * perHour);
            var right = lane.X + ((endHours - StartHour) * perHour);
            if (right < lane.X || left > lane.Right) continue;

            var slice = lane.Height / Math.Max(1, box.Columns);
            var rect = new Rect(
                Math.Max(lane.X, left),
                lane.Y + (box.Column * slice) + 1,
                Math.Max(6, Math.Min(lane.Right, right) - Math.Max(lane.X, left) - 1),
                Math.Max(ChipHeight(1), (slice * box.Span) - 2));

            var entry = box.Item;
            var lines = Wrap(entry.Summary, rect.Width - ChipTextInset - 2, 1, ChipTextSize, SemiBoldFace);
            DrawChip(context, rect, Palette.Chip(entry.Colour ?? row.Colour, entry.Busy), lines, ReferenceEquals(entry, SelectedEntry), boldFirstLine: true, reminder: entry.HasReminder);
            _entryHits.Add((rect, entry));
        }
    }

    private void DrawNow(DrawingContext context, Rect lanes, double perHour)
    {
        if (Now is not { } now || DateOnly.FromDateTime(now) != Day) return;
        var x = lanes.X + ((now.TimeOfDay.TotalHours - StartHour) * perHour);
        if (x < lanes.X || x > lanes.Right) return;
        Fill(context, new Rect(Math.Round(x), lanes.Y, 2, lanes.Height), Palette.Colour(TokenKeys.Calendar.CurrentTimeIndicator));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            if (!_entryHits[i].Box.Contains(point)) continue;
            var (box, entry) = _entryHits[i];
            SelectedEntry = entry;
            EntrySelected?.Invoke(this, entry);

            if (e.ClickCount >= 2)
            {
                EntryActivated?.Invoke(this, entry);
            }
            else if (ChipDrag.CanDrag(entry) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // Time runs sideways here, so an edge grip is a left or right edge — the one
                // place this view's geometry differs from the time grid's for a drag.
                _drag = new ChipDrag
                {
                    Entry = entry,
                    Grip = ChipDrag.GripAt(box, point, horizontal: true),
                    Origin = point,
                    Box = box,
                    Start = entry.StartWall,
                    End = entry.EndWall,
                    AllDay = entry.AllDay,
                    ToCollectionId = entry.CollectionId,
                };
                e.Pointer.Capture(this);
            }

            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } drag) return;

        if (!drag.Live)
        {
            var far = Math.Abs(e.GetPosition(this).X - drag.Origin.X) > ChipDrag.Threshold
                      || Math.Abs(e.GetPosition(this).Y - drag.Origin.Y) > ChipDrag.Threshold;
            if (!far) return;
            drag.Live = true;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }

        Propose(drag, e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var drag = _drag;
        _drag = null;
        Cursor = Cursor.Default;
        if (drag is null) return;

        e.Pointer.Capture(null);
        if (!drag.Live) return;

        InvalidateVisual();
        if (!drag.Moved) return;

        RaiseMoved(new EntryMove(drag.Entry, drag.Start, drag.End, drag.AllDay)
        {
            Resized = drag.Grip != DragGrip.Move,

            // Only when it actually changed rows: a drag within one calendar must not queue a
            // move to the calendar it is already on.
            ToCollectionId = drag.ToCollectionId is { } to && to != drag.Entry.CollectionId ? to : null,
        });
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || _drag is null) return;

        // A drag let go of: nothing is written and the chip is where it was.
        _drag = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Where the drag now proposes to leave the appointment: when, from the pointer's distance
    /// along the day, and on which calendar, from the row it is over.
    /// </summary>
    /// <remarks>
    /// Read off the pointer each time rather than accumulated, for the reason the time grid's is:
    /// an accumulated delta drifts by whatever the snapping rounded away on each step.
    /// <para>
    /// A row belonging to a calendar nobody may write to is not a place to drop one, so the
    /// proposal stays on the last row that was — the drag is refused where it would fail rather
    /// than accepted and then undone.
    /// </para>
    /// </remarks>
    private void Propose(ChipDrag drag, Point point)
    {
        if (_lanes.Width <= 0 || _perHour <= 0) return;

        var entry = drag.Entry;
        var dayStart = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var least = TimeSpan.FromMinutes(SnapMinutes);

        // An all-day appointment has no place along the ruler, so a drag across one moves it
        // between calendars and leaves the day alone.
        if (!entry.AllDay)
        {
            if (drag.Grip == DragGrip.Move)
            {
                var at = TimeAt(point.X - (drag.Origin.X - drag.Box.X));
                drag.Start = dayStart + at;
                drag.End = drag.Start + (entry.EndWall - entry.StartWall);
            }
            else if (drag.Grip == DragGrip.Start)
            {
                var at = dayStart + TimeAt(point.X);
                drag.Start = at <= drag.End - least ? at : drag.End - least;
            }
            else
            {
                var at = dayStart + TimeAt(point.X);
                drag.End = at >= drag.Start + least ? at : drag.Start + least;
            }
        }

        // Resizing is about when, not about whose: an edge dragged off the row it started on is
        // still that appointment's edge.
        if (drag.Grip != DragGrip.Move) return;

        foreach (var (lane, row) in _rowHits)
        {
            if (point.Y < lane.Y || point.Y >= lane.Bottom) continue;
            if (row.IsReadOnly) return;
            drag.ToCollectionId = row.CollectionId;
            return;
        }
    }

    /// <summary>The time a distance along the lanes reads as, snapped and kept inside the day.</summary>
    private TimeSpan TimeAt(double x)
    {
        var hours = StartHour + ((x - _lanes.X) / _perHour);
        var minutes = Math.Round(hours * 60 / SnapMinutes) * SnapMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 0, (24 * 60) - SnapMinutes));
    }

    /// <summary>
    /// The chip a drag is proposing, drawn where it would land while the original stays put — so
    /// the two can be compared, which is the whole point of showing it.
    /// </summary>
    private void DrawGhost(DrawingContext context)
    {
        if (_drag is not { Live: true } drag || !drag.Moved) return;
        if (_rowHits.FirstOrDefault(r => r.Row.CollectionId == (drag.ToCollectionId ?? drag.Entry.CollectionId)) is not { Row: not null } target) return;

        var dayStart = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var left = _lanes.X + (((drag.Start - dayStart).TotalHours - StartHour) * _perHour);
        var right = _lanes.X + (((drag.End - dayStart).TotalHours - StartHour) * _perHour);
        if (right < _lanes.X || left > _lanes.Right) return;

        var box = new Rect(
            Math.Max(_lanes.X, left),
            target.Lane.Y + 1,
            Math.Max(12, Math.Min(_lanes.Right, right) - Math.Max(_lanes.X, left) - 1),
            Math.Max(ChipHeight(1), target.Lane.Height - 2));

        var entry = drag.Entry;
        var lines = Wrap(entry.Summary, box.Width - ChipTextInset - 2, 1, ChipTextSize, SemiBoldFace);
        using var clip = context.PushClip(_lanes);
        using var fade = context.PushOpacity(0.65);
        DrawChip(context, box, Palette.Chip(entry.Colour ?? target.Row.Colour, entry.Busy), lines, selected: true, boldFirstLine: true, reminder: entry.HasReminder);
    }

    /// <summary>Where an entry was drawn, for a harness that has to press a real drag.</summary>
    public Rect? BoxOf(CalendarEntry entry)
    {
        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_entryHits[i].Entry, entry)) return _entryHits[i].Box;
        }

        return null;
    }

    /// <summary>The band a calendar was drawn in, or null when it is not on show.</summary>
    public Rect? LaneOf(long collectionId)
    {
        foreach (var (lane, row) in _rowHits)
        {
            if (row.CollectionId == collectionId) return lane;
        }

        return null;
    }

    /// <summary>The calendars this view is drawing, in the order it drew them.</summary>
    public IReadOnlyList<ScheduleRow> DrawnRows => [.. _rowHits.Select(r => r.Row)];

    /// <summary>The point a time of day sits at, or null when that hour is not on show.</summary>
    public Point? PointAt(TimeSpan at, long collectionId)
    {
        if (LaneOf(collectionId) is not { } lane || _perHour <= 0) return null;
        var x = _lanes.X + ((at.TotalHours - StartHour) * _perHour);
        return x < _lanes.X || x > _lanes.Right ? null : new Point(x, lane.Center.Y);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        StartHour -= e.Delta.Y;
        e.Handled = true;
    }
}
