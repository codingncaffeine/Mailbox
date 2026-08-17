using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// A task's reminder: when it is due to be shown, and what dismissing or putting one off does.
/// </summary>
/// <remarks>
/// The appointment rules with two differences, both of which are what a task is: the alarm hangs
/// from the due date rather than a start, and it does not stop when that date passes.
/// </remarks>
public class TaskReminderTests
{
    private static (PimStore Store, PimRepository Repo, long List) Fresh()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        return (store, repository, repository.AddCollection(CollectionKind.Tasks, "Tasks").Id);
    }

    private static PimItem Add(
        PimRepository repository,
        long list,
        string summary,
        DateTime? due,
        int? reminder,
        bool complete = false,
        string? rrule = null)
        => repository.AddItem(PimTodoCodec.ToItem(
            new TaskItem
            {
                Uid = summary,
                Summary = summary,
                Due = due is { } when ? EventTime.At(when, TimeZoneInfo.Local.Id) : null,
                ReminderMinutes = reminder,
                Rrule = rrule,
                Progress = complete ? TaskProgress.Completed : TaskProgress.NotStarted,
                PercentComplete = complete ? 100 : 0,
            },
            list));

    [Fact]
    public void AReminderComesDueBeforeTheTaskIs()
    {
        var (store, repo, list) = Fresh();
        using var _ = store;

        Add(repo, list, "soon", DateTime.Now.AddMinutes(10), reminder: 15);
        Add(repo, list, "later", DateTime.Now.AddHours(6), reminder: 15);

        var due = TaskReminders.Due(repo, DateTimeOffset.UtcNow);
        Assert.Equal(["soon"], due.Select(d => d.Summary));
    }

    [Fact]
    public void AnOverdueTaskKeepsReminding()
    {
        var (store, repo, list) = Fresh();
        using var _ = store;

        // An appointment that has been and gone stops being shown; a task that was due last week
        // is exactly what the window is for, so it is still on it.
        Add(repo, list, "late", DateTime.Now.AddDays(-7), reminder: 15);

        Assert.Equal(["late"], TaskReminders.Due(repo, DateTimeOffset.UtcNow).Select(d => d.Summary));
    }

    [Fact]
    public void NeitherAFinishedTaskNorAnUndatedOneReminds()
    {
        var (store, repo, list) = Fresh();
        using var _ = store;

        Add(repo, list, "done", DateTime.Now.AddMinutes(5), reminder: 15, complete: true);

        // RFC 5545 hangs a VTODO's alarm off its DUE: with no due date there is nothing to be
        // early about, however the window's tick was filled in.
        Add(repo, list, "someday", null, reminder: 15);
        Add(repo, list, "quiet", DateTime.Now.AddMinutes(5), reminder: null);

        Assert.Empty(TaskReminders.Due(repo, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DismissingOneOccurrenceLeavesTheNextOne()
    {
        var (store, repo, list) = Fresh();
        using var _ = store;

        var when = DateTime.Today.AddHours(DateTime.Now.Hour).AddMinutes(10);
        Add(repo, list, "series", when.AddDays(-7), reminder: 15, rrule: "FREQ=DAILY");

        var first = Assert.Single(TaskReminders.Due(repo, DateTimeOffset.UtcNow));
        TaskReminders.Dismiss(repo, first);

        // Today's is answered; tomorrow's is a different reminder and still comes round.
        Assert.DoesNotContain(TaskReminders.Due(repo, DateTimeOffset.UtcNow), d => d.DueUtc == first.DueUtc);
        Assert.NotEmpty(TaskReminders.Due(repo, DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public void SnoozingPutsItOffUntilTheTimeGiven()
    {
        var (store, repo, list) = Fresh();
        using var _ = store;

        Add(repo, list, "soon", DateTime.Now.AddMinutes(10), reminder: 15);
        var task = Assert.Single(TaskReminders.Due(repo, DateTimeOffset.UtcNow));

        TaskReminders.Snooze(repo, task, DateTimeOffset.UtcNow.AddHours(2));
        Assert.Empty(TaskReminders.Due(repo, DateTimeOffset.UtcNow));
        Assert.NotEmpty(TaskReminders.Due(repo, DateTimeOffset.UtcNow.AddHours(3)));
    }

    [Fact]
    public void AnAppointmentsReminderIsNotATasks()
    {
        var (store, repo, list) = Fresh();
        using var _ = store;

        Add(repo, list, "soon", DateTime.Now.AddMinutes(10), reminder: 15);

        // The two passes read their own kind of collection: a task on the calendar's pass would
        // be parsed as an event, and an appointment on this one as a task.
        Assert.Empty(AppointmentReminders.Due(repo, DateTimeOffset.UtcNow));
        Assert.NotEmpty(TaskReminders.Due(repo, DateTimeOffset.UtcNow));
    }
}
