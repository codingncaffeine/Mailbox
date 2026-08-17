using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// Tasks to and from RFC 5545 text, and to and from the row the store keeps for one.
/// </summary>
public class TaskCodecTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    private static TaskItem Sample() => new()
    {
        Uid = "task-1@mailbox",
        Summary = "Send the quarterly numbers",
        Description = "The variance on line 14 wants a note.",
        Start = EventTime.At(new DateTime(2026, 8, 17, 9, 0, 0), "Europe/London"),
        Due = EventTime.At(new DateTime(2026, 8, 20, 17, 0, 0), "Europe/London"),
        Progress = TaskProgress.InProgress,
        PercentComplete = 40,
        Urgency = TaskUrgency.High,
        ReminderMinutes = 60,
        Categories = ["Finance"],
        Owner = "you@example.com",
        LastModified = Stamp,
    };

    [Fact]
    public void ATaskSurvivesARoundTripThroughText()
    {
        var task = Sample();
        var back = TodoCodec.Parse(TodoCodec.Serialize(task)).Single();

        Assert.Equal(task.Uid, back.Uid);
        Assert.Equal(task.Summary, back.Summary);
        Assert.Equal(task.Description, back.Description);
        Assert.Equal(task.Start, back.Start);
        Assert.Equal(task.Due, back.Due);
        Assert.Equal(TaskProgress.InProgress, back.Progress);
        Assert.Equal(40, back.PercentComplete);
        Assert.Equal(TaskUrgency.High, back.Urgency);
        Assert.Equal(60, back.ReminderMinutes);
        Assert.Equal(["Finance"], back.Categories);
        Assert.Equal("you@example.com", back.Owner);
    }

    [Fact]
    public void ATaskWithNoDatesIsStillATask()
    {
        var task = new TaskItem { Uid = "u@mailbox", Summary = "Think about it", LastModified = Stamp };
        var back = TodoCodec.Parse(TodoCodec.Serialize(task)).Single();

        Assert.Null(back.Start);
        Assert.Null(back.Due);
        Assert.Equal("Think about it", back.Summary);
        Assert.Equal(TaskProgress.NotStarted, back.Progress);
    }

    [Fact]
    public void AnAllDayDueDateStaysADate()
    {
        var task = Sample() with { Start = null, Due = EventTime.Date(new DateOnly(2026, 8, 20)) };
        var back = TodoCodec.Parse(TodoCodec.Serialize(task)).Single();

        Assert.NotNull(back.Due);
        Assert.True(back.Due!.AllDay);
        Assert.Equal(new DateTime(2026, 8, 20), back.Due.Wall);
    }

    [Fact]
    public void CompletingATaskWritesAllThreeThingsThatSaySo()
    {
        var done = PimTodoCodec.Complete(Sample(), done: true, Stamp);
        Assert.Equal(TaskProgress.Completed, done.Progress);
        Assert.Equal(100, done.PercentComplete);
        Assert.Equal(Stamp, done.CompletedUtc);
        Assert.True(done.IsComplete);

        var back = TodoCodec.Parse(TodoCodec.Serialize(done)).Single();
        Assert.Equal(TaskProgress.Completed, back.Progress);
        Assert.Equal(Stamp, back.CompletedUtc);
    }

    [Fact]
    public void UndoingACompletionClearsAllThreeAgain()
    {
        var task = PimTodoCodec.Complete(PimTodoCodec.Complete(Sample(), done: true, Stamp), done: false, Stamp);
        Assert.False(task.IsComplete);
        Assert.Null(task.CompletedUtc);
        Assert.Equal(0, task.PercentComplete);
    }

    [Theory]
    [InlineData(TaskProgress.Waiting)]
    [InlineData(TaskProgress.Deferred)]
    public void TheTwoStatesTheStandardCannotSayTravelBesideNeedsAction(TaskProgress progress)
    {
        var text = TodoCodec.Serialize(Sample() with { Progress = progress });

        // Beside, not instead of: a client that ignores the extra property still reads a task
        // that needs doing rather than one that has been cancelled.
        Assert.Contains("STATUS:NEEDS-ACTION", text, StringComparison.Ordinal);
        Assert.Contains("X-MAILBOX-TASK-STATUS:" + progress.ToString().ToUpperInvariant(), text, StringComparison.Ordinal);
        Assert.Equal(progress, TodoCodec.Parse(text).Single().Progress);
    }

    [Fact]
    public void ATaskAnotherClientMarkedDoneByWritingCompletedAloneIsDone()
    {
        var text = """
            BEGIN:VTODO
            UID:other@example.com
            DTSTAMP:20260816T090000Z
            SUMMARY:Booked by somebody else
            COMPLETED:20260816T090000Z
            END:VTODO
            """;

        var task = TodoCodec.Parse(text).Single();
        Assert.True(task.IsComplete);
        Assert.Equal(TaskProgress.Completed, task.Progress);
        Assert.Equal(100, task.PercentComplete);
    }

    [Theory]
    [InlineData(1, TaskUrgency.High)]
    [InlineData(4, TaskUrgency.High)]
    [InlineData(0, TaskUrgency.Normal)]
    [InlineData(5, TaskUrgency.Normal)]
    [InlineData(6, TaskUrgency.Low)]
    [InlineData(9, TaskUrgency.Low)]
    public void ThePriorityNumbersReadBackAsTheThreeStepsTheReferenceOffers(int number, TaskUrgency urgency)
        => Assert.Equal(urgency, TaskItem.UrgencyFor(number));

    [Fact]
    public void AnOverdueTaskIsOneWhoseDeadlineHasPassedAndIsNotDone()
    {
        var task = Sample() with { Due = EventTime.At(new DateTime(2026, 8, 16, 9, 0, 0), "UTC") };
        Assert.True(task.IsOverdue(Stamp));
        Assert.False(task.IsOverdue(Stamp.AddMinutes(-1)));

        // Done is never late.
        Assert.False(PimTodoCodec.Complete(task, done: true, Stamp).IsOverdue(Stamp));

        // An all-day task is late when its day is over, not when it begins.
        var allDay = Sample() with { Due = EventTime.Date(new DateOnly(2026, 8, 16)) };
        Assert.False(allDay.IsOverdue(Stamp, TimeZoneInfo.Utc));
        Assert.True(allDay.IsOverdue(Stamp.AddDays(1), TimeZoneInfo.Utc));
    }

    // ---- The store's row ---------------------------------------------------------------------

    [Fact]
    public void TheRowsColumnsAgreeWithTheTaskTheTextHolds()
    {
        var row = PimTodoCodec.ToItem(Sample(), collectionId: 7);

        Assert.Equal(CollectionKind.Tasks, row.Kind);
        Assert.Equal("Send the quarterly numbers", row.Summary);
        Assert.Equal(1, row.Priority);
        Assert.Equal(40, row.PercentComplete);
        Assert.Equal("in-progress", row.Status);
        Assert.Equal("2026-08-17T09:00:00", row.StartsLocal);
        Assert.Equal("2026-08-20T17:00:00", row.EndsLocal);
        Assert.Equal("Europe/London", row.TzId);
        Assert.Equal("Finance", row.Categories);
        Assert.Equal(PimSyncState.New, row.SyncState);
        Assert.Equal(PimTodoCodec.FromItem(row), Sample());
    }

    [Fact]
    public void ADueDateAloneStillGivesTheRowASpanToBeFoundBy()
    {
        // The instant columns are what a range query reads, so a task with no start date is
        // still found by a query over the day it is due.
        var row = PimTodoCodec.ToItem(
            Sample() with { Start = null, Due = EventTime.At(new DateTime(2026, 8, 20, 17, 0, 0), "UTC") },
            collectionId: 7);

        Assert.Equal(new DateTimeOffset(2026, 8, 20, 17, 0, 0, TimeSpan.Zero), row.StartsUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 17, 0, 0, TimeSpan.Zero), row.EndsUtc);

        // …and rebuilding from the columns does not invent the start date it never had.
        Assert.Null(row.StartsLocal);
        Assert.Null(PimTodoCodec.FromColumns(row).Start);
    }

    [Fact]
    public void ARowWhoseTextIsDamagedStillDescribesItsTask()
    {
        var row = PimTodoCodec.ToItem(Sample(), collectionId: 7) with { RawPayload = "BEGIN:VTODO\r\nnonsense" };
        var task = PimTodoCodec.FromItem(row);

        Assert.Equal("Send the quarterly numbers", task.Summary);
        Assert.Equal(TaskProgress.InProgress, task.Progress);
        Assert.Equal(TaskUrgency.High, task.Urgency);
        Assert.Equal(40, task.PercentComplete);
    }

    [Fact]
    public void EditingAStoredTaskKeepsItsIdentityAndItsSyncBookkeeping()
    {
        var first = PimTodoCodec.ToItem(Sample(), 7) with { Id = 12, DavHref = "/tasks/1.ics", Etag = "\"abc\"", SyncState = PimSyncState.Synced };
        var edited = PimTodoCodec.ToItem(Sample() with { Summary = "Renamed" }, 7, first);

        Assert.Equal(12, edited.Id);
        Assert.Equal("/tasks/1.ics", edited.DavHref);
        Assert.Equal("\"abc\"", edited.Etag);
        Assert.Equal(PimSyncState.Modified, edited.SyncState);
    }

    [Fact]
    public void AWholeCalendarOfTasksCarriesEveryOneOfThem()
    {
        var master = Sample() with { Rrule = "FREQ=WEEKLY;BYDAY=MO" };
        var override1 = Sample() with
        {
            RecurrenceId = EventTime.At(new DateTime(2026, 8, 24, 9, 0, 0), "Europe/London"),
            Summary = "Moved one",
        };

        var text = TodoCodec.SerializeCalendar([master, override1]);
        var back = TodoCodec.Parse(text);

        Assert.Equal(2, back.Count);
        Assert.False(back[0].IsOverride);
        Assert.True(back[1].IsOverride);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", back[0].Rrule);
        Assert.Contains("BEGIN:VCALENDAR", text, StringComparison.Ordinal);
    }
}
