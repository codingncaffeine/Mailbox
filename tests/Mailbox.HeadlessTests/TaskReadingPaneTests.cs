using Mailbox.App.Views;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The Tasks module's reading pane: selecting a task shows it, and moving off it stops showing it.
/// </summary>
/// <remarks>
/// The module had no pane at all — a row ran the whole width of the window and selecting one
/// showed nothing anywhere — so what is worth holding is that the pane follows the selection and
/// says the task's own fields rather than a plausible set of them. Read back through
/// <c>ReadingLines</c>, which walks the pane's real text blocks: a test that rebuilt the sentences
/// itself would pass over a pane that drew nothing.
/// </remarks>
public class TaskReadingPaneTests
{
    private static (PimStore Store, TasksWorkspace Workspace) Fresh()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var list = repository.AddCollection(CollectionKind.Tasks, "Tasks", "#0078D4", string.Empty);

        Add(repository, list.Id, new TaskItem
        {
            Uid = "late@example.com",
            Summary = "Send the quarterly numbers",
            Description = "The three sheets, and the note about the rounding.",
            Due = EventTime.Date(new DateOnly(2026, 8, 14)),
            Progress = TaskProgress.InProgress,
            PercentComplete = 40,
            Urgency = TaskUrgency.High,
            ReminderMinutes = 15,
        });

        Add(repository, list.Id, new TaskItem
        {
            Uid = "later@example.com",
            Summary = "Plan the offsite",
            Due = EventTime.Date(new DateOnly(2026, 9, 13)),
        });

        return (store, new TasksWorkspace(repository, new DateOnly(2026, 8, 16)));
    }

    private static void Add(PimRepository repository, long listId, TaskItem task)
        => repository.AddItem(PimTodoCodec.ToItem(task, listId));

    [Fact]
    public void SelectingATaskShowsItsOwnFields()
    {
        var lines = HeadlessApp.OnUiThread(() =>
        {
            var (store, workspace) = Fresh();
            using var _ = store;
            workspace.PoseSelect("quarterly");
            return workspace.ReadingLines;
        });

        Assert.Contains("Send the quarterly numbers", lines);
        Assert.Contains("Due Fri 14/08/2026. This is overdue.", lines);
        Assert.Contains("In Progress", lines);
        Assert.Contains("40%", lines);
        Assert.Contains("High", lines);
        Assert.Contains("15 minutes before", lines);
        Assert.Contains("The three sheets, and the note about the rounding.", lines);
    }

    /// <summary>
    /// Only what the task has. A column of "None" says less than an absence does, and a pane that
    /// draws every field of every task is a pane nobody reads.
    /// </summary>
    [Fact]
    public void ATaskWithNothingInItSaysNothingAboutIt()
    {
        var lines = HeadlessApp.OnUiThread(() =>
        {
            var (store, workspace) = Fresh();
            using var _ = store;
            workspace.PoseSelect("offsite");
            return workspace.ReadingLines;
        });

        Assert.Contains("Plan the offsite", lines);
        Assert.Contains("Not Started", lines);

        // Not late, so nothing is coloured; and nothing was written in it, no reminder was set,
        // and its priority is the ordinary one.
        Assert.DoesNotContain(lines, line => line.Contains("overdue", StringComparison.Ordinal));
        Assert.DoesNotContain("% Complete", lines);
        Assert.DoesNotContain("Reminder", lines);
        Assert.DoesNotContain("Priority", lines);
    }

    /// <summary>
    /// The pane is one of the window's two arrangements and can be turned off, as View › Layout
    /// offers for mail — and with it off the list has the width back rather than a dead column.
    /// </summary>
    [Fact]
    public void ThePaneCanBeMovedAndTurnedOff()
    {
        HeadlessApp.OnUiThread(() =>
        {
            var (store, workspace) = Fresh();
            using var _ = store;

            workspace.PoseSelect("quarterly");
            Assert.True(workspace.ReadingPaneVisible);
            Assert.NotEmpty(workspace.ReadingLines);

            workspace.ReadingPaneAtBottom = true;
            Assert.True(workspace.ReadingPaneAtBottom);
            Assert.Contains("Send the quarterly numbers", workspace.ReadingLines);

            workspace.ReadingPaneVisible = false;
            Assert.False(workspace.ReadingPaneVisible);
            return 0;
        });
    }
}
