using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Controls.Calendar;
using Mailbox.Controls.People;
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
/// The People section is the third, and the same again: the module's own <c>ContactListView</c>
/// over the favourite contacts, with its alphabet index off — a short list has no Ws to reach.
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

    /// <summary>
    /// How tall the People section is when it is sharing the pane with the tasks.
    /// </summary>
    /// <remarks>
    /// Authored, no capture of the section existing: room for about five favourites, which is
    /// what the reference's own short list holds before it scrolls.
    /// </remarks>
    private const double PeopleHeight = 5 * 30;

    private readonly Grid _grid = new();

    public ToDoBar(PeekView? peek, TaskListView? tasks, ContactListView? people = null)
    {
        Peek = peek;
        Tasks = tasks;
        People = people;

        this[!BackgroundProperty] = new DynamicResourceExtension("peek.background.brush");
        Width = PeekLayout.DockedWidth + PeekLayout.DividerWidth;

        // The calendar first, the tasks under it and the people under those, which is the order
        // the reference stacks them in and the order its menu lists them. Whatever is on takes
        // the room the ones above it leave; with only one on, it takes the pane.
        var calendarRow = peek is null ? "0" : tasks is null && people is null ? "*" : CalendarHeight(peek).ToString(Culture);
        var peopleRow = people is null ? "0" : tasks is null ? "*" : PeopleHeight.ToString(Culture);
        _grid.RowDefinitions = new RowDefinitions($"{calendarRow},*,{peopleRow}");

        Place(peek, 0, ruled: false);
        Place(tasks, 1, ruled: peek is not null);
        Place(people, 2, ruled: peek is not null || tasks is not null);

        Child = _grid;
    }

    /// <summary>Puts one section in its row, with the hairline that separates it from the one above.</summary>
    private void Place(Control? section, int row, bool ruled)
    {
        if (section is null) return;

        Grid.SetRow(section, row);
        _grid.Children.Add(section);
        if (!ruled) return;

        // The same hairline the peek rules under its own grid, rather than a second idea about
        // how a divider looks.
        var rule = new Border { Height = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        rule[!BackgroundProperty] = new DynamicResourceExtension("peek.divider.brush");
        Grid.SetRow(rule, row);
        _grid.Children.Add(rule);
    }

    /// <summary>The calendar section, when it is on.</summary>
    public PeekView? Peek { get; }

    /// <summary>The tasks section, when it is on.</summary>
    public TaskListView? Tasks { get; }

    /// <summary>The People section — the favourite contacts — when it is on.</summary>
    public ContactListView? People { get; }

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

        // A day with nothing on it used to leave room for the line that says so, and there is no
        // such line: the agenda draws the day's name and then its entries, and neither the
        // reference's docked bar nor its floating peek — the two captures there are — says
        // anything at all about a day that is empty. So an empty day's section stood twenty
        // pixels taller than what it drew, pushing the tasks down to make room for a sentence
        // nothing was going to write.
        return Math.Round(height + PeekLayout.EntryGap);
    }
}
