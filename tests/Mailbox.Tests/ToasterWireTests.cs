using Mailbox.Protocols;

namespace Mailbox.Tests;

/// <summary>
/// The line format between the application and its toaster process.
/// </summary>
/// <remarks>
/// The claim the toaster rests on is that its table is the application's table: the same class,
/// fed the same reports, ending in the same state. <see cref="TheToastersTableIsTheApplicationsTable"/>
/// is that claim, run through the wire both ways, and the two after it are the same claim for a
/// toaster that starts late; the rest are what the wire has to survive to make it.
/// </remarks>
public class ToasterWireTests
{
    private const string One = "you@example.com";
    private const string Two = "other@example.com";

    public static TheoryData<ToasterMessage> Messages() =>
    [
        ToasterMessage.Begin.For(new SendReceiveTasks([One, Two]), 5160, 720),
        ToasterMessage.Begin.For(MidRun(), -1720, 0),
        new ToasterMessage.Progress(new PollProgress(One, 3, 8, "Downloading")),
        new ToasterMessage.Finish([new AccountRunResult(One, 8, 2), new AccountRunResult(Two, 0, 0, "The server could not be reached.")]),
        new ToasterMessage.Show(),
        new ToasterMessage.Close(),
        new ToasterMessage.Ready(),
        new ToasterMessage.Shown("Windowing: X11 through XWayland"),
        new ToasterMessage.CancelAll(),
        new ToasterMessage.HideChanged(true),
        new ToasterMessage.HideChanged(false),
        new ToasterMessage.Closed(),
    ];

    [Theory]
    [MemberData(nameof(Messages))]
    public void EveryMessageComesBackAsItWent(ToasterMessage message)
    {
        var back = ToasterWire.Decode(ToasterWire.Encode(message));

        Assert.NotNull(back);
        Assert.Equal(message.GetType(), back.GetType());

        // Field by field, lists included — records compare lists by reference, and comparing two
        // encodings instead could not see an encoder that dropped a field on both trips.
        Assert.Equivalent(message, back, strict: true);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void EveryMessageIsOneLine(ToasterMessage message)
    {
        var line = ToasterWire.Encode(message);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal(line, new StringReader(line).ReadLine());
    }

    [Fact]
    public void AServersErrorWithANewlineAndQuotesArrivesWhole()
    {
        const string said = "-ERR \"mailbox locked\"\r\n+OK\tsomething else";
        var line = ToasterWire.Encode(new ToasterMessage.Finish([new AccountRunResult(One, 0, 0, said)]));

        Assert.DoesNotContain('\n', line);
        var back = Assert.IsType<ToasterMessage.Finish>(ToasterWire.Decode(line));
        Assert.Equal(said, Assert.Single(back.Accounts).Error);
    }

    [Fact]
    public void AnAccountThatWorkedHasNoError()
    {
        var line = ToasterWire.Encode(new ToasterMessage.Finish([new AccountRunResult(One, 4, 1)]));
        var back = Assert.IsType<ToasterMessage.Finish>(ToasterWire.Decode(line));

        Assert.True(Assert.Single(back.Accounts).Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"op\":\"launch-missiles\"}")]
    [InlineData("{\"op\":\"progress\",\"account\":\"a@b\"}")]
    [InlineData("{\"op\":\"begin\",\"addresses\":[1,2],\"rows\":[],\"errors\":[],\"x\":0,\"y\":0}")]
    [InlineData("{\"op\":\"begin\",\"addresses\":[\"a@b\"],\"rows\":[],\"errors\":[],\"x\":0,\"y\":0}")]
    [InlineData("{\"op\":\"begin\",\"addresses\":[\"a@b\"],\"rows\":[{\"name\":\"a@b - Sending\",\"state\":\"Exploded\",\"progress\":\"\"},{\"name\":\"a@b - Receiving\",\"state\":\"Waiting\",\"progress\":\"\"}],\"errors\":[],\"x\":0,\"y\":0}")]
    [InlineData("{\"op\":\"begin\",\"addresses\":[\"a@b\"],\"rows\":[{\"name\":\"a@b - Sending\",\"state\":\"7\",\"progress\":\"\"},{\"name\":\"a@b - Receiving\",\"state\":\"Waiting\",\"progress\":\"\"}],\"errors\":[],\"x\":0,\"y\":0}")]
    [InlineData("{\"op\":\"hide\",\"hidden\":\"yes\"}")]
    [InlineData("{\"op\":\"finish\",\"accounts\":[{\"address\":\"a@b\"}]}")]
    [InlineData("{\"op\":")]
    public void ADamagedLineIsDroppedRatherThanGuessedAt(string? line)
        => Assert.Null(ToasterWire.Decode(line));

    [Fact]
    public void TheToastersTableIsTheApplicationsTable()
    {
        PollProgress[] reports =
        [
            new(One, 0, 0, "Sending"),
            new(One, 0, 0, "Connecting"),
            new(One, 3, 8, "Downloading"),
            new(Two, 0, 0, "Sending"),
            new(Two, 0, 0, "Connecting"),
        ];
        var result = new SendReceiveResult(
        [
            new AccountRunResult(One, 8, 2),
            new AccountRunResult(Two, 0, 0, "The server could not be reached."),
        ]);

        // The application's own table, fed directly.
        var application = new SendReceiveTasks([One, Two]);
        foreach (var report in reports) application.Report(report);

        // The toaster's, fed only what came down the wire.
        var begin = Assert.IsType<ToasterMessage.Begin>(
            ToasterWire.Decode(ToasterWire.Encode(ToasterMessage.Begin.For(new SendReceiveTasks([One, Two]), 0, 0))));
        var toaster = begin.Table();
        foreach (var report in reports)
        {
            var sent = Assert.IsType<ToasterMessage.Progress>(
                ToasterWire.Decode(ToasterWire.Encode(new ToasterMessage.Progress(report))));
            toaster.Report(sent.Report);
        }

        AssertSame(application, toaster);

        application.Finish(result);
        var finish = Assert.IsType<ToasterMessage.Finish>(
            ToasterWire.Decode(ToasterWire.Encode(new ToasterMessage.Finish(result.Accounts))));
        toaster.Finish(new SendReceiveResult(finish.Accounts));

        AssertSame(application, toaster);
        Assert.True(toaster.IsFinished);
        Assert.Single(toaster.Errors);
    }

    /// <summary>
    /// A toaster that starts after the run has begun reporting shows what the run has done.
    /// </summary>
    /// <remarks>
    /// Found by setting the toaster's photograph against the in-process dialog's: a toaster that
    /// was sent the account list and not the table showed the second account's Sending row still
    /// waiting, because the report that set it Processing had arrived before the toaster did. And
    /// it keeps up afterwards — the next report lands on the same rows either side.
    /// </remarks>
    [Fact]
    public void AToasterStartedMidRunStandsWhereTheRunStands()
    {
        var application = MidRun();
        var toaster = Assert.IsType<ToasterMessage.Begin>(
            ToasterWire.Decode(ToasterWire.Encode(ToasterMessage.Begin.For(application, 0, 0)))).Table();

        AssertSame(application, toaster);
        Assert.Contains(toaster.Tasks, t => t.Name == $"{Two} - Sending" && t.State == TransferTaskState.Processing);

        var next = new PollProgress(One, 5, 9, "Downloading");
        application.Report(next);
        toaster.Report(next);
        AssertSame(application, toaster);
    }

    /// <summary>A toaster that starts after a run has failed shows the failure, not a run that never began.</summary>
    [Fact]
    public void AToasterStartedAfterAFailureShowsTheFailure()
    {
        var application = MidRun();
        application.Finish(new SendReceiveResult(
        [
            new AccountRunResult(One, 8, 2),
            new AccountRunResult(Two, 0, 0, "The server could not be reached."),
        ]));

        var toaster = Assert.IsType<ToasterMessage.Begin>(
            ToasterWire.Decode(ToasterWire.Encode(ToasterMessage.Begin.For(application, 0, 0)))).Table();

        AssertSame(application, toaster);
        Assert.True(toaster.IsFinished);
        Assert.Equal($"{Two}: The server could not be reached.", Assert.Single(toaster.Errors));
    }

    [Fact]
    public void ATableIsNotRebuiltFromTheWrongNumberOfRows()
        => Assert.Throws<ArgumentException>(() => SendReceiveTasks.From(
            [One, Two], new SendReceiveTasks([One]).Tasks, []));

    /// <summary>The posed run the harness's progress doors show: one account downloading, the other sending.</summary>
    private static SendReceiveTasks MidRun()
    {
        var tasks = new SendReceiveTasks([One, Two]);
        tasks.Report(new PollProgress(One, 0, 0, "Sending"));
        tasks.Report(new PollProgress(One, 0, 0, "Connecting"));
        tasks.Report(new PollProgress(One, 3, 8, "Downloading"));
        tasks.Report(new PollProgress(Two, 0, 0, "Sending"));
        return tasks;
    }

    private static void AssertSame(SendReceiveTasks expected, SendReceiveTasks actual)
    {
        Assert.Equal(expected.Headline, actual.Headline);
        Assert.Equal(expected.Current, actual.Current);
        Assert.Equal(expected.Fraction, actual.Fraction);
        Assert.Equal(expected.IsFinished, actual.IsFinished);
        Assert.Equal(expected.Tasks, actual.Tasks);
        Assert.Equal(expected.Errors, actual.Errors);
    }
}
