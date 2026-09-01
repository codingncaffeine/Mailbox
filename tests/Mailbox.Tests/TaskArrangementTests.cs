using Mailbox.Scheduling;

namespace Mailbox.Tests;

/// <summary>
/// What the task list groups by, now that it can group by more than one thing.
/// </summary>
/// <remarks>
/// The bands used to be an enum all the way through — <c>Entry</c>, the heading drawing and the
/// spoken rows each took a <see cref="TaskBand"/> — so a category or a task list, which are names
/// rather than members of a fixed set, had nowhere to go. These hold the key that replaced it:
/// every arrangement puts a row somewhere, nothing is dropped, and a row with nothing to group by
/// lands in a band that says so.
/// </remarks>
public class TaskArrangementTests
{
    private static readonly DateOnly Today = new(2026, 9, 1);

    private static TaskRow Row(
        string summary,
        DateOnly? due = null,
        DateOnly? start = null,
        string[]? categories = null,
        TaskUrgency urgency = TaskUrgency.Normal,
        long collectionId = 1,
        bool complete = false,
        FlaggedMessage? message = null,
        FlaggedContact? contact = null)
    {
        var task = new TaskItem
        {
            Uid = summary + "@arrange",
            Summary = summary,
            Due = due is { } d ? EventTime.Date(d) : null,
            Start = start is { } s ? EventTime.Date(s) : null,
            Categories = categories ?? [],
            Urgency = urgency,
            Progress = complete ? TaskProgress.Completed : TaskProgress.NotStarted,
            PercentComplete = complete ? 100 : 0,
        };

        return new TaskRow
        {
            ItemId = 1,
            CollectionId = collectionId,
            Task = task,
            Band = TaskBook.BandOf(task, Today),
            IsOverdue = false,
            Message = message,
            Contact = contact,
        };
    }

    private static string Heading(TaskRow row, TaskArrangement by, Func<long, string>? names = null)
        => TaskArrangements.GroupOf(row, by, Today, names).Heading;

    // ---- A row nobody arranged ----------------------------------------------------------------

    /// <summary>
    /// The list has always banded by due date, and a row that has not been through an arrangement
    /// still does — otherwise every unarranged list would draw empty headings.
    /// </summary>
    [Fact]
    public void AnUnarrangedRowCarriesItsDueDateBand()
    {
        var row = Row("Book the room", due: Today);

        Assert.Equal("Today", row.Group.Heading);
        Assert.Equal("band:Today", row.Group.Key);
    }

    // ---- Each arrangement ----------------------------------------------------------------------

    [Fact]
    public void ByCategoriesUsesTheFirstOne()
    {
        Assert.Equal("Work", Heading(Row("A", categories: ["Work", "Urgent"]), TaskArrangement.Categories));
        Assert.Equal("Personal", Heading(Row("B", categories: ["Personal"]), TaskArrangement.Categories));
    }

    /// <summary>
    /// A task with no category is a fact about the task. A list that hid it while grouped by
    /// category would be losing rows, which is the one thing a grouping must never do.
    /// </summary>
    [Fact]
    public void ByCategoriesGivesTheUncategorizedABandOfTheirOwn()
    {
        var group = TaskArrangements.GroupOf(Row("A"), TaskArrangement.Categories, Today);

        Assert.Equal("None", group.Heading);
        Assert.Equal(1, group.Order);
    }

    /// <summary>Two spellings of one category are one band: the key is folded, the heading is not.</summary>
    [Fact]
    public void ACategorysCaseDoesNotSplitItsBand()
    {
        var one = TaskArrangements.GroupOf(Row("A", categories: ["Work"]), TaskArrangement.Categories, Today);
        var two = TaskArrangements.GroupOf(Row("B", categories: ["work"]), TaskArrangement.Categories, Today);

        Assert.Equal(one.Key, two.Key);
    }

    [Fact]
    public void ByStartDateBandsTheStartRatherThanTheDue()
    {
        // Due next month, starting today: under Due Date it is far away, under Start Date it is now.
        var row = Row("Read the spec", due: Today.AddDays(40), start: Today);

        Assert.Equal("Next Month", Heading(row, TaskArrangement.DueDate));
        Assert.Equal("Today", Heading(row, TaskArrangement.StartDate));
    }

    /// <summary>A task with no start date says so rather than landing in Today.</summary>
    [Fact]
    public void ByStartDateAnUndatedTaskHasNoDate()
        => Assert.Equal("No Date", Heading(Row("A", due: Today), TaskArrangement.StartDate));

    /// <summary>Finished is finished whichever date is being grouped on.</summary>
    [Fact]
    public void ByStartDateAFinishedTaskStaysUnderCompleted()
        => Assert.Equal("Completed", Heading(Row("A", start: Today, complete: true), TaskArrangement.StartDate));

    [Fact]
    public void ByFolderNamesTheList()
    {
        var names = (long id) => id == 7 ? "Shopping" : "Tasks";

        Assert.Equal("Shopping", Heading(Row("A", collectionId: 7), TaskArrangement.Folder, names));
        Assert.Equal("Tasks", Heading(Row("B", collectionId: 1), TaskArrangement.Folder, names));
    }

    /// <summary>
    /// A borrowed row is not on a task list at all, so it gets a band of its own rather than being
    /// filed under whichever collection id it happens to carry — a different store's numbering.
    /// </summary>
    [Fact]
    public void ByFolderABorrowedRowIsNotFiledUnderATaskList()
    {
        var mail = Row("Re: Q3", collectionId: 1, message: new FlaggedMessage("you@example.com", 4, "A. Person"));
        var contact = Row("A. Person", collectionId: 1, contact: new FlaggedContact(9, "A. Person"));

        Assert.Equal("Mail", Heading(mail, TaskArrangement.Folder, _ => "Tasks"));
        Assert.Equal("Contacts", Heading(contact, TaskArrangement.Folder, _ => "Tasks"));
    }

    [Fact]
    public void ByTypeTellsTheThreeApart()
    {
        Assert.Equal("Task", Heading(Row("A"), TaskArrangement.Type));
        Assert.Equal(
            "Mail",
            Heading(Row("B", message: new FlaggedMessage("you@example.com", 4, "A. Person")), TaskArrangement.Type));
        Assert.Equal(
            "Contact",
            Heading(Row("C", contact: new FlaggedContact(9, "A. Person")), TaskArrangement.Type));
    }

    /// <summary>High first: the point of grouping by importance is to see what matters at the top.</summary>
    [Fact]
    public void ByImportanceRunsHighToLow()
    {
        var rows = TaskArrangements.Arrange(
            [
                Row("Low one", urgency: TaskUrgency.Low),
                Row("Normal one"),
                Row("High one", urgency: TaskUrgency.High),
            ],
            TaskArrangement.Importance,
            Today);

        Assert.Equal(["High", "Normal", "Low"], rows.Select(r => r.Group.Heading));
    }

    // ---- Arranging a whole list ----------------------------------------------------------------

    /// <summary>Every row comes back, whatever it is grouped by. A grouping that loses rows is a bug.</summary>
    [Theory]
    [InlineData(TaskArrangement.DueDate)]
    [InlineData(TaskArrangement.StartDate)]
    [InlineData(TaskArrangement.Categories)]
    [InlineData(TaskArrangement.Folder)]
    [InlineData(TaskArrangement.Type)]
    [InlineData(TaskArrangement.Importance)]
    [InlineData(TaskArrangement.Modified)]
    public void NoArrangementLosesARow(TaskArrangement by)
    {
        List<TaskRow> rows =
        [
            Row("One", due: Today),
            Row("Two", categories: ["Work"], urgency: TaskUrgency.High),
            Row("Three", start: Today, collectionId: 7),
            Row("Four", message: new FlaggedMessage("you@example.com", 4, "A. Person")),
            Row("Five", complete: true),
        ];

        var arranged = TaskArrangements.Arrange(rows, by, Today, _ => "Shopping");

        Assert.Equal(rows.Count, arranged.Count);
        Assert.Equal(
            rows.Select(r => r.Summary).OrderBy(s => s),
            arranged.Select(r => r.Summary).OrderBy(s => s));
        Assert.All(arranged, r => Assert.NotEmpty(r.Group.Heading));
    }

    /// <summary>
    /// The rows arrive already sorted — by due date and then by name — and the arrangement is
    /// stable within a band, so what changes is which headings appear, not the order under one.
    /// </summary>
    [Fact]
    public void ArrangingIsStableWithinABand()
    {
        var arranged = TaskArrangements.Arrange(
            [
                Row("Alpha", categories: ["Work"]),
                Row("Bravo", categories: ["Work"]),
                Row("Charlie", categories: ["Work"]),
            ],
            TaskArrangement.Categories,
            Today);

        Assert.Equal(["Alpha", "Bravo", "Charlie"], arranged.Select(r => r.Summary));
    }

    /// <summary>Every arrangement the gallery offers has a label, and no two share one.</summary>
    [Fact]
    public void EveryArrangementIsNamedOnce()
    {
        var labels = TaskArrangements.All.Select(TaskArrangements.Label).ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.All(labels, l => Assert.NotEmpty(l));
        Assert.Equal(Enum.GetValues<TaskArrangement>().Length, TaskArrangements.All.Count);
    }
}
