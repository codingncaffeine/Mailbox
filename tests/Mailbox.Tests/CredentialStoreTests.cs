using System.Diagnostics;
using Mailbox.Protocols;

namespace Mailbox.Tests;

/// <summary>
/// What happens when the keyring is slow, or never answers at all.
/// </summary>
/// <remarks>
/// These exist because of a freeze. Pressing Send/Receive read each account's password out of the
/// desktop keyring from the UI thread, with <c>GetAwaiter().GetResult()</c>; the continuation of
/// that read needed the UI thread to run on, and the UI thread was inside the wait. Neither could
/// move, there was no timeout, and the only way out was killing the process — no log, no dialog,
/// nothing on screen but a window that had stopped repainting.
/// <para>
/// Two rules came out of it, and both are checked here: a credential read must never need the
/// thread that is waiting for it, and it must give up rather than wait forever.
/// </para>
/// </remarks>
public class CredentialStoreTests
{
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>
    /// A store that never answers, standing in for a locked wallet whose prompt cannot be shown.
    /// </summary>
    private sealed class NeverAnswers : ICredentialStore
    {
        public bool IsAvailable => true;

        public string Description => "a keyring that has stopped answering";

        // Awaited rather than continued: a ContinueWith runs on any completion, so a cancelled
        // delay would come back as an answer of "no password" instead of as a cancellation —
        // which is the very confusion this fake exists to rule out.
        public async Task<bool> SaveAsync(string a, string p, string s, CancellationToken c = default)
        {
            await Task.Delay(Timeout.Infinite, c);
            return false;
        }

        public async Task<string?> LoadAsync(string a, string p, CancellationToken c = default)
        {
            await Task.Delay(Timeout.Infinite, c);
            return null;
        }

        public async Task<bool> DeleteAsync(string a, string p, CancellationToken c = default)
        {
            await Task.Delay(Timeout.Infinite, c);
            return false;
        }
    }

    /// <summary>
    /// A synchronization context with one thread behind it, which is what a UI thread is.
    /// </summary>
    /// <remarks>
    /// The deadlock needs exactly this shape to reproduce: a context that runs its callbacks on
    /// one particular thread, and code on that thread blocking on work whose continuation was
    /// posted back to it. Without a context, sync-over-async merely wastes a thread; with one, it
    /// stops forever, which is why this was invisible to a test suite that had none.
    /// </remarks>
    private sealed class OneThreadContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Work, object? State)> _queue = [];
        private readonly Thread _thread;

        public OneThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (work, state) in _queue.GetConsumingEnumerable()) work(state);
            })
            { IsBackground = true };

            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted) _queue.Add((d, state));
        }

        /// <summary>Runs something on the one thread and waits for it, as a UI event handler does.</summary>
        public Task RunAsync(Action work)
        {
            var done = new TaskCompletionSource();
            Post(_ =>
            {
                try { work(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
            }, null);

            return done.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    /// <summary>
    /// The freeze itself, in miniature: blocking on a credential read from a single-threaded
    /// context deadlocks unless the read refuses to come back to that thread.
    /// </summary>
    /// <remarks>
    /// It asserts on the in-memory store rather than the real one because the rule is about every
    /// store: whatever <see cref="ICredentialStore"/> is behind it, a caller must be able to wait
    /// without the answer needing the waiting thread. The real store's own awaits are
    /// <c>ConfigureAwait(false)</c> for exactly this.
    /// </remarks>
    [Fact]
    public async Task ACredentialReadDoesNotNeedTheThreadWaitingForIt()
    {
        using var context = new OneThreadContext();
        var store = new InMemoryCredentialStore();
        await store.SaveAsync("you@example.com", Credentials.Incoming, "hunter2", Stop);

        string? read = null;

        // Exactly what the shell used to do, on exactly the shape of thread it used to do it on.
        var blocking = context.RunAsync(
            () => read = store.LoadAsync("you@example.com", Credentials.Incoming).GetAwaiter().GetResult());

        var finished = await Task.WhenAny(blocking, Task.Delay(TimeSpan.FromSeconds(10), Stop));

        Assert.True(
            ReferenceEquals(finished, blocking),
            "A credential read blocked the thread it was posted back to — this is the freeze.");

        await blocking;
        Assert.Equal("hunter2", read);
    }

    /// <summary>
    /// A keyring that never answers must not be able to stop what asked it.
    /// </summary>
    /// <remarks>
    /// The caller's own cancellation is what does it here. The real store also carries a timeout
    /// of its own (<see cref="SecretServiceStore.Patience"/>), because a caller with no deadline
    /// is exactly the caller that gets stuck.
    /// </remarks>
    [Fact]
    public async Task AKeyringThatNeverAnswersCanBeGivenUpOn()
    {
        var store = new NeverAnswers();
        using var patience = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.LoadAsync("you@example.com", Credentials.Incoming, patience.Token));
    }

    /// <summary>
    /// The real store gives up on its own, whatever the caller does.
    /// </summary>
    /// <remarks>
    /// Skipped where <c>secret-tool</c> is not installed, because there is then nothing to time
    /// out — the store says so and answers immediately, which is the other correct behaviour.
    /// </remarks>
    [Fact]
    public async Task TheRealStoreHasAPatienceOfItsOwn()
    {
        Assert.True(SecretServiceStore.Patience > TimeSpan.Zero);
        Assert.True(SecretServiceStore.Patience < TimeSpan.FromMinutes(1),
            "A timeout longer than somebody will wait is not a timeout.");

        var installed = Available();
        Assert.SkipUnless(installed, "secret-tool is not installed, so there is nothing to ask.");

        // A lookup for something that is certainly not there: it has to come back, and quickly.
        var store = new SecretServiceStore();
        var clock = Stopwatch.StartNew();
        var answer = await store.LoadAsync($"nobody-{Guid.NewGuid():N}@example.com", Credentials.Incoming, Stop);
        clock.Stop();

        Assert.Null(answer);
        Assert.True(
            clock.Elapsed < SecretServiceStore.Patience + TimeSpan.FromSeconds(2),
            $"The keyring took {clock.Elapsed} for a lookup that should have been immediate.");
    }

    private static bool Available()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("secret-tool", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (probe is null) return false;
            probe.WaitForExit(2000);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The in-memory store is honest about being only this session's, which is what the account
    /// wizard tells a reader when no keyring is running.
    /// </summary>
    [Fact]
    public async Task TheInMemoryStoreKeepsAndForgets()
    {
        var store = new InMemoryCredentialStore();

        Assert.True(store.IsAvailable);
        Assert.Contains("session", store.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(await store.SaveAsync("you@example.com", Credentials.Incoming, "hunter2", Stop));
        Assert.Equal("hunter2", await store.LoadAsync("you@example.com", Credentials.Incoming, Stop));

        // Purposes do not collide: an incoming password is not an outgoing one.
        Assert.Null(await store.LoadAsync("you@example.com", Credentials.Outgoing, Stop));

        Assert.True(await store.DeleteAsync("you@example.com", Credentials.Incoming, Stop));
        Assert.Null(await store.LoadAsync("you@example.com", Credentials.Incoming, Stop));
    }
}
