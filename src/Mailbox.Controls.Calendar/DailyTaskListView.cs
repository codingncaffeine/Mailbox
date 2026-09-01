using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Media;
using Mailbox.Controls.Common;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>One task in the band, and which day's column it belongs in.</summary>
public sealed record DailyTask(DateOnly Day, string Summary, bool IsComplete, bool IsOverdue);

/// <summary>
/// The Daily Task List: a band under the day and week grids holding what is due on each day,
/// in columns lined up with the grid's own.
/// </summary>
/// <remarks>
/// <strong>Authored.</strong> No capture of the reference's band exists, so its geometry is
/// this application's — but the two numbers that would be guesses are borrowed rather than
/// invented: the header row is the grid's own 28 and a task line is the chip's 13, which is
/// what keeps the band reading as part of the grid above it rather than a second control
/// pushed underneath.
/// <para>
/// The columns are the grid's columns. The band takes the same ruler inset and the same day
/// widths from whoever hosts it, because a band whose columns are one pixel out from the grid's
/// is worse than no band: every task would sit under the wrong day at the edges.
/// </para>
/// </remarks>
public sealed class DailyTaskListView : CalendarSurface, ISpokenRows
{
    /// <summary>The band's own header, the height the grid gives its weekday row.</summary>
    public const double HeaderHeight = 28;

    /// <summary>A task's line, the height a chip gives one line of text.</summary>
    public const double RowHeight = ChipLineHeight + 4;

    /// <summary>How many rows of tasks the band shows before it stops drawing them.</summary>
    public const int VisibleRows = 4;

    private const double TextSize = 12;
    private const double TextInset = 6;

    private IReadOnlyList<DateOnly> _days = [];
    private IReadOnlyList<DailyTask> _tasks = [];
    private double _rulerWidth = 62;
    private bool _minimized;

    /// <summary>What the last render actually drew, which is what there is to speak.</summary>
    private readonly List<(Rect Box, DailyTask Task)> _drawn = [];

    /// <summary>The days the grid above is showing, in its order.</summary>
    public IReadOnlyList<DateOnly> Days
    {
        get => _days;
        set
        {
            Set(ref _days, value ?? []);
            SpokenRowsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<DailyTask> Tasks
    {
        get => _tasks;
        set
        {
            Set(ref _tasks, value ?? []);
            SpokenRowsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The grid's ruler column, so the band's first column starts where the grid's does.</summary>
    public double RulerWidth
    {
        get => _rulerWidth;
        set => Set(ref _rulerWidth, value);
    }

    /// <summary>Minimized: the header alone, so the band can be opened again without the menu.</summary>
    public bool Minimized
    {
        get => _minimized;
        set => Set(ref _minimized, value);
    }

    /// <summary>How tall the band wants to be for what it holds.</summary>
    public double DesiredHeight
    {
        get
        {
            if (_minimized || _days.Count == 0) return HeaderHeight;
            var deepest = _days.Count == 0 ? 0 : _days.Max(day => _tasks.Count(t => t.Day == day));
            return HeaderHeight + (Math.Clamp(deepest, 1, VisibleRows) * RowHeight) + 4;
        }
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        InvalidateVisual();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(availableSize.Width, DesiredHeight);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var ground = Palette.Colour(TokenKeys.Calendar.Background);
        var line = Palette.Colour(TokenKeys.Calendar.GridLine);
        var headerLine = Palette.Colour(TokenKeys.Calendar.HeaderLine);
        var text = Palette.Colour(TokenKeys.Calendar.DayText);
        var subtle = Palette.Colour(TokenKeys.Calendar.PastText);

        _drawn.Clear();
        Fill(context, new Rect(0, 0, width, height), ground);

        // The rule that separates the band from the grid, and the one under its own header.
        Fill(context, new Rect(0, 0, width, 1), headerLine);
        Fill(context, new Rect(0, HeaderHeight, width, 1), line);

        DrawAt(context, Ink("Tasks", TextSize, text, SemiBoldFace), TextInset, 19);
        Fill(context, new Rect(_rulerWidth, 0, 1, height), headerLine);

        if (_minimized || _days.Count == 0) return;

        // The grid's own column arithmetic: whole pixels, the remainder to the earliest columns,
        // so the band's lines fall on the grid's lines rather than a rounding away from them.
        var slices = Slice(width - _rulerWidth, _days.Count);
        var x = _rulerWidth;

        for (var i = 0; i < _days.Count; i++)
        {
            var day = _days[i];
            var column = slices[i];
            var due = _tasks.Where(t => t.Day == day).Take(VisibleRows).ToList();

            var top = HeaderHeight + 3;
            foreach (var task in due)
            {
                _drawn.Add((new Rect(x, top, column, RowHeight), task));
                var colour = task.IsOverdue ? Palette.Colour(TokenKeys.Status.Danger)
                    : task.IsComplete ? subtle
                    : text;

                var label = task.IsComplete ? "✓ " + task.Summary : task.Summary;
                DrawAt(
                    context,
                    Ink(Ellipsize(label, column - TextInset - 4, TextSize), TextSize, colour),
                    x + TextInset,
                    top + ChipLineHeight - 2);
                top += RowHeight;
            }

            x += column;
            if (i < _days.Count - 1) Fill(context, new Rect(x - 1, HeaderHeight, 1, height - HeaderHeight), line);
        }
    }

    /// <summary>
    /// The grid's own slicing: whole pixels, the remainder handed to the earliest columns.
    /// </summary>
    internal static IReadOnlyList<double> Slice(double width, int columns)
    {
        if (columns <= 0) return [];
        var whole = Math.Floor(width / columns);
        var over = (int)(width - (whole * columns));
        return [.. Enumerable.Range(0, columns).Select(i => whole + (i < over ? 1 : 0))];
    }


    // ---- The band, spoken for --------------------------------------------------------------
    //
    // Read-only, which is what this band is: it shows what is due under each day and nothing on
    // it can be pressed. So the rows are named and stated and there is no current one — a list
    // with no selection rather than a list pretending to have one.

    public event EventHandler? SpokenRowsChanged;

    /// <summary>
    /// Never raised, because nothing here is selectable and so the current row never moves.
    /// Written as an explicit implementation so that saying so costs no field and no pretence.
    /// </summary>
    event EventHandler? ISpokenRows.SpokenSelectionChanged
    {
        add { }
        remove { }
    }

    int ISpokenRows.SpokenCount => _drawn.Count;

    string ISpokenRows.SpokenRow(int index)
    {
        var task = _drawn[index].Task;
        var said = new System.Text.StringBuilder(task.Summary.Length > 0 ? task.Summary : "(No subject)");
        said.Append(". Due ").Append(task.Day.ToString("ddd d MMM", Culture));
        if (task.IsComplete) said.Append(", done");
        else if (task.IsOverdue) said.Append(", overdue");
        said.Append('.');
        return said.ToString();
    }

    int ISpokenRows.SpokenSelectedIndex => -1;

    void ISpokenRows.SpokenSelect(int index)
    {
    }

    Rect? ISpokenRows.SpokenRowBounds(int index)
        => index >= 0 && index < _drawn.Count ? _drawn[index].Box : null;

    bool? ISpokenRows.SpokenRowToggled(int index)
        => index >= 0 && index < _drawn.Count ? _drawn[index].Task.IsComplete : null;

    protected override AutomationPeer OnCreateAutomationPeer() => new SpokenRowsPeer(this);
}
