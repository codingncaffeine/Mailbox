using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Controls.Common;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// The calendar peek: a miniature month over the chosen day's appointments, drawn either as the
/// popup the rail's Calendar icon opens or as the pane that popup docks itself into.
/// </summary>
/// <remarks>
/// One control for both states, because they are the same content and drift apart the moment
/// they are two. What differs is held in <see cref="PeekLayout"/> and in which half of the
/// <c>peek.*</c> family is read: the pane is part of the window and follows the theme, while the
/// popup is a desktop popup and keeps the desktop's light colours in every theme — which is what
/// the reference's own capture over the Dark Gray shell shows.
/// </remarks>
public sealed class PeekView : CalendarSurface, ISpokenRows
{
    private readonly List<(Rect Box, DateOnly Day)> _dayHits = [];
    private readonly List<(Rect Box, PeekAgendaRow Row)> _entryHits = [];

    private IReadOnlyList<CalendarEntry> _entries = [];
    private IReadOnlyList<PeekAgendaRow> _agenda = [];

    private DateOnly _anchor = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _selected = DateOnly.FromDateTime(DateTime.Today);
    private DayOfWeek _firstDayOfWeek = DayOfWeek.Sunday;
    private bool _weekNumbers;
    private double _scroll;

    private DateOnly? _hoverDay;
    private bool _hoverCorner;

    public PeekView(bool docked)
    {
        IsDocked = docked;
        Focusable = true;
    }

    /// <summary>True when pinned down the right-hand edge rather than floating over the window.</summary>
    public bool IsDocked { get; }

    /// <summary>Today, as the module believes it — pinned by the harness so a capture holds still.</summary>
    public DateOnly Today
    {
        get => _today;
        set => Set(ref _today, value);
    }

    /// <summary>The month on show. Set by the arrows, and by picking a day outside it.</summary>
    public DateOnly Anchor
    {
        get => _anchor;
        set => Set(ref _anchor, new DateOnly(value.Year, value.Month, 1));
    }

    /// <summary>The day whose appointments are listed under the grid.</summary>
    public DateOnly Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            RebuildAgenda();
        }
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _firstDayOfWeek;
        set => Set(ref _firstDayOfWeek, value);
    }

    /// <summary>The ISO week column down the left, which the Calendar Options page asks for.</summary>
    public bool ShowWeekNumbers
    {
        get => _weekNumbers;
        set => Set(ref _weekNumbers, value);
    }

    /// <summary>
    /// Everything the store holds for the selected day. The peek keeps what it is given rather
    /// than reading the store itself: the shell owns the repository, and a view that reads one
    /// cannot be drawn in a test.
    /// </summary>
    public IReadOnlyList<CalendarEntry> Entries
    {
        get => _entries;
        set
        {
            _entries = value ?? [];
            RebuildAgenda();
        }
    }

    /// <summary>What the agenda is showing, which is what a harness pose reads back.</summary>
    public IReadOnlyList<PeekAgendaRow> Agenda => _agenda;

    /// <summary>How far the agenda has been wheeled down, in pixels.</summary>
    public double Scroll => _scroll;

    /// <summary>
    /// How much taller the day's agenda is than the room it has, or zero when all of it fits —
    /// which is also whether the scrollbar is drawn.
    /// </summary>
    public double Overflow { get; private set; }

    /// <summary>The arrows: -1 a month back, +1 on.</summary>
    public event EventHandler<int>? Stepped;

    /// <summary>A day picked in the grid — the agenda follows it.</summary>
    public event EventHandler<DateOnly>? DayPicked;

    /// <summary>An appointment in the agenda pressed, which opens it.</summary>
    public event EventHandler<CalendarEntry>? EntryActivated;

    /// <summary>The corner button: dock when floating, close when docked.</summary>
    public event EventHandler? CornerPressed;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        InvalidateVisual();
    }

    private void RebuildAgenda()
    {
        _agenda = PeekAgenda.For(_entries, _selected, Culture);
        _scroll = 0;
        InvalidateVisual();
        SpokenRowsChanged?.Invoke(this, EventArgs.Empty);

        // The rows are gone and with them whatever a reader was on, which is a move to nothing —
        // the bridge is told, because a reader left pointing at a row that no longer exists is
        // the failure this event is for.
        SpokenSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Measure ---------------------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!IsDocked)
        {
            return new Size(
                PeekLayout.PopupWidth + (2 * (PeekLayout.FrameX + PeekLayout.Outline)),
                PeekLayout.PopupHeight + (2 * (PeekLayout.FrameY + PeekLayout.Outline)));
        }

        var height = double.IsInfinity(availableSize.Height) ? PeekLayout.PopupHeight : availableSize.Height;
        return new Size(PeekLayout.DockedWidth + PeekLayout.DividerWidth, height);
    }

    /// <summary>Where the content starts: inside the popup's frame, or right of the pane's divider.</summary>
    private Point Origin => IsDocked
        ? new Point(PeekLayout.DividerWidth, 0)
        : new Point(PeekLayout.Outline + PeekLayout.FrameX, PeekLayout.Outline + PeekLayout.FrameY);

    private double ContentWidth => IsDocked
        ? Math.Max(0, Bounds.Width - PeekLayout.DividerWidth)
        : PeekLayout.PopupWidth;

    private PeekLayout Solve() => new(IsDocked, ContentWidth, ShowWeekNumbers);

    // ---- Palette ---------------------------------------------------------------------------

    // The two halves of the family. A popup takes the desktop's, a pane the theme's.
    private Color Ground => Colour(TokenKeys.Peek.Background, TokenKeys.Peek.PopBackground);
    private Color TitleInk => Colour(TokenKeys.Peek.Title, TokenKeys.Peek.PopTitle);
    private Color DayInk => Colour(TokenKeys.Peek.Day, TokenKeys.Peek.PopDay);
    private Color OtherInk => Colour(TokenKeys.Peek.DayOther, TokenKeys.Peek.PopDayOther);
    private Color TodayFill => Colour(TokenKeys.Peek.Today, TokenKeys.Peek.PopToday);
    private Color TodayInk => Colour(TokenKeys.Peek.TodayText, TokenKeys.Peek.PopTodayText);
    private Color HoverFill => Colour(TokenKeys.Peek.Hover, TokenKeys.Peek.PopHover);
    private Color TextInk => Colour(TokenKeys.Peek.Text, TokenKeys.Peek.PopText);
    private Color DimInk => Colour(TokenKeys.Peek.TextDim, TokenKeys.Peek.PopTextDim);
    private Color Hatch => Colour(TokenKeys.Peek.Hatch, TokenKeys.Peek.PopHatch);

    private Color Colour(string docked, string floating) => Palette.Colour(IsDocked ? docked : floating);

    // ---- Render ----------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        _dayHits.Clear();
        _entryHits.Clear();
        if (Bounds.Width < 40 || Bounds.Height < 40) return;

        var layout = Solve();
        DrawFrame(context);

        var origin = Origin;
        using var _ = context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y));

        DrawCorner(context, layout);
        DrawMonth(context, layout);
        if (IsDocked && layout.Rule.Width > 0)
        {
            Fill(context, new Rect(layout.Rule.X, layout.Rule.Y, layout.Rule.Width, 1), Palette.Colour(TokenKeys.Peek.RuleSoft));
            Fill(context, new Rect(layout.Rule.X, layout.Rule.Y + 1, layout.Rule.Width, 1), Palette.Colour(TokenKeys.Peek.Rule));
        }

        DrawAgenda(context, layout);
    }

    /// <summary>
    /// The popup's own edges: a hairline round a broad light frame, which is how the desktop
    /// draws a window of this kind. The docked pane has neither — only the line that divides it
    /// from what it sits beside.
    /// </summary>
    private void DrawFrame(DrawingContext context)
    {
        var whole = new Rect(0, 0, Bounds.Width, Bounds.Height);

        if (IsDocked)
        {
            Fill(context, whole, Ground);
            Fill(context, new Rect(0, 0, PeekLayout.DividerWidth, Bounds.Height), Palette.Colour(TokenKeys.Peek.Divider));
            return;
        }

        Fill(context, whole, Palette.Colour(TokenKeys.Peek.PopOutline));
        Fill(context, whole.Deflate(PeekLayout.Outline), Palette.Colour(TokenKeys.Peek.PopFrame));
        Fill(
            context,
            new Rect(
                PeekLayout.Outline + PeekLayout.FrameX,
                PeekLayout.Outline + PeekLayout.FrameY,
                Math.Max(0, Bounds.Width - (2 * (PeekLayout.Outline + PeekLayout.FrameX))),
                Math.Max(0, Bounds.Height - (2 * (PeekLayout.Outline + PeekLayout.FrameY)))),
            Ground);
    }

    private void DrawCorner(DrawingContext context, PeekLayout layout)
    {
        if (_hoverCorner) Fill(context, layout.Corner, HoverFill);

        if (IsDocked) DrawClose(context, layout.Corner.Center);
        else DrawDock(context, layout.Corner);
    }

    /// <summary>
    /// The dock glyph: a pane with an arrow leaving it by the top-right corner, which is the
    /// one part of the peek the reference draws in two colours. Measured 11×9 with the arrow's
    /// point two pixels past it, hard against the content's right edge.
    /// </summary>
    private void DrawDock(DrawingContext context, Rect corner)
    {
        var box = new Rect(corner.Right - 13, corner.Y + 4, 11, 9);
        var ink = TextInk;

        // The pane, with its top-right corner left open for the arrow to pass through.
        Fill(context, new Rect(box.X, box.Y, 6, 1), ink);
        Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), ink);
        Fill(context, new Rect(box.X, box.Y, 1, box.Height), ink);
        Fill(context, new Rect(box.Right - 1, box.Y + 6, 1, box.Height - 6), ink);
        Fill(context, new Rect(box.X + 1, box.Y + 3, 4, 1), ink);

        var arrow = Palette.Brush(TodayFill);
        var tip = new Point(box.Right + 0.5, box.Y - 1.5);
        context.DrawLine(new Pen(arrow, 1.6), new Point(box.X + 4, box.Bottom - 3.5), tip);

        var head = new StreamGeometry();
        using (var draw = head.Open())
        {
            draw.BeginFigure(new Point(tip.X + 1, tip.Y - 1), isFilled: true);
            draw.LineTo(new Point(tip.X + 1, tip.Y + 4.5));
            draw.LineTo(new Point(tip.X - 4.5, tip.Y - 1));
            draw.EndFigure(true);
        }

        context.DrawGeometry(arrow, null, head);
    }

    /// <summary>The docked pane's close cross, measured 13 wide by 14 tall.</summary>
    private void DrawClose(DrawingContext context, Point centre)
    {
        var pen = new Pen(Palette.Brush(TextInk), 1.8);
        const double R = 6;
        context.DrawLine(pen, new Point(centre.X - R, centre.Y - R), new Point(centre.X + R, centre.Y + R));
        context.DrawLine(pen, new Point(centre.X + R, centre.Y - R), new Point(centre.X - R, centre.Y + R));
    }

    private void DrawMonth(DrawingContext context, PeekLayout layout)
    {
        var title = Ink(Anchor.ToString("MMMM yyyy", Culture), PeekLayout.TitleSize, TitleInk, SemiBoldFace);
        DrawAt(context, title, layout.TitleCentre - (title.Width / 2), layout.TitleBaseline);

        Chevron(context, layout.Previous.Center, pointsLeft: true, TitleInk);
        Chevron(context, layout.Next.Center, pointsLeft: false, TitleInk);

        for (var c = 0; c < 7; c++)
        {
            var day = (DayOfWeek)(((int)FirstDayOfWeek + c) % 7);
            var name = Culture.DateTimeFormat.GetShortestDayName(day).ToUpper(Culture);
            if (name.Length > 2) name = name[..2];
            var text = Ink(name, PeekLayout.CellSize, TitleInk);
            DrawAt(context, text, layout.WeekdayCentre(c) - (text.Width / 2), layout.WeekdayBaseline);
        }

        var lead = (((int)Anchor.DayOfWeek - (int)FirstDayOfWeek) + 7) % 7;
        var cursor = Anchor.AddDays(-lead);

        for (var row = 0; row < PeekLayout.WeekRows; row++)
        {
            if (ShowWeekNumbers)
            {
                var box = layout.WeekCell(row);

                // Asked about the row's Thursday, not its first cell. An ISO week runs Monday to
                // Sunday, so with Sunday first in the grid the row's own first cell belongs to
                // the week that is ending: the row Sun 16 – Sat 22 August 2026 was labelled 33,
                // which is Mon 10 – Sun 16. Thursday is in the same ISO week as every other day
                // of its Monday-week, whichever day the grid starts on, so it is the one cell
                // that answers correctly for both settings.
                var thursday = cursor.AddDays((((int)DayOfWeek.Thursday - (int)cursor.DayOfWeek) + 7) % 7);
                var week = Ink(ISOWeek.GetWeekOfYear(thursday.ToDateTime(TimeOnly.MinValue)).ToString(Culture), PeekLayout.CellSize, OtherInk);
                DrawAt(context, week, box.X + ((box.Width - week.Width) / 2), box.Y + PeekLayout.CellBaselineOffset);
            }

            for (var column = 0; column < 7; column++)
            {
                DrawDay(context, layout.DayCell(row, column), cursor);
                cursor = cursor.AddDays(1);
            }
        }
    }

    private void DrawDay(DrawingContext context, Rect box, DateOnly date)
    {
        _dayHits.Add((box, date));

        var isToday = date == Today;
        if (isToday) Fill(context, box, TodayFill);
        else if (_hoverDay == date) Fill(context, box, HoverFill);

        // Today keeps its filled cell whatever is selected, and a selected day that is not today
        // is outlined instead, so the two read as different things rather than one hiding the
        // other. No capture holds a selected day; the outline is authored.
        if (date == Selected && !isToday)
        {
            var edge = TodayFill;
            Fill(context, new Rect(box.X, box.Y, box.Width, 1), edge);
            Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), edge);
            Fill(context, new Rect(box.X, box.Y, 1, box.Height), edge);
            Fill(context, new Rect(box.Right - 1, box.Y, 1, box.Height), edge);
        }

        var ink = isToday
            ? TodayInk
            : date.Month == Anchor.Month && date.Year == Anchor.Year ? DayInk : OtherInk;

        var text = Ink(date.Day.ToString(Culture), PeekLayout.CellSize, ink);
        DrawAt(context, text, box.X + ((box.Width - text.Width) / 2), box.Y + PeekLayout.CellBaselineOffset);
    }

    /// <summary>The month arrows, measured 6 wide by 11 tall.</summary>
    private void Chevron(DrawingContext context, Point centre, bool pointsLeft, Color colour)
    {
        var dx = pointsLeft ? 2.5 : -2.5;
        var pen = new Pen(Palette.Brush(colour), 1.5);
        context.DrawLine(pen, new Point(centre.X + dx, centre.Y - 4.5), new Point(centre.X - dx, centre.Y));
        context.DrawLine(pen, new Point(centre.X - dx, centre.Y), new Point(centre.X + dx, centre.Y + 4.5));
    }

    /// <summary>
    /// The day's name and what is on it. Clipped to what is left under the grid, and scrolled by
    /// the wheel when there is more of it than there is room.
    /// </summary>
    private void DrawAgenda(DrawingContext context, PeekLayout layout)
    {
        var floor = AgendaFloor;
        var top = layout.HeadingBaseline - PeekLayout.EntryLineHeight;
        if (floor <= top)
        {
            Overflow = 0;
            return;
        }

        {
            using var clip = context.PushClip(new Rect(0, top, layout.Width, floor - top));
            using var scroll = context.PushTransform(Matrix.CreateTranslation(0, -Math.Round(_scroll)));

            var heading = Ink(_selected.ToString("dddd", Culture), PeekLayout.AgendaSize, TextInk, SemiBoldFace);
            DrawAt(context, heading, layout.AgendaLeft, layout.HeadingBaseline);

            // One time column for the whole day, so the bars line up whatever the times are.
            var column = PeekLayout.EntryBarInset;
            foreach (var row in _agenda)
            {
                var width = Measure(row.Time, PeekLayout.AgendaSize) + PeekLayout.EntryTimeInset + PeekLayout.EntryTimeGap;
                if (width > column) column = Math.Ceiling(width);
            }

            var y = layout.AgendaTop;
            foreach (var row in _agenda)
            {
                DrawEntry(context, layout, row, y, column);
                y += PeekLayout.EntryHeight(row.Lines) + PeekLayout.EntryGap;
            }

            _content = Math.Max(0, y - PeekLayout.EntryGap - top);
        }

        // Outside the clip's scroll transform: a scrollbar that scrolled with what it scrolls
        // would leave the box the moment it was used.
        DrawScrollbar(context, layout, top, floor);
    }

    /// <summary>
    /// The agenda's scrollbar, in the gutter the layout reserves for it, on the days that have
    /// more on them than fits.
    /// </summary>
    /// <remarks>
    /// The gutter was reserved and nothing was ever drawn in it, so a busy day was clipped in
    /// silence: the peek is a fixed height, five appointments do not fit in it, and the only way
    /// to find out there were more was to wheel over it and see the list move. The reference's
    /// own bar is the model — 17 columns wide with a 9-wide thumb, which is exactly the popup's
    /// gutter — and its arrow buttons are deliberately left off, the docked pane's gutter being
    /// 12 and too narrow to hold them without the two states drawing different marks.
    /// </remarks>
    private void DrawScrollbar(DrawingContext context, PeekLayout layout, double top, double floor)
    {
        var room = floor - top;
        Overflow = Math.Max(0, _content - room);
        if (Overflow <= 0 || layout.Gutter <= 0) return;

        var box = layout.Scrollbar(top, floor);
        Fill(context, box, Colour(TokenKeys.Peek.Scroll, TokenKeys.Peek.PopScroll));

        // As tall as the visible share of the day, and never so short it cannot be seen.
        var height = Math.Max(PeekLayout.ScrollThumbMinimum, Math.Round(room * room / _content));
        var travel = Math.Max(0, room - height);
        var offset = Overflow > 0 ? Math.Round(travel * Math.Clamp(_scroll / Overflow, 0, 1)) : 0;

        var thumb = new Rect(
            box.X + Math.Round((box.Width - PeekLayout.ScrollThumbWidth) / 2),
            box.Y + offset,
            PeekLayout.ScrollThumbWidth,
            height);

        context.DrawRectangle(
            Palette.Brush(Colour(TokenKeys.Peek.ScrollThumb, TokenKeys.Peek.PopScrollThumb)),
            null,
            thumb,
            PeekLayout.ScrollThumbWidth / 2,
            PeekLayout.ScrollThumbWidth / 2);
    }

    /// <summary>The content's own bottom edge: inside the popup's frame, or the pane's whole height.</summary>
    private double AgendaFloor => IsDocked
        ? Bounds.Height
        : Bounds.Height - (2 * (PeekLayout.Outline + PeekLayout.FrameY));

    /// <summary>How tall the agenda would be if nothing clipped it, which is what wheeling knows.</summary>
    private double _content;

    private void DrawEntry(DrawingContext context, PeekLayout layout, PeekAgendaRow row, double top, double column)
    {
        var height = PeekLayout.EntryHeight(row.Lines);
        var left = layout.AgendaLeft;

        _entryHits.Add((
            new Rect(left, top - Math.Round(_scroll), Math.Max(0, layout.AgendaWidth), height),
            row));

        // The bar down its left is the appointment's Show As, drawn as the views draw a chip's:
        // solid for Busy, pale for Free, and diagonals over a hatch ground for Tentative.
        var paint = Palette.Chip(row.Entry.Colour, row.Entry.Busy);
        var bar = new Rect(left + column, top, PeekLayout.EntryBarWidth, height);
        if (paint.Hatched)
        {
            Fill(context, bar, Hatch);
            DrawHatch(context, bar, paint.Bar);
        }
        else
        {
            Fill(context, bar, row.Entry.Busy == BusyStatus.Free ? paint.Edge : paint.Bar);
        }

        var textLeft = left + column + (PeekLayout.EntryTextInset - PeekLayout.EntryBarInset);
        var room = Math.Max(0, layout.AgendaWidth - (textLeft - left));
        var baseline = top + PeekLayout.EntryBaseline;

        DrawAt(context, Ink(row.Time, PeekLayout.AgendaSize, TextInk), left + PeekLayout.EntryTimeInset, baseline);
        DrawAt(
            context,
            Ink(Ellipsize(row.Subject, room, PeekLayout.AgendaSize, SemiBoldFace), PeekLayout.AgendaSize, TextInk, SemiBoldFace),
            textLeft,
            baseline);

        if (row.Detail.Length == 0) return;
        DrawAt(
            context,
            Ink(Ellipsize(row.Detail, room, PeekLayout.AgendaSize), PeekLayout.AgendaSize, DimInk),
            textLeft,
            baseline + PeekLayout.EntryLineHeight);
    }

    // ---- What a pose aims at ---------------------------------------------------------------

    /// <summary>
    /// Where a day's cell was drawn, in this control's own coordinates, or null when that day is
    /// not on the grid. What the fidelity harness presses, the pointer having no other way in.
    /// </summary>
    public Rect? BoxOf(DateOnly day)
    {
        foreach (var (box, date) in _dayHits)
        {
            if (date == day) return Shift(box);
        }

        return null;
    }

    /// <summary>Where an agenda entry was drawn, by its place in the day's list.</summary>
    public Rect? BoxOf(int index)
        => index >= 0 && index < _entryHits.Count ? Shift(_entryHits[index].Box) : null;

    /// <summary>The corner button and the two month arrows, in this control's coordinates.</summary>
    public Rect CornerBox => Shift(Solve().Corner);

    public Rect PreviousBox => Shift(Solve().Previous);

    public Rect NextBox => Shift(Solve().Next);

    private Rect Shift(Rect box) => box.Translate(new Vector(Origin.X, Origin.Y));

    // ---- Input -----------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var layout = Solve();
        var inside = point - Origin;

        if (layout.Corner.Contains(inside))
        {
            CornerPressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (layout.Previous.Contains(inside))
        {
            Stepped?.Invoke(this, -1);
            e.Handled = true;
            return;
        }

        if (layout.Next.Contains(inside))
        {
            Stepped?.Invoke(this, 1);
            e.Handled = true;
            return;
        }

        foreach (var (box, day) in _dayHits)
        {
            if (!box.Contains(inside)) continue;
            DayPicked?.Invoke(this, day);
            e.Handled = true;
            return;
        }

        foreach (var (box, row) in _entryHits)
        {
            if (!box.Contains(inside)) continue;
            EntryActivated?.Invoke(this, row.Entry);
            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var inside = point - Origin;

        DateOnly? day = null;
        foreach (var (box, date) in _dayHits)
        {
            if (!box.Contains(inside)) continue;
            day = date;
            break;
        }

        var corner = Solve().Corner.Contains(inside);
        if (day == _hoverDay && corner == _hoverCorner) return;
        _hoverDay = day;
        _hoverCorner = corner;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverDay is null && !_hoverCorner) return;
        _hoverDay = null;
        _hoverCorner = false;
        InvalidateVisual();
    }

    /// <summary>
    /// The wheel means two things by where it is: over the grid it turns the month, as the date
    /// navigator's does, and over the agenda it scrolls what will not fit.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var layout = Solve();
        var inside = e.GetPosition(this) - Origin;

        if (inside.Y < layout.Grid.Bottom)
        {
            Stepped?.Invoke(this, e.Delta.Y < 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        var room = Math.Max(0, AgendaFloor - (layout.HeadingBaseline - PeekLayout.EntryLineHeight));
        var limit = Math.Max(0, _content - room);
        var moved = Math.Clamp(_scroll - (e.Delta.Y * PeekLayout.EntryLineHeight), 0, limit);
        if (Math.Abs(moved - _scroll) < 0.5) return;
        _scroll = moved;
        e.Handled = true;
        InvalidateVisual();
    }

    // ---- The day's agenda, spoken for ------------------------------------------------------
    //
    // The rows are the appointments, not the little month's days: the grid up there is a way of
    // choosing which day the agenda lists, and the agenda is what the peek is for. A reader is
    // told the day in the list's own name and then each appointment on it.

    public event EventHandler? SpokenRowsChanged;

    public event EventHandler? SpokenSelectionChanged;

    int ISpokenRows.SpokenCount => _agenda.Count;

    string ISpokenRows.SpokenRow(int index)
    {
        var row = _agenda[index];
        var said = new System.Text.StringBuilder();
        said.Append(row.Subject.Length > 0 ? row.Subject : "(No subject)").Append(". ");
        said.Append(row.Time);
        if (row.Detail.Length > 0) said.Append(". ").Append(row.Detail);
        said.Append('.');
        return said.ToString();
    }

    /// <summary>
    /// The peek has no selected appointment: a press on one opens it. So nothing is current, and
    /// the row's own door is the press.
    /// </summary>
    int ISpokenRows.SpokenSelectedIndex => -1;

    /// <summary>Opens the appointment, which is what a press on its row does.</summary>
    void ISpokenRows.SpokenSelect(int index) => EntryActivated?.Invoke(this, _agenda[index].Entry);

    /// <summary>
    /// Where the row was drawn. The agenda scrolls under a fixed frame, so a row wheeled out of
    /// sight has a box outside the view's bounds and the peer works the rest out.
    /// </summary>
    Rect? ISpokenRows.SpokenRowBounds(int index)
    {
        foreach (var (box, row) in _entryHits)
        {
            if (ReferenceEquals(row, _agenda[index])) return box;
        }

        return null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpokenRowsPeer(this);
}
