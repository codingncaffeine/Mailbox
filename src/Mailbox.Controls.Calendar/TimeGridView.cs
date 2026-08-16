using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>Which run of days the time grid is showing.</summary>
public enum TimeGridSpan
{
    Day,
    WorkWeek,
    Week,
}

/// <summary>
/// The Day, Work Week and Week views: a time ruler down the left, an all-day band under the
/// header, and a column per day with the appointments packed into it by
/// <see cref="EventLayout"/>.
/// </summary>
/// <remarks>
/// One control for all three, because they differ only in which days they show — the reference's
/// Day view is its Week view with one column, down to the ruler's numbers and the way an
/// appointment widens into the space beside it.
/// <para>
/// <b>No reference capture exists for these three.</b> The geometry is authored from the
/// reference's shape and from the month view's own measurements, which the day views share: the
/// same 28px header band, the same chip drawing, the same lines. Where a number here is a
/// decision rather than a measurement it says so, and a capture would settle it.
/// </para>
/// </remarks>
public sealed class TimeGridView : CalendarSurface
{
    /// <summary>The ruler's width. Authored: wide enough for "12" at 20px and "00" beside it.</summary>
    private const double RulerWidth = 62;

    /// <summary>The header band over the day columns, the month view's own height.</summary>
    private const double HeaderHeight = 28;

    private const double HeaderBaseline = 20;
    private const double HeaderTextSize = 15;

    /// <summary>The hour numeral, and the minutes drawn small beside it.</summary>
    private const double HourTextSize = 20;
    private const double MinuteTextSize = 11;

    /// <summary>The scroll gutter down the right-hand edge, as the month view has.</summary>
    private const double GutterWidth = 17;

    private readonly List<(Rect Box, CalendarEntry Entry)> _entryHits = [];
    private readonly List<(Rect Box, DateOnly Day, TimeOnly? At)> _slotHits = [];
    private Rect _gutter;

    public TimeGridView()
    {
        Focusable = true;
    }

    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);
    private TimeGridSpan _span = TimeGridSpan.Week;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private DateTime? _now;
    private IReadOnlyList<CalendarEntry> _entries = [];
    private CalendarEntry? _selectedEntry;
    private DateOnly? _selected;
    private double _scrollMinutes = 8 * 60;
    private int _slotMinutes = 30;
    private double _slotHeight = 21;
    private TimeOnly _workStart = new(8, 0);
    private TimeOnly _workEnd = new(17, 0);
    private DayOfWeek _firstDayOfWeek = DayOfWeek.Sunday;
    private IReadOnlySet<DayOfWeek> _workDays = new HashSet<DayOfWeek>
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
    };

    /// <summary>A day inside the run to show; the run itself follows from <see cref="Span"/>.</summary>
    public DateOnly Anchor
    {
        get => _anchor;
        set => Set(ref _anchor, value);
    }

    public TimeGridSpan Span
    {
        get => _span;
        set => Set(ref _span, value);
    }

    public DateOnly Today
    {
        get => _today;
        set => Set(ref _today, value);
    }

    /// <summary>The moment the now line is drawn at, or null to leave it off.</summary>
    public DateTime? Now
    {
        get => _now;
        set => Set(ref _now, value);
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

    public DateOnly? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Minutes past midnight at the top of the grid.</summary>
    public double ScrollMinutes
    {
        get => _scrollMinutes;
        set => Set(ref _scrollMinutes, Math.Clamp(value, 0, (24 * 60) - _slotMinutes));
    }

    /// <summary>The time scale: 5, 6, 10, 15, 30 or 60 minutes, as the reference offers.</summary>
    public int SlotMinutes
    {
        get => _slotMinutes;
        set => Set(ref _slotMinutes, Math.Clamp(value, 5, 60));
    }

    public double SlotHeight
    {
        get => _slotHeight;
        set => Set(ref _slotHeight, Math.Clamp(value, 10, 60));
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

    public DayOfWeek FirstDayOfWeek
    {
        get => _firstDayOfWeek;
        set => Set(ref _firstDayOfWeek, value);
    }

    public IReadOnlySet<DayOfWeek> WorkDays
    {
        get => _workDays;
        set
        {
            _workDays = value ?? new HashSet<DayOfWeek>();
            InvalidateVisual();
        }
    }

    public event EventHandler<DateOnly>? DaySelected;
    public event EventHandler<CalendarEntry>? EntrySelected;
    public event EventHandler<CalendarEntry>? EntryActivated;

    /// <summary>A double click on empty time: the day and the moment it landed on.</summary>
    public event EventHandler<(DateOnly Day, TimeOnly At)>? SlotActivated;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        InvalidateVisual();
    }

    /// <summary>The days on show, in order.</summary>
    public IReadOnlyList<DateOnly> Days()
    {
        switch (Span)
        {
            case TimeGridSpan.Day:
                return [Anchor];
            case TimeGridSpan.WorkWeek:
            {
                var week = WeekStart(Anchor);
                var days = new List<DateOnly>();
                for (var i = 0; i < 7; i++)
                {
                    var day = week.AddDays(i);
                    if (WorkDays.Contains(day.DayOfWeek)) days.Add(day);
                }

                return days.Count > 0 ? days : [Anchor];
            }

            default:
            {
                var week = WeekStart(Anchor);
                return Enumerable.Range(0, 7).Select(week.AddDays).ToList();
            }
        }
    }

    private DateOnly WeekStart(DateOnly date)
    {
        var lead = (((int)date.DayOfWeek - (int)FirstDayOfWeek) + 7) % 7;
        return date.AddDays(-lead);
    }

    // ---- Render ----------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        _entryHits.Clear();
        _slotHits.Clear();

        var width = Math.Max(0, Bounds.Width - GutterWidth);
        if (width < RulerWidth + 40 || Bounds.Height < 80) return;

        var days = Days();
        var gridLine = Palette.Colour(TokenKeys.Calendar.GridLine);
        var columns = MonthView.Slice(width - RulerWidth, days.Count);
        var columnX = new double[days.Count];
        var at = RulerWidth;
        for (var i = 0; i < days.Count; i++)
        {
            columnX[i] = at;
            at += columns[i] + 1;
        }

        // Header, all-day band, then the timed grid under both.
        Fill(context, new Rect(0, 0, width, 1), gridLine);
        Fill(context, new Rect(0, 1, width, HeaderHeight), Palette.Colour(TokenKeys.Calendar.HeaderBackground));
        DrawHeader(context, days, columns, columnX);
        Fill(context, new Rect(0, 1 + HeaderHeight, width, 1), Palette.Colour(TokenKeys.Calendar.HeaderLine));

        var bandTop = 2 + HeaderHeight;
        var bandHeight = DrawAllDayBand(context, days, columns, columnX, bandTop, width);
        Fill(context, new Rect(0, bandTop + bandHeight, width, 1), gridLine);

        var gridTop = bandTop + bandHeight + 1;
        DrawGrid(context, days, columns, columnX, gridTop, width, gridLine);

        _gutter = new Rect(width, 0, GutterWidth, Bounds.Height);
        DrawGutter(context, _gutter, gridTop);
    }

    private void DrawHeader(DrawingContext context, IReadOnlyList<DateOnly> days, int[] columns, double[] columnX)
    {
        var ink = Palette.Colour(TokenKeys.Calendar.HeaderText);
        for (var i = 0; i < days.Count; i++)
        {
            var date = days[i];
            var face = date == Today ? BoldFace : Face;
            // Longest label that fits, as the reference shortens a narrow column's heading.
            string[] candidates =
            [
                date.ToString("dddd, MMMM d", Culture),
                date.ToString("dddd d", Culture),
                date.ToString("ddd d", Culture),
                date.Day.ToString(Culture),
            ];
            var label = candidates.FirstOrDefault(c => Measure(c, HeaderTextSize, face) <= columns[i] - 8) ?? candidates[^1];
            var text = Ink(label, HeaderTextSize, ink, face);
            DrawAt(context, text, columnX[i] + Math.Max(2, (columns[i] - text.Width) / 2), 1 + HeaderBaseline);
        }
    }

    /// <summary>
    /// The band above the ruler holding the day's all-day and multi-day items, one lane per
    /// overlapping run, growing as it needs to and never shorter than one lane.
    /// </summary>
    private double DrawAllDayBand(DrawingContext context, IReadOnlyList<DateOnly> days, int[] columns, double[] columnX, double top, double width)
    {
        var laneHeight = ChipHeight(1);
        var first = days[0];
        var last = days[^1];

        var spanning = Entries
            .Where(e => e.IsMultiDay)
            .Where(e =>
            {
                var (s, l) = e.Days();
                return s <= last && l >= first;
            })
            .OrderByDescending(e => e.Days().Last.DayNumber - e.Days().First.DayNumber)
            .ThenBy(e => e.StartUtc)
            .ToList();

        var bars = MonthLayout.Solve(
            spanning,
            e =>
            {
                var (s, l) = e.Days();
                return (s.DayNumber - first.DayNumber, l.DayNumber - first.DayNumber);
            },
            days.Count);

        var lanes = bars.Count == 0 ? 1 : bars.Max(b => b.Lane) + 1;
        var height = Math.Max(laneHeight + 4, (lanes * (laneHeight + 1)) + 3);

        Fill(context, new Rect(0, top, width, height), Palette.Colour(TokenKeys.Calendar.AllDayBandBackground));
        Fill(context, new Rect(RulerWidth, top, 1, height), Palette.Colour(TokenKeys.Calendar.GridLine));

        foreach (var bar in bars)
        {
            var left = columnX[bar.StartColumn];
            var right = columnX[bar.EndColumn] + columns[bar.EndColumn];
            var box = new Rect(left + 1, top + 2 + (bar.Lane * (laneHeight + 1)), Math.Max(0, right - left - 2), laneHeight);
            var lines = Wrap(bar.Item.Summary, box.Width - ChipTextInset - 2, 1, ChipTextSize);
            DrawChip(context, box, Palette.Chip(bar.Item.Colour, bar.Item.Busy), lines, ReferenceEquals(bar.Item, SelectedEntry));
            _entryHits.Add((box, bar.Item));
        }

        return height;
    }

    private void DrawGrid(DrawingContext context, IReadOnlyList<DateOnly> days, int[] columns, double[] columnX, double top, double width, Color gridLine)
    {
        var height = Bounds.Height - top;
        if (height <= 0) return;

        // A grid taller than what is left of the day would draw empty rows past midnight, so the
        // scroll stops where the last row is the day's last row. A grid shorter than the day
        // scrolls freely.
        var visibleMinutes = height / SlotHeight * SlotMinutes;
        _scrollMinutes = Math.Clamp(_scrollMinutes, 0, Math.Max(0, (24 * 60) - visibleMinutes));

        using var clip = context.PushClip(new Rect(0, top, width, height));
        var minorLine = Palette.Colour(TokenKeys.Calendar.HeaderLine);
        var slots = (int)Math.Ceiling(height / SlotHeight) + 1;
        var firstSlot = (int)Math.Floor(ScrollMinutes / SlotMinutes);

        // The columns' backgrounds first: working hours light, the rest shaded, exactly as the
        // reference separates the working day from the night either side of it.
        for (var c = 0; c < days.Count; c++)
        {
            var date = days[c];
            var working = WorkDays.Contains(date.DayOfWeek);
            for (var s = 0; s < slots; s++)
                {
                var minutes = (firstSlot + s) * SlotMinutes;
                if (minutes >= 24 * 60) break;
                var y = top + (((minutes - ScrollMinutes) / SlotMinutes) * SlotHeight);
                var slotTime = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes));
                var inHours = working && slotTime >= WorkDayStart && slotTime < WorkDayEnd;
                var box = new Rect(columnX[c], y, columns[c], SlotHeight);
                Fill(context, box, Palette.Colour(inHours ? TokenKeys.Calendar.WorkingHoursFill : TokenKeys.Calendar.NonWorkingFill));
                _slotHits.Add((box, date, slotTime));
            }
        }

        // Then the lines: the hour's own across the whole width, the sub-hour ones lighter.
        for (var s = 0; s < slots; s++)
        {
            var minutes = (firstSlot + s) * SlotMinutes;
            if (minutes > 24 * 60) break;
            var y = Math.Round(top + (((minutes - ScrollMinutes) / SlotMinutes) * SlotHeight));
            Fill(context, new Rect(RulerWidth, y, width - RulerWidth, 1), minutes % 60 == 0 ? gridLine : minorLine);
        }

        for (var c = 0; c < days.Count; c++)
        {
            Fill(context, new Rect(columnX[c] + columns[c], top, 1, height), gridLine);
        }

        DrawRuler(context, top, height, firstSlot, slots, gridLine);
        Fill(context, new Rect(RulerWidth, top, 1, height), gridLine);

        for (var c = 0; c < days.Count; c++)
        {
            DrawDayEntries(context, days[c], new Rect(columnX[c], top, columns[c], height));
        }

        DrawNowLine(context, days, columns, columnX, top, height);
    }

    /// <summary>
    /// The time ruler: the hour as a numeral with its minutes small beside it, which is how the
    /// reference draws it — one label per hour however fine the time scale is.
    /// </summary>
    private void DrawRuler(DrawingContext context, double top, double height, int firstSlot, int slots, Color gridLine)
    {
        Fill(context, new Rect(0, top, RulerWidth, height), Palette.Colour(TokenKeys.Calendar.HeaderBackground));

        var ink = Palette.Colour(TokenKeys.Calendar.HourText);
        for (var s = 0; s <= slots; s++)
        {
            var minutes = (firstSlot + s) * SlotMinutes;
            if (minutes % 60 != 0 || minutes >= 24 * 60) continue;
            var y = top + (((minutes - ScrollMinutes) / SlotMinutes) * SlotHeight);
            var when = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes));
            // "%h" rather than "h": a lone custom specifier reads as a standard format, and "h"
            // is not one — the ruler threw rather than drawing.
            var hour = Ink(when.ToString("%h", Culture), HourTextSize, ink);
            var rest = Ink(when.ToString("mm", Culture), MinuteTextSize, ink);
            DrawAt(context, hour, RulerWidth - 26 - hour.Width, y + HourTextSize);
            DrawAt(context, rest, RulerWidth - 22, y + MinuteTextSize + 2);

            Fill(context, new Rect(RulerWidth - 8, Math.Round(y), 8, 1), gridLine);
        }
    }

    private void DrawDayEntries(DrawingContext context, DateOnly date, Rect column)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var timed = Entries
            .Where(e => !e.IsMultiDay && e.Days().First == date)
            .ToList();
        if (timed.Count == 0) return;

        var boxes = EventLayout.Solve(
            timed,
            e => new DateTimeOffset(e.StartWall, TimeSpan.Zero),
            e => new DateTimeOffset(e.EndWall, TimeSpan.Zero));

        foreach (var placed in boxes)
        {
            var startMinutes = (placed.Start.DateTime - dayStart).TotalMinutes;
            var endMinutes = (placed.End.DateTime - dayStart).TotalMinutes;
            var y = column.Y + (((startMinutes - ScrollMinutes) / SlotMinutes) * SlotHeight);
            var bottom = column.Y + (((endMinutes - ScrollMinutes) / SlotMinutes) * SlotHeight);
            if (bottom < column.Y || y > column.Bottom) continue;

            var slice = column.Width / Math.Max(1, placed.Columns);
            var box = new Rect(
                column.X + (placed.Column * slice) + 1,
                y,
                Math.Max(12, (slice * placed.Span) - 2),
                Math.Max(ChipHeight(1), bottom - y - 1));

            var entry = placed.Item;
            var maxLines = (int)Math.Floor((box.Height - ChipPadding) / ChipLineHeight);
            var lines = new List<string>(Wrap(entry.Summary, box.Width - ChipTextInset - 2, Math.Max(1, maxLines), ChipTextSize, SemiBoldFace));
            if (lines.Count < maxLines && entry.Location.Length > 0)
            {
                lines.AddRange(Wrap(entry.Location, box.Width - ChipTextInset - 2, maxLines - lines.Count, ChipTextSize));
            }

            DrawChip(context, box, Palette.Chip(entry.Colour, entry.Busy), lines, ReferenceEquals(entry, SelectedEntry), boldFirstLine: true);
            _entryHits.Add((box, entry));
        }
    }

    private void DrawNowLine(DrawingContext context, IReadOnlyList<DateOnly> days, int[] columns, double[] columnX, double top, double height)
    {
        if (Now is not { } now) return;
        var date = DateOnly.FromDateTime(now);
        var index = -1;
        for (var i = 0; i < days.Count; i++)
        {
            if (days[i] == date) index = i;
        }

        if (index < 0) return;

        var minutes = now.TimeOfDay.TotalMinutes;
        var y = Math.Round(top + (((minutes - ScrollMinutes) / SlotMinutes) * SlotHeight));
        if (y < top || y > top + height) return;

        var colour = Palette.Colour(TokenKeys.Calendar.CurrentTimeIndicator);
        Fill(context, new Rect(columnX[index], y, columns[index], 2), colour);

        var figure = new StreamGeometry();
        using (var draw = figure.Open())
        {
            draw.BeginFigure(new Point(RulerWidth - 7, y - 4), isFilled: true);
            draw.LineTo(new Point(RulerWidth - 1, y + 1));
            draw.LineTo(new Point(RulerWidth - 7, y + 6));
            draw.EndFigure(true);
        }

        context.DrawGeometry(Palette.Brush(colour), null, figure);
    }

    private void DrawGutter(DrawingContext context, Rect gutter, double gridTop)
    {
        if (gutter.Width < 4) return;
        Fill(context, gutter, Palette.Colour(TokenKeys.Nav.Background));

        var mark = Palette.Colour(TokenKeys.Border.Strong);
        var track = new Rect(gutter.X, gridTop, gutter.Width, Math.Max(0, gutter.Bottom - gridTop));
        if (track.Height < 24) return;

        var visible = track.Height / SlotHeight * SlotMinutes;
        var thumbHeight = Math.Max(20, track.Height * Math.Min(1, visible / (24 * 60)));
        var offset = (track.Height - thumbHeight) * (ScrollMinutes / Math.Max(1, (24 * 60) - visible));
        Fill(context, new Rect(track.X + 1, track.Y + Math.Clamp(offset, 0, track.Height - thumbHeight), track.Width - 2, thumbHeight), mark);
    }

    // ---- Input -----------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            if (!_entryHits[i].Box.Contains(point)) continue;
            var entry = _entryHits[i].Entry;
            SelectedEntry = entry;
            EntrySelected?.Invoke(this, entry);
            if (e.ClickCount >= 2) EntryActivated?.Invoke(this, entry);
            e.Handled = true;
            return;
        }

        foreach (var (box, day, when) in _slotHits)
        {
            if (!box.Contains(point)) continue;
            SelectedEntry = null;
            Selected = day;
            DaySelected?.Invoke(this, day);
            if (e.ClickCount >= 2 && when is { } at) SlotActivated?.Invoke(this, (day, at));
            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ScrollMinutes -= e.Delta.Y * SlotMinutes * 3;
        e.Handled = true;
    }
}
