using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Controls.Common;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Journal;

/// <summary>
/// The Journal module's view: the timeline the module opens in, and the grouped rows its other
/// three arrangements draw.
/// </summary>
/// <remarks>
/// <b>No capture of this module exists</b> — the reference hides it behind Ctrl+8 and stopped
/// developing it long ago — so everything here is authored from its shape: two heading rows over a run of
/// day columns, entries hung under the moment they started and packed into lanes so none covers
/// another, and the same entries as rows grouped by what kind of thing each was. The numbers
/// borrowed are the ones already measured elsewhere — a 26px heading and a 21px row.
/// <para>
/// <b>Divergence, stated:</b> the reference's timeline scrolls sideways through time. This one
/// fits its span to the width it is given — a day, a week or a month, whole — because a horizontal
/// scrollbar is the one thing a drawn view cannot borrow from the rest of the application, and a
/// span that always fits is what makes Back, Forward and Today mean something.
/// </para>
/// </remarks>
public sealed class JournalView : DrawnSurface
{
    /// <summary>Authored: the span's name over the days inside it.</summary>
    public const double SpanRowHeight = 22;
    public const double DayRowHeight = 22;
    public const double HeadingHeight = SpanRowHeight + DayRowHeight;

    /// <summary>Authored: one entry's box on the timeline, and the gap under it.</summary>
    private const double EntryHeight = 18;
    private const double LaneGap = 3;
    private const double EntryGlyph = 11;
    private const double EntryPad = 5;

    /// <summary>What the bar and the glyph take before the text, and the gap after it.</summary>
    private const double EntryLead = EntryPad + 3 + EntryGlyph + EntryPad;
    private const double EntryTail = EntryPad * 2;
    private const double TimelineInset = 6;

    /// <summary>The same 26 and 21 the message list and the to-do list are measured at.</summary>
    public const double GroupHeight = 26;
    public const double RowHeight = 21;

    private const double GlyphColumn = 26;
    private const double StartColumn = 130;
    private const double DurationColumn = 90;
    private const double ContactColumn = 140;
    private const double TextSize = 12;

    private IReadOnlyList<JournalRow> _rows = [];
    private JournalRow? _selected;
    private JournalRow? _hover;
    private int _scroll;

    public JournalView()
    {
        Focusable = true;
    }

    /// <summary>The entries on show, newest first.</summary>
    public IReadOnlyList<JournalRow> Rows
    {
        get => _rows;
        set
        {
            _rows = value ?? [];
            if (_selected is { } chosen) _selected = _rows.FirstOrDefault(r => r.ItemId == chosen.ItemId);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, LineCount() - 1));
            InvalidateVisual();
        }
    }

    /// <summary>Which of the four the Current View group has chosen.</summary>
    public JournalArrangement Arrangement { get; set; } = JournalArrangement.Timeline;

    /// <summary>How wide a slice of time the timeline is showing.</summary>
    public TimelineScale Scale { get; set; } = TimelineScale.Week;

    /// <summary>The day the span is taken from: the day itself, the week around it, or its month.</summary>
    public DateOnly Anchor { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Where a week starts, which the calendar's own option decides.</summary>
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Sunday;

    public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    public JournalRow? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// What the status bar counts: what the view is showing, which on the timeline is the span
    /// rather than everything the journal holds.
    /// </summary>
    public int Count => IsTimeline ? InSpan().Count : _rows.Count;

    public event EventHandler<JournalRow>? EntrySelected;
    public event EventHandler<JournalRow>? EntryActivated;

    // ---- The span ------------------------------------------------------------------------------

    private bool IsTimeline => Arrangement == JournalArrangement.Timeline;

    /// <summary>The first day on show.</summary>
    public DateOnly SpanStart => Scale switch
    {
        TimelineScale.Day => Anchor,
        TimelineScale.Month => new DateOnly(Anchor.Year, Anchor.Month, 1),
        _ => Anchor.AddDays(-(((int)Anchor.DayOfWeek - (int)FirstDayOfWeek + 7) % 7)),
    };

    /// <summary>How many columns the span is drawn in: hours for a day, days otherwise.</summary>
    public int ColumnCount => Scale switch
    {
        TimelineScale.Day => 24,
        TimelineScale.Month => DateTime.DaysInMonth(Anchor.Year, Anchor.Month),
        _ => 7,
    };

    /// <summary>What the top heading writes over the columns.</summary>
    public string SpanText() => Scale switch
    {
        TimelineScale.Day => SpanStart.ToString("dddd, d MMMM yyyy", Culture),
        TimelineScale.Month => SpanStart.ToString("MMMM yyyy", Culture),
        _ => SpanStart.ToString("d MMM", Culture) + " – " + SpanStart.AddDays(6).ToString("d MMM yyyy", Culture),
    };

    /// <summary>The entries inside the span, which is what the timeline hangs.</summary>
    private List<JournalRow> InSpan()
    {
        var from = SpanStart;
        var to = Scale == TimelineScale.Day ? from.AddDays(1) : from.AddDays(ColumnCount);
        return [.. _rows.Where(r => DateOnly.FromDateTime(r.Start) >= from && DateOnly.FromDateTime(r.Start) < to)];
    }

    // ---- Where everything goes -----------------------------------------------------------------

    private double ColumnWidth => Math.Max(1, (Bounds.Width - (TimelineInset * 2)) / ColumnCount);

    /// <summary>Where a moment falls across the view.</summary>
    private double X(DateTime moment)
    {
        var offset = Scale == TimelineScale.Day
            ? moment.TimeOfDay.TotalHours
            : (DateOnly.FromDateTime(moment).DayNumber - SpanStart.DayNumber) + (moment.TimeOfDay.TotalHours / 24);

        return TimelineInset + (Math.Clamp(offset, 0, ColumnCount) * ColumnWidth);
    }

    /// <summary>Lanes for the timeline, or rows and headings for the lists — whichever scrolls.</summary>
    private int LineCount()
    {
        if (IsTimeline) return Packed().Count == 0 ? 0 : Packed().Max(p => p.Lane) + 1;
        return Lines().Count;
    }

    /// <summary>
    /// Every entry in the span with the lane it goes in: the first lane whose last box ends
    /// before this one starts, which is what keeps two entries an hour apart on one line and
    /// pushes an overlapping pair onto two.
    /// </summary>
    private List<(JournalRow Row, double Left, double Width, int Lane)> Packed()
    {
        var packed = new List<(JournalRow Row, double Left, double Width, int Lane)>();
        var laneEnds = new List<double>();

        // Oldest first along the line, whatever order the rows arrived in: a timeline reads
        // left to right and packing right to left would fill the lanes backwards.
        foreach (var row in InSpan().OrderBy(r => r.Start))
        {
            var left = X(row.Start);
            var text = Measure(row.Subject, TextSize);
            var span = row.Duration is { } duration && duration > TimeSpan.Zero
                ? X(row.Start + duration) - left
                : 0;
            var width = Math.Max(Math.Max(span, text + EntryLead + EntryTail), 40);
            width = Math.Min(width, Math.Max(40, Bounds.Width - TimelineInset - left));

            var lane = 0;
            while (lane < laneEnds.Count && laneEnds[lane] > left) lane++;
            if (lane == laneEnds.Count) laneEnds.Add(0);
            laneEnds[lane] = left + width + 4;

            packed.Add((row, left, width, lane));
        }

        return packed;
    }

    /// <summary>A heading or an entry: the list is one run of both, which is what scrolls.</summary>
    private readonly record struct ListLine(string Type, JournalRow? Row)
    {
        public bool IsHeading => Row is null;
    }

    private List<ListLine> Lines()
    {
        var lines = new List<ListLine>();

        // Phone Calls and Last Seven Days are the Entry List filtered, so they group the same
        // way — which for the calls means one heading, exactly as the reference shows it.
        foreach (var (type, rows) in JournalBook.ByType(_rows))
        {
            lines.Add(new ListLine(type, null));
            foreach (var row in rows) lines.Add(new ListLine(type, row));
        }

        return lines;
    }

    private IEnumerable<(JournalRow Row, Rect Box)> PlacedEntries()
    {
        var height = Bounds.Height > 0 ? Bounds.Height : double.MaxValue;

        foreach (var (row, left, width, lane) in Packed())
        {
            var y = HeadingHeight + 6 + ((lane - _scroll) * (EntryHeight + LaneGap));
            if (y < HeadingHeight || y >= height) continue;
            yield return (row, new Rect(left, y, width, EntryHeight));
        }
    }

    private IEnumerable<(ListLine Line, Rect Box)> PlacedLines()
    {
        var width = Bounds.Width;
        var height = Bounds.Height > 0 ? Bounds.Height : double.MaxValue;
        var y = 0d;
        var lines = Lines();

        for (var i = _scroll; i < lines.Count && y < height; i++)
        {
            var line = lines[i];
            var box = new Rect(0, y, width, line.IsHeading ? GroupHeight : RowHeight);
            yield return (line, box);
            y = box.Bottom;
        }
    }

    // ---- Render --------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 60 || height < 40) return;

        Fill(context, new Rect(0, 0, width, height), Colour(IsTimeline ? TokenKeys.Journal.Background : TokenKeys.List.Background));

        if (IsTimeline)
        {
            DrawTimeline(context, width, height);
            return;
        }

        if (_rows.Count == 0)
        {
            DrawEmpty(context, width, 40);
            return;
        }

        foreach (var (line, box) in PlacedLines())
        {
            if (line.IsHeading) DrawHeading(context, box, line.Type);
            else DrawRow(context, box, line.Row!);
        }
    }

    private void DrawEmpty(DrawingContext context, double width, double baseline)
    {
        var line = Ink("There are no journal entries to show.", TextSize, Colour(TokenKeys.List.PreviewText));
        DrawAt(context, line, Math.Round((width - line.Width) / 2), baseline);
    }

    /// <summary>The span's name, the columns inside it, and everything hung under them.</summary>
    private void DrawTimeline(DrawingContext context, double width, double height)
    {
        var headerInk = Colour(TokenKeys.Journal.HeaderText);
        Fill(context, new Rect(0, 0, width, HeadingHeight), Colour(TokenKeys.Journal.HeaderBackground));

        var span = Ink(SpanText(), 12, headerInk, SemiBoldFace);
        DrawAt(context, span, TimelineInset + 4, 16);

        var columnWidth = ColumnWidth;
        var line = Colour(TokenKeys.Journal.GridLine);

        for (var i = 0; i < ColumnCount; i++)
        {
            var x = TimelineInset + (i * columnWidth);
            var label = Scale == TimelineScale.Day
                ? SpanStart.ToDateTime(new TimeOnly(i, 0)).ToString("%h tt", Culture)
                : ColumnLabel(SpanStart.AddDays(i), columnWidth);

            // Today's column is shaded whole, which is what tells a week apart at a glance.
            if (Scale != TimelineScale.Day && SpanStart.AddDays(i) == Today)
            {
                Fill(context, new Rect(x, HeadingHeight, columnWidth, height - HeadingHeight), Colour(TokenKeys.Journal.TodayFill));
            }

            var text = Ink(label, 11, headerInk);
            if (text.Width < columnWidth - 4) DrawAt(context, text, x + 4, SpanRowHeight + 15);

            Fill(context, new Rect(Math.Round(x), SpanRowHeight, 1, height - SpanRowHeight), line);
        }

        Fill(context, new Rect(0, SpanRowHeight, width, 1), Colour(TokenKeys.Journal.HeaderLine));
        Fill(context, new Rect(0, HeadingHeight - 1, width, 1), Colour(TokenKeys.Journal.HeaderLine));

        if (InSpan().Count == 0)
        {
            var empty = Ink("Nothing was recorded in this period.", TextSize, Colour(TokenKeys.List.PreviewText));
            DrawAt(context, empty, Math.Round((width - empty.Width) / 2), HeadingHeight + 40);
            return;
        }

        foreach (var (row, box) in PlacedEntries()) DrawEntry(context, box, row);
    }

    /// <summary>A day's heading: as much of its name as the column has room for.</summary>
    private string ColumnLabel(DateOnly day, double columnWidth)
    {
        var full = day.ToString("ddd d", Culture);
        if (Measure(full, 11) + 8 <= columnWidth) return full;
        return day.Day.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>One entry on the timeline: its colour, its type's mark, and what it was called.</summary>
    private void DrawEntry(DrawingContext context, Rect box, JournalRow row)
    {
        var colour = EntryColour(row);
        var face = Mix(colour, Colour(TokenKeys.Journal.EntryGround), Number(TokenKeys.Journal.EntryTint, 0.8));
        var chosen = _selected is { } s && s.ItemId == row.ItemId;
        var hovered = _hover is { } h && h.ItemId == row.ItemId;

        Fill(context, box, face);
        Fill(context, new Rect(box.X, box.Y, 3, box.Height), colour);
        Outline(context, box, chosen ? Colour(TokenKeys.Accent.Rest) : Colour(TokenKeys.Journal.EntryBorder), chosen || hovered ? 2 : 1);

        var glyph = new Rect(box.X + EntryPad + 3, box.Y + ((box.Height - EntryGlyph) / 2), EntryGlyph, EntryGlyph);
        Fill(context, glyph, colour);

        var room = box.Width - EntryLead - EntryTail;
        if (room < 12) return;
        var text = Ellipsize(row.Subject, room, TextSize);
        DrawAt(context, Ink(text, TextSize, Colour(TokenKeys.Journal.EntryText)), glyph.Right + EntryPad, box.Y + 13);
    }

    /// <summary>
    /// What colour an entry is drawn in: its category's, as a note's is, so one colour set runs
    /// across the modules — and the accent for an entry that carries none.
    /// </summary>
    private Color EntryColour(JournalRow row)
        => Colour(CategoryTokens.First(row.Categories) ?? TokenKeys.Accent.Rest);

    private void DrawHeading(DrawingContext context, Rect box, string type)
    {
        Fill(context, box, Colour(TokenKeys.List.GroupHeaderBackground));
        var edge = Colour(TokenKeys.List.Separator);
        Fill(context, new Rect(box.X, box.Y - 1, box.Width, 1), edge);
        Fill(context, new Rect(box.X, box.Bottom, box.Width, 1), edge);

        var ink = Colour(TokenKeys.List.GroupHeaderText);

        // The chevron the reference draws open, then the type this group holds.
        var pen = new Pen(Brush(ink), 1.3);
        var centre = new Point(box.X + 12, box.Y + (box.Height / 2));
        context.DrawLine(pen, new Point(centre.X - 4, centre.Y - 2), centre);
        context.DrawLine(pen, centre, new Point(centre.X + 4, centre.Y - 2));

        DrawAt(context, Ink("Entry Type: " + type, TextSize, ink, SemiBoldFace), box.X + 24, box.Y + 17);
    }

    private void DrawRow(DrawingContext context, Rect box, JournalRow row)
    {
        var chosen = _selected is { } s && s.ItemId == row.ItemId;
        Fill(context, box, Colour(chosen
            ? TokenKeys.List.RowSelected
            : _hover is { } h && h.ItemId == row.ItemId ? TokenKeys.List.RowHover : TokenKeys.List.RowBackground));

        var glyph = new Rect(box.X + 7, box.Y + 5, 11, 11);
        Fill(context, glyph, EntryColour(row));

        var ink = Colour(TokenKeys.List.ReadText);
        var dim = Colour(TokenKeys.List.PreviewText);

        var contactLeft = box.Width - ContactColumn;
        var durationLeft = contactLeft - DurationColumn;
        var startLeft = durationLeft - StartColumn;
        var room = Math.Max(40, startLeft - GlyphColumn - 8);

        DrawAt(context, Ink(Ellipsize(row.Subject, room, TextSize), TextSize, ink), box.X + GlyphColumn, box.Y + 15);
        DrawAt(context, Ink(row.StartText(Culture), TextSize, ink), box.X + startLeft, box.Y + 15);

        if (row.DurationText(Culture) is { Length: > 0 } duration)
        {
            DrawAt(context, Ink(Ellipsize(duration, DurationColumn - 8, TextSize), TextSize, ink), box.X + durationLeft, box.Y + 15);
        }

        if (row.Contacts is { Length: > 0 } contacts)
        {
            DrawAt(context, Ink(Ellipsize(contacts, ContactColumn - 8, TextSize), TextSize, dim), box.X + contactLeft, box.Y + 15);
        }

        Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), Colour(TokenKeys.List.Separator));
    }

    private void Outline(DrawingContext context, Rect box, Color colour, double weight = 1)
    {
        Fill(context, new Rect(box.X, box.Y, box.Width, weight), colour);
        Fill(context, new Rect(box.X, box.Bottom - weight, box.Width, weight), colour);
        Fill(context, new Rect(box.X, box.Y, weight, box.Height), colour);
        Fill(context, new Rect(box.Right - weight, box.Y, weight, box.Height), colour);
    }

    // ---- Input ---------------------------------------------------------------------------------

    private IEnumerable<(JournalRow Row, Rect Box)> Hits()
    {
        if (IsTimeline) return PlacedEntries();
        return PlacedLines().Where(p => p.Line.Row is not null).Select(p => (p.Line.Row!, p.Box));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        Focus();

        foreach (var (row, box) in Hits())
        {
            if (!box.Contains(point)) continue;

            Selected = row;
            EntrySelected?.Invoke(this, row);
            if (e.ClickCount >= 2) EntryActivated?.Invoke(this, row);
            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        JournalRow? over = null;
        foreach (var (row, box) in Hits())
        {
            if (!box.Contains(point)) continue;
            over = row;
            break;
        }

        if (over?.ItemId == _hover?.ItemId) return;
        _hover = over;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var moved = _scroll - ((int)e.Delta.Y * (IsTimeline ? 1 : 3));
        var clamped = Math.Clamp(moved, 0, Math.Max(0, LineCount() - 1));
        if (clamped == _scroll) return;
        _scroll = clamped;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0) return;

        var ordered = IsTimeline ? InSpan() : [.. _rows];
        var index = _selected is { } chosen ? ordered.FindIndex(r => r.ItemId == chosen.ItemId) : -1;

        switch (e.Key)
        {
            case Key.Down or Key.Right:
                Select(ordered, Math.Min(index + 1, ordered.Count - 1));
                break;
            case Key.Up or Key.Left:
                Select(ordered, Math.Max(index - 1, 0));
                break;
            case Key.Home:
                Select(ordered, 0);
                break;
            case Key.End:
                Select(ordered, ordered.Count - 1);
                break;
            case Key.Enter when _selected is { } open:
                EntryActivated?.Invoke(this, open);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Select(List<JournalRow> among, int index)
    {
        if (index < 0 || index >= among.Count) return;
        Selected = among[index];
        EntrySelected?.Invoke(this, among[index]);
    }

    /// <summary>Where an entry is drawn, which is what a harness pose presses.</summary>
    public Rect? BoxOf(long itemId)
    {
        foreach (var (row, box) in Hits())
        {
            if (row.ItemId == itemId) return box;
        }

        return null;
    }
}
