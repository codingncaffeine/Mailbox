using System.Collections.Concurrent;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Tests;

/// <summary>
/// The dispatcher stall watchdog: it catches the UI thread when it blocks, and stays quiet when
/// it does not.
/// </summary>
/// <remarks>
/// Driven against a fake UI thread — one thread draining a queue — because that is exactly what
/// the watchdog watches: a single thread that runs the callbacks posted to it, and blocks when
/// something on it does too much. The real one is Avalonia's dispatcher; the shape is the same.
/// </remarks>
public sealed class DispatcherStallWatchdogTests
{
    /// <summary>A single thread that runs what is posted to it, and can be made to block.</summary>
    private sealed class FakeUiThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public FakeUiThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable()) work();
            })
            { IsBackground = true };
            _thread.Start();
        }

        public void Post(Action action) => _queue.Add(action);

        /// <summary>Blocks the thread for a while, the way a slow synchronous call would.</summary>
        public void BlockFor(TimeSpan duration) => Post(() => Thread.Sleep(duration));

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public void ItCatchesTheUiThreadWhenItBlocks()
    {
        using var ui = new FakeUiThread();
        using var watchdog = new DispatcherStallWatchdog(ui.Post, TimeSpan.FromMilliseconds(100));
        watchdog.Start();

        // Let it settle into a clean rhythm, then block the thread for well over the threshold.
        Thread.Sleep(150);
        ui.BlockFor(TimeSpan.FromMilliseconds(600));

        // Long enough for the blocked ping to come back and be timed.
        Thread.Sleep(900);

        var (count, worst) = watchdog.Summary;
        Assert.True(count >= 1, $"expected a stall to be caught, saw {count}");
        Assert.True(worst >= 400, $"the stall should have been reported near its real length, was {worst:0}ms");
    }

    [Fact]
    public void ItStaysQuietWhenTheUiThreadKeepsUp()
    {
        using var ui = new FakeUiThread();

        // A threshold generous enough that a shared CI machine's own scheduling hiccup is not
        // mistaken for the code stalling — the test is about the watchdog's quiet, not the
        // host's jitter. Its work units are an order of magnitude under it.
        using var watchdog = new DispatcherStallWatchdog(ui.Post, TimeSpan.FromMilliseconds(1000));
        watchdog.Start();

        // A thread that only ever does tiny units of work never stalls, and the watchdog says so
        // by never firing — the property that keeps it from being noise in an ordinary run.
        for (var i = 0; i < 10; i++)
        {
            ui.Post(() => Thread.Sleep(5));
            Thread.Sleep(50);
        }

        Assert.Equal(0, watchdog.Summary.Count);
    }
}
