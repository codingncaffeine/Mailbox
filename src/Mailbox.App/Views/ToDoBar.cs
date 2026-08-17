using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Controls.Calendar;
using Mailbox.Controls.Tasks;
using Mailbox.Scheduling;

namespace Mailbox.App.Views;

/// <summary>
/// The To-Do Bar: the pane down the right-hand edge holding what is coming, in the sections the
/// reference's own View · To-Do Bar menu switches on and off.
/// </summary>
/// <remarks>
/// The cross-module pane §9 asks for, and the reason it is three lines of composition rather than
/// a view of its own: <b>both of its sections already exist</b>. The calendar section is the docked
/// peek — the same <c>PeekView</c> the rail's hover opens, over the same month and the same
/// agenda — and the tasks section is the to-do list's own <c>TaskListView</c>, banded by due date
/// with the row that makes a task by being typed in. Nothing here draws; it stacks.
/// <para>
/// When both are on the calendar takes the height its month and the day's appointments need and
/// the tasks take the rest, which is the reference's own arrangement. When only one is on it
/// takes the whole pane, which is what the docked peek did before there was anything to share
/// with.
/// </para>
/// <para>
/// The People section the reference offers is the third: it lists favourite contacts, which is a
/// list this application does not keep yet. Its menu entry says so rather than being absent.
/// </para>
/// </remarks>
internal sealed class ToDoBar : Border
{
    /// <summary>
    /// How many of the day's appointments the calendar section shows before the tasks begin.
    /// Authored: the reference's own bar shows a handful and scrolls the rest, and the peek
    /// already scrolls.
    /// </summary>
    private const int AppointmentsShown = 3;

    private readonly Grid _grid = new();

    public ToDoBar(PeekView? peek, TaskListView? tasks)
    {
        Peek = peek;
        Tasks = tasks;

        this[!BackgroundProperty] = new DynamicResourceExtension("peek.background.brush");
        Width = PeekLayout.DockedWidth + PeekLayout.DividerWidth;

        // The calendar first and the tasks under it, which is the order the reference stacks
        // them in and the order its menu lists them.
        var calendarRow = peek is null ? "0" : tasks is null ? "*" : CalendarHeight(peek).ToString(Culture);
        _grid.RowDefinitions = new RowDefinitions($"{calendarRow},*");

        if (peek is not null)
        {
            Grid.SetRow(peek, 0);
            _grid.Children.Add(peek);
        }

        if (tasks is not null)
        {
            Grid.SetRow(tasks, 1);
            _grid.Children.Add(tasks);

            // The line between the two sections, which is the same hairline the peek rules under
            // its own grid rather than a second idea about how a divider looks.
            if (peek is not null)
            {
                var rule = new Border { Height = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
                rule[!BackgroundProperty] = new DynamicResourceExtension("peek.divider.brush");
                Grid.SetRow(rule, 1);
                _grid.Children.Add(rule);
            }
        }

        Child = _grid;
    }

    /// <summary>The calendar section, when it is on.</summary>
    public PeekView? Peek { get; }

    /// <summary>The tasks section, when it is on.</summary>
    public TaskListView? Tasks { get; }

    private static System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// How tall the calendar section is when it is sharing the pane: its month block, then room
    /// for the first few of the day's appointments.
    /// </summary>
    /// <remarks>
    /// Measured from the peek's own layout rather than guessed, so the two cannot drift: whatever
    /// <see cref="PeekLayout"/> says the agenda starts at is where the count begins.
    /// </remarks>
    private static double CalendarHeight(PeekView peek)
    {
        var layout = new PeekLayout(docked: true, PeekLayout.DockedWidth, peek.ShowWeekNumbers);
        var height = layout.AgendaTop;

        foreach (var row in peek.Agenda.Take(AppointmentsShown))
        {
            height += PeekLayout.EntryHeight(row.Lines) + PeekLayout.EntryGap;
        }

        // A day with nothing on it still leaves room for the line that says so.
        if (peek.Agenda.Count == 0) height += PeekLayout.EntryHeight(1) + PeekLayout.EntryGap;

        return Math.Round(height + PeekLayout.EntryGap);
    }
}
