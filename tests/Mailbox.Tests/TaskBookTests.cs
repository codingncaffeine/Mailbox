using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The task list as the module draws it: which band a task falls in, and what order they come in.
/// </summary>
public class TaskBookTests
{
    // A Sunday, which is what makes the week's end worth asserting: "this week" has to mean
    // something on the day a week ends as well as in the middle of one.
    private static readonly DateOnly Today = new(2026, 8, 16);

    private static TaskItem Due(DateOnly? day, bool complete = false) => new()
    {
        Uid = day?.ToString() ?? "none",
        Summary = "Task",
        Due = day is { } d ? EventTime.Date(d) : null,
        Progress = complete ? TaskProgress.Completed : TaskProgress.NotStarted,
        PercentComplete = complete ? 100 : 0,
    };

    [Theory]
    [InlineData(0, TaskBand.Today)]
    [InlineData(-1, TaskBand.Today)]      // late is what wants doing now
    [InlineData(-30, TaskBand.Today)]
    [InlineData(1, TaskBand.Tomorrow)]
    [InlineData(3, TaskBand.ThisWeek)]
    [InlineData(6, TaskBand.ThisWeek)]
    [InlineData(7, TaskBand.NextWeek)]
    [InlineData(13, TaskBand.NextWeek)]
    [InlineData(20, TaskBand.NextMonth)]
    [InlineData(60, TaskBand.Later)]
    public void ADueDateFallsInTheBandItsDistanceSays(int offset, TaskBand band)
        => Assert.Equal(band, TaskBook.BandOf(Due(Today.AddDays(offset)), Today));

    [Fact]
    public void ATaskWithNoDueDateHasABandOfItsOwn()
        => Assert.Equal(TaskBand.NoDate, TaskBook.BandOf(Due(null), Today));

    [Fact]
    public void ADoneTaskIsCompletedWhateverItsDueDateSays()
        => Assert.Equal(TaskBand.Completed, TaskBook.BandOf(Due(Today.AddDays(-5), complete: true), Today));

    [Fact]
    public void EveryBandHasAHeading()
        => Assert.All(Enum.GetValues<TaskBand>(), b => Assert.NotEmpty(TaskBook.Heading(b)));

    // ---- Against the store --------------------------------------------------------------------

    private static (PimStore Store, PimRepository Repository, TaskBook Book, Collection List) Fresh()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var list = repository.AddCollection(CollectionKind.Tasks, "Tasks", "#0078D4", "you@example.net");
        return (store, repository, new TaskBook(repository), list);
    }

    private static void Add(PimRepository repository, long listId, string summary, DateOnly? due, bool complete = false)
        => repository.AddItem(PimTodoCodec.ToItem(
            new TaskItem
            {
                Uid = summary,
                Summary = summary,
                Due = due is { } d ? EventTime.Date(d) : null,
                Progress = complete ? TaskProgress.Completed : TaskProgress.NotStarted,
                PercentComplete = complete ? 100 : 0,
            },
            listId));

    [Fact]
    public void TheListComesBackBandedAndInOrder()
    {
        var (store, repository, book, list) = Fresh();
        using var _ = store;

        Add(repository, list.Id, "Later on", Today.AddDays(60));
        Add(repository, list.Id, "Undated", null);
        Add(repository, list.Id, "Due now", Today);
        Add(repository, list.Id, "Was due", Today.AddDays(-2));
        Add(repository, list.Id, "Tomorrow", Today.AddDays(1));

        var rows = book.Rows(Today);

        Assert.Equal(["Was due", "Due now", "Tomorrow", "Later on", "Undated"], rows.Select(r => r.Summary));
        Assert.Equal([TaskBand.Today, TaskBand.Today, TaskBand.Tomorrow, TaskBand.Later, TaskBand.NoDate], rows.Select(r => r.Band));
        Assert.True(rows[0].IsOverdue);
        Assert.False(rows[1].IsOverdue);
    }

    [Fact]
    public void WhatIsDoneIsLeftOutUntilItIsAskedFor()
    {
        var (store, repository, book, list) = Fresh();
        using var _ = store;

        Add(repository, list.Id, "Standing", Today);
        Add(repository, list.Id, "Finished", Today, complete: true);

        Assert.Equal(["Standing"], book.Rows(Today).Select(r => r.Summary));

        var all = book.Rows(Today, includeCompleted: true);
        Assert.Equal(["Standing", "Finished"], all.Select(r => r.Summary));
        Assert.Equal(TaskBand.Completed, all[1].Band);
        Assert.False(all[1].IsOverdue);
    }

    [Fact]
    public void AHiddenListIsNotOnIt()
    {
        var (store, repository, book, list) = Fresh();
        using var _ = store;
        Add(repository, list.Id, "On a hidden list", Today);

        repository.SetCollectionVisible(list.Id, false);
        Assert.Empty(book.Rows(Today));

        // …unless it is named, which is how a view of one list works.
        Assert.Single(book.Rows(Today, collectionIds: [list.Id]));
    }

    [Fact]
    public void OpeningARowParsesTheWholeTask()
    {
        var (store, repository, book, list) = Fresh();
        using var _ = store;

        var row = repository.AddItem(PimTodoCodec.ToItem(
            new TaskItem { Uid = "u@mailbox", Summary = "Read it back", Description = "Every word of it." },
            list.Id));

        Assert.Equal("Every word of it.", book.Open(row.Id)!.Description);
        Assert.Null(book.Open(row.Id + 100));
    }
}
