using Mailbox.App.Views;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The reading pane hands its engine one document at a time, and the one it waits with is the
/// newest.
/// </summary>
/// <remarks>
/// The pane itself cannot be asked this: proving it there needs a machine with WebKit, a window
/// on screen and a race that showed up about one run in twelve. The rule the race was about is
/// separable, so this holds the rule — a load lands only when nothing is in flight, a reader
/// moving faster than the engine loses the rows they passed through rather than the row they
/// stopped on, and a wait that is never answered still ends.
/// </remarks>
public class ReadingPaneLoadTests
{
    [Fact]
    public void ASecondLoadWaitsForTheFirst()
    {
        var loads = new ReadingPaneLoads();

        Assert.Equal("first", loads.Ask("first"));
        Assert.Null(loads.Ask("second"));

        // Still the first document's load, and nothing has been handed to the engine behind it.
        Assert.Equal(1, loads.Ran);
        Assert.Equal(2, loads.Asked);
        Assert.NotEqual(0, loads.InFlight);
    }

    [Fact]
    public void TheDocumentThatWaitsIsTheNewestOne()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("first");
        loads.Ask("second");
        loads.Ask("third");
        loads.Ask("fourth");

        loads.Finished();

        // The two in the middle are rows the reader passed through on the way. What the pane owes
        // them is where they stopped.
        Assert.Equal("fourth", loads.Next());
        Assert.Equal(4, loads.Asked);
        Assert.Equal(2, loads.Ran);
    }

    [Fact]
    public void NothingStartsWhileTheEngineIsStillLoading()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("first");
        loads.Ask("second");

        // The completion event has not arrived, so the second document stays where it is.
        Assert.Null(loads.Next());
        Assert.Equal(1, loads.Ran);
    }

    [Fact]
    public void AFinishedLoadWithNothingWaitingStartsNothing()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("only");
        loads.Finished();

        Assert.Null(loads.Next());
        Assert.Equal(1, loads.Ran);
    }

    [Fact]
    public void EachLoadIsANumberOfItsOwn()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("first");
        var first = loads.InFlight;

        loads.Ask("second");
        loads.Finished();
        loads.Next();

        // What the nudges and the read-backs compare themselves against: work started for the
        // first load can tell that it is no longer the load on show.
        Assert.NotEqual(first, loads.InFlight);
        Assert.Equal(loads.InFlight, loads.Started);
    }

    [Fact]
    public void AnEngineThatNeverAnswersIsStillWaitedOn()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("first");
        var stuck = loads.InFlight;
        loads.Ask("second");

        Assert.True(loads.StillWaitingOn(stuck));

        // Which is what the pane's watchdog does about it: give up on the load in flight and let
        // the document behind it past.
        loads.Finished();
        Assert.False(loads.StillWaitingOn(stuck));
        Assert.Equal("second", loads.Next());
    }

    [Fact]
    public void AWaitThatWasAnsweredIsNotStuck()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("first");
        var first = loads.InFlight;
        loads.Ask("second");
        loads.Finished();
        loads.Next();

        // The watchdog armed for the first load must not fire over the second one's load.
        Assert.False(loads.StillWaitingOn(first));
    }

    [Fact]
    public void ALoadWithNothingBehindItDoesNotWait()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("first");
        var first = loads.InFlight;

        // What the pane does when there is no engine running behind it — the reading pane
        // switched off, where a load is never answered for and nothing can be raced.
        Assert.Equal("second", loads.Now("second"));
        Assert.NotEqual(first, loads.InFlight);
        Assert.Equal(2, loads.Asked);
        Assert.Equal(2, loads.Ran);

        // And it takes the place of the document that was waiting, rather than joining it.
        Assert.Null(loads.Next());
    }

    [Fact]
    public void ALoadTakenNowDropsWhatWasWaiting()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("first");
        loads.Ask("second");

        Assert.Equal("third", loads.Now("third"));

        loads.Finished();
        Assert.Null(loads.Next());
    }

    [Fact]
    public void AnEngineThatHasGoneLeavesNothingBehind()
    {
        var loads = new ReadingPaneLoads();

        loads.Ask("first");
        var first = loads.InFlight;
        loads.Ask("second");

        loads.Forget();

        Assert.Equal(0, loads.InFlight);
        Assert.Null(loads.Next());
        Assert.False(loads.StillWaitingOn(first));

        // And the number moved on, so a nudge still running for the last load stops rather than
        // scripting a view whose engine is being torn down.
        Assert.NotEqual(first, loads.Started);
    }
}
