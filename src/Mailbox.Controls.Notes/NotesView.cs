using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Controls.Common;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Notes;

/// <summary>
/// The Notes module's view: the wall of squares the module opens in, and the rows its other two
/// arrangements draw.
/// </summary>
/// <remarks>
/// <b>No capture of this module exists</b>, so everything here is authored from the reference's
/// shape rather than measured: a square with a folded corner, its first line written under it,
/// and the same notes as rows with what they say beside them. The numbers that <em>are</em>
/// borrowed are the ones the rest of the application already measured — a 21px row and a 26px
/// heading, the same the message list and the to-do list draw — so a capture of this module beside
/// the others will not have the wrong rhythm even where it has the wrong picture.
/// <para>
/// A note's colour is the colour of the category on it (<see cref="NoteColours"/>), tinted toward
/// <c>notes.ground</c> exactly as a calendar chip is tinted toward its own: the colour is data and
/// everything drawn over it comes from the theme.
/// </para>
/// </remarks>
public sealed class NotesView : DrawnSurface
{
    /// <summary>Authored: one note's cell on the wall, and the square inside it.</summary>
    public const double CellWidth = 106;
    public const double CellHeight = 104;
    private const double SquareSize = 64;
    private const double SquareTop = 8;
    private const double FoldSize = 14;
    private const double WallInset = 10;
    private const double CaptionSize = 11;
    private const double CaptionLine = 14;

    /// <summary>The same 26 and 21 the message list and the to-do list are measured at.</summary>
    public const double HeaderHeight = 26;
    public const double RowHeight = 21;

    private const double GlyphColumn = 26;
    private const double MadeColumn = 130;
    private const double CategoryColumn = 120;
    private const double TextSize = 12;

    private IReadOnlyList<NoteRow> _rows = [];
    private NoteRow? _selected;
    private NoteRow? _hover;
    private int _scroll;
    private string _sortKey = "created";
    private bool _sortDescending = true;

    public NotesView()
    {
        Focusable = true;
    }

    /// <summary>The notes on show, newest first.</summary>
    public IReadOnlyList<NoteRow> Rows
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

    /// <summary>Which of the three the Current View group has chosen.</summary>
    public NoteArrangement Arrangement { get; set; } = NoteArrangement.Icons;

    /// <summary>Today, as the module believes it — what decides whether a date is written as a time.</summary>
    public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    public NoteRow? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            InvalidateVisual();
        }
    }

    /// <summary>What the status bar counts.</summary>
    public int Count => _rows.Count;

    public event EventHandler<NoteRow>? NoteSelected;
    public event EventHandler<NoteRow>? NoteActivated;

    /// <summary>A double click on the wall itself, which is how the reference makes a note.</summary>
    public event EventHandler? NewNoteRequested;

    // ---- Where everything goes -----------------------------------------------------------------

    private bool IsWall => Arrangement == NoteArrangement.Icons;

    /// <summary>How many notes fit across the wall, which is what the width decides.</summary>
    private int Columns()
        => Math.Max(1, (int)Math.Floor(Math.Max(0, Bounds.Width - (WallInset * 2)) / CellWidth));

    /// <summary>Rows of cells on the wall, or notes in the list — whichever is scrolling.</summary>
    private int LineCount()
        => IsWall ? (int)Math.Ceiling(_rows.Count / (double)Columns()) : _rows.Count;

    /// <summary>
    /// Where every note is drawn, from the first visible line down.
    /// </summary>
    /// <remarks>
    /// Computed rather than remembered from the last render, so what a pose presses is answerable
    /// before a frame has been painted — the to-do list learned this first.
    /// </remarks>
    private IEnumerable<(NoteRow Row, Rect Box)> Placed()
    {
        var height = Bounds.Height > 0 ? Bounds.Height : double.MaxValue;

        if (IsWall)
        {
            var columns = Columns();
            for (var i = _scroll * columns; i < _rows.Count; i++)
            {
                var line = (i / columns) - _scroll;
                var y = WallInset + (line * CellHeight);
                if (y >= height) yield break;
                var x = WallInset + ((i % columns) * CellWidth);
                yield return (_rows[i], new Rect(x, y, CellWidth, CellHeight));
            }

            yield break;
        }

        var top = HeaderHeight;
        var ordered = Ordered();
        for (var i = _scroll; i < ordered.Count; i++)
        {
            var y = top + ((i - _scroll) * RowHeight);
            if (y >= height) yield break;
            yield return (ordered[i], new Rect(0, y, Bounds.Width, RowHeight));
        }
    }

    /// <summary>The rows in the list's own order, which is whichever heading was pressed.</summary>
    private List<NoteRow> Ordered()
    {
        IEnumerable<NoteRow> ordered = _sortKey switch
        {
            "subject" => _rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase),
            "categories" => _rows.OrderBy(r => string.Join(",", r.Categories), StringComparer.CurrentCultureIgnoreCase),
            // The rows arrive newest first, which is created-descending already.
            _ => _rows.Reverse(),
        };

        return _sortDescending ? [.. ordered.Reverse()] : [.. ordered];
    }

    /// <summary>The square inside a wall cell — what the fold and the frame are drawn on.</summary>
    private static Rect Square(Rect cell)
        => new(cell.X + ((cell.Width - SquareSize) / 2), cell.Y + SquareTop, SquareSize, SquareSize);

    // ---- Render --------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 60 || height < 40) return;

        // The wall is content and the rows are a list, so the two grounds are two tokens: in Dark
        // Gray the wall stays light where the list's own ground is the dark pane below its rows.
        Fill(context, new Rect(0, 0, width, height), Colour(IsWall ? TokenKeys.Notes.Background : TokenKeys.List.Background));

        if (IsWall)
        {
            if (_rows.Count == 0)
            {
                DrawEmpty(context, width);
                return;
            }

            foreach (var (row, cell) in Placed()) DrawCell(context, cell, row);
            return;
        }

        DrawColumnHeadings(context, width);
        if (_rows.Count == 0)
        {
            DrawEmpty(context, width);
            return;
        }

        foreach (var (row, box) in Placed()) DrawRow(context, box, row);
    }

    /// <summary>
    /// What the reference writes on an empty wall — its own two lines, the second naming the
    /// double click this view really answers (the People list draws the same first line).
    /// </summary>
    private void DrawEmpty(DrawingContext context, double width)
    {
        var ink = Colour(TokenKeys.List.PreviewText);
        var baseline = IsWall ? 60 : HeaderHeight + 40;
        var first = Ink("We didn't find anything to show here.", TextSize, ink);
        DrawAt(context, first, Math.Round((width - first.Width) / 2), baseline);
        var second = Ink("Double-click here to create a new Note.", TextSize, ink);
        DrawAt(context, second, Math.Round((width - second.Width) / 2), baseline + 20);
    }

    /// <summary>One note on the wall: the square, its fold, and its first line under it.</summary>
    private void DrawCell(DrawingContext context, Rect cell, NoteRow row)
    {
        var square = Square(cell);
        var colour = NoteColours.For(row.Categories, Colour);
        var face = Mix(colour, Colour(TokenKeys.Notes.Ground), Number(TokenKeys.Notes.Tint, 0.72));
        var fold = Mix(face, Colour(TokenKeys.Notes.FoldGround), Number(TokenKeys.Notes.FoldTint, 0.18));
        var edge = Colour(TokenKeys.Notes.Edge);

        // The page with its corner missing, then the flap turned up over the gap — which is what
        // makes a square read as a note rather than as a swatch. A shade of the face rather than
        // a second colour: a note folded is still one piece of paper.
        var page = new StreamGeometry();
        using (var draw = page.Open())
        {
            draw.BeginFigure(square.TopLeft, isFilled: true);
            draw.LineTo(square.TopRight);
            draw.LineTo(new Point(square.Right, square.Bottom - FoldSize));
            draw.LineTo(new Point(square.Right - FoldSize, square.Bottom));
            draw.LineTo(square.BottomLeft);
            draw.EndFigure(true);
        }

        context.DrawGeometry(Brush(face), new Pen(Brush(edge), 1), page);

        var flap = new StreamGeometry();
        using (var draw = flap.Open())
        {
            draw.BeginFigure(new Point(square.Right - FoldSize, square.Bottom), isFilled: true);
            draw.LineTo(new Point(square.Right, square.Bottom - FoldSize));
            draw.LineTo(new Point(square.Right - FoldSize, square.Bottom - FoldSize));
            draw.EndFigure(true);
        }

        context.DrawGeometry(Brush(fold), new Pen(Brush(edge), 1), flap);

        var chosen = _selected is { } s && s.ItemId == row.ItemId;
        if (chosen) Outline(context, square.Inflate(3), Colour(TokenKeys.Notes.Selected), 2);
        else if (_hover is { } h && h.ItemId == row.ItemId) Outline(context, square.Inflate(3), edge);

        // The caption: the note's first line, centred under the square, two lines at most —
        // which is what the reference gives a title before it gives up and ellipsizes.
        var ink = Colour(TokenKeys.List.ReadText);
        var lines = Wrap(row.Title, cell.Width - 8, 2, CaptionSize);
        var baseline = square.Bottom + 15;
        foreach (var line in lines)
        {
            var text = Ink(line, CaptionSize, ink);
            DrawAt(context, text, cell.X + Math.Round((cell.Width - text.Width) / 2), baseline);
            baseline += CaptionLine;
        }
    }

    /// <summary>
    /// The row of column names the two list arrangements draw above their rows — real headings,
    /// as the message list's are: a divider between the columns, a press that sorts, and the
    /// sorted one carrying the mark.
    /// </summary>
    private void DrawColumnHeadings(DrawingContext context, double width)
    {
        var box = new Rect(0, 0, width, HeaderHeight);
        Fill(context, box, Colour(TokenKeys.List.HeaderBackground));
        Fill(context, new Rect(0, HeaderHeight - 1, width, 1), Colour(TokenKeys.List.Separator));

        var ink = Colour(TokenKeys.List.HeaderText);
        foreach (var (key, label, left, columnWidth) in Headings(width))
        {
            Fill(context, new Rect(Math.Round(left + columnWidth) - 1, 5, 1, HeaderHeight - 10), Colour(TokenKeys.List.Separator));
            var text = Ink(label, 11, ink);
            DrawAt(context, text, left + (key == "subject" ? GlyphColumn : 7), 17);

            if (key == _sortKey)
            {
                DrawSortMark(context, left + (key == "subject" ? GlyphColumn : 7) + text.Width + 6, HeaderHeight / 2, ink, _sortDescending);
            }
        }
    }

    /// <summary>The three headings with the spans their columns really occupy.</summary>
    private static List<(string Key, string Label, double Left, double Width)> Headings(double width)
    {
        var madeLeft = width - MadeColumn - CategoryColumn;
        return
        [
            ("subject", "Subject", 0, madeLeft),
            ("created", "Created", madeLeft, MadeColumn),
            ("categories", "Categories", width - CategoryColumn, CategoryColumn),
        ];
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

    /// <summary>One note as a row: its square in miniature, its first line, and what it says after.</summary>
    private void DrawRow(DrawingContext context, Rect box, NoteRow row)
    {
        var chosen = _selected is { } s && s.ItemId == row.ItemId;
        Fill(context, box, Colour(chosen
            ? TokenKeys.List.RowSelected
            : _hover is { } h && h.ItemId == row.ItemId ? TokenKeys.List.RowHover : TokenKeys.List.RowBackground));

        var colour = NoteColours.For(row.Categories, Colour);
        var face = Mix(colour, Colour(TokenKeys.Notes.Ground), Number(TokenKeys.Notes.Tint, 0.72));
        var glyph = new Rect(box.X + 6, box.Y + 4, 13, 13);
        Fill(context, glyph, face);
        Outline(context, glyph, Colour(TokenKeys.Notes.Edge));

        var ink = Colour(TokenKeys.List.ReadText);
        var dim = Colour(TokenKeys.List.PreviewText);
        var madeLeft = box.Width - MadeColumn - CategoryColumn;
        var room = madeLeft - GlyphColumn - 8;

        var title = Ink(Ellipsize(row.Title, room, TextSize), TextSize, ink);
        DrawAt(context, title, box.X + GlyphColumn, box.Y + 15);

        // What the note says after its first line, in the ink the message list writes a preview
        // in — the same idea, and the reason a note needs no separate Contents column.
        var rest = row.Preview;
        if (rest.Length > 0 && room - title.Width > 40)
        {
            var preview = Ellipsize(rest, room - title.Width - 10, TextSize);
            DrawAt(context, Ink(preview, TextSize, dim), box.X + GlyphColumn + title.Width + 10, box.Y + 15);
        }

        DrawAt(context, Ink(row.MadeText(Today, Culture), TextSize, ink), box.X + madeLeft, box.Y + 15);

        if (row.Categories.Count > 0)
        {
            var categories = Ellipsize(string.Join(", ", row.Categories), CategoryColumn - 8, TextSize);
            DrawAt(context, Ink(categories, TextSize, dim), box.Right - CategoryColumn, box.Y + 15);
        }

        Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), Colour(TokenKeys.List.Separator));
    }

    /// <summary>A rectangle's four hairlines, which is what a drawn view has instead of a border.</summary>
    private void Outline(DrawingContext context, Rect box, Color colour, double weight = 1)
    {
        Fill(context, new Rect(box.X, box.Y, box.Width, weight), colour);
        Fill(context, new Rect(box.X, box.Bottom - weight, box.Width, weight), colour);
        Fill(context, new Rect(box.X, box.Y, weight, box.Height), colour);
        Fill(context, new Rect(box.Right - weight, box.Y, weight, box.Height), colour);
    }

    // ---- Input ---------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        Focus();

        // A press on a column heading sorts by it, the second press the other way round.
        if (!IsWall && point.Y < HeaderHeight)
        {
            foreach (var (key, _, left, columnWidth) in Headings(Bounds.Width))
            {
                if (point.X < left || point.X >= left + columnWidth) continue;
                if (_sortKey == key) _sortDescending = !_sortDescending;
                else
                {
                    _sortKey = key;
                    _sortDescending = key == "created";
                }

                InvalidateVisual();
                e.Handled = true;
                return;
            }

            return;
        }

        foreach (var (row, box) in Placed())
        {
            if (!box.Contains(point)) continue;

            Selected = row;
            NoteSelected?.Invoke(this, row);
            if (e.ClickCount >= 2) NoteActivated?.Invoke(this, row);
            e.Handled = true;
            return;
        }

        // A double click on the wall itself makes a note, which is the reference's own gesture
        // and the only way a module with nothing in it gets its first one. The column headings
        // are not the wall: they are drawn like the message list's, where a press sorts, and
        // opening a new note there is not a thing any reader asked for.
        if (e.ClickCount >= 2 && (IsWall || point.Y >= HeaderHeight))
        {
            NewNoteRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        NoteRow? over = null;
        foreach (var (row, box) in Placed())
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
        var moved = _scroll - ((int)e.Delta.Y * (IsWall ? 1 : 3));
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

        // The list's own order, so the arrow keys walk what the reader sees, not the store's.
        var ordered = IsWall ? [.. _rows] : Ordered();
        var index = _selected is { } chosen ? ordered.FindIndex(r => r.ItemId == chosen.ItemId) : -1;
        var step = IsWall ? Columns() : 1;

        switch (e.Key)
        {
            case Key.Down:
                Select(ordered, Math.Min(index + step, ordered.Count - 1));
                break;
            case Key.Up:
                Select(ordered, Math.Max(index - step, 0));
                break;
            case Key.Right when IsWall:
                Select(ordered, Math.Min(index + 1, ordered.Count - 1));
                break;
            case Key.Left when IsWall:
                Select(ordered, Math.Max(index - 1, 0));
                break;
            case Key.Home:
                Select(ordered, 0);
                break;
            case Key.End:
                Select(ordered, ordered.Count - 1);
                break;
            case Key.Enter when _selected is { } open:
                NoteActivated?.Invoke(this, open);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Select(List<NoteRow> among, int index)
    {
        if (index < 0 || index >= among.Count) return;
        Selected = among[index];
        NoteSelected?.Invoke(this, among[index]);
    }

    /// <summary>The headings as drawn, with the sort on each — the read-back for the header row.</summary>
    public IReadOnlyList<(string Key, string Label, double Left, double Width, string Sort)> HeadingsLaid()
        => IsWall ? [] : [.. Headings(Bounds.Width).Select(h => (h.Key, h.Label, h.Left, h.Width,
            h.Key == _sortKey ? (_sortDescending ? "descending" : "ascending") : string.Empty))];

    /// <summary>Sorts the list by a column, as pressing its heading does.</summary>
    public void SortBy(string key, bool descending)
    {
        _sortKey = key;
        _sortDescending = descending;
        InvalidateVisual();
    }

    /// <summary>Where a note is drawn, which is what a harness pose presses.</summary>
    public Rect? BoxOf(long itemId)
    {
        foreach (var (row, box) in Placed())
        {
            if (row.ItemId == itemId) return box;
        }

        return null;
    }

    /// <summary>Somewhere on the wall with no note on it, which is where a new one is made.</summary>
    public Point EmptySpot()
    {
        var columns = Columns();
        var used = _rows.Count - (_scroll * columns);
        var line = Math.Max(0, (int)Math.Ceiling(Math.Max(0, used) / (double)columns));
        return new Point(WallInset + (CellWidth / 2), WallInset + (line * CellHeight) + (CellHeight / 2));
    }
}
