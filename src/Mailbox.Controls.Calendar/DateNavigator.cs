using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// The date navigator down the navigation pane: two or more mini months, with a block over the
/// days the main view is showing.
/// </summary>
/// <remarks>
/// Measured off the reference: a 224-wide panel of its own inside the pane, cells 28×24 inset 14
/// either side, a 24px title row, a 20px row of weekday letters and six rows of days — 188 per
/// month, stacked with no gap, which is how the two months read as one run of dates.
/// <para>
/// The months shown are one calendar, not two: the first draws the days that lead into it and the
/// last draws the days that trail out of it, and neither draws the other's. Drawing both would
/// print the same week twice, one row apart, which is the tell of a mini-month built as a control
/// per month.
/// </para>
/// </remarks>
public sealed class DateNavigator : CalendarSurface
{
    private const double TitleRowHeight = 24;
    private const double WeekdayRowHeight = 20;
    private const double RowHeight = 24;
    private const double CellWidth = 28;
    private const double SideInset = 14;
    private const double TextSize = 13;

    /// <summary>The title's baseline, from the top of its row.</summary>
    private const double TitleBaseline = 15;

    /// <summary>The weekday letters' baseline, from the top of their row.</summary>
    private const double WeekdayBaseline = 13;

    /// <summary>A day number's baseline, from the top of its cell.</summary>
    private const double DayBaseline = 17;

    /// <summary>One month block: title, weekday letters, six week rows.</summary>
    public const double MonthHeight = TitleRowHeight + WeekdayRowHeight + (6 * RowHeight);

    private readonly List<(Rect Box, DateOnly Day)> _dayHits = [];
    private Rect _previous;
    private Rect _next;

    private DateOnly _anchor = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _rangeStart = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _rangeEnd = DateOnly.FromDateTime(DateTime.Today);
    private DayOfWeek _firstDayOfWeek = DayOfWeek.Sunday;
    private IReadOnlySet<DateOnly> _busyDays = new HashSet<DateOnly>();

    public DateNavigator()
    {
        Focusable = true;
    }

    /// <summary>The first month shown.</summary>
    public DateOnly Anchor
    {
        get => _anchor;
        set => Set(ref _anchor, new DateOnly(value.Year, value.Month, 1));
    }

    public DateOnly Today
    {
        get => _today;
        set => Set(ref _today, value);
    }

    /// <summary>The first and last day the main view is showing, inclusive.</summary>
    public DateOnly RangeStart
    {
        get => _rangeStart;
        set => Set(ref _rangeStart, value);
    }

    public DateOnly RangeEnd
    {
        get => _rangeEnd;
        set => Set(ref _rangeEnd, value);
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _firstDayOfWeek;
        set => Set(ref _firstDayOfWeek, value);
    }

    /// <summary>Days with something on them, which the reference draws in bold.</summary>
    public IReadOnlySet<DateOnly> BusyDays
    {
        get => _busyDays;
        set
        {
            _busyDays = value ?? new HashSet<DateOnly>();
            InvalidateVisual();
        }
    }

    /// <summary>How many months the pane has room for.</summary>
    public int MonthsShown { get; private set; } = 2;

    public event EventHandler<DateOnly>? DayPicked;

    /// <summary>A drag across the grid picks a run of days.</summary>
    public event EventHandler<(DateOnly First, DateOnly Last)>? RangePicked;

    /// <summary>The two arrows: -1 back a month, +1 on.</summary>
    public event EventHandler<int>? Stepped;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var months = double.IsInfinity(availableSize.Height)
            ? 2
            : Math.Max(1, (int)Math.Floor(availableSize.Height / MonthHeight));
        return new Size(
            double.IsInfinity(availableSize.Width) ? (CellWidth * 7) + (SideInset * 2) : availableSize.Width,
            months * MonthHeight);
    }

    // ---- Render ----------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        _dayHits.Clear();
        if (Bounds.Width < 60 || Bounds.Height < MonthHeight) return;

        MonthsShown = Math.Max(1, (int)Math.Floor(Bounds.Height / MonthHeight));
        Fill(context, new Rect(0, 0, Bounds.Width, MonthsShown * MonthHeight), Palette.Colour(TokenKeys.Calendar.NavigatorBackground));

        var cell = Math.Max(16, Math.Min(CellWidth, Math.Floor((Bounds.Width - (SideInset * 2)) / 7)));
        var left = Math.Round((Bounds.Width - (cell * 7)) / 2);

        for (var m = 0; m < MonthsShown; m++)
        {
            DrawMonth(context, Anchor.AddMonths(m), m, m * MonthHeight, left, cell);
        }
    }

    private void DrawMonth(DrawingContext context, DateOnly month, int index, double top, double left, double cell)
    {
        var ink = Palette.Colour(TokenKeys.Calendar.NavigatorText);

        var title = month.ToString("MMMM yyyy", Culture);
        var run = Ink(title, TextSize, ink, BoldFace);
        DrawAt(context, run, Math.Round((Bounds.Width - run.Width) / 2), top + TitleBaseline);

        // Only the first month carries the arrows; the rest follow it.
        if (index == 0)
        {
            _previous = new Rect(0, top, SideInset + 6, TitleRowHeight);
            _next = new Rect(Bounds.Width - SideInset - 6, top, SideInset + 6, TitleRowHeight);
            Chevron(context, new Point(10, top + (TitleRowHeight / 2)), pointsLeft: true, ink);
            Chevron(context, new Point(Bounds.Width - 12, top + (TitleRowHeight / 2)), pointsLeft: false, ink);
        }

        var letters = top + TitleRowHeight;
        for (var c = 0; c < 7; c++)
        {
            var day = (DayOfWeek)(((int)FirstDayOfWeek + c) % 7);
            var name = Culture.DateTimeFormat.GetShortestDayName(day).ToUpper(Culture);
            if (name.Length > 2) name = name[..2];
            var text = Ink(name, TextSize, ink);
            DrawAt(context, text, left + (c * cell) + ((cell - text.Width) / 2), letters + WeekdayBaseline);
        }

        var gridTop = letters + WeekdayRowHeight;
        var first = WeekStart(month);
        for (var row = 0; row < 6; row++)
        {
            DrawWeek(context, month, index, first.AddDays(row * 7), gridTop + (row * RowHeight), left, cell);
        }
    }

    private DateOnly WeekStart(DateOnly month)
    {
        var lead = (((int)month.DayOfWeek - (int)FirstDayOfWeek) + 7) % 7;
        return month.AddDays(-lead);
    }

    private void DrawWeek(DrawingContext context, DateOnly month, int index, DateOnly weekStart, double top, double left, double cell)
    {
        // The block over the shown days is drawn per row as one rectangle, so a week wholly
        // inside the range has no seams in it.
        var firstIn = -1;
        var lastIn = -1;
        for (var c = 0; c < 7; c++)
        {
            var date = weekStart.AddDays(c);
            if (!Drawn(month, index, date)) continue;
            if (date < RangeStart || date > RangeEnd) continue;
            if (firstIn < 0) firstIn = c;
            lastIn = c;
        }

        if (firstIn >= 0)
        {
            Fill(
                context,
                new Rect(left + (firstIn * cell), top, (lastIn - firstIn + 1) * cell, RowHeight),
                Palette.Colour(TokenKeys.Calendar.NavigatorRange));
        }

        for (var c = 0; c < 7; c++)
        {
            var date = weekStart.AddDays(c);
            if (!Drawn(month, index, date)) continue;

            var box = new Rect(left + (c * cell), top, cell, RowHeight);
            _dayHits.Add((box, date));

            var inRange = date >= RangeStart && date <= RangeEnd;
            if (date == Today) Fill(context, box, Palette.Colour(TokenKeys.Calendar.NavigatorToday));

            var ink = date == Today
                ? Palette.Colour(TokenKeys.Calendar.TodayText)
                : inRange
                    ? Palette.Colour(TokenKeys.Calendar.NavigatorRangeText)
                    : Palette.Colour(TokenKeys.Calendar.NavigatorText);

            var face = date == Today || BusyDays.Contains(date) ? BoldFace : Face;
            var text = Ink(date.Day.ToString(Culture), TextSize, ink, face);
            DrawAt(context, text, box.X + ((cell - text.Width) / 2), top + DayBaseline);
        }
    }

    /// <summary>
    /// Whether this month's grid draws a day belonging to another month: the first month shown
    /// draws the days leading into it, the last draws the days trailing out of it, and no month
    /// draws a day another one has.
    /// </summary>
    private bool Drawn(DateOnly month, int index, DateOnly date)
    {
        if (date.Year == month.Year && date.Month == month.Month) return true;
        if (date < month) return index == 0;
        return index == MonthsShown - 1;
    }

    private void Chevron(DrawingContext context, Point centre, bool pointsLeft, Color colour)
    {
        var dx = pointsLeft ? 3.0 : -3.0;
        var pen = new Pen(Palette.Brush(colour), 1.4);
        context.DrawLine(pen, new Point(centre.X + dx, centre.Y - 4.5), new Point(centre.X - dx, centre.Y));
        context.DrawLine(pen, new Point(centre.X - dx, centre.Y), new Point(centre.X + dx, centre.Y + 4.5));
    }

    // ---- Input -----------------------------------------------------------------------------

    private DateOnly? _dragFrom;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        if (_previous.Contains(point))
        {
            Stepped?.Invoke(this, -1);
            e.Handled = true;
            return;
        }

        if (_next.Contains(point))
        {
            Stepped?.Invoke(this, 1);
            e.Handled = true;
            return;
        }

        if (Hit(point) is not { } day) return;
        _dragFrom = day;
        e.Pointer.Capture(this);
        DayPicked?.Invoke(this, day);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        if (Hit(e.GetPosition(this)) is not { } day || day == from) return;
        RangePicked?.Invoke(this, day < from ? (day, from) : (from, day));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Stepped?.Invoke(this, e.Delta.Y < 0 ? 1 : -1);
        e.Handled = true;
    }

    private DateOnly? Hit(Point point)
    {
        foreach (var (box, day) in _dayHits)
        {
            if (box.Contains(point)) return day;
        }

        return null;
    }

    // ---- Where things were drawn -------------------------------------------------------------

    /// <summary>The days this navigator last drew, in order.</summary>
    /// <remarks>
    /// Which days are on show is the question a bold-day check has to start from: a day outside
    /// the drawn run is neither bold nor not bold, and counting it either way is wrong.
    /// </remarks>
    public IReadOnlyList<DateOnly> DrawnDays => [.. _dayHits.Select(hit => hit.Day)];

    /// <summary>The middle of a day's cell, or null when that day was not drawn.</summary>
    public Point? PointAt(DateOnly day)
    {
        foreach (var (box, drawn) in _dayHits)
        {
            if (drawn == day) return box.Center;
        }

        return null;
    }

    /// <summary>The middle of one of the two month arrows, or null before the first render.</summary>
    public Point? ArrowAt(bool back)
    {
        var box = back ? _previous : _next;
        return box.Width > 0 ? box.Center : null;
    }
}
