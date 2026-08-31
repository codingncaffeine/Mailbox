using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Controls.Common;
using Mailbox.Scheduling;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Journal;

/// <summary>
/// The Journal module's view: the three banded timelines the module opens in, and the headed
/// table its other three arrangements draw.
/// </summary>
/// <remarks>
/// <b>No capture of this module exists</b> — the reference hides it behind Ctrl+8 and stopped
/// developing it long ago — so everything here is authored from its shape: month bands over a run
/// of day columns, entries hung under the moment they started inside collapsible bands (one per
/// entry type, contact or category, which is what tells its three timeline views apart), and the
/// same entries as table rows under sortable, resizable column headers. The numbers borrowed are
/// the ones already measured elsewhere — a 26px heading and a 21px row.
/// <para>
/// <b>Divergence, stated:</b> the reference's timeline scrolls sideways through time. This one
/// fits its span to the width it is given — a day, a week or a month, whole — because a horizontal
/// scrollbar is the one thing a drawn view cannot borrow from the rest of the application, and a
/// span that always fits is what makes Back, Forward and Today mean something.
/// </para>
/// </remarks>
public sealed class JournalView : DrawnSurface, ISpokenRows
{
    /// <summary>Authored: the months over the days inside them.</summary>
    public const double SpanRowHeight = 22;
    public const double DayRowHeight = 22;
    public const double HeadingHeight = SpanRowHeight + DayRowHeight;

    /// <summary>Authored: one entry's box on the timeline, and the gap under it.</summary>
    public const double EntryHeight = 18;
    public const double LaneGap = 3;
    private const double EntryGlyph = 11;
    private const double EntryPad = 5;

    /// <summary>What the bar and the glyph take before the text, and the gap after it.</summary>
    private const double EntryLead = EntryPad + 3 + EntryGlyph + EntryPad;
    private const double EntryTail = EntryPad * 2;

    /// <summary>
    /// The most of a subject an entry's box will grow for — the reference's Format Timeline View
    /// names exactly this number ("Maximum label width: 80 characters").
    /// </summary>
    public const int LabelCap = 80;

    /// <summary>The margin the columns are drawn inside, left and right.</summary>
    public const double TimelineInset = 6;

    /// <summary>The same 26 and 21 the message list and the to-do list are measured at.</summary>
    public const double GroupHeight = 26;
    public const double RowHeight = 21;
    public const double HeaderHeight = 26;

    private const double TextSize = 12;

    /// <summary>The table's columns, in the reference's own order.</summary>
    /// <remarks>
    /// The attachment column is drawn because the reference's table has one; a journal entry
    /// here cannot carry an attachment yet, so its cells stay empty and the absence is queued
    /// where absences go.
    /// </remarks>
    private static readonly (string Key, string Label, double Width, bool Fixed)[] TableColumns =
    [
        ("icon", "", 26, true),
        ("attach", "", 22, true),
        ("type", "Entry Type", 110, false),
        ("subject", "Subject", 0, false),
        ("start", "Start", 130, false),
        ("duration", "Duration", 90, false),
        ("contact", "Contact", 140, false),
        ("company", "Company", 120, false),
        ("categories", "Categories", 110, false),
    ];

    private const double MinColumn = 44;
    private const double MinSubject = 60;

    private IReadOnlyList<JournalRow> _rows = [];
    private JournalRow? _selected;
    private JournalRow? _hover;
    private int _scroll;
    private readonly HashSet<string> _collapsed = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<string, double> _resized = new(StringComparer.Ordinal);
    private string _sortKey = "start";
    private bool _sortDescending = true;
    private string? _dragColumn;
    private double _dragStart;
    private double _dragWidth;
    private Typeface? _iconFace;

    public JournalView()
    {
        Focusable = true;
    }

    protected override void OnPaletteChanged() => _iconFace = null;

    private Typeface IconFace => _iconFace ??= new Typeface(IconFont.Family);

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
            SpokenRowsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Which of the six the Current View group has chosen.</summary>
    public JournalArrangement Arrangement { get; set; } = JournalArrangement.ByType;

    /// <summary>
    /// True while Instant Search owns the view: the rows are the matches, drawn as one flat
    /// table whatever arrangement is chosen, so the answer never depends on the span on show.
    /// </summary>
    public bool IsSearch { get; set; }

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
            SpokenSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// What the status bar counts: what the view is showing, which on the timeline is the span
    /// rather than everything the journal holds — and under a search is every match, wherever
    /// it falls.
    /// </summary>
    public int Count => IsTimeline ? InSpan().Count : _rows.Count;

    public event EventHandler<JournalRow>? EntrySelected;
    public event EventHandler<JournalRow>? EntryActivated;

    /// <summary>A month band on the upper scale was pressed — its drop-down wants opening.</summary>
    public event EventHandler<(DateOnly Month, Rect Band)>? MonthBandPressed;

    // ---- The span ------------------------------------------------------------------------------

    private bool IsTimeline => !IsSearch && JournalBook.IsTimeline(Arrangement);

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

    /// <summary>What the heading spans, written as one line — the status bar's own text.</summary>
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

    /// <summary>Vertical slots below the heading — a band heading, or one lane of one band.</summary>
    private int LineCount()
    {
        if (IsTimeline)
        {
            var count = 0;
            foreach (var band in Bands()) count += 1 + (band.Collapsed ? 0 : band.Lanes);
            return count;
        }

        return Lines().Count;
    }

    /// <summary>One band of the timeline: its heading, and its entries packed into lanes.</summary>
    private readonly record struct TimelineBand(
        string Label, bool Collapsed, int Lanes, IReadOnlyList<(JournalRow Row, double Left, double Width, int Lane)> Packed);

    /// <summary>
    /// The timeline's bands, in the arrangement's own order, each with its entries packed: the
    /// first lane whose last box ends before this one starts, which is what keeps two entries an
    /// hour apart on one line and pushes an overlapping pair onto two.
    /// </summary>
    private List<TimelineBand> Bands()
    {
        var bands = new List<TimelineBand>();

        foreach (var (label, rows) in JournalBook.Grouped(InSpan(), Arrangement))
        {
            var collapsed = _collapsed.Contains(label);
            var packed = new List<(JournalRow Row, double Left, double Width, int Lane)>();
            var laneEnds = new List<double>();

            // Oldest first along the line, whatever order the rows arrived in: a timeline reads
            // left to right and packing right to left would fill the lanes backwards.
            foreach (var row in rows.OrderBy(r => r.Start))
            {
                var left = X(row.Start);
                var width = EntryWidth(row, left);

                var lane = 0;
                while (lane < laneEnds.Count && laneEnds[lane] > left) lane++;
                if (lane == laneEnds.Count) laneEnds.Add(0);
                laneEnds[lane] = left + width + 4;

                packed.Add((row, left, width, lane));
            }

            bands.Add(new TimelineBand(label, collapsed, laneEnds.Count, packed));
        }

        return bands;
    }

    /// <summary>
    /// How wide an entry's box is: its duration, or its label capped at the reference's own
    /// eighty characters — and at the month scale the glyph alone, which is the reference's
    /// "Show label when viewing by month" left off.
    /// </summary>
    private double EntryWidth(JournalRow row, double left)
    {
        var span = row.Duration is { } duration && duration > TimeSpan.Zero
            ? X(row.Start + duration) - left
            : 0;

        var least = EntryLead + EntryPad;
        var width = Scale == TimelineScale.Month
            ? Math.Max(span, least)
            : Math.Max(Math.Max(span, Measure(Label(row), TextSize) + EntryLead + EntryTail), 40);

        return Math.Min(width, Math.Max(least, Bounds.Width - TimelineInset - left));
    }

    private static string Label(JournalRow row)
        => row.Subject.Length > LabelCap ? row.Subject[..LabelCap] : row.Subject;

    /// <summary>A heading or an entry: the table is one run of both, which is what scrolls.</summary>
    private readonly record struct ListLine(string Label, JournalRow? Row)
    {
        public bool IsHeading => Row is null;
    }

    private List<ListLine> Lines()
    {
        var lines = new List<ListLine>();
        var ordered = Sorted(_rows);

        // A search's answer is one flat run of matches: grouping it by company would make the
        // count depend on how the reader files things rather than on what matched.
        if (IsSearch)
        {
            lines.AddRange(ordered.Select(r => new ListLine(string.Empty, r)));
            return lines;
        }

        foreach (var (label, rows) in JournalBook.Grouped(ordered, Arrangement))
        {
            lines.Add(new ListLine(label, null));
            if (_collapsed.Contains(label)) continue;
            foreach (var row in rows) lines.Add(new ListLine(label, row));
        }

        return lines;
    }

    /// <summary>The rows in the table's own order, which is whichever column heading was pressed.</summary>
    private List<JournalRow> Sorted(IEnumerable<JournalRow> rows)
    {
        IEnumerable<JournalRow> ordered = _sortKey switch
        {
            "type" => rows.OrderBy(r => r.EntryType, StringComparer.CurrentCultureIgnoreCase),
            "subject" => rows.OrderBy(r => r.Subject, StringComparer.CurrentCultureIgnoreCase),
            "duration" => rows.OrderBy(r => r.Duration ?? TimeSpan.Zero),
            "contact" => rows.OrderBy(r => r.Contacts, StringComparer.CurrentCultureIgnoreCase),
            "company" => rows.OrderBy(r => r.Company, StringComparer.CurrentCultureIgnoreCase),
            "categories" => rows.OrderBy(r => string.Join(",", r.Categories), StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderBy(r => r.Start),
        };

        return _sortDescending ? [.. ordered.Reverse()] : [.. ordered];
    }

    private IEnumerable<(JournalRow Row, Rect Box, int Slot)> PlacedEntries()
    {
        var height = Bounds.Height > 0 ? Bounds.Height : double.MaxValue;
        var slot = 0;

        foreach (var band in Bands())
        {
            slot++; // The band's own heading.
            if (band.Collapsed) continue;

            foreach (var (row, left, width, lane) in band.Packed)
            {
                var y = SlotTop(slot + lane);
                if (y < HeadingHeight || y >= height) continue;
                yield return (row, new Rect(left, y + 2, width, EntryHeight), slot + lane);
            }

            slot += band.Lanes;
        }
    }

    /// <summary>Where a slot starts, with the scroll taken off: headings and lanes share the run.</summary>
    private double SlotTop(int slot) => HeadingHeight + SlotHeights().Take(slot).Sum() - ScrolledPast();

    private double ScrolledPast() => SlotHeights().Take(_scroll).Sum();

    /// <summary>Each slot's height, headings taller than lanes, in display order.</summary>
    private List<double> SlotHeights()
    {
        var heights = new List<double>();
        foreach (var band in Bands())
        {
            heights.Add(GroupHeight);
            if (band.Collapsed) continue;
            for (var lane = 0; lane < band.Lanes; lane++) heights.Add(EntryHeight + LaneGap + 2);
        }

        return heights;
    }

    private IEnumerable<(ListLine Line, Rect Box)> PlacedLines()
    {
        var width = Bounds.Width;
        var height = Bounds.Height > 0 ? Bounds.Height : double.MaxValue;
        var y = HeaderHeight;
        var lines = Lines();

        for (var i = _scroll; i < lines.Count && y < height; i++)
        {
            var line = lines[i];
            var box = new Rect(0, y, width, line.IsHeading ? GroupHeight : RowHeight);
            yield return (line, box);
            y = box.Bottom;
        }
    }

    // ---- The table's columns -------------------------------------------------------------------

    /// <summary>
    /// Every column with the width it gets at this view width: resized widths first, then the
    /// authored ones, the subject taking what is left — and when even that is not enough, the
    /// giving columns shrink together rather than the subject alone collapsing.
    /// </summary>
    private List<(string Key, string Label, double Left, double Width)> Placed()
    {
        var width = Math.Max(Bounds.Width, 120);
        var fixedSum = 0d;
        var giving = new List<(string Key, double Width)>();

        foreach (var (key, _, authored, isFixed) in TableColumns)
        {
            var chosen = _resized.TryGetValue(key, out var resized) ? resized : authored;
            if (key == "subject") continue;
            if (isFixed) fixedSum += chosen;
            else giving.Add((key, chosen));
        }

        var subject = width - fixedSum - giving.Sum(g => g.Width);
        var squeeze = 1d;
        if (subject < MinSubject)
        {
            // Shrink the giving columns toward their floor so the subject keeps its least.
            var have = giving.Sum(g => g.Width);
            var need = have - (MinSubject - subject);
            squeeze = have > 0 ? Math.Max(need / have, MinColumn / giving.Max(g => g.Width)) : 1;
            subject = Math.Max(MinSubject, width - fixedSum - giving.Sum(g => Math.Max(MinColumn, g.Width * squeeze)));
        }

        var placed = new List<(string, string, double, double)>();
        var x = 0d;
        foreach (var (key, label, authored, isFixed) in TableColumns)
        {
            var chosen = _resized.TryGetValue(key, out var resized) ? resized : authored;
            var final = key == "subject" ? subject
                : isFixed ? chosen
                : Math.Max(MinColumn, chosen * squeeze);
            placed.Add((key, label, x, final));
            x += final;
        }

        return placed;
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

        DrawTableHeader(context, width);

        if (_rows.Count == 0)
        {
            DrawEmpty(context, width, HeaderHeight + 40);
            return;
        }

        foreach (var (line, box) in PlacedLines())
        {
            if (line.IsHeading) DrawHeading(context, box, line.Label, _collapsed.Contains(line.Label));
            else DrawRow(context, box, line.Row!);
        }
    }

    private void DrawEmpty(DrawingContext context, double width, double baseline)
    {
        var line = Ink(IsSearch ? "We couldn't find what you were looking for." : "There are no journal entries to show.",
            TextSize, Colour(TokenKeys.List.PreviewText));
        DrawAt(context, line, Math.Round((width - line.Width) / 2), baseline);
    }

    /// <summary>The month bands, the columns under them, and the arrangement's bands of entries.</summary>
    private void DrawTimeline(DrawingContext context, double width, double height)
    {
        var headerInk = Colour(TokenKeys.Journal.HeaderText);
        Fill(context, new Rect(0, 0, width, HeadingHeight), Colour(TokenKeys.Journal.HeaderBackground));

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

        // The upper scale: one band per month the span crosses, positioned over the days that
        // belong to it, each carrying the drop-down that jumps the timeline.
        foreach (var (label, _, band) in MonthBands())
        {
            var text = Ink(label, 12, headerInk, SemiBoldFace);
            if (text.Width + 22 <= band.Width)
            {
                DrawAt(context, text, band.X + 6, 16);
                var chevron = Ink(IconGlyphs.GetOrEmpty("chevron-down", 16), 9, headerInk, IconFace);
                DrawAt(context, chevron, band.X + 6 + text.Width + 6, 15);
            }

            if (band.X > TimelineInset + 1)
            {
                Fill(context, new Rect(Math.Round(band.X) - 3, 3, 1, SpanRowHeight - 6), Colour(TokenKeys.Journal.HeaderLine));
            }
        }

        Fill(context, new Rect(0, SpanRowHeight, width, 1), Colour(TokenKeys.Journal.HeaderLine));
        Fill(context, new Rect(0, HeadingHeight - 1, width, 1), Colour(TokenKeys.Journal.HeaderLine));

        if (InSpan().Count == 0)
        {
            var empty = Ink("Nothing was recorded in this period.", TextSize, Colour(TokenKeys.List.PreviewText));
            DrawAt(context, empty, Math.Round((width - empty.Width) / 2), HeadingHeight + 40);
            return;
        }

        // The bands and their entries, drawn heading-then-lanes down the view.
        var slot = 0;
        foreach (var band in Bands())
        {
            var y = SlotTop(slot);
            if (y >= HeadingHeight - GroupHeight && y < height)
            {
                DrawHeading(context, new Rect(0, Math.Max(HeadingHeight, y), width, GroupHeight), band.Label, band.Collapsed);
            }

            slot += 1 + (band.Collapsed ? 0 : band.Lanes);
        }

        foreach (var (row, box, _) in PlacedEntries()) DrawEntry(context, box, row);
    }

    /// <summary>The upper scale's bands: each month's name over the days that are its own.</summary>
    private List<(string Label, DateOnly Month, Rect Box)> MonthBands()
    {
        var bands = new List<(string, DateOnly, Rect)>();

        if (Scale == TimelineScale.Day)
        {
            bands.Add((SpanText(), new DateOnly(SpanStart.Year, SpanStart.Month, 1),
                new Rect(TimelineInset, 0, Bounds.Width - (TimelineInset * 2), SpanRowHeight)));
            return bands;
        }

        var from = 0;
        while (from < ColumnCount)
        {
            var day = SpanStart.AddDays(from);
            var month = new DateOnly(day.Year, day.Month, 1);
            var to = from;
            while (to + 1 < ColumnCount && SpanStart.AddDays(to + 1).Month == day.Month) to++;

            var left = TimelineInset + (from * ColumnWidth);
            var width = ((to - from) + 1) * ColumnWidth;
            bands.Add((month.ToString("MMMM yyyy", Culture), month, new Rect(left, 0, width, SpanRowHeight)));
            from = to + 1;
        }

        return bands;
    }

    /// <summary>A day's heading: as much of its name as the column has room for.</summary>
    private string ColumnLabel(DateOnly day, double columnWidth)
    {
        var full = day.ToString("ddd d", Culture);
        if (Measure(full, 11) + 8 <= columnWidth) return full;
        return day.Day.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>One entry on the timeline: its colour, its type's own glyph, and what it was called.</summary>
    private void DrawEntry(DrawingContext context, Rect box, JournalRow row)
    {
        var colour = EntryColour(row);
        var face = Mix(colour, Colour(TokenKeys.Journal.EntryGround), Number(TokenKeys.Journal.EntryTint, 0.8));
        var chosen = _selected is { } s && s.ItemId == row.ItemId;
        var hovered = _hover is { } h && h.ItemId == row.ItemId;

        Fill(context, box, face);
        Fill(context, new Rect(box.X, box.Y, 3, box.Height), colour);
        Outline(context, box, chosen ? Colour(TokenKeys.Accent.Rest) : Colour(TokenKeys.Journal.EntryBorder), chosen || hovered ? 2 : 1);

        DrawTypeGlyph(context, row, box.X + EntryPad + 3, box.Y + (box.Height / 2), Colour(TokenKeys.Journal.EntryText));

        if (Scale == TimelineScale.Month) return;
        var room = box.Width - EntryLead - EntryTail;
        if (room < 12) return;
        var text = Ellipsize(Label(row), room, TextSize);
        DrawAt(context, Ink(text, TextSize, Colour(TokenKeys.Journal.EntryText)), box.X + EntryLead, box.Y + 13);
    }

    /// <summary>The entry type's glyph, centred on the line it sits in.</summary>
    private void DrawTypeGlyph(DrawingContext context, JournalRow row, double left, double middle, Color ink)
    {
        var glyph = IconGlyphs.GetOrEmpty(JournalBook.IconName(row.EntryType), 16);
        if (glyph.Length == 0) return;
        var text = Ink(glyph, EntryGlyph + 1, ink, IconFace);
        context.DrawText(text, new Point(left, middle - (text.Height / 2)));
    }

    /// <summary>
    /// What colour an entry is drawn in: its category's, as a note's is, so one colour set runs
    /// across the modules — and the accent for an entry that carries none.
    /// </summary>
    private Color EntryColour(JournalRow row)
        => Colour(CategoryTokens.First(row.Categories) ?? TokenKeys.Accent.Rest);

    private void DrawHeading(DrawingContext context, Rect box, string label, bool collapsed)
    {
        Fill(context, box, Colour(TokenKeys.List.GroupHeaderBackground));
        var edge = Colour(TokenKeys.List.Separator);
        Fill(context, new Rect(box.X, box.Y - 1, box.Width, 1), edge);
        Fill(context, new Rect(box.X, box.Bottom, box.Width, 1), edge);

        var ink = Colour(TokenKeys.List.GroupHeaderText);

        // The chevron the reference draws open, folded to a right-pointer while the band is shut.
        var pen = new Pen(Brush(ink), 1.3);
        var centre = new Point(box.X + 12, box.Y + (box.Height / 2));
        if (collapsed)
        {
            context.DrawLine(pen, new Point(centre.X - 2, centre.Y - 4), new Point(centre.X + 2, centre.Y));
            context.DrawLine(pen, new Point(centre.X + 2, centre.Y), new Point(centre.X - 2, centre.Y + 4));
        }
        else
        {
            context.DrawLine(pen, new Point(centre.X - 4, centre.Y - 2), centre);
            context.DrawLine(pen, centre, new Point(centre.X + 4, centre.Y - 2));
        }

        DrawAt(context, Ink(label, TextSize, ink, SemiBoldFace), box.X + 24, box.Y + 17);
    }

    /// <summary>The table's header row: every column named, the sorted one marked.</summary>
    private void DrawTableHeader(DrawingContext context, double width)
    {
        var box = new Rect(0, 0, width, HeaderHeight);
        Fill(context, box, Colour(TokenKeys.List.HeaderBackground));
        Fill(context, new Rect(0, HeaderHeight - 1, width, 1), Colour(TokenKeys.List.Separator));

        var ink = Colour(TokenKeys.List.HeaderText);

        foreach (var (key, label, left, columnWidth) in Placed())
        {
            // The named columns get a divider on their right edge, which is also the resize grip.
            Fill(context, new Rect(Math.Round(left + columnWidth) - 1, 5, 1, HeaderHeight - 10), Colour(TokenKeys.List.Separator));

            if (key == "icon")
            {
                DrawTypeGlyphNamed(context, "journal-entry", left + 7, HeaderHeight / 2, ink);
                continue;
            }

            if (key == "attach")
            {
                DrawTypeGlyphNamed(context, "attach", left + 4, HeaderHeight / 2, ink);
                continue;
            }

            var text = Ink(Ellipsize(label, columnWidth - 22, TextSize), TextSize, ink, SemiBoldFace);
            DrawAt(context, text, left + 7, 17);

            if (key == _sortKey && columnWidth > text.Width + 26)
            {
                DrawSortMark(context, left + 12 + text.Width, HeaderHeight / 2, ink, _sortDescending);
            }
        }
    }

    private void DrawTypeGlyphNamed(DrawingContext context, string name, double left, double middle, Color ink)
    {
        var glyph = IconGlyphs.GetOrEmpty(name, 16);
        if (glyph.Length == 0) return;
        var text = Ink(glyph, 12, ink, IconFace);
        context.DrawText(text, new Point(left, middle - (text.Height / 2)));
    }

    /// <summary>The solid little triangle the sorted column carries, up for ascending.</summary>
    private void DrawSortMark(DrawingContext context, double left, double middle, Color ink, bool descending)
    {
        var geometry = new StreamGeometry();
        using (var open = geometry.Open())
        {
            if (descending)
            {
                open.BeginFigure(new Point(left, middle - 2.5), true);
                open.LineTo(new Point(left + 7, middle - 2.5));
                open.LineTo(new Point(left + 3.5, middle + 2.5));
            }
            else
            {
                open.BeginFigure(new Point(left, middle + 2.5), true);
                open.LineTo(new Point(left + 7, middle + 2.5));
                open.LineTo(new Point(left + 3.5, middle - 2.5));
            }

            open.EndFigure(true);
        }

        context.DrawGeometry(Brush(ink), null, geometry);
    }

    private void DrawRow(DrawingContext context, Rect box, JournalRow row)
    {
        var chosen = _selected is { } s && s.ItemId == row.ItemId;
        Fill(context, box, Colour(chosen
            ? TokenKeys.List.RowSelected
            : _hover is { } h && h.ItemId == row.ItemId ? TokenKeys.List.RowHover : TokenKeys.List.RowBackground));

        var ink = Colour(TokenKeys.List.ReadText);
        var dim = Colour(TokenKeys.List.PreviewText);
        var middle = box.Y + (box.Height / 2);

        foreach (var (key, _, left, columnWidth) in Placed())
        {
            var room = columnWidth - 12;
            if (room < 8 && key != "icon") continue;

            switch (key)
            {
                case "icon":
                    DrawTypeGlyph(context, row, box.X + left + 7, middle, ink);
                    break;
                case "attach":
                    break;
                case "type":
                    DrawAt(context, Ink(Ellipsize(row.EntryType, room, TextSize), TextSize, ink), box.X + left + 7, box.Y + 15);
                    break;
                case "subject":
                    DrawAt(context, Ink(Ellipsize(row.Subject, room, TextSize), TextSize, ink), box.X + left + 7, box.Y + 15);
                    break;
                case "start":
                    DrawAt(context, Ink(Ellipsize(row.StartText(Culture), room, TextSize), TextSize, ink), box.X + left + 7, box.Y + 15);
                    break;
                case "duration" when row.DurationText(Culture) is { Length: > 0 } duration:
                    DrawAt(context, Ink(Ellipsize(duration, room, TextSize), TextSize, ink), box.X + left + 7, box.Y + 15);
                    break;
                case "contact" when row.Contacts is { Length: > 0 } contacts:
                    DrawAt(context, Ink(Ellipsize(contacts, room, TextSize), TextSize, dim), box.X + left + 7, box.Y + 15);
                    break;
                case "company" when row.Company.Length > 0:
                    DrawAt(context, Ink(Ellipsize(row.Company, room, TextSize), TextSize, dim), box.X + left + 7, box.Y + 15);
                    break;
                case "categories" when row.Categories.Count > 0:
                    DrawAt(context, Ink(Ellipsize(string.Join(", ", row.Categories), room, TextSize), TextSize, dim), box.X + left + 7, box.Y + 15);
                    break;
            }
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
        if (IsTimeline) return PlacedEntries().Select(p => (p.Row, p.Box));
        return PlacedLines().Where(p => p.Line.Row is not null).Select(p => (p.Line.Row!, p.Box));
    }

    /// <summary>The band headings as drawn, so a press can fold one.</summary>
    private IEnumerable<(string Label, Rect Box)> HeadingHits()
    {
        if (IsSearch) yield break;

        if (IsTimeline)
        {
            var slot = 0;
            foreach (var band in Bands())
            {
                var y = SlotTop(slot);
                if (y >= HeadingHeight - 1 && y < Bounds.Height) yield return (band.Label, new Rect(0, y, Bounds.Width, GroupHeight));
                slot += 1 + (band.Collapsed ? 0 : band.Lanes);
            }

            yield break;
        }

        foreach (var (line, box) in PlacedLines())
        {
            if (line.IsHeading) yield return (line.Label, box);
        }
    }

    /// <summary>The divider at a header column's right edge, give or take three pixels.</summary>
    private string? DividerAt(Point point)
    {
        if (IsTimeline || point.Y > HeaderHeight) return null;
        foreach (var (key, _, left, width) in Placed())
        {
            if (Math.Abs(point.X - (left + width)) <= 3) return key;
        }

        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        Focus();

        // A grab on a header divider starts a resize; a press on the header sorts its column.
        if (!IsTimeline && point.Y <= HeaderHeight)
        {
            if (DividerAt(point) is { } grip)
            {
                _dragColumn = grip;
                _dragStart = point.X;
                _dragWidth = Placed().First(c => c.Key == grip).Width;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            foreach (var (key, _, left, width) in Placed())
            {
                if (point.X < left || point.X >= left + width || key is "icon" or "attach") continue;
                if (_sortKey == key) _sortDescending = !_sortDescending;
                else
                {
                    _sortKey = key;
                    _sortDescending = key == "start";
                }

                InvalidateVisual();
                e.Handled = true;
                return;
            }

            return;
        }

        // A month band's drop-down, on the timeline's upper scale.
        if (IsTimeline && point.Y < SpanRowHeight)
        {
            foreach (var (_, month, band) in MonthBands())
            {
                if (!band.Contains(point)) continue;
                MonthBandPressed?.Invoke(this, (month, band));
                e.Handled = true;
                return;
            }

            return;
        }

        foreach (var (label, box) in HeadingHits())
        {
            if (!box.Contains(point)) continue;
            if (!_collapsed.Remove(label)) _collapsed.Add(label);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, LineCount() - 1));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

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

        if (_dragColumn is { } dragging)
        {
            _resized[dragging] = Math.Max(MinColumn, _dragWidth + (point.X - _dragStart));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        Cursor = DividerAt(point) is not null ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;

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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragColumn is null) return;
        _dragColumn = null;
        e.Pointer.Capture(null);
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

        var ordered = IsTimeline ? InSpan() : Sorted(_rows);
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

    // ---- What a reader can only measure --------------------------------------------------------
    //
    // A drawn view has no children to walk, so the axis, the bands and where an entry ended up are
    // invisible to everything but the eye — and an eye reading a picture cannot say whether a box
    // is as wide as the time it stands for or merely as wide as its own words. These hand the
    // same numbers Render draws with to whoever asks.

    /// <summary>Where each column of the timeline falls, what it is labelled, and which is today.</summary>
    public IReadOnlyList<(int Index, string Label, DateOnly Day, double Left, double Width, bool IsToday)> Columns()
    {
        var columns = new List<(int, string, DateOnly, double, double, bool)>();
        if (!IsTimeline) return columns;

        var columnWidth = ColumnWidth;
        for (var i = 0; i < ColumnCount; i++)
        {
            var day = Scale == TimelineScale.Day ? SpanStart : SpanStart.AddDays(i);
            var label = Scale == TimelineScale.Day
                ? SpanStart.ToDateTime(new TimeOnly(i, 0)).ToString("%h tt", Culture)
                : ColumnLabel(day, columnWidth);

            columns.Add((i, label, day, TimelineInset + (i * columnWidth), columnWidth,
                Scale != TimelineScale.Day && day == Today));
        }

        return columns;
    }

    /// <summary>The upper scale's month bands as drawn: what each says and the days it covers.</summary>
    public IReadOnlyList<(string Label, DateOnly Month, Rect Box)> ScaleBands() => MonthBands();

    /// <summary>Every entry the span holds, with the band, the lane and the width the packer gave it.</summary>
    public IReadOnlyList<(JournalRow Row, double Left, double Width, int Lane)> Laid()
        => [.. Bands().SelectMany(b => b.Packed)];

    /// <summary>The timeline's bands: each heading, whether it is folded, and how many lanes it holds.</summary>
    public IReadOnlyList<(string Label, bool Collapsed, int Lanes, int Entries)> BandsLaid()
        => [.. Bands().Select(b => (b.Label, b.Collapsed, b.Lanes, b.Packed.Count))];

    /// <summary>The rows actually drawn, with their boxes — the timeline's, or the table's.</summary>
    public IReadOnlyList<(JournalRow Row, Rect Box)> DrawnRows() => [.. Hits()];

    /// <summary>The table's lines as drawn: a heading carries no row.</summary>
    public IReadOnlyList<(string Label, JournalRow? Row, Rect Box)> DrawnLines()
        => [.. PlacedLines().Select(p => (p.Line.Label, p.Line.Row, p.Box))];

    /// <summary>The table's header as drawn: each column's place, and the sort on it.</summary>
    public IReadOnlyList<(string Key, string Label, double Left, double Width, string Sort)> HeaderLaid()
        => [.. Placed().Select(c => (c.Key, c.Label, c.Left, c.Width,
            c.Key == _sortKey ? (_sortDescending ? "descending" : "ascending") : string.Empty))];

    /// <summary>Folds or opens a band by its heading, which is what a pose presses.</summary>
    public bool ToggleBand(string label)
    {
        if (!_collapsed.Remove(label)) _collapsed.Add(label);
        InvalidateVisual();
        return _collapsed.Contains(label);
    }

    /// <summary>Sorts the table by a column, as pressing its heading does.</summary>
    public void SortBy(string key, bool descending)
    {
        _sortKey = key;
        _sortDescending = descending;
        InvalidateVisual();
    }

    /// <summary>Where a moment lands across the view, which is where an entry is hung.</summary>
    public double XOf(DateTime moment) => X(moment);

    // ---- The timeline, spoken for ----------------------------------------------------------

    /// <summary>The entries in the order the arrow keys walk them.</summary>
    private List<JournalRow> SpokenOrder() => IsTimeline ? InSpan() : Sorted(_rows);

    public event EventHandler? SpokenRowsChanged;

    public event EventHandler? SpokenSelectionChanged;

    int ISpokenRows.SpokenCount => SpokenOrder().Count;

    string ISpokenRows.SpokenRow(int index)
    {
        var row = SpokenOrder()[index];
        var said = new System.Text.StringBuilder();
        said.Append(row.EntryType).Append(". ").Append(row.Subject).Append(". ")
            .Append(row.StartText(Culture)).Append('.');
        if (row.DurationText(Culture) is { Length: > 0 } duration) said.Append(' ').Append(duration).Append('.');
        if (row.Contacts is { Length: > 0 } contacts) said.Append(" With ").Append(contacts).Append('.');
        return said.ToString();
    }

    int ISpokenRows.SpokenSelectedIndex
        => _selected is { } chosen ? SpokenOrder().FindIndex(r => r.ItemId == chosen.ItemId) : -1;

    void ISpokenRows.SpokenSelect(int index)
    {
        var row = SpokenOrder()[index];
        Selected = row;
        EntrySelected?.Invoke(this, row);
    }

    Rect? ISpokenRows.SpokenRowBounds(int index)
    {
        var order = SpokenOrder();
        if (index < 0 || index >= order.Count) return null;
        return BoxOf(order[index].ItemId);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpokenRowsPeer(this);
}
