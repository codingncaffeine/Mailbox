using Mailbox.Protocols;

namespace Mailbox.Tests;

public class SendReceiveTasksTests
{
    private const string One = "you@example.com";
    private const string Two = "other@example.com";

    private static SendReceiveTasks Two_Accounts() => new([One, Two]);

    [Fact]
    public void EveryAccountGetsASendingAndAReceivingTask()
    {
        var tasks = Two_Accounts();

        Assert.Equal(4, tasks.Total);
        Assert.Equal(
            [
                $"{One} - Sending", $"{One} - Receiving",
                $"{Two} - Sending", $"{Two} - Receiving",
            ],
            tasks.Tasks.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void NothingHasSucceededBeforeTheRunStarts()
    {
        var tasks = Two_Accounts();

        Assert.Equal(0, tasks.Succeeded);
        Assert.Equal("0 of 4 Tasks have completed successfully", tasks.Headline);
        Assert.Equal(0, tasks.Fraction);
        Assert.False(tasks.IsFinished);
        Assert.All(tasks.Tasks, t => Assert.Equal(TransferTaskState.Waiting, t.State));
    }

    [Fact]
    public void SendingIsReportedAgainstTheSendingTask()
    {
        var tasks = Two_Accounts();
        tasks.Report(new PollProgress(One, 0, 0, "Sending"));

        Assert.Equal(TransferTaskState.Processing, tasks.Tasks[0].State);
        Assert.Equal($"{One} - Sending", tasks.Current);
    }

    /// <summary>
    /// The receiver reports several stages and they all belong to one row — and reaching any of
    /// them means sending is over.
    /// </summary>
    [Fact]
    public void AReceiveStageClosesTheSendingTaskAndOpensTheReceivingOne()
    {
        var tasks = Two_Accounts();
        tasks.Report(new PollProgress(One, 0, 0, "Sending"));
        tasks.Report(new PollProgress(One, 0, 0, "Connecting"));

        Assert.Equal(TransferTaskState.Completed, tasks.Tasks[0].State);
        Assert.Equal(TransferTaskState.Processing, tasks.Tasks[1].State);

        tasks.Report(new PollProgress(One, 3, 10, "Downloading"));
        Assert.Equal("3 of 10", tasks.Tasks[1].Progress);
    }

    [Fact]
    public void AReportForAnUnknownAccountIsIgnored()
    {
        var tasks = Two_Accounts();
        tasks.Report(new PollProgress("stranger@example.com", 0, 0, "Sending"));

        Assert.All(tasks.Tasks, t => Assert.Equal(TransferTaskState.Waiting, t.State));
    }

    [Fact]
    public void FinishingSuccessfullyCompletesEveryTask()
    {
        var tasks = Two_Accounts();
        tasks.Finish(new SendReceiveResult(
        [
            new AccountRunResult(One, Received: 4, Sent: 1),
            new AccountRunResult(Two, Received: 0, Sent: 0),
        ]));

        Assert.True(tasks.IsFinished);
        Assert.Equal(4, tasks.Succeeded);
        Assert.Equal(0, tasks.Failed);
        Assert.Equal(1, tasks.Fraction);
        Assert.Empty(tasks.Errors);
        Assert.Equal("4 of 4 Tasks have completed successfully", tasks.Headline);
        Assert.Equal("4 received", tasks.Tasks[1].Progress);
    }

    /// <summary>
    /// A run reports one error per account rather than one per direction, so it lands on
    /// whichever half had not finished — which is what makes the table say where it broke.
    /// </summary>
    [Fact]
    public void AFailureBeforeSendingFinishesIsASendingFailure()
    {
        var tasks = Two_Accounts();
        tasks.Report(new PollProgress(One, 0, 0, "Sending"));
        tasks.Finish(new SendReceiveResult(
        [
            new AccountRunResult(One, 0, 0, "The server refused the password."),
        ]));

        Assert.Equal(TransferTaskState.Failed, tasks.Tasks[0].State);
        Assert.Equal(TransferTaskState.Completed, tasks.Tasks[1].State);
        Assert.Equal([$"{One}: The server refused the password."], tasks.Errors);
    }

    [Fact]
    public void AFailureAfterSendingIsAReceivingFailure()
    {
        var tasks = Two_Accounts();
        tasks.Report(new PollProgress(One, 0, 0, "Sending"));
        tasks.Report(new PollProgress(One, 0, 0, "Connecting"));
        tasks.Finish(new SendReceiveResult(
        [
            new AccountRunResult(One, 0, 2, "The mailbox is locked."),
        ]));

        Assert.Equal(TransferTaskState.Completed, tasks.Tasks[0].State);
        Assert.Equal(TransferTaskState.Failed, tasks.Tasks[1].State);
        Assert.Equal("Failed", tasks.Tasks[1].Progress);
    }

    /// <summary>
    /// A cancelled run leaves accounts it never reached. They are finished as far as the dialog
    /// is concerned, and they did not succeed — otherwise the bar sits half full forever.
    /// </summary>
    [Fact]
    public void AnAccountTheRunNeverReachedIsFinishedAndNotSucceeded()
    {
        var tasks = Two_Accounts();
        tasks.Finish(new SendReceiveResult([new AccountRunResult(One, 1, 0)]));

        Assert.True(tasks.IsFinished);
        Assert.Equal(2, tasks.Succeeded);
        Assert.Equal(2, tasks.Failed);
        Assert.Equal("Cancelled", tasks.Tasks[2].Progress);
    }

    [Fact]
    public void OneAccountReadsAsOneTaskInTheHeadline()
    {
        var tasks = new SendReceiveTasks([One]);
        tasks.Finish(new SendReceiveResult([new AccountRunResult(One, 0, 0)]));

        Assert.Equal("2 of 2 Tasks have completed successfully", tasks.Headline);
    }

    [Fact]
    public void NoAccountsMeansNothingToReport()
    {
        var tasks = new SendReceiveTasks([]);

        Assert.Equal(0, tasks.Total);
        Assert.Equal(0, tasks.Fraction);
        Assert.True(tasks.IsFinished);
        Assert.Equal(string.Empty, tasks.Current);
    }
}
