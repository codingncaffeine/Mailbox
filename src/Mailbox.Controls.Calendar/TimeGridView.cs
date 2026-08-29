using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Core.Settings;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>Which run of days the time grid is showing.</summary>
public enum TimeGridSpan
{
    Day,
    WorkWeek,
    Week,

    /// <summary>Seven days from the anchor itself — Next 7 Days, which no week-snap answers.</summary>
    Rolling,
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

    /// <summary>The zone names over the rulers, small enough to sit inside the header band.</summary>
    private const double ZoneLabelTextSize = 11;

    /// <summary>The header band over the day columns, the month view's own height.</summary>
    private const double HeaderHeight = 28;

    private const double HeaderBaseline = 20;
    private const double HeaderTextSize = 15;

    /// <summary>The hour numeral, and the minutes drawn small beside it.</summary>
    private const double HourTextSize = 20;
    private const double MinuteTextSize = 11;

    /// <summary>The scroll gutter down the right-hand edge, as the month view has.</summary>
    private const double GutterWidth = 17;

    private readonly List<(Rect Box, CalendarEntry Entry, bool Banded)> _entryHits = [];
    private readonly List<(Rect Box, DateOnly Day, TimeOnly? At)> _slotHits = [];
    private Rect _gutter;

    // What the last render laid out, which is what a drag reads to turn a point into a day and
    // a time. Kept rather than recomputed so the two cannot disagree by a pixel.
    private IReadOnlyList<DateOnly> _laidOut = [];
    private int[] _columnWidths = [];
    private double[] _columnX = [];
    private double _bandTop;
    private double _bandBottom;
    private double _gridTop;
    private ChipDrag? _drag;

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
    private TimeZoneInfo _viewZone = TimeZoneInfo.Local;
    private TimeZoneInfo? _secondZone;
    private string _zoneLabel = string.Empty;
    private string _secondZoneLabel = string.Empty;
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

    /// <summary>The clock the grid is drawn on — the machine's own unless a test says otherwise.</summary>
    public TimeZoneInfo ViewZone
    {
        get => _viewZone;
        set => Set(ref _viewZone, value ?? TimeZoneInfo.Local);
    }

    /// <summary>
    /// A second clock, drawn in a column of its own to the left of the first, or null for none.
    /// </summary>
    public TimeZoneInfo? SecondZone
    {
        get => _secondZone;
        set => Set(ref _secondZone, value);
    }

    /// <summary>What the two columns are headed; empty takes the zone's offset instead.</summary>
    public string ZoneLabel
    {
        get => _zoneLabel;
        set => Set(ref _zoneLabel, value ?? string.Empty);
    }

    public string SecondZoneLabel
    {
        get => _secondZoneLabel;
        set => Set(ref _secondZoneLabel, value ?? string.Empty);
    }

    /// <summary>How much of the left-hand edge the hours take: two columns when a second zone shows.</summary>
    private double RulerSpan => RulerSpanFor(SecondZone is not null);

    /// <summary>
    /// How wide the time ruler is, with or without a second zone beside it.
    /// </summary>
    /// <remarks>
    /// Public because the Daily Task List draws its columns under this grid's, and a band whose
    /// first column starts a ruler-width away from the grid's would put every task under the
    /// wrong day. One number, read by both.
    /// </remarks>
    public static double RulerSpanFor(bool secondZone) => secondZone ? RulerWidth * 2 : RulerWidth;

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

            case TimeGridSpan.Rolling:
                return Enumerable.Range(0, 7).Select(Anchor.AddDays).ToList();

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
        if (width < RulerSpan + 40 || Bounds.Height < 80) return;

        var days = Days();
        var gridLine = Palette.Colour(TokenKeys.Calendar.GridLine);
        var columns = MonthView.Slice(width - RulerSpan, days.Count);
        var columnX = new double[days.Count];
        var at = RulerSpan;
        for (var i = 0; i < days.Count; i++)
        {
            columnX[i] = at;
            at += columns[i] + 1;
        }

        // Header, all-day band, then the timed grid under both.
        Fill(context, new Rect(0, 0, width, 1), gridLine);
        Fill(context, new Rect(0, 1, width, HeaderHeight), Palette.Colour(TokenKeys.Calendar.HeaderBackground));
        DrawHeader(context, days, columns, columnX);
        DrawZoneLabels(context);
        Fill(context, new Rect(0, 1 + HeaderHeight, width, 1), Palette.Colour(TokenKeys.Calendar.HeaderLine));

        var bandTop = 2 + HeaderHeight;
        var bandHeight = DrawAllDayBand(context, days, columns, columnX, bandTop, width);
        Fill(context, new Rect(0, bandTop + bandHeight, width, 1), gridLine);

        var gridTop = bandTop + bandHeight + 1;
        _laidOut = days;
        _columnWidths = columns;
        _columnX = columnX;
        _bandTop = bandTop;
        _bandBottom = bandTop + bandHeight;
        _gridTop = gridTop;

        DrawGrid(context, days, columns, columnX, gridTop, width, gridLine);

        _gutter = new Rect(width, 0, GutterWidth, Bounds.Height);
        DrawGutter(context, _gutter, gridTop);
        DrawGhost(context, width);
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
        Fill(context, new Rect(RulerSpan, top, 1, height), Palette.Colour(TokenKeys.Calendar.GridLine));

        foreach (var bar in bars)
        {
            var left = columnX[bar.StartColumn];
            var right = columnX[bar.EndColumn] + columns[bar.EndColumn];
            var box = new Rect(left + 1, top + 2 + (bar.Lane * (laneHeight + 1)), Math.Max(0, right - left - 2), laneHeight);
            var lines = Wrap(bar.Item.Summary, box.Width - ChipTextInset - 2, 1, ChipTextSize);
            DrawChip(context, box, Palette.Chip(bar.Item.Colour, bar.Item.Busy), lines, ReferenceEquals(bar.Item, SelectedEntry), reminder: bar.Item.HasReminder);
            _entryHits.Add((box, bar.Item, true));
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
            Fill(context, new Rect(RulerSpan, y, width - RulerSpan, 1), minutes % 60 == 0 ? gridLine : minorLine);
        }

        for (var c = 0; c < days.Count; c++)
        {
            Fill(context, new Rect(columnX[c] + columns[c], top, 1, height), gridLine);
        }

        DrawRuler(context, top, height, firstSlot, slots, gridLine);
        Fill(context, new Rect(RulerSpan, top, 1, height), gridLine);

        for (var c = 0; c < days.Count; c++)
        {
            DrawDayEntries(context, days[c], new Rect(columnX[c], top, columns[c], height));
        }

        DrawNowLine(context, days, columns, columnX, top, height);
    }

    /// <summary>
    /// The time ruler: the hour as a numeral with its minutes small beside it, which is how the
    /// reference draws it — one label per hour however fine the time scale is. A second zone
    /// puts a column of its own to the left, as the reference does.
    /// </summary>
    private void DrawRuler(DrawingContext context, double top, double height, int firstSlot, int slots, Color gridLine)
    {
        Fill(context, new Rect(0, top, RulerSpan, height), Palette.Colour(TokenKeys.Calendar.HeaderBackground));

        DrawHours(context, RulerSpan, top, height, firstSlot, slots, gridLine, zone: null);
        if (SecondZone is not { } second) return;

        DrawHours(context, RulerWidth, top, height, firstSlot, slots, gridLine, second);

        // The line between the two columns, so they read as two clocks rather than one wide one.
        Fill(context, new Rect(RulerWidth, top, 1, height), Palette.Colour(TokenKeys.Calendar.HeaderLine));
    }

    /// <summary>
    /// One column of hours, right-aligned on <paramref name="right"/>.
    /// </summary>
    /// <param name="zone">
    /// The clock to write, or null for the view's own. A second zone's hours are read at each
    /// row's own instant rather than at a fixed offset: half the world moves its clocks twice a
    /// year, and the two columns are a different distance apart on either side of the day it
    /// happens.
    /// </param>
    private void DrawHours(DrawingContext context, double right, double top, double height, int firstSlot, int slots, Color gridLine, TimeZoneInfo? zone)
    {
        var ink = Palette.Colour(TokenKeys.Calendar.HourText);
        var day = Days() is { Count: > 0 } days ? days[0] : Anchor;

        for (var s = 0; s <= slots; s++)
        {
            var minutes = (firstSlot + s) * SlotMinutes;
            if (minutes % 60 != 0 || minutes >= 24 * 60) continue;
            var y = top + (((minutes - ScrollMinutes) / SlotMinutes) * SlotHeight);
            var when = HourAt(minutes, zone);

            // "%h" rather than "h": a lone custom specifier reads as a standard format, and "h"
            // is not one — the ruler threw rather than drawing.
            var hour = Ink(when.ToString("%h", Culture), HourTextSize, ink);
            var rest = Ink(when.ToString("mm", Culture), MinuteTextSize, ink);
            DrawAt(context, hour, right - 26 - hour.Width, y + HourTextSize);
            DrawAt(context, rest, right - 22, y + MinuteTextSize + 2);

            Fill(context, new Rect(right - 8, Math.Round(y), 8, 1), gridLine);
        }
    }

    /// <summary>
    /// What one row of the hour ruler reads: the view's own clock for <paramref name="zone"/>
    /// null, and the second column's clock otherwise.
    /// </summary>
    /// <remarks>
    /// Public because it is the one claim the ruler makes that a picture cannot settle. On the
    /// two days a year a zone moves its clocks the two columns are a different distance apart,
    /// and two numbers in a screenshot prove neither of them right; drawing and reading back call
    /// the same method so a read-back cannot agree with a paint that is wrong.
    /// </remarks>
    public TimeOnly HourAt(int minutesPastMidnight, TimeZoneInfo? zone)
    {
        var day = Days() is { Count: > 0 } days ? days[0] : Anchor;
        var when = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutesPastMidnight));
        return zone is null ? when : TimeOnly.FromDateTime(Elsewhere(day, when, zone));
    }

    /// <summary>What another zone's clock reads at a moment on this one.</summary>
    private DateTime Elsewhere(DateOnly day, TimeOnly at, TimeZoneInfo zone)
    {
        var here = day.ToDateTime(at, DateTimeKind.Unspecified);
        var instant = new DateTimeOffset(here, ViewZone.GetUtcOffset(here)).ToUniversalTime();
        return TimeZoneInfo.ConvertTime(instant, zone).DateTime;
    }

    /// <summary>
    /// The zone labels over the two columns of hours: what each is called, or its offset when it
    /// is called nothing.
    /// </summary>
    private void DrawZoneLabels(DrawingContext context)
    {
        if (SecondZone is null && ZoneLabel.Length == 0) return;

        var ink = Palette.Colour(TokenKeys.Calendar.HeaderText);
        var day = Days() is { Count: > 0 } days ? days[0] : Anchor;
        var noon = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), ViewZone.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));

        Label(RulerSpan, ZoneLabel.Length > 0 ? ZoneLabel : TimeZoneChoices.ShortLabel(ViewZone, noon));
        if (SecondZone is { } second)
        {
            Label(RulerWidth, SecondZoneLabel.Length > 0 ? SecondZoneLabel : TimeZoneChoices.ShortLabel(second, noon));
        }

        void Label(double right, string text)
        {
            var run = Ink(text, ZoneLabelTextSize, ink);
            var left = Math.Max(2, right - 6 - run.Width);
            using var clip = context.PushClip(new Rect(right - RulerWidth, 1, RulerWidth, HeaderHeight));
            DrawAt(context, run, left, 1 + HeaderBaseline - 2);
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

            DrawChip(context, box, Palette.Chip(entry.Colour, entry.Busy), lines, ReferenceEquals(entry, SelectedEntry), boldFirstLine: true, reminder: entry.HasReminder);
            _entryHits.Add((box, entry, false));
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
            draw.BeginFigure(new Point(RulerSpan - 7, y - 4), isFilled: true);
            draw.LineTo(new Point(RulerSpan - 1, y + 1));
            draw.LineTo(new Point(RulerSpan - 7, y + 6));
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

    /// <summary>
    /// The chip a drag is proposing, drawn where it would land while the original stays where it
    /// is — so the two can be compared, which is the whole point of showing it.
    /// </summary>
    private void DrawGhost(DrawingContext context, double width)
    {
        if (_drag is not { Live: true } drag || !drag.Moved) return;
        if (GhostBox(drag) is not { } box) return;

        var entry = drag.Entry;
        var lines = Wrap(entry.Summary, box.Width - ChipTextInset - 2, Math.Max(1, (int)((box.Height - ChipPadding) / ChipLineHeight)), ChipTextSize, SemiBoldFace);
        using var clip = context.PushClip(new Rect(0, _bandTop, width, Bounds.Height - _bandTop));
        using var fade = context.PushOpacity(0.65);
        DrawChip(context, box, Palette.Chip(entry.Colour, entry.Busy), lines, selected: true, boldFirstLine: true, reminder: entry.HasReminder);
    }

    /// <summary>Where the ghost goes: the band for an all-day proposal, the grid for a timed one.</summary>
    private Rect? GhostBox(ChipDrag drag)
    {
        var first = ColumnOf(DateOnly.FromDateTime(drag.Start));
        if (first < 0) return null;

        if (drag.AllDay)
        {
            var lastDay = DateOnly.FromDateTime(drag.End.AddTicks(-1));
            var last = Math.Clamp(ColumnOf(lastDay) is var found and >= 0 ? found : _laidOut.Count - 1, first, _laidOut.Count - 1);
            var left = _columnX[first];
            var right = _columnX[last] + _columnWidths[last];
            return new Rect(left + 1, _bandTop + 2, Math.Max(12, right - left - 2), ChipHeight(1));
        }

        var dayStart = drag.Start.Date;
        var top = _gridTop + (((drag.Start - dayStart).TotalMinutes - ScrollMinutes) / SlotMinutes * SlotHeight);
        var bottom = _gridTop + (((drag.End - dayStart).TotalMinutes - ScrollMinutes) / SlotMinutes * SlotHeight);
        return new Rect(
            _columnX[first] + 1,
            top,
            Math.Max(12, _columnWidths[first] - 2),
            Math.Max(ChipHeight(1), bottom - top - 1));
    }

    // ---- Where things were drawn ---------------------------------------------------------------

    /// <summary>
    /// The box an appointment was last drawn in, or null when it is not on show.
    /// </summary>
    /// <remarks>
    /// What hit-testing already knows, said out loud: the harness cannot click, so a drag is
    /// pressed at real coordinates read from here rather than at coordinates guessed from the
    /// geometry — a pose that guessed would prove the guess and not the view.
    /// </remarks>
    public Rect? BoxOf(CalendarEntry entry)
    {
        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_entryHits[i].Entry, entry)) return _entryHits[i].Box;
        }

        return null;
    }

    /// <summary>The point a day and a time of day sit at, or null when that day is not on show.</summary>
    public Point? PointAt(DateOnly day, TimeSpan at, bool allDay = false)
    {
        var column = ColumnOf(day);
        if (column < 0) return null;

        var x = _columnX[column] + (_columnWidths[column] / 2);
        return allDay
            ? new Point(x, _bandTop + ((_bandBottom - _bandTop) / 2))
            : new Point(x, _gridTop + ((at.TotalMinutes - ScrollMinutes) / SlotMinutes * SlotHeight));
    }

    // ---- Input -----------------------------------------------------------------------------

    /// <summary>The column a date is in, or -1 when it is not on show.</summary>
    private int ColumnOf(DateOnly date)
    {
        for (var i = 0; i < _laidOut.Count; i++)
        {
            if (_laidOut[i] == date) return i;
        }

        return -1;
    }

    /// <summary>The column a point is over, clamped to the ones there are.</summary>
    private int ColumnAt(double x)
    {
        if (_laidOut.Count == 0) return -1;
        for (var i = 0; i < _laidOut.Count; i++)
        {
            if (x < _columnX[i] + _columnWidths[i]) return i;
        }

        return _laidOut.Count - 1;
    }

    /// <summary>The moment a height in the grid stands for, snapped to the time scale.</summary>
    private TimeSpan TimeAt(double y)
    {
        var minutes = ScrollMinutes + ((y - _gridTop) / SlotHeight * SlotMinutes);
        var snapped = Math.Round(minutes / SlotMinutes) * SlotMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(snapped, 0, (24 * 60) - SlotMinutes));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            var (box, entry, banded) = _entryHits[i];
            if (!box.Contains(point)) continue;
            SelectedEntry = entry;
            EntrySelected?.Invoke(this, entry);
            if (e.ClickCount >= 2) EntryActivated?.Invoke(this, entry);
            else if (ChipDrag.CanDrag(entry) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // Held, not yet dragged: the same press still selects, and only the pointer
                // leaving the threshold turns it into a move.
                _drag = new ChipDrag
                {
                    Entry = entry,
                    Grip = ChipDrag.GripAt(box, point, horizontal: banded),
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
            Cursor = new Cursor(drag.Grip == DragGrip.Move ? StandardCursorType.SizeAll : GripCursor(drag.Grip, drag.Entry.AllDay));
        }

        Propose(drag, point);
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

        // A drag let go of: nothing is written and the chip is where it was.
        _drag = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Where the drag now proposes to leave the appointment. A move keeps its length and the
    /// grab's own offset inside it; an edge moves only that end, and never past the other.
    /// </summary>
    /// <remarks>
    /// Read off the pointer each time rather than accumulated, so a drag that wanders and comes
    /// back proposes exactly what it did before — an accumulated delta drifts by whatever the
    /// snapping rounded away on each step.
    /// </remarks>
    private void Propose(ChipDrag drag, Point point)
    {
        var column = ColumnAt(point.X);
        if (column < 0) return;

        var day = _laidOut[column];
        var entry = drag.Entry;
        var least = TimeSpan.FromMinutes(SlotMinutes);
        var overBand = point.Y >= _bandTop && point.Y <= _bandBottom;

        if (drag.Grip == DragGrip.Move)
        {
            // Whole days in the band, and an all-day item stays one wherever it is let go: the
            // band is where the reference keeps them, and dropping one on a Wednesday means
            // Wednesday, not Wednesday at whatever hour the pointer was over.
            if (overBand || entry.AllDay)
            {
                var origin = DateOnly.FromDateTime(entry.StartWall);
                var grabbed = ColumnAt(drag.Origin.X);
                var lead = entry.AllDay && grabbed >= 0 ? Math.Max(0, _laidOut[grabbed].DayNumber - origin.DayNumber) : 0;
                var first = day.AddDays(-lead);
                var length = entry.AllDay
                    ? Math.Max(1, DateOnly.FromDateTime(entry.EndWall).DayNumber - origin.DayNumber)
                    : 1;

                drag.AllDay = true;
                drag.Start = first.ToDateTime(TimeOnly.MinValue);
                drag.End = first.AddDays(length).ToDateTime(TimeOnly.MinValue);
                return;
            }

            var at = TimeAt(point.Y - (drag.Origin.Y - drag.Box.Y));
            drag.AllDay = false;
            drag.Start = day.ToDateTime(TimeOnly.MinValue) + at;
            drag.End = drag.Start + (entry.EndWall - entry.StartWall);
            return;
        }

        if (entry.AllDay)
        {
            // An all-day run's edges move by whole days, and the run never turns inside out.
            var first = DateOnly.FromDateTime(entry.StartWall);
            var last = DateOnly.FromDateTime(entry.EndWall.AddTicks(-1));
            if (drag.Grip == DragGrip.Start) first = day <= last ? day : last;
            else last = day >= first ? day : first;

            drag.Start = first.ToDateTime(TimeOnly.MinValue);
            drag.End = last.AddDays(1).ToDateTime(TimeOnly.MinValue);
            return;
        }

        // A timed edge stays on its own day: dragging the bottom of Tuesday's meeting into
        // Wednesday's column lengthens it, it does not move it.
        var edge = entry.StartWall.Date + TimeAt(point.Y);
        if (drag.Grip == DragGrip.Start)
        {
            drag.Start = edge <= entry.EndWall - least ? edge : entry.EndWall - least;
            drag.End = entry.EndWall;
        }
        else
        {
            drag.Start = entry.StartWall;
            drag.End = edge >= entry.StartWall + least ? edge : entry.StartWall + least;
        }
    }

    /// <summary>The pointer over an edge says so, as every resizable thing on the desktop does.</summary>
    private void ShowGripCursor(Point point)
    {
        for (var i = _entryHits.Count - 1; i >= 0; i--)
        {
            var (box, entry, banded) = _entryHits[i];
            if (!box.Contains(point)) continue;
            var grip = ChipDrag.CanDrag(entry) ? ChipDrag.GripAt(box, point, horizontal: banded) : DragGrip.Move;
            Cursor = grip == DragGrip.Move ? Cursor.Default : new Cursor(GripCursor(grip, banded));
            return;
        }

        Cursor = Cursor.Default;
    }

    private static StandardCursorType GripCursor(DragGrip grip, bool horizontal)
        => horizontal
            ? (grip == DragGrip.Start ? StandardCursorType.LeftSide : StandardCursorType.RightSide)
            : (grip == DragGrip.Start ? StandardCursorType.TopSide : StandardCursorType.BottomSide);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ScrollMinutes -= e.Delta.Y * SlotMinutes * 3;
        e.Handled = true;
    }
}
