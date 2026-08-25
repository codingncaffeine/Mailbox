using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// The month grid: seven columns of week days, six rows of weeks, and the appointments in them.
/// </summary>
/// <remarks>
/// Measured off the four month-view captures. The parts that are numbers rather than tokens are
/// named below and every one of them was read off a capture rather than chosen: the 28px weekday
/// header, the 30px a cell leaves above its first appointment, the 21px baseline of a day number,
/// the 13px between a chip's lines and the 5px it adds to them, and the 10px a chip stops short
/// of its cell's right edge.
/// <para>
/// The row's all-day and multi-day items are packed into bands that run across the columns and
/// line up, and each cell stacks its own timed appointments beneath them — which is the only
/// arrangement in which a bar spanning Monday to Wednesday can be one bar while the three cells
/// under it hold different numbers of appointments.
/// </para>
/// </remarks>
public sealed class MonthView : CalendarSurface
{
    /// <summary>The weekday header's own height, between the grid's top line and its own.</summary>
    private const double HeaderHeight = 28;

    /// <summary>The baseline of the weekday names, from the header's top.</summary>
    private const double HeaderBaseline = 20;

    /// <summary>The baseline of a day number, from its cell's top.</summary>
    private const double DayBaseline = 21;

    /// <summary>Where a day number starts, from its cell's left.</summary>
    private const double DayInset = 7;

    /// <summary>The day number's size, and the weekday header's.</summary>
    private const double DayTextSize = 15;

    /// <summary>What a cell leaves above its first appointment.</summary>
    private const double CellTopReserve = 30;

    /// <summary>How far short of its cell's right edge a chip stops.</summary>
    private const double ChipRightInset = 10;

    /// <summary>The scroll gutter down the right-hand edge.</summary>
    private const double GutterWidth = 17;

    private readonly List<(Rect Box, CalendarEntry Entry)> _entryHits = [];
    private readonly List<(Rect Box, DateOnly Day)> _dayHits = [];
    private Rect _gutter;
    private ChipDrag? _drag;

    public MonthView()
    {
        Focusable = true;
    }

    private DateOnly _firstDay = DateOnly.FromDateTime(DateTime.Today);
    private int _weeks = 6;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly? _selected;
    private CalendarEntry? _selectedEntry;
    private IReadOnlyList<CalendarEntry> _entries = [];

    /// <summary>The date in the top-left cell. Always the start of a week.</summary>
    public DateOnly FirstDay
    {
        get => _firstDay;
        set => Set(ref _firstDay, value);
    }

    public int Weeks
    {
        get => _weeks;
        set => Set(ref _weeks, Math.Clamp(value, 1, 10));
    }

    /// <summary>Today, as the view believes it — pinned by the harness, live otherwise.</summary>
    public DateOnly Today
    {
        get => _today;
        set => Set(ref _today, value);
    }

    public DateOnly? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public CalendarEntry? SelectedEntry
    {
        get => _selectedEntry;
        set => Set(ref _selectedEntry, value);
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

    /// <summary>The month the title names — the one most of the shown weeks belong to.</summary>
    public DateOnly DominantMonth
    {
        get
        {
            var middle = FirstDay.AddDays(((Weeks * 7) - 1) / 2);
            return new DateOnly(middle.Year, middle.Month, 1);
        }
    }

    public event EventHandler<DateOnly>? DaySelected;
    public event EventHandler<DateOnly>? DayActivated;
    public event EventHandler<CalendarEntry>? EntryActivated;
    public event EventHandler<CalendarEntry>? EntrySelected;

    /// <summary>Raised with a number of weeks when the view is scrolled.</summary>
    public event EventHandler<int>? Scrolled;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        InvalidateVisual();
    }

    // ---- Geometry --------------------------------------------------------------------------

    /// <summary>
    /// Whole-pixel sizes for <paramref name="count"/> cells and the 1px line that closes each,
    /// with the remainder handed to the earliest cells.
    /// </summary>
    /// <remarks>
    /// The reference's own arithmetic: at 1352px its seven columns are 193, 192, 192, 192, 192,
    /// 192, 192 with a line after each. Dividing evenly and rounding gives a grid whose lines
    /// wander by a pixel; this does not.
    /// </remarks>
    internal static int[] Slice(double total, int count)
    {
        var content = (int)Math.Round(total) - count;
        if (content < count) content = count;
        var each = content / count;
        var spare = content - (each * count);
        var sizes = new int[count];
        for (var i = 0; i < count; i++) sizes[i] = each + (i < spare ? 1 : 0);
        return sizes;
    }

    private static double[] Offsets(int[] sizes)
    {
        var offsets = new double[sizes.Length];
        double at = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            offsets[i] = at;
            at += sizes[i] + 1;
        }

        return offsets;
    }

    // ---- Render ----------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        _entryHits.Clear();
        _dayHits.Clear();

        var width = Math.Max(0, Bounds.Width - GutterWidth);
        var height = Bounds.Height;
        if (width < 40 || height < 60) return;

        var gridLine = Palette.Colour(TokenKeys.Calendar.GridLine);

        // The grid's top border, then the weekday header, then its own lighter line.
        Fill(context, new Rect(0, 0, width, 1), gridLine);
        var headerTop = 1;
        Fill(context, new Rect(0, headerTop, width, HeaderHeight), Palette.Colour(TokenKeys.Calendar.HeaderBackground));
        Fill(context, new Rect(0, headerTop + HeaderHeight, width, 1), Palette.Colour(TokenKeys.Calendar.HeaderLine));

        var rowsTop = headerTop + HeaderHeight + 1;
        var columns = Slice(width, 7);
        var columnX = Offsets(columns);
        var rows = Slice(height - rowsTop, Weeks);
        var rowY = Offsets(rows);

        DrawHeader(context, columns, columnX, headerTop);

        // Cells, then the lines, then what sits in them. A band spanning three days is one bar
        // that crosses two column lines, so the lines have to be underneath it — drawing them
        // last cut every multi-day bar into pieces.
        for (var week = 0; week < Weeks; week++)
        {
            DrawCells(context, FirstDay.AddDays(week * 7), columns, columnX, rowsTop + rowY[week], rows[week]);
        }

        for (var week = 0; week < Weeks; week++)
        {
            Fill(context, new Rect(0, rowsTop + rowY[week] + rows[week], width, 1), gridLine);
        }

        // The vertical lines run the whole height of the grid, header included.
        for (var i = 0; i < 7; i++)
        {
            Fill(context, new Rect(columnX[i] + columns[i], 0, 1, height), gridLine);
        }

        for (var week = 0; week < Weeks; week++)
        {
            DrawWeekItems(context, week, columns, columnX, rowsTop + rowY[week], rows[week]);
        }

        _gutter = new Rect(width, 0, GutterWidth, height);
        DrawGutter(context, _gutter);
        DrawGhost(context);
    }

    /// <summary>
    /// What a drag is proposing: the chip drawn over the cell it would land in, the original
    /// staying where it is until the pointer is let go.
    /// </summary>
    /// <remarks>
    /// A month cell is one line of text tall, so the ghost is the chip moved by the distance
    /// between the two cells rather than laid out again — which is exact for a grid of cells that
    /// differ by at most the pixel <see cref="Slice"/> hands to the earliest columns.
    /// </remarks>
    private void DrawGhost(DrawingContext context)
    {
        if (_drag is not { Live: true } drag || !drag.Moved) return;
        if (CellOf(DateOnly.FromDateTime(drag.Entry.StartWall)) is not { } from) return;
        if (CellOf(DateOnly.FromDateTime(drag.Start)) is not { } to) return;

        var box = drag.Box.Translate(new Vector(to.X - from.X, to.Y - from.Y));
        var entry = drag.Entry;
        var lines = Wrap(entry.MonthLabel(Culture), box.Width - ChipTextInset - 2, 1, ChipTextSize);
        using var fade = context.PushOpacity(0.65);
        DrawChip(context, box, Palette.Chip(entry.Colour, entry.Busy), lines, selected: true, reminder: entry.HasReminder);
    }

    private Rect? CellOf(DateOnly date)
    {
        foreach (var (box, day) in _dayHits)
        {
            if (day == date) return box;
        }

        return null;
    }

    /// <summary>
    /// The box an appointment was last drawn in, or null when it is not on show.
    /// </summary>
    /// <remarks>
    /// What hit-testing already knows, said out loud, so that a harness drag can be pressed at
    /// coordinates the view really used rather than at coordinates guessed from the geometry.
    /// </remarks>
    public Rect? BoxOf(CalendarEntry entry)
    {
        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_entryHits[i].Entry, entry)) return _entryHits[i].Box;
        }

        return null;
    }

    /// <summary>The middle of a day's cell, or null when that day is not on show.</summary>
    public Point? PointAt(DateOnly day) => CellOf(day)?.Center;

    private void DrawHeader(DrawingContext context, int[] columns, double[] columnX, double top)
    {
        var ink = Palette.Colour(TokenKeys.Calendar.HeaderText);
        for (var i = 0; i < 7; i++)
        {
            var date = FirstDay.AddDays(i);
            var name = date.ToString("dddd", Culture);

            // The weekday today falls on, not the date in the first row — the header names a
            // column, and comparing it to today only matched when the grid happened to start on
            // today's own date.
            var face = date.DayOfWeek == Today.DayOfWeek ? BoldFace : Face;
            if (Measure(name, DayTextSize, face) > columns[i] - (DayInset * 2)) name = date.ToString("ddd", Culture);
            DrawAt(context, Ink(name, DayTextSize, ink, face), columnX[i] + DayInset, top + HeaderBaseline);
        }
    }

    private void DrawCells(DrawingContext context, DateOnly start, int[] columns, double[] columnX, double top, double height)
    {
        for (var i = 0; i < 7; i++)
        {
            var date = start.AddDays(i);
            var cell = new Rect(columnX[i], top, columns[i], height);
            _dayHits.Add((cell, date));

            var fill = date == Today
                ? Palette.Colour(TokenKeys.Calendar.TodayFill)
                : date < Today
                    ? Palette.Colour(TokenKeys.Calendar.PastFill)
                    : Palette.Colour(TokenKeys.Calendar.Background);
            Fill(context, cell, fill);

            if (Selected == date && date != Today)
            {
                Fill(context, cell, Palette.Colour(TokenKeys.Calendar.SelectedFill));
            }

            DrawDayNumber(context, date, start == FirstDay && i == 0, cell);
        }
    }

    private void DrawWeekItems(DrawingContext context, int week, int[] columns, double[] columnX, double top, double height)
    {
        var start = FirstDay.AddDays(week * 7);

        // The row's bands: all-day and multi-day items, packed so a bar is one bar.
        var bands = Bands(start);
        var bandHeight = ChipHeight(1);
        var bandsBottom = 0.0;
        foreach (var band in bands)
        {
            var left = columnX[band.StartColumn];
            var right = columnX[band.EndColumn] + columns[band.EndColumn] - ChipRightInset;
            var y = top + CellTopReserve + (band.Lane * (bandHeight + 1));
            if (y + bandHeight > top + height) break;

            var box = new Rect(left, y, Math.Max(0, right - left), bandHeight);
            var paint = Palette.Chip(band.Item.Colour, band.Item.Busy);
            var lines = Wrap(band.Item.MonthLabel(Culture), box.Width - ChipTextInset - 2, 1, ChipTextSize);
            DrawChip(context, box, paint, lines, ReferenceEquals(band.Item, SelectedEntry), reminder: band.Item.HasReminder);
            _entryHits.Add((box, band.Item));
            bandsBottom = Math.Max(bandsBottom, (band.Lane + 1) * (bandHeight + 1));
        }

        // Then each cell's own timed appointments, stacked under whatever bands crossed it.
        for (var i = 0; i < 7; i++)
        {
            var date = start.AddDays(i);
            var cell = new Rect(columnX[i], top, columns[i], height);
            DrawTimed(context, date, cell, bandsBottom);
        }
    }

    private void DrawDayNumber(DrawingContext context, DateOnly date, bool isFirstCell, Rect cell)
    {
        var firstOfMonth = date.Day == 1;
        var text = firstOfMonth || isFirstCell ? date.ToString("MMM d", Culture) : date.Day.ToString(Culture);
        var face = firstOfMonth || date == Today ? BoldFace : Face;
        var ink = date == Today
            ? Palette.Colour(TokenKeys.Calendar.TodayText)
            : date < Today
                ? Palette.Colour(TokenKeys.Calendar.PastText)
                : Palette.Colour(TokenKeys.Calendar.DayText);

        DrawAt(context, Ink(text, DayTextSize, ink, face), cell.X + DayInset, cell.Y + DayBaseline);
    }

    /// <summary>The week's all-day and multi-day entries, in lanes across its columns.</summary>
    private IReadOnlyList<MonthBar<CalendarEntry>> Bands(DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(6);
        var spanning = Entries
            .Where(e => e.IsMultiDay)
            .Where(e =>
            {
                var (first, last) = e.Days();
                return first <= weekEnd && last >= weekStart;
            })
            .OrderByDescending(e => e.Days().Last.DayNumber - e.Days().First.DayNumber)
            .ThenBy(e => e.StartUtc)
            .ThenBy(e => e.Summary, StringComparer.CurrentCulture)
            .ToList();

        return MonthLayout.Solve(
            spanning,
            e =>
            {
                var (first, last) = e.Days();
                return (first.DayNumber - weekStart.DayNumber, last.DayNumber - weekStart.DayNumber);
            },
            7);
    }

    private void DrawTimed(DrawingContext context, DateOnly date, Rect cell, double bandsBottom)
    {
        var timed = Entries
            .Where(e => !e.IsMultiDay && e.Days().First == date)
            .OrderBy(e => e.StartUtc)
            .ThenBy(e => e.Summary, StringComparer.CurrentCulture)
            .ToList();
        if (timed.Count == 0) return;

        var width = cell.Width - ChipRightInset;
        var textWidth = width - ChipTextInset - 2;
        var y = cell.Y + CellTopReserve + bandsBottom;
        var bottom = cell.Bottom;

        foreach (var entry in timed)
        {
            var room = bottom - y;
            if (room < ChipHeight(1))
            {
                DrawMoreMark(context, cell);
                return;
            }

            var maxLines = (int)Math.Floor((room - ChipPadding) / ChipLineHeight);
            var lines = Wrap(entry.MonthLabel(Culture), textWidth, Math.Max(1, maxLines), ChipTextSize);
            var box = new Rect(cell.X, y, width, ChipHeight(lines.Count));
            DrawChip(context, box, Palette.Chip(entry.Colour, entry.Busy), lines, ReferenceEquals(entry, SelectedEntry), reminder: entry.HasReminder);
            _entryHits.Add((box, entry));
            y = box.Bottom + 1;
        }
    }

    /// <summary>The mark a cell carries when it holds more than it can show.</summary>
    private void DrawMoreMark(DrawingContext context, Rect cell)
    {
        var ink = Palette.Brush(Palette.Colour(TokenKeys.Calendar.DayText));
        var x = cell.Right - ChipRightInset - 8;
        var y = cell.Y + 10;
        var figure = new StreamGeometry();
        using (var draw = figure.Open())
        {
            draw.BeginFigure(new Point(x, y), isFilled: true);
            draw.LineTo(new Point(x + 8, y));
            draw.LineTo(new Point(x + 4, y + 5));
            draw.EndFigure(true);
        }

        context.DrawGeometry(ink, null, figure);
    }

    /// <summary>
    /// The scroll gutter. Its thumb stands for the six weeks on show inside about a year either
    /// side of them, which is why the reference parks it in the middle of a long track whatever
    /// month is up.
    /// </summary>
    private void DrawGutter(DrawingContext context, Rect gutter)
    {
        if (gutter.Width < 4) return;
        Fill(context, gutter, Palette.Colour(TokenKeys.Nav.Background));

        var mark = Palette.Colour(TokenKeys.Border.Strong);
        Arrow(context, new Point(gutter.Center.X, gutter.Y + 8), up: true, mark);
        Arrow(context, new Point(gutter.Center.X, gutter.Bottom - 8), up: false, mark);

        const double Window = 111;
        var track = new Rect(gutter.X, gutter.Y + 16, gutter.Width, Math.Max(0, gutter.Height - 32));
        var thumbHeight = Math.Max(20, track.Height * Weeks / Window);
        var thumb = new Rect(track.X + 1, track.Y + ((track.Height - thumbHeight) / 2), track.Width - 2, thumbHeight);
        Fill(context, thumb, mark);
    }

    private void Arrow(DrawingContext context, Point centre, bool up, Color colour)
    {
        var figure = new StreamGeometry();
        using (var draw = figure.Open())
        {
            var dy = up ? -3.0 : 3.0;
            draw.BeginFigure(new Point(centre.X - 4, centre.Y - dy), isFilled: true);
            draw.LineTo(new Point(centre.X + 4, centre.Y - dy));
            draw.LineTo(new Point(centre.X, centre.Y + dy));
            draw.EndFigure(true);
        }

        context.DrawGeometry(Palette.Brush(colour), null, figure);
    }

    // ---- Input -----------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        if (_gutter.Contains(point))
        {
            Scrolled?.Invoke(this, point.Y < _gutter.Center.Y ? -1 : 1);
            e.Handled = true;
            return;
        }

        // Latest first: a chip is drawn over its cell, so the last box that contains the point
        // is the one on top.
        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            var (box, entry) = _entryHits[i];
            if (!box.Contains(point)) continue;
            SelectedEntry = entry;
            Selected = entry.Days().First;
            EntrySelected?.Invoke(this, entry);
            if (e.ClickCount >= 2) EntryActivated?.Invoke(this, entry);
            else if (ChipDrag.CanDrag(entry) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _drag = new ChipDrag
                {
                    Entry = entry,
                    // Only a bar that spans days has ends to take hold of: a single day's chip
                    // is one line of text, and every grab near its edge would be a resize.
                    Grip = entry.IsMultiDay ? ChipDrag.GripAt(box, point, horizontal: true) : DragGrip.Move,
                    Origin = point,
                    Box = box,
                    Start = entry.StartWall,
                    End = entry.EndWall,
                    AllDay = entry.AllDay,
                };
                e.Pointer.Capture(this);
            }

            e.Handled = true;
            return;
        }

        foreach (var (box, day) in _dayHits)
        {
            if (!box.Contains(point)) continue;
            SelectedEntry = null;
            Selected = day;
            DaySelected?.Invoke(this, day);
            if (e.ClickCount >= 2) DayActivated?.Invoke(this, day);
            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_drag is not { } drag)
        {
            ShowGripCursor(point);
            return;
        }

        if (!drag.Live)
        {
            var far = Math.Abs(point.X - drag.Origin.X) > ChipDrag.Threshold
                      || Math.Abs(point.Y - drag.Origin.Y) > ChipDrag.Threshold;
            if (!far) return;
            drag.Live = true;
            Cursor = new Cursor(drag.Grip == DragGrip.Move ? StandardCursorType.SizeAll : GripCursor(drag.Grip));
        }

        if (DayAt(point) is { } day) Propose(drag, day);

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
        });
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || _drag is null) return;

        _drag = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Where the drag leaves it. A month view moves things by whole days and keeps the time of
    /// day: dropping Tuesday's ten o'clock meeting on Thursday means Thursday at ten.
    /// </summary>
    private void Propose(ChipDrag drag, DateOnly day)
    {
        var entry = drag.Entry;
        var (first, last) = entry.Days();

        if (drag.Grip == DragGrip.Move)
        {
            // The grab's own offset into a multi-day bar is kept, so a bar picked up by its
            // middle does not jump forward by half its length.
            var lead = DayAt(drag.Origin) is { } held ? Math.Clamp(held.DayNumber - first.DayNumber, 0, last.DayNumber - first.DayNumber) : 0;
            var shift = day.DayNumber - lead - first.DayNumber;
            drag.Start = entry.StartWall.AddDays(shift);
            drag.End = entry.EndWall.AddDays(shift);
            return;
        }

        if (drag.Grip == DragGrip.Start)
        {
            var start = day <= last ? day : last;
            drag.Start = start.ToDateTime(TimeOnly.FromDateTime(entry.StartWall));
            drag.End = entry.EndWall;
            return;
        }

        // The end of an all-day run is the day after its last, which is what makes a one-day
        // item's end tomorrow rather than today.
        var end = day >= first ? day : first;
        drag.Start = entry.StartWall;
        drag.End = entry.AllDay
            ? end.AddDays(1).ToDateTime(TimeOnly.MinValue)
            : end.ToDateTime(TimeOnly.FromDateTime(entry.EndWall));
    }

    private DateOnly? DayAt(Point point)
    {
        foreach (var (box, day) in _dayHits)
        {
            if (box.Contains(point)) return day;
        }

        return null;
    }

    private void ShowGripCursor(Point point)
    {
        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            var (box, entry) = _entryHits[i];
            if (!box.Contains(point)) continue;
            var grip = ChipDrag.CanDrag(entry) && entry.IsMultiDay ? ChipDrag.GripAt(box, point, horizontal: true) : DragGrip.Move;
            Cursor = grip == DragGrip.Move ? Cursor.Default : new Cursor(GripCursor(grip));
            return;
        }

        Cursor = Cursor.Default;
    }

    private static StandardCursorType GripCursor(DragGrip grip)
        => grip == DragGrip.Start ? StandardCursorType.LeftSide : StandardCursorType.RightSide;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var weeks = -(int)Math.Round(e.Delta.Y);
        if (weeks == 0) weeks = e.Delta.Y < 0 ? 1 : -1;
        Scrolled?.Invoke(this, weeks);
        e.Handled = true;
    }
}
