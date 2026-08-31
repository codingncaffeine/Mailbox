using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Mailbox.Controls.Calendar;
using Mailbox.Controls.Common;
using Mailbox.Controls.Journal;
using Mailbox.Controls.Notes;
using Mailbox.Controls.People;
using Mailbox.Controls.Tasks;
using Mailbox.Scheduling;

namespace Mailbox.Tests;

/// <summary>
/// The drawn lists speak: every view that describes its rows through <see cref="ISpokenRows"/>
/// exposes them to assistive technology as selectable items, and the two notifications a screen
/// reader keys on — children changed, selection changed — are raised when the model moves.
/// </summary>
/// <remarks>
/// At the peer, not the bus: what the accessibility bridge publishes is read off the peer tree,
/// so the peer answering rightly is the half this suite can hold. The bridge's own leg — the
/// same rows arriving over the accessibility bus with their names and states — was proved by
/// reading the bus from outside when the kit was built.
/// </remarks>
public sealed class SpokenRowsTests
{
    private static TaskRow Task(string summary, TaskBand band, long id = 1, bool overdue = false) => new()
    {
        ItemId = id,
        CollectionId = 1,
        Task = new TaskItem { Uid = $"u{id}", Summary = summary },
        Band = band,
        IsOverdue = overdue,
    };

    private static TaskListView Tasks(params TaskRow[] rows)
    {
        var view = new TaskListView();
        view.Rows = rows;
        return view;
    }

    [Fact]
    public void TheTaskListSpeaksItsBandsAndItsRows()
    {
        ISpokenRows view = Tasks(
            Task("Send the numbers", TaskBand.Today, 1, overdue: true),
            Task("Book the room", TaskBand.Today, 2));

        Assert.Equal(3, view.SpokenCount);
        Assert.Equal("Today, 2 tasks", view.SpokenRow(0));
        Assert.Equal("Overdue. Send the numbers.", view.SpokenRow(1));
        Assert.Equal("Book the room.", view.SpokenRow(2));
    }

    [Fact]
    public void SelectingThroughTheReaderTakesTheViewsOwnDoors()
    {
        var view = Tasks(Task("One", TaskBand.Today, 1), Task("Two", TaskBand.Today, 2));
        var spoken = (ISpokenRows)view;

        TaskRow? selected = null;
        var moved = 0;
        view.TaskSelected += (_, row) => selected = row;
        spoken.SpokenSelectionChanged += (_, _) => moved++;

        spoken.SpokenSelect(2);

        Assert.Equal("Two", selected?.Summary);
        Assert.Equal("Two", view.Selected?.Summary);
        Assert.Equal(2, spoken.SpokenSelectedIndex);
        Assert.Equal(1, moved);

        // A heading is not a row anybody can hold: asking selects nothing and says so.
        spoken.SpokenSelect(0);
        Assert.Equal("Two", view.Selected?.Summary);
    }

    [Fact]
    public void TheTickIsOfferedOnTasksAndWithheldFromHeadings()
    {
        var view = Tasks(Task("One", TaskBand.Today, 1));
        var spoken = (ISpokenRows)view;

        Assert.Null(spoken.SpokenRowToggled(0));
        Assert.False(spoken.SpokenRowToggled(1));

        TaskRow? ticked = null;
        view.TaskToggled += (_, row) => ticked = row;
        spoken.SpokenToggle(1);
        Assert.Equal("One", ticked?.Summary);
    }

    [Fact]
    public void ThePeerExposesTheRowsAsSelectableItems()
    {
        var view = Tasks(Task("One", TaskBand.Today, 1), Task("Two", TaskBand.Today, 2));
        var peer = ControlAutomationPeer.CreatePeerForElement(view);

        Assert.IsType<SpokenRowsPeer>(peer);
        Assert.Equal(AutomationControlType.List, peer.GetAutomationControlType());

        var rows = peer.GetChildren();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Today, 2 tasks", rows[0].GetName());
        Assert.Equal(AutomationControlType.ListItem, rows[1].GetAutomationControlType());
        Assert.Same(peer, rows[1].GetParent());

        // A task offers the toggle pattern; the heading over it does not.
        Assert.NotNull(rows[1].GetProvider<IToggleProvider>());
        Assert.Null(rows[0].GetProvider<IToggleProvider>());

        // Selecting through the item provider is the click's own path.
        var item = Assert.IsAssignableFrom<ISelectionItemProvider>(rows[2]);
        item.Select();
        Assert.Equal("Two", view.Selected?.Summary);
        Assert.True(item.IsSelected);
        var container = Assert.IsAssignableFrom<ISelectionProvider>(peer);
        Assert.Same(rows[2], Assert.Single(container.GetSelection()));
    }

    [Fact]
    public void ThePeerRaisesTheTwoNotificationsAReaderKeysOn()
    {
        var view = Tasks(Task("One", TaskBand.Today, 1));
        var peer = ControlAutomationPeer.CreatePeerForElement(view);
        peer.GetChildren();

        var selectionRaises = 0;
        var childrenRaises = 0;
        peer.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectionPatternIdentifiers.SelectionProperty) selectionRaises++;
        };
        peer.ChildrenChanged += (_, _) => childrenRaises++;

        view.Selected = view.Rows[0];
        Assert.Equal(1, selectionRaises);

        view.Rows = [Task("One", TaskBand.Today, 1), Task("Two", TaskBand.Tomorrow, 2)];
        Assert.Equal(1, childrenRaises);
        Assert.Equal(4, peer.GetChildren().Count);
    }

    [Fact]
    public void EveryDrawnListMakesASpokenPeer()
    {
        // The calendar surfaces place entries during render, so headless they hold no rows —
        // the peer's existence and role are the half that can be held here.
        Avalonia.Controls.Control[] views =
        [
            new TaskListView(), new NotesView(), new JournalView(), new ContactListView(),
            new TimeGridView(), new MonthView(), new ScheduleView(),
        ];

        foreach (var view in views)
        {
            Assert.IsAssignableFrom<ISpokenRows>(view);
            var peer = ControlAutomationPeer.CreatePeerForElement(view);
            Assert.True(peer is SpokenRowsPeer, $"{view.GetType().Name} makes no spoken peer");
            Assert.Equal(AutomationControlType.List, peer.GetAutomationControlType());
        }
    }

    [Fact]
    public void TheNotesAndTheJournalSayTheirEntries()
    {
        var note = new NoteRow
        {
            ItemId = 7,
            CollectionId = 1,
            Entry = new JournalEntry { Uid = "n7", Summary = "Buy stamps", Description = "Buy stamps\nTwo books." },
            Made = new DateTime(2026, 8, 14, 10, 0, 0),
        };
        var notes = new NotesView { Today = new DateOnly(2026, 8, 16) };
        notes.Rows = [note];
        Assert.StartsWith("Buy stamps.", ((ISpokenRows)notes).SpokenRow(0));

        var entry = new JournalRow
        {
            ItemId = 9,
            CollectionId = 1,
            Entry = new JournalEntry { Uid = "j9", Summary = "Called the bank", EntryType = "Phone call" },
            Start = new DateTime(2026, 8, 14, 9, 30, 0),
        };
        var journal = new JournalView { IsSearch = true };
        journal.Rows = [entry];
        var spoken = (ISpokenRows)journal;
        Assert.Equal(1, spoken.SpokenCount);
        Assert.StartsWith("Phone call. Called the bank.", spoken.SpokenRow(0));
    }
}
