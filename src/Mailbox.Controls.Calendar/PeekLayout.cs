using Avalonia;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// Where every part of the calendar peek is drawn, in the two shapes the reference gives it.
/// </summary>
/// <remarks>
/// Its own class rather than numbers inside the view, because these are measurements and a
/// measurement can be asserted: the tests hold the reference's own pixels, and a layout that
/// drifts fails there rather than in a screenshot nobody compared.
/// <para>
/// Measured off the two captures at 100%. The floating popup is a fixed 286×330 of which
/// 274×320 is content, inside a 5px frame with a hairline round it; the docked pane is 254 wide
/// beside a 1px divider and as tall as the window leaves it. Both draw the same month block —
/// seven 28×24 cells under a 24px row of weekday letters and a 24px title row — and the same
/// agenda under it, and they differ in three things: the corner button is a dock glyph or a
/// close cross, the docked pane rules a line under its grid where the popup rules none, and the
/// docked pane holds everything 5px further in.
/// </para>
/// </remarks>
public sealed class PeekLayout
{
    // ---- The popup's own box -------------------------------------------------------------

    /// <summary>The hairline round the floating popup: measured black against the desktop.</summary>
    public const double Outline = 1;

    /// <summary>The light frame inside it, which the desktop draws wider than it is tall.</summary>
    public const double FrameX = 5;
    public const double FrameY = 4;

    /// <summary>The popup's content, which is a fixed size — its agenda scrolls rather than grows.</summary>
    public const double PopupWidth = 274;
    public const double PopupHeight = 320;

    /// <summary>The docked pane, and the line between it and what it sits beside.</summary>
    public const double DockedWidth = 254;
    public const double DividerWidth = 1;

    // ---- The month block -------------------------------------------------------------------

    public const double CellWidth = 28;
    public const double RowHeight = 24;
    public const double TitleRowHeight = 24;
    public const double WeekdayRowHeight = 24;
    public const int WeekRows = 6;

    /// <summary>The month name's baseline, from the top of its row.</summary>
    public const double TitleBaselineOffset = 20;

    /// <summary>A weekday letter's and a day number's baseline, from the top of their row.</summary>
    public const double CellBaselineOffset = 17;

    /// <summary>
    /// What the agenda's scrollbar is held clear of on the right, and so what the month block is
    /// centred inside. The two states measure differently and both are reproduced: the popup's
    /// block sits 31px in from a 274-wide content, the pane's 23px in from a 254-wide one.
    /// </summary>
    public const double PopupGutter = 17;
    public const double DockedGutter = 12;

    /// <summary>
    /// Where the arrows sit against the grid: the left one's point five pixels before its first
    /// column, the right one's a pixel past its last. Read off both captures, which agree.
    /// </summary>
    public const double ArrowBefore = 4;
    public const double ArrowAfter = 1;

    /// <summary>The arrows' own box, which is bigger than the glyph so it can be hit.</summary>
    public const double ArrowSize = 20;

    /// <summary>
    /// The arrows' centre, from the top of the title row: level with the month's name rather
    /// than with the row, which is two pixels lower.
    /// </summary>
    public const double ArrowCentre = 14;

    // ---- The agenda ------------------------------------------------------------------------

    /// <summary>How far the agenda is held off the content's left edge in each state.</summary>
    public const double PopupAgendaInset = 4;
    public const double DockedAgendaInset = 9;

    /// <summary>The gap under the grid before the docked pane's rule, and the rule itself.</summary>
    public const double RuleGap = 14;
    public const double RuleHeight = 2;

    /// <summary>The day name's baseline, from the bottom of the grid or the bottom of the rule.</summary>
    public const double HeadingAfterGrid = 25;
    public const double HeadingAfterRule = 22;

    /// <summary>The first entry's top, from that baseline.</summary>
    public const double HeadingToEntry = 9;

    /// <summary>An entry's own measurements: the time, the Show As bar, and the text beside it.</summary>
    public const double EntryTimeInset = 2;
    public const double EntryBarInset = 51;
    public const double EntryBarWidth = 6;
    public const double EntryTextInset = 66;

    /// <summary>
    /// What the bar keeps clear of the time when a time is wider than the column the reference
    /// measured — "10:00 AM" is wider than the "5:00 PM" the capture holds, and would otherwise
    /// run into it. The whole day's entries share one column, so their bars stay in line.
    /// </summary>
    public const double EntryTimeGap = 6;

    /// <summary>An entry's first baseline from its top, and the distance to its second.</summary>
    public const double EntryBaseline = 13;
    public const double EntryLineHeight = 16;

    /// <summary>What separates one entry from the next. Authored: the capture holds one entry.</summary>
    public const double EntryGap = 6;

    /// <summary>
    /// The corner button's width, and how far its right edge is held off the content's. The
    /// popup's is flush: the reference's dock glyph runs to the last pixel before the frame.
    /// </summary>
    public const double CornerSize = 16;
    public const double PopupCornerRight = 0;
    public const double DockedCornerRight = 4;

    /// <summary>The row the corner button sits in, which is what holds the title row down.</summary>
    public const double PopupCornerHeight = 18;
    public const double DockedCornerHeight = 24;

    // ---- Type ------------------------------------------------------------------------------

    /// <summary>The month's name: bigger than the grid and drawn semibold.</summary>
    public const double TitleSize = 14;

    /// <summary>The weekday letters and the day numbers.</summary>
    public const double CellSize = 13;

    /// <summary>Everything in the agenda.</summary>
    public const double AgendaSize = 12;

    private readonly double _weekNumberColumn;

    public PeekLayout(bool docked, double width, bool weekNumbers = false)
    {
        Docked = docked;
        Width = width;
        _weekNumberColumn = weekNumbers ? CellWidth : 0;

        var gutter = docked ? DockedGutter : PopupGutter;
        var columns = (7 * CellWidth) + _weekNumberColumn;
        // Away from zero, not to even: the popup's own half-pixel lands on 31 in the reference
        // and banker's rounding would put the whole grid a pixel left of it.
        var left = Math.Max(2, Math.Round((width - gutter - columns) / 2, MidpointRounding.AwayFromZero));

        AgendaLeft = docked ? DockedAgendaInset : PopupAgendaInset;
        AgendaWidth = Math.Max(0, width - gutter - AgendaLeft);

        var cornerHeight = docked ? DockedCornerHeight : PopupCornerHeight;
        var cornerRight = docked ? DockedCornerRight : PopupCornerRight;
        Corner = new Rect(width - cornerRight - CornerSize, 0, CornerSize, cornerHeight);

        TitleTop = cornerHeight;
        Grid = new Rect(left, TitleTop + TitleRowHeight, columns, WeekdayRowHeight + (WeekRows * RowHeight));

        // The arrows hang off the grid's own edges rather than off the centre — which is what
        // makes them land a pixel differently either side, and is what both captures show.
        TitleCentre = Grid.X + (Grid.Width / 2);
        var arrowTop = TitleTop + ArrowCentre - (ArrowSize / 2);
        Previous = new Rect(Grid.X - ArrowBefore - (ArrowSize / 2), arrowTop, ArrowSize, ArrowSize);
        Next = new Rect(Grid.Right + ArrowAfter - (ArrowSize / 2), arrowTop, ArrowSize, ArrowSize);

        Rule = docked
            ? new Rect(AgendaLeft, Grid.Bottom + RuleGap, Math.Max(0, width - AgendaLeft - 24), RuleHeight)
            : default;

        HeadingBaseline = docked
            ? Rule.Bottom + HeadingAfterRule
            : Grid.Bottom + HeadingAfterGrid;

        AgendaTop = HeadingBaseline + HeadingToEntry;
    }

    public bool Docked { get; }

    public double Width { get; }

    /// <summary>The dock glyph when floating, the close cross when docked.</summary>
    public Rect Corner { get; }

    public Rect Previous { get; }

    public Rect Next { get; }

    /// <summary>The top of the title row, which the corner button's row holds down.</summary>
    public double TitleTop { get; }

    /// <summary>The x the month's name is centred on: the grid's own centre, not the pane's.</summary>
    public double TitleCentre { get; }

    public double TitleBaseline => TitleTop + TitleBaselineOffset;

    /// <summary>The weekday letters and the six week rows together.</summary>
    public Rect Grid { get; }

    /// <summary>The week-number column's width, or zero when Options has not asked for one.</summary>
    public double WeekNumberColumn => _weekNumberColumn;

    /// <summary>The rule under the grid. Empty in the popup, which draws none.</summary>
    public Rect Rule { get; }

    public double HeadingBaseline { get; }

    public double AgendaLeft { get; }

    /// <summary>The first entry's top.</summary>
    public double AgendaTop { get; }

    /// <summary>How wide an entry may draw before the scroll gutter.</summary>
    public double AgendaWidth { get; }

    /// <summary>The weekday letters' row.</summary>
    public double WeekdayBaseline => Grid.Y + CellBaselineOffset;

    /// <summary>One day's cell: row 0 is the first week, column 0 the first weekday.</summary>
    public Rect DayCell(int row, int column) => new(
        Grid.X + _weekNumberColumn + (column * CellWidth),
        Grid.Y + WeekdayRowHeight + (row * RowHeight),
        CellWidth,
        RowHeight);

    /// <summary>The week-number cell down the left of a row.</summary>
    public Rect WeekCell(int row) => new(
        Grid.X,
        Grid.Y + WeekdayRowHeight + (row * RowHeight),
        _weekNumberColumn,
        RowHeight);

    /// <summary>Where a weekday letter is centred.</summary>
    public double WeekdayCentre(int column) => Grid.X + _weekNumberColumn + (column * CellWidth) + (CellWidth / 2);

    /// <summary>How tall an entry of one or two lines is drawn.</summary>
    public static double EntryHeight(int lines) => (Math.Max(1, lines) * EntryLineHeight) - 2;
}
