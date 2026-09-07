using Mailbox.Core.Settings;

namespace Mailbox.Tests;

public class SendReceiveScheduleTests
{
    private static readonly DateTimeOffset Opened = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

    private static SendReceiveGroup Every(int minutes, string name = "All Accounts")
        => new() { Name = name, ScheduleEnabled = true, ScheduleMinutes = minutes };

    /// <summary>
    /// The reference checks mail when it starts, so a scheduled group is due the moment the
    /// application opens rather than one interval later.
    /// </summary>
    [Fact]
    public void AScheduledGroupIsDueTheMomentTheApplicationOpens()
    {
        var schedule = new SendReceiveSchedule();

        Assert.Same(SendReceiveGroups.AllAccounts, schedule.Due([SendReceiveGroups.AllAccounts], Opened));
    }

    [Fact]
    public void AGroupWithNoScheduleIsNeverDue()
    {
        var schedule = new SendReceiveSchedule();
        var onRequest = new SendReceiveGroup { Name = "Manual", ScheduleEnabled = false };

        Assert.Null(schedule.Due([onRequest], Opened));
        Assert.Null(schedule.Due([onRequest], Opened.AddDays(1)));
    }

    [Fact]
    public void AGroupIsNotDueAgainUntilItsIntervalHasPassed()
    {
        var schedule = new SendReceiveSchedule();
        var group = Every(30);

        schedule.Started(group, Opened);

        Assert.Null(schedule.Due([group], Opened.AddMinutes(29)));
        Assert.Same(group, schedule.Due([group], Opened.AddMinutes(30)));
    }

    /// <summary>
    /// One group per ask: two send/receives at once would open two sessions to the same server.
    /// The caller runs the first group due and asks again on the next tick.
    /// </summary>
    [Fact]
    public void OneGroupIsDueAtATime()
    {
        var schedule = new SendReceiveSchedule();
        var personal = Every(10, "Personal");
        var work = Every(10, "Work");

        Assert.Same(personal, schedule.Due([personal, work], Opened));

        schedule.Started(personal, Opened);
        Assert.Same(work, schedule.Due([personal, work], Opened));

        schedule.Started(work, Opened);
        Assert.Null(schedule.Due([personal, work], Opened));
    }

    /// <summary>
    /// The dialog replaces a group with an edited copy. The schedule knows a group by its
    /// name, so an interval shortened from thirty minutes to five counts from the run that
    /// already happened rather than starting a fresh thirty.
    /// </summary>
    [Fact]
    public void AShortenedIntervalCountsFromTheLastRun()
    {
        var schedule = new SendReceiveSchedule();
        schedule.Started(Every(30), Opened);

        var shortened = Every(5);

        Assert.Null(schedule.Due([shortened], Opened.AddMinutes(4)));
        Assert.Same(shortened, schedule.Due([shortened], Opened.AddMinutes(5)));
    }

    [Fact]
    public void ANameIsMatchedWithoutRegardToCase()
    {
        var schedule = new SendReceiveSchedule();
        schedule.Started(Every(30, "Work"), Opened);

        Assert.Null(schedule.Due([Every(30, "work")], Opened.AddMinutes(1)));
    }
}
