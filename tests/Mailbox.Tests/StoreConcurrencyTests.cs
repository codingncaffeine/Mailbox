using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Two threads on one store: the poll writes while the interface reads and writes.
/// </summary>
/// <remarks>
/// This is the arrangement the application actually runs in — a send/receive on a thread-pool
/// thread, the message list and the reading pane on the interface thread — and the one the store
/// used to answer badly. All three of these fail against a store with a single unguarded
/// connection: the first loses a write, the second blocks the interface for as long as a poll's
/// transaction lasts, and the third hands back another thread's row id.
/// <para>
/// A file rather than <c>:memory:</c>, because an in-memory store is one connection by
/// definition and these are about what happens when there is more than one.
/// </para>
/// </remarks>
public class StoreConcurrencyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mailbox-store-threads", Guid.NewGuid().ToString("n"));

    private MailStore Open() => new(Path.Combine(_directory, "mail.db"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static long Accounts(MailStore store) =>
        store.ScalarLong("SELECT count(*) FROM accounts");

    private static void Add(MailStore store, string address) => store.Execute(
        "INSERT INTO accounts (address, protocol, created_utc) VALUES ($a, 'pop3', 0)",
        ("$a", address));

    /// <summary>How long a thread may be left waiting before the test calls it stuck.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AWriteFromAnotherThreadIsNotSweptIntoATransactionThatRollsBack()
    {
        using var store = Open();

        var open = new ManualResetEventSlim();
        var attempted = new ManualResetEventSlim();

        // The poll's transaction: writes, lets the other thread try, then fails.
        var poll = Task.Run(
            () =>
        {
            try
            {
                store.InTransaction<object?>(() =>
                {
                    Add(store, "poll@example.com");
                    open.Set();

                    // Long enough for the other thread to have reached its write and be waiting
                    // on the gate. If it has not, the assertions below still hold — what must
                    // never happen is that its row disappears with this one.
                    attempted.Wait(Patience, Ct);
                    Thread.Sleep(150);

                    throw new InvalidOperationException("the poll failed");
                });
            }
            catch (InvalidOperationException)
            {
                // Expected: this is the rollback the reader's write must survive.
            }
        },
            Ct);

        Assert.True(open.Wait(Patience, Ct), "the transaction never opened");

        var interface_ = Task.Run(
            () =>
            {
                attempted.Set();
                Add(store, "reader@example.com");
            },
            Ct);

        await Task.WhenAll(poll, interface_).WaitAsync(Patience, Ct);

        var addresses = store.Query("SELECT address FROM accounts", r => r.GetString(0));

        Assert.Equal(["reader@example.com"], addresses);
    }

    [Fact]
    public async Task AReadFromAnotherThreadDoesNotWaitForATransactionToFinish()
    {
        using var store = Open();
        Add(store, "before@example.com");

        var open = new ManualResetEventSlim();
        var read = new ManualResetEventSlim();

        var poll = Task.Run(
            () => store.InTransaction<object?>(() =>
        {
            Add(store, "during@example.com");
            open.Set();

            // Held until the read has happened. A store that made readers wait on the writer
            // would deadlock here rather than fail an assertion, which is what Patience is for.
            Assert.True(read.Wait(Patience, Ct), "the read never finished");
            return null;
        }),
            Ct);

        Assert.True(open.Wait(Patience, Ct), "the transaction never opened");

        // The interface's own read, while the poll holds its transaction. It sees the store as
        // it was before the transaction, which is what a snapshot means — and it sees it now.
        Assert.Equal(1, Accounts(store));
        read.Set();

        await poll.WaitAsync(Patience, Ct);
        Assert.Equal(2, Accounts(store));
    }

    [Fact]
    public async Task TheRowIdBelongsToTheThreadThatInsertedIt()
    {
        using var store = Open();

        var ready = new Barrier(2);
        var ids = new long[2];

        void Insert(int which, string address)
        {
            ready.SignalAndWait();

            for (var round = 0; round < 40; round++)
            {
                Add(store, $"{address}-{round}@example.com");
                var mine = store.LastInsertId;

                // The id has to name the row this thread just wrote, whatever the other thread
                // is doing to the same table at the same moment.
                var written = store.Query(
                    "SELECT address FROM accounts WHERE id = $id", r => r.GetString(0), ("$id", mine));

                Assert.Equal([$"{address}-{round}@example.com"], written);
                ids[which] = mine;
            }
        }

        var first = Task.Run(() => Insert(0, "one"), Ct);
        var second = Task.Run(() => Insert(1, "two"), Ct);

        await Task.WhenAll(first, second).WaitAsync(Patience, Ct);
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(80, Accounts(store));
    }
}
