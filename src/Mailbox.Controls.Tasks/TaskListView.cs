using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Controls.Common;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Tasks;

/// <summary>
/// The To-Do List: everything outstanding, under a heading per band, with the row that makes a
/// new task at the top of it.
/// </summary>
/// <remarks>
/// Measured off the Tasks capture, which has the peek open over the left-hand third of the
/// window: the arrangement bar is 16 tall closed by a hairline, the "Type a new task" box is 20
/// tall with an accent line round it, a group heading is 26 tall — the same 26 the message list's
/// are — and a row is 21. What the peek covers is the left of each row, so the tick box's size
/// and where the subject starts are authored; a capture of an uncovered list would settle them.
/// <para>
/// A task that is late or due today is drawn in <c>list.overdue.text</c>, which is the red the
/// capture's own row and its flag are drawn in.
/// </para>
/// </remarks>
public sealed class TaskListView : DrawnSurface
{
    /// <summary>Measured: the arrangement bar, and the hairline closing it.</summary>
    public const double ArrangeHeight = 16;

    /// <summary>Measured: the box that makes a new task, borders included.</summary>
    public const double NewTaskHeight = 20;

    /// <summary>Measured: the same heading the message list draws.</summary>
    public const double GroupHeight = 26;

    /// <summary>Measured: a task's own row.</summary>
    public const double RowHeight = 21;

    /// <summary>Authored — the peek covers this part of the reference's own list.</summary>
    private const double TickLeft = 8;
    private const double TickSize = 12;
    private const double SubjectLeft = 28;
    private const double FlagColumn = 20;
    private const double TextSize = 12;

    private readonly Dictionary<string, Color> _colours = [];
    private IReadOnlyList<TaskRow> _rows = [];
    private TaskRow? _selected;
    private TaskRow? _hover;
    private int _scroll;
    private string _typed = string.Empty;
    private bool _typing;

    public TaskListView()
    {
        Focusable = true;
    }

    /// <summary>The tasks on show, already banded and in order.</summary>
    public IReadOnlyList<TaskRow> Rows
    {
        get => _rows;
        set
        {
            _rows = value ?? [];
            if (_selected is { } chosen) _selected = _rows.FirstOrDefault(r => r.ItemId == chosen.ItemId);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Lines().Count - 1));
            InvalidateVisual();
        }
    }

    public TaskRow? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            InvalidateVisual();
        }
    }

    /// <summary>What the arrangement bar says the list is arranged by.</summary>
    public string ArrangedBy { get; set; } = "Flag: Due Date";

    /// <summary>What is in the new-task box, which is empty until somebody types in it.</summary>
    public string Typed => _typed;

    /// <summary>What the status bar counts.</summary>
    public int Count => _rows.Count;

    public event EventHandler<TaskRow>? TaskSelected;
    public event EventHandler<TaskRow>? TaskActivated;

    /// <summary>The tick box pressed: this task should now be done, or not done.</summary>
    public event EventHandler<TaskRow>? TaskToggled;

    /// <summary>Enter in the new-task box, carrying what was typed.</summary>
    public event EventHandler<string>? TaskTyped;

    // ---- What is drawn, line by line -----------------------------------------------------------

    /// <summary>A heading or a task: the list is one run of both, which is what scrolls.</summary>
    private readonly record struct Entry(TaskBand Band, TaskRow? Row)
    {
        public bool IsHeading => Row is null;
    }

    private List<Entry> Lines()
    {
        var lines = new List<Entry>();
        TaskBand? band = null;
        foreach (var row in _rows)
        {
            if (band != row.Band)
            {
                band = row.Band;
                lines.Add(new Entry(row.Band, null));
            }

            lines.Add(new Entry(row.Band, row));
        }

        return lines;
    }

    // ---- Render ----------------------------------------------------------------------------

    /// <summary>
    /// Where every heading and row goes, from the top of the first one down.
    /// </summary>
    /// <remarks>
    /// Computed rather than remembered from the last render, so that what a pose presses is
    /// answerable before anything has been drawn — a hit list filled during <c>Render</c> is
    /// empty until a frame has been painted, and a capture run poses before its first.
    /// </remarks>
    private IEnumerable<(Entry Entry, Rect Box)> Placed()
    {
        var width = Bounds.Width;
        var height = Bounds.Height > 0 ? Bounds.Height : double.MaxValue;
        var y = ArrangeHeight + 1 + NewTaskHeight + 1;
        var lines = Lines();

        for (var i = _scroll; i < lines.Count && y < height; i++)
        {
            var line = lines[i];
            var box = new Rect(0, y, width, line.IsHeading ? GroupHeight : RowHeight);
            yield return (line, box);
            y = box.Bottom;
        }
    }

    /// <summary>A row's tick box, from the row's own.</summary>
    private static Rect TickBox(Rect row)
        => new(row.X + TickLeft, row.Y + ((row.Height - TickSize) / 2), TickSize, TickSize);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 60 || height < 40) return;

        Fill(context, new Rect(0, 0, width, height), Colour(TokenKeys.List.Background));

        DrawArrangement(context, width);
        DrawNewTask(context, width);

        foreach (var (line, box) in Placed())
        {
            if (line.IsHeading) DrawHeading(context, box, line.Band);
            else DrawRow(context, box, line.Row!);
        }
    }

    /// <summary>
    /// The bar above the list: what it is arranged by on the left, and the column it is sorted
    /// on with its direction against the right edge.
    /// </summary>
    private void DrawArrangement(DrawingContext context, double width)
    {
        var ink = Colour(TokenKeys.List.HeaderText);
        DrawAt(context, Ink("Arrange by: " + ArrangedBy, 11, ink), 6, 12);

        var by = Ink("Today", 11, ink);
        DrawAt(context, by, width - by.Width - 20, 12);

        // The direction mark: pointing up, the reference's own soonest-first.
        var pen = new Pen(Brush(ink), 1.2);
        var x = width - 14;
        context.DrawLine(pen, new Point(x - 4, 9), new Point(x, 5));
        context.DrawLine(pen, new Point(x, 5), new Point(x + 4, 9));

        // The rule under the bar is dark on the pane, which is what the capture shows — the
        // list's own separator is the light line between two rows and reads as a gap here.
        Fill(context, new Rect(0, ArrangeHeight, width, 1), Colour(TokenKeys.Border.Subtle));
    }

    /// <summary>
    /// The row that makes a task by being typed in. Drawn rather than a text box, so that it is
    /// part of the list's own picture — and because the list is drawn, a control here would be
    /// the only one in it.
    /// </summary>
    private void DrawNewTask(DrawingContext context, double width)
    {
        var box = new Rect(0, ArrangeHeight + 1, width, NewTaskHeight);
        Fill(context, box, Colour(TokenKeys.List.RowBackground));
        var edge = Colour(_typing ? TokenKeys.Accent.Rest : TokenKeys.Border.Subtle);
        Fill(context, new Rect(box.X, box.Y, box.Width, 1), edge);
        Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), edge);

        var typing = _typed.Length > 0;
        var ink = Colour(typing ? TokenKeys.List.ReadText : TokenKeys.List.PreviewText);
        DrawAt(context, Ink(typing ? _typed : "Type a new task", TextSize, ink), 6, box.Y + 14);

        // The caret, so that a list being typed into looks like one.
        if (!_typing) return;
        var width2 = Measure(_typed, TextSize);
        Fill(context, new Rect(6 + width2 + 1, box.Y + 4, 1, 12), Colour(TokenKeys.List.ReadText));
    }

    private void DrawHeading(DrawingContext context, Rect box, TaskBand band)
    {
        Fill(context, box, Colour(TokenKeys.List.GroupHeaderBackground));

        // The pale line either side of the band, measured #E1E1E1 against its #444444 — which is
        // what stops a heading from reading as a hole in the list.
        var edge = Colour(TokenKeys.List.Separator);
        Fill(context, new Rect(box.X, box.Y - 1, box.Width, 1), edge);
        Fill(context, new Rect(box.X, box.Bottom, box.Width, 1), edge);

        var ink = Colour(TokenKeys.List.GroupHeaderText);

        // The chevron the reference draws open, then the flag, then the band's name.
        var pen = new Pen(Brush(ink), 1.3);
        var centre = new Point(box.X + 12, box.Y + (box.Height / 2));
        context.DrawLine(pen, new Point(centre.X - 4, centre.Y - 2), centre);
        context.DrawLine(pen, centre, new Point(centre.X + 4, centre.Y - 2));

        DrawFlag(context, new Rect(box.X + 22, box.Y + 7, 11, 12), Colour(TokenKeys.List.OverdueText));
        DrawAt(context, Ink(TaskBook.Heading(band), TextSize, ink, SemiBoldFace), box.X + 40, box.Y + 17);
    }

    private void DrawRow(DrawingContext context, Rect box, TaskRow row)
    {
        var chosen = _selected is { } s && s.ItemId == row.ItemId;
        Fill(context, box, Colour(chosen
            ? TokenKeys.List.RowSelected
            : _hover is { } h && h.ItemId == row.ItemId ? TokenKeys.List.RowHover : TokenKeys.List.RowBackground));

        var ink = Colour(row.IsOverdue || row.Band == TaskBand.Today ? TokenKeys.List.OverdueText : TokenKeys.List.ReadText);

        DrawTick(context, TickBox(box), row.IsComplete);

        var room = box.Width - SubjectLeft - FlagColumn - 6;
        var subject = Ellipsize(row.Summary, room, TextSize);
        DrawAt(context, Ink(subject, TextSize, ink), box.X + SubjectLeft, box.Y + 15);

        // A finished task is struck through, which is the one thing about a done row the list
        // says without being asked.
        if (row.IsComplete)
        {
            var width = Measure(subject, TextSize);
            Fill(context, new Rect(box.X + SubjectLeft, box.Y + 11, width, 1), ink);
        }

        DrawFlag(context, new Rect(box.Right - 17, box.Y + 4, 11, 12), Colour(TokenKeys.List.OverdueText));
    }

    /// <summary>The completion box: empty, or ticked once the task is done.</summary>
    private void DrawTick(DrawingContext context, Rect box, bool done)
    {
        var edge = Colour(TokenKeys.Border.Strong);
        Fill(context, new Rect(box.X, box.Y, box.Width, 1), edge);
        Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), edge);
        Fill(context, new Rect(box.X, box.Y, 1, box.Height), edge);
        Fill(context, new Rect(box.Right - 1, box.Y, 1, box.Height), edge);
        if (!done) return;

        var pen = new Pen(Brush(Colour(TokenKeys.List.ReadText)), 1.6);
        context.DrawLine(pen, new Point(box.X + 2.5, box.Center.Y), new Point(box.Center.X - 0.5, box.Bottom - 3));
        context.DrawLine(pen, new Point(box.Center.X - 0.5, box.Bottom - 3), new Point(box.Right - 2.5, box.Y + 2.5));
    }

    /// <summary>The follow-up flag, drawn rather than glyphed so it is the colour it means.</summary>
    private void DrawFlag(DrawingContext context, Rect box, Color colour)
    {
        var pole = Colour(TokenKeys.List.PreviewText);
        Fill(context, new Rect(box.X, box.Y, 1, box.Height), pole);

        var cloth = new StreamGeometry();
        using (var draw = cloth.Open())
        {
            draw.BeginFigure(new Point(box.X + 1, box.Y), isFilled: true);
            draw.LineTo(new Point(box.Right, box.Y + 2));
            draw.LineTo(new Point(box.Right, box.Y + 7));
            draw.LineTo(new Point(box.X + 1, box.Y + 5));
            draw.EndFigure(true);
        }

        context.DrawGeometry(Brush(colour), null, cloth);
    }

    private Color Colour(string key)
    {
        if (_colours.TryGetValue(key, out var cached)) return cached;
        var colour = this.TryFindResource(key + ".color", out var found) && found is Color resolved ? resolved : Colors.Magenta;
        _colours[key] = colour;
        return colour;
    }

    protected override void OnPaletteChanged() => _colours.Clear();

    // ---- Input -----------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        Focus();

        // The new-task box takes the keyboard when it is pressed, and gives it back when
        // anything else is.
        _typing = point.Y >= ArrangeHeight && point.Y < ArrangeHeight + 1 + NewTaskHeight;

        foreach (var (line, box) in Placed())
        {
            if (line.Row is not { } row || !box.Contains(point)) continue;

            // The tick box first: it is inside the row, and pressing it means the one thing
            // rather than both.
            if (TickBox(box).Inflate(3).Contains(point))
            {
                TaskToggled?.Invoke(this, row);
                e.Handled = true;
                return;
            }

            Selected = row;
            TaskSelected?.Invoke(this, row);
            if (e.ClickCount >= 2) TaskActivated?.Invoke(this, row);
            e.Handled = true;
            return;
        }

        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        TaskRow? over = null;
        foreach (var (line, box) in Placed())
        {
            if (line.Row is not { } row || !box.Contains(point)) continue;
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
        var moved = _scroll - ((int)e.Delta.Y * 3);
        var clamped = Math.Clamp(moved, 0, Math.Max(0, Lines().Count - 1));
        if (clamped == _scroll) return;
        _scroll = clamped;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!_typing || e.Text is not { Length: > 0 } text) return;
        _typed += text;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_typing)
        {
            switch (e.Key)
            {
                case Key.Back when _typed.Length > 0:
                    _typed = _typed[..^1];
                    break;
                case Key.Enter when _typed.Trim().Length > 0:
                    TaskTyped?.Invoke(this, _typed.Trim());
                    _typed = string.Empty;
                    break;
                case Key.Escape:
                    _typed = string.Empty;
                    _typing = false;
                    break;
                default:
                    return;
            }

            e.Handled = true;
            InvalidateVisual();
            return;
        }

        var index = _selected is { } chosen ? _rows.ToList().FindIndex(r => r.ItemId == chosen.ItemId) : -1;
        switch (e.Key)
        {
            case Key.Down when _rows.Count > 0:
                Select(Math.Min(index + 1, _rows.Count - 1));
                break;
            case Key.Up when _rows.Count > 0:
                Select(Math.Max(index - 1, 0));
                break;
            case Key.Home when _rows.Count > 0:
                Select(0);
                break;
            case Key.End when _rows.Count > 0:
                Select(_rows.Count - 1);
                break;
            case Key.Enter when _selected is { } open:
                TaskActivated?.Invoke(this, open);
                break;
            case Key.Space when _selected is { } toggle:
                TaskToggled?.Invoke(this, toggle);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        Selected = _rows[index];
        TaskSelected?.Invoke(this, _rows[index]);
    }

    /// <summary>Where a task's row goes, which is what a harness pose presses.</summary>
    public Rect? BoxOf(long itemId)
    {
        foreach (var (line, box) in Placed())
        {
            if (line.Row is { } row && row.ItemId == itemId) return box;
        }

        return null;
    }

    /// <summary>The tick box of a task's row.</summary>
    public Rect? TickOf(long itemId) => BoxOf(itemId) is { } row ? TickBox(row) : null;

    /// <summary>The new-task box, which a pose types into.</summary>
    public Rect NewTaskBox => new(0, ArrangeHeight + 1, Bounds.Width, NewTaskHeight);
}
