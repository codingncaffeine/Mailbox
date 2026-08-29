using System.Diagnostics;

namespace Mailbox.Core.Diagnostics;

/// <summary>
/// Watches the UI thread and writes its own evidence when it stalls.
/// </summary>
/// <remarks>
/// A blocked UI thread is the one fault a mail client cannot hide and cannot easily diagnose
/// after the fact: the window stops repainting, the reader waits, and nothing in the log says
/// which call did it. This is the standing answer — a background thread (its own, because a
/// dispatcher that is blocked cannot run a timer of its own) pings the UI thread on an interval
/// and measures how long the ping takes to come back. A round trip longer than the threshold is
/// a stall of that length, and it is logged the moment the thread frees rather than lost.
/// <para>
/// Off unless asked for, because the point is to carry no cost in an ordinary run:
/// <c>MAILBOX_STALL_WATCHDOG=1</c> turns it on, or <c>=&lt;ms&gt;</c> sets the threshold. Debug
/// logging turns it on too, so a session already asking for detail gets this with it. The
/// interval is a quarter of the threshold, so a stall is caught within one ping of ending.
/// </para>
/// <para>
/// It measures rather than interrupts: it never touches the UI thread's work, only posts a
/// no-op onto its queue and times it. What it cannot do from a background thread is walk the
/// stalled thread's stack — that needs the platform's own tools (<c>dotnet-stack</c> against the
/// live process, which the audit's rules of evidence already reach for) — so it reports the
/// duration and the moment, which is what names the window of code to look in.
/// </para>
/// </remarks>
public sealed class DispatcherStallWatchdog : IDisposable
{
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _interval;
    private readonly Action<Action> _postToUi;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Whether a ping is on the UI thread's queue, so a stalled thread is not sent more.</summary>
    private int _pingInFlight;

    /// <summary>How many stalls have been seen, for the summary at the end.</summary>
    private int _stalls;

    /// <summary>The longest stall seen, in milliseconds.</summary>
    private double _worstMs;

    /// <param name="postToUi">
    /// How to put a callback on the UI thread's queue — <c>Dispatcher.UIThread.Post</c> in the
    /// application, or anything that marshals onto the thread being watched in a test.
    /// </param>
    /// <param name="threshold">A round trip longer than this is a stall. Default 500ms.</param>
    public DispatcherStallWatchdog(Action<Action> postToUi, TimeSpan? threshold = null)
    {
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _threshold = threshold ?? TimeSpan.FromMilliseconds(500);

        // A quarter of the threshold, floored at 50ms: frequent enough that a stall is caught
        // just after it ends, cheap enough that the watching itself is not the thing being felt.
        var quarter = TimeSpan.FromMilliseconds(Math.Max(25, _threshold.TotalMilliseconds / 4));
        _interval = quarter;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "mailbox-stall-watchdog",
        };
    }

    /// <summary>Whether an ordinary run should carry the watchdog: only when asked, or in debug.</summary>
    public static bool Requested
    {
        get
        {
            var setting = Environment.GetEnvironmentVariable("MAILBOX_STALL_WATCHDOG");
            if (setting is { Length: > 0 } && setting != "0") return true;
            return Log.Minimum == LogLevel.Debug;
        }
    }

    /// <summary>The threshold a run asked for, or the default.</summary>
    public static TimeSpan RequestedThreshold
        => Environment.GetEnvironmentVariable("MAILBOX_STALL_WATCHDOG") is { Length: > 0 } value
           && int.TryParse(value, out var ms) && ms > 1
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromMilliseconds(500);

    public void Start()
    {
        Log.Info($"Dispatcher stall watchdog on — threshold {_threshold.TotalMilliseconds:0}ms, "
                 + $"ping every {_interval.TotalMilliseconds:0}ms.");
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            // One ping outstanding at a time. While the UI thread is blocked its callback has not
            // run, so this stays 1 and no more are piled on; the one already queued times the whole
            // stall when the thread frees. When it is 0, the last ping came back and it is time for
            // the next.
            if (Interlocked.CompareExchange(ref _pingInFlight, 1, 0) == 0)
            {
                var sent = Stopwatch.GetTimestamp();
                _postToUi(() =>
                {
                    var waited = Stopwatch.GetElapsedTime(sent);
                    Volatile.Write(ref _pingInFlight, 0);

                    if (waited > _threshold)
                    {
                        Interlocked.Increment(ref _stalls);
                        if (waited.TotalMilliseconds > _worstMs) _worstMs = waited.TotalMilliseconds;
                        Log.Warn($"Dispatcher stalled for {waited.TotalMilliseconds:0}ms — the UI thread "
                                 + "was blocked. Something on it did more work than a frame allows; "
                                 + "run dotnet-stack against the process to see what.");
                    }
                });
            }

            // A kernel wait on this thread, not Task.Delay: a delay task wakes through the
            // thread pool, and a starved pool — which is company the stalls being hunted often
            // keep — stretches the cadence from milliseconds to seconds. The wait ends early
            // when the token's handle is signalled, which is what stopping does.
            if (_stop.Token.WaitHandle.WaitOne(_interval)) return;
        }
    }

    /// <summary>How many stalls were seen, and the worst, for a test or a shutdown line to read.</summary>
    public (int Count, double WorstMs) Summary => (_stalls, _worstMs);

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();

        // The loop wakes on the cancel and leaves; seen out before the token is disposed, so
        // its wait handle cannot be pulled from under a thread still waiting on it.
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));

        if (_stalls > 0)
        {
            Log.Info($"Dispatcher stall watchdog off — {_stalls} stall(s), worst {_worstMs:0}ms.");
        }

        _stop.Dispose();
    }
}
