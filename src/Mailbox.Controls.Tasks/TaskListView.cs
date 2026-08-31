using Avalonia;
using Avalonia.Automation.Peers;
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
/// A task that is <em>late</em> is drawn in <c>list.overdue.text</c>, which is the red the
/// capture's own row and its flag are drawn in. Only a late one: the reference draws what is
/// overdue in red and everything with a due date still ahead of it in the ordinary ink, and the
/// Today band holds both — late work is filed there because it is what wants doing now, not
/// because it has stopped being late. Colouring the whole band red made the two
/// indistinguishable, which threw away the only thing the red says.
/// </para>
/// </remarks>
public sealed class TaskListView : DrawnSurface, ISpokenRows
{
    /// <summary>Measured: the arrangement bar itself.</summary>
    public const double ArrangeHeight = 16;

    /// <summary>
    /// Measured: the pane ground above the bar's rule, and the rule itself. The reference draws
    /// the rule <em>above</em> the bar — the new-task box's accent edge follows the bar with no
    /// second rule between.
    /// </summary>
    public const double ArrangeInset = 23;
    public const double ArrangeTop = ArrangeInset + 1;

    /// <summary>Measured: the box that makes a new task, borders included.</summary>
    public const double NewTaskHeight = 20;

    /// <summary>Measured: the same heading the message list draws.</summary>
    public const double GroupHeight = 26;

    /// <summary>The column header the detailed view puts where the arrangement bar goes.</summary>
    /// <remarks>
    /// The message list's own header height, since this is the same kind of row over the same kind
    /// of columns and no capture of this view exists to say otherwise.
    /// </remarks>
    public const double HeaderHeight = 26;

    /// <summary>Measured: a task's own row.</summary>
    public const double RowHeight = 21;

    /// <summary>Authored — the peek covers this part of the reference's own list.</summary>
    private const double TickLeft = 8;
    private const double TickSize = 12;
    private const double SubjectLeft = 28;
    private const double FlagColumn = 20;
    private const double TextSize = 12;

    /// <summary>
    /// The columns the detailed view draws, in the reference's own order and each with the width
    /// it is given.
    /// </summary>
    /// <remarks>
    /// Authored: no capture of this view exists, so the order is the reference's — priority, the
    /// subject, then what it says about itself — and the widths are what the words in each need at
    /// 12px, the subject taking whatever is left. The reference has an Attachment column here as
    /// well; a task in this application carries no attachments yet, and a column that could never
    /// have anything in it is worse than one that is not drawn; the absence is queued.
    /// </remarks>
    private static readonly (string Heading, double Width)[] Columns =
    [
        ("!", 20),
        ("Subject", 0),
        ("Status", 130),
        ("Due Date", 110),
        ("% Complete", 80),
        ("Categories", 140),
    ];

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
            if (_selected is { } chosen) _selected = _rows.FirstOrDefault(r => r.Key == chosen.Key);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Lines().Count - 1));
            InvalidateVisual();
            SpokenRowsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public TaskRow? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            InvalidateVisual();
            SpokenSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What the arrangement bar says the list is arranged by.</summary>
    public string ArrangedBy { get; set; } = "Flag: Due Date";

    /// <summary>
    /// Whether the list is drawn as a table of columns — the reference's Detailed view.
    /// </summary>
    /// <remarks>
    /// The same rows either way; what changes is that every column a task has is drawn instead of
    /// its subject alone, and that the bands go, a table of columns being sorted rather than
    /// grouped. The row that makes a task by being typed in stays, as it does in the reference's
    /// own table views.
    /// </remarks>
    public bool ShowColumns
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            _scroll = 0;
            InvalidateVisual();
        }
    }

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

        // A table is sorted, not grouped: the detailed view is one run of rows under its columns,
        // and the bands belong to the two views that have no columns to say the same thing.
        if (ShowColumns)
        {
            foreach (var row in _rows) lines.Add(new Entry(row.Band, row));
            return lines;
        }

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
        var y = NewTaskTop + NewTaskHeight + 1;
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

        if (ShowColumns) DrawColumnHeader(context, width);
        else DrawArrangement(context, width);

        DrawNewTask(context, width);

        foreach (var (line, box) in Placed())
        {
            if (line.IsHeading) DrawHeading(context, box, line.Band);
            else if (ShowColumns) DrawDetailedRow(context, box, line.Row!);
            else DrawRow(context, box, line.Row!);
        }
    }

    /// <summary>
    /// Where each column starts and how wide it is, for a list of this width.
    /// </summary>
    /// <remarks>
    /// The subject takes what the others leave, and never less than a hundred pixels — a narrow
    /// pane keeps the columns and lets the subject ellipsize, which is what a table does rather
    /// than dropping columns nobody asked it to drop.
    /// </remarks>
    private static IEnumerable<(string Heading, Rect Box)> Slice(double width)
    {
        var fixedWidth = Columns.Sum(c => c.Width);
        var subject = Math.Max(100, width - SubjectLeft - fixedWidth - 6);
        var x = 0.0;

        foreach (var (heading, given) in Columns)
        {
            var cell = given > 0 ? given : subject;
            if (heading == "!") x = TickLeft + TickSize + 4;
            yield return (heading, new Rect(x, 0, cell, 0));
            x += cell + (heading == "!" ? 6 : 8);
        }
    }

    /// <summary>The header the detailed view draws in the arrangement bar's place.</summary>
    private void DrawColumnHeader(DrawingContext context, double width)
    {
        var box = new Rect(0, 0, width, HeaderHeight);
        Fill(context, box, Colour(TokenKeys.List.HeaderBackground));
        Fill(context, new Rect(0, box.Bottom - 1, width, 1), Colour(TokenKeys.Border.Subtle));

        var ink = Colour(TokenKeys.List.HeaderText);
        foreach (var (heading, cell) in Slice(width))
        {
            if (cell.X > width - 20) break;
            DrawAt(context, Ink(Ellipsize(heading, cell.Width, 11), 11, ink), cell.X, 17);
        }
    }

    /// <summary>
    /// One row of the detailed view: the tick box, then a cell per column.
    /// </summary>
    /// <remarks>
    /// The subject cell carries what the other views draw in the subject — the envelope of a
    /// flagged message and who sent it — so a row still says which of the two things it is.
    /// </remarks>
    private void DrawDetailedRow(DrawingContext context, Rect box, TaskRow row)
    {
        var chosen = _selected is { } s && s.Key == row.Key;
        Fill(context, box, Colour(chosen
            ? TokenKeys.List.RowSelected
            : _hover is { } h && h.Key == row.Key ? TokenKeys.List.RowHover : TokenKeys.List.RowBackground));

        var ink = Colour(row.IsOverdue ? TokenKeys.List.OverdueText : TokenKeys.List.ReadText);
        var quiet = Colour(TokenKeys.List.PreviewText);
        var baseline = box.Y + 15;

        DrawTick(context, TickBox(box), row.IsComplete);

        var task = row.Task;
        foreach (var (heading, cell) in Slice(box.Width))
        {
            if (cell.X > box.Right - 20) break;
            var left = box.X + cell.X;

            switch (heading)
            {
                case "!":
                    // The reference's own marks: a red exclamation for high, a blue arrow for
                    // low, and nothing at all for the normal that most tasks are.
                    if (task.Urgency == TaskUrgency.High) DrawAt(context, Ink("!", TextSize, Colour(TokenKeys.List.OverdueText), BoldFace), left + 4, baseline);
                    else if (task.Urgency == TaskUrgency.Low) DrawAt(context, Ink("↓", TextSize, quiet), left + 3, baseline);
                    break;

                case "Subject":
                {
                    var start = left;
                    if (row.IsMessage)
                    {
                        DrawEnvelope(context, new Rect(start, box.Y + 5, 13, 10), quiet);
                        start += 18;
                    }
                    else if (row.IsContact)
                    {
                        DrawPerson(context, new Rect(start, box.Y + 5, 13, 10), quiet);
                        start += 18;
                    }

                    var room = cell.Width - (start - left);
                    var text = Ink(Ellipsize(row.Summary, room, TextSize), TextSize, ink);
                    DrawAt(context, text, start, baseline);
                    if (row.IsComplete) Fill(context, new Rect(start, box.Y + 11, text.Width, 1), ink);
                    break;
                }

                // A borrowed row is a message or a contact, and neither has a status or a
                // percentage: their cells stay empty rather than claiming "Not Started".
                case "Status" when !row.IsBorrowed:
                    DrawAt(context, Ink(Ellipsize(TodoCodec.ProgressLabel(task.Progress), cell.Width, TextSize), TextSize, ink), left, baseline);
                    break;

                case "Due Date":
                    DrawAt(context, Ink(Ellipsize(row.DueText(), cell.Width, TextSize), TextSize, ink), left, baseline);
                    break;

                case "% Complete" when !row.IsBorrowed:
                    DrawAt(context, Ink($"{task.PercentComplete}%", TextSize, ink), left, baseline);
                    break;

                default:
                    if (task.Categories.Count > 0)
                    {
                        DrawAt(context, Ink(Ellipsize(string.Join(", ", task.Categories), cell.Width, TextSize), TextSize, quiet), left, baseline);
                    }

                    break;
            }
        }

        // The line under a row, which is what keeps a table of cells from reading as a block.
        Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), Colour(TokenKeys.List.Separator));
    }

    /// <summary>
    /// The bar above the list, as the capture measures it: pane ground, then the dark rule,
    /// then the bar — what it is arranged by on the left, a column divider, the sort bucket
    /// starting its own column, and the solid triangle held clear of the right edge. No rule
    /// underneath: the new-task box's accent edge follows the bar directly.
    /// </summary>
    private void DrawArrangement(DrawingContext context, double width)
    {
        Fill(context, new Rect(0, ArrangeInset, width, 1), Colour(TokenKeys.Border.Subtle));

        var ink = Colour(TokenKeys.List.HeaderText);
        var baseline = ArrangeTop + 12;
        DrawAt(context, Ink("Arrange by: " + ArrangedBy, 11, ink), 6, baseline);

        // The sort bucket's own column: the divider, then "Today" flush to it.
        var column = Math.Max(120, width - 90);
        Fill(context, new Rect(Math.Round(column), ArrangeTop + 3, 1, ArrangeHeight - 6), Colour(TokenKeys.List.Separator));
        DrawAt(context, Ink("Today", 11, ink), column + 8, baseline);

        // The direction mark: a filled 9px triangle pointing up — soonest first — held clear
        // of the edge, as the capture measures it.
        var x = width - 19;
        var top = ArrangeTop + 5;
        var mark = new StreamGeometry();
        using (var open = mark.Open())
        {
            open.BeginFigure(new Point(x + 4.5, top), true);
            open.LineTo(new Point(x + 9, top + 5));
            open.LineTo(new Point(x, top + 5));
            open.EndFigure(true);
        }

        context.DrawGeometry(Brush(ink), null, mark);
    }

    /// <summary>
    /// The row that makes a task by being typed in. Drawn rather than a text box, so that it is
    /// part of the list's own picture — and because the list is drawn, a control here would be
    /// the only one in it.
    /// </summary>
    private void DrawNewTask(DrawingContext context, double width)
    {
        var box = new Rect(0, NewTaskTop, width, NewTaskHeight);
        Fill(context, box, Colour(TokenKeys.List.RowBackground));

        // The accent line round the box at rest, which is what the capture measures — the box
        // is the module's one always-armed control, and the edge is what says so.
        var edge = Colour(TokenKeys.Accent.Rest);
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

        DrawFlag(context, new Rect(box.X + 22, box.Y + 7, 11, 12));
        DrawAt(context, Ink(TaskBook.Heading(band), TextSize, ink, SemiBoldFace), box.X + 40, box.Y + 17);
    }

    private void DrawRow(DrawingContext context, Rect box, TaskRow row)
    {
        var chosen = _selected is { } s && s.Key == row.Key;
        Fill(context, box, Colour(chosen
            ? TokenKeys.List.RowSelected
            : _hover is { } h && h.Key == row.Key ? TokenKeys.List.RowHover : TokenKeys.List.RowBackground));

        var ink = Colour(row.IsOverdue ? TokenKeys.List.OverdueText : TokenKeys.List.ReadText);

        DrawTick(context, TickBox(box), row.IsComplete);

        // A flagged message wears an envelope where a task wears nothing, which is what tells the
        // two apart on a list that otherwise treats them alike — and who sent it after the
        // subject, as the reference writes it.
        var left = box.X + SubjectLeft;
        if (row.Message is not null)
        {
            DrawEnvelope(context, new Rect(left, box.Y + 5, 13, 10), Colour(TokenKeys.List.PreviewText));
            left += 18;
        }
        else if (row.IsContact)
        {
            DrawPerson(context, new Rect(left, box.Y + 5, 13, 10), Colour(TokenKeys.List.PreviewText));
            left += 18;
        }

        var room = box.Right - left - FlagColumn - 6;
        var subject = Ellipsize(row.Summary, room, TextSize);
        var text = Ink(subject, TextSize, ink);
        DrawAt(context, text, left, box.Y + 15);

        if (row.Message is { } from && room - text.Width > 60)
        {
            var who = Ellipsize(from.From, room - text.Width - 12, TextSize);
            DrawAt(context, Ink(who, TextSize, Colour(TokenKeys.List.PreviewText)), left + text.Width + 8, box.Y + 15);
        }

        // A finished task is struck through, which is the one thing about a done row the list
        // says without being asked.
        if (row.IsComplete)
        {
            Fill(context, new Rect(left, box.Y + 11, text.Width, 1), ink);
        }

        DrawFlag(context, new Rect(box.Right - 17, box.Y + 4, 11, 12));
    }

    /// <summary>The envelope a flagged message's row carries: a rectangle with its flap creased.</summary>
    private void DrawEnvelope(DrawingContext context, Rect box, Color colour)
    {
        var pen = new Pen(Brush(colour), 1);
        context.DrawRectangle(null, pen, box);
        context.DrawLine(pen, box.TopLeft, new Point(box.Center.X, box.Center.Y));
        context.DrawLine(pen, new Point(box.Center.X, box.Center.Y), box.TopRight);
    }

    /// <summary>A head and shoulders, where a flagged message wears an envelope.</summary>
    /// <remarks>
    /// Outlined rather than filled, and on the envelope's own 13×10 so the subjects of all three
    /// kinds of row start at the same place — a list whose text stepped left and right depending
    /// on what each row was would read as ragged rather than as informative.
    /// </remarks>
    private void DrawPerson(DrawingContext context, Rect box, Color colour)
    {
        var pen = new Pen(Brush(colour), 1);
        var head = Math.Min(box.Width, box.Height) / 3;

        context.DrawEllipse(null, pen, new Point(box.Center.X, box.Y + head), head, head);

        var shoulders = new StreamGeometry();
        using (var draw = shoulders.Open())
        {
            draw.BeginFigure(new Point(box.X + 1, box.Bottom), isFilled: false);
            draw.ArcTo(
                new Point(box.Right - 1, box.Bottom),
                new Size(box.Width / 2, head * 1.6),
                0,
                isLargeArc: false,
                SweepDirection.Clockwise);
            draw.EndFigure(false);
        }

        context.DrawGeometry(null, pen, shoulders);
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

    /// <summary>
    /// The follow-up flag: a cloth inside a 1px outline, on a pole.
    /// </summary>
    /// <remarks>
    /// The application's one flag (`tags.flag`), which is also what the ribbon's Follow Up button
    /// draws — the reference's own to-do list carries the same three colours its icon does, so a
    /// second red here would be a second red nowhere in the reference.
    /// </remarks>
    private void DrawFlag(DrawingContext context, Rect box)
    {
        // The pole runs the whole height, the cloth hanging from its top as the reference's does.
        Fill(context, new Rect(box.X, box.Y, 1, box.Height), Colour(TokenKeys.Tags.FlagPole));

        var panel = new Rect(box.X, box.Y, Math.Min(7, box.Width), Math.Min(9, box.Height));
        Fill(context, panel, Colour(TokenKeys.Tags.FlagOutline));
        Fill(context, panel.Deflate(1), Colour(TokenKeys.Tags.Flag));
    }

    // ---- Input -----------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        Focus();

        // The new-task box takes the keyboard when it is pressed, and gives it back when
        // anything else is.
        _typing = NewTaskBox.Contains(point);

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

        if (over?.Key == _hover?.Key) return;
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

        var index = _selected is { } chosen ? _rows.ToList().FindIndex(r => r.Key == chosen.Key) : -1;
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

    /// <summary>Where a row goes, which is what a harness pose presses.</summary>
    /// <remarks>
    /// By <see cref="TaskRow.Key"/> rather than by id: the list holds tasks and flagged messages
    /// together and the two are numbered by different stores, so an id alone names two rows.
    /// </remarks>
    public Rect? BoxOf(string key)
    {
        foreach (var (line, box) in Placed())
        {
            if (line.Row is { } row && row.Key == key) return box;
        }

        return null;
    }

    /// <summary>The tick box of a row.</summary>
    public Rect? TickOf(string key) => BoxOf(key) is { } row ? TickBox(row) : null;

    /// <summary>
    /// Where the new-task row starts: under the arrangement bar, or under the columns when the
    /// detailed view has drawn a header in its place.
    /// </summary>
    private double NewTaskTop => ShowColumns ? HeaderHeight : ArrangeTop + ArrangeHeight;

    /// <summary>The new-task box, which a pose types into.</summary>
    public Rect NewTaskBox => new(0, NewTaskTop, Bounds.Width, NewTaskHeight);

    // ---- The list, spoken for --------------------------------------------------------------

    public event EventHandler? SpokenRowsChanged;

    public event EventHandler? SpokenSelectionChanged;

    int ISpokenRows.SpokenCount => Lines().Count;

    /// <summary>A heading says its band and how many are under it; a task says its state and when.</summary>
    string ISpokenRows.SpokenRow(int index)
    {
        var line = Lines()[index];
        if (line.Row is not { } row)
            return $"{TaskBook.Heading(line.Band)}, {_rows.Count(r => r.Band == line.Band)} " +
                   (_rows.Count(r => r.Band == line.Band) == 1 ? "task" : "tasks");

        var said = new System.Text.StringBuilder();
        if (row.IsComplete) said.Append("Complete. ");
        else if (row.IsOverdue) said.Append("Overdue. ");
        said.Append(row.Summary).Append('.');
        if (row.DueText() is { Length: > 0 } due) said.Append(" Due ").Append(due).Append('.');
        if (row.Message is { } message) said.Append(" Flagged message from ").Append(message.From).Append('.');
        else if (row.IsContact) said.Append(" Flagged contact.");
        return said.ToString();
    }

    int ISpokenRows.SpokenSelectedIndex
    {
        get
        {
            if (_selected is not { } chosen) return -1;
            var lines = Lines();
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Row?.Key == chosen.Key) return i;
            }

            return -1;
        }
    }

    void ISpokenRows.SpokenSelect(int index)
    {
        if (Lines()[index].Row is not { } row) return;
        Selected = row;
        TaskSelected?.Invoke(this, row);
    }

    Rect? ISpokenRows.SpokenRowBounds(int index)
    {
        var at = _scroll;
        foreach (var (_, box) in Placed())
        {
            if (at == index) return box;
            at++;
        }

        return null;
    }

    bool? ISpokenRows.SpokenRowToggled(int index)
        => Lines()[index].Row is { } row ? row.IsComplete : null;

    void ISpokenRows.SpokenToggle(int index)
    {
        if (Lines()[index].Row is { } row) TaskToggled?.Invoke(this, row);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpokenRowsPeer(this);
}
