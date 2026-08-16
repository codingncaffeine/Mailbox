using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The IDLE watcher: a change the server reports raises the event the shell syncs on, a server
/// that cannot IDLE stops quietly for the poll timer to cover, and a dropped connection is a
/// reconnect rather than a crash.
/// </summary>
public class ImapIdleTests
{
    private static AccountConnection Connection() => new(
        1, "you@example.com",
        new ServerSettings("imap.example.com", 993),
        new ServerSettings("smtp.example.com", 587))
    { Protocol = MailProtocol.Imap };

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail(because);
    }

    [Fact]
    public async Task AServerChangeRaisesTheSyncEvent()
    {
        var server = new FakeImap();
        server.Deliver("INBOX", "Waiting");

        using var watcher = new ImapIdleWatcher(Connection(), () => server)
        {
            RenewAfter = TimeSpan.FromSeconds(30),
        };

        var changes = 0;
        watcher.ChangeDetected += (_, address) =>
        {
            Assert.Equal("you@example.com", address);
            Interlocked.Increment(ref changes);
        };

        watcher.Start();
        await Eventually(() => server.IsIdling, "the watcher should reach IDLE");

        server.Raise();
        await Eventually(() => Volatile.Read(ref changes) >= 1, "a server change should raise the event");
    }

    [Fact]
    public async Task AServerWithoutIdleStopsQuietly()
    {
        var server = new FakeImap { Features = ImapFeatures.CondStore };

        using var watcher = new ImapIdleWatcher(Connection(), () => server);
        var raised = false;
        watcher.ChangeDetected += (_, _) => raised = true;

        watcher.Start();

        // It connects, sees no IDLE, and returns. Nothing is raised, and it does not spin.
        await Eventually(() => !watcher.IsRunning, "the watcher should stop when IDLE is unsupported");
        server.Raise();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(raised);
    }

    [Fact]
    public async Task ADroppedConnectionIsReconnectedRatherThanFatal()
    {
        var attempts = 0;
        var good = new FakeImap();

        // The first session throws on connect; the watcher backs off and tries again.
        IImapSession Factory()
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return new ThrowingImap();
            }

            return good;
        }

        using var watcher = new ImapIdleWatcher(Connection(), Factory)
        {
            InitialBackoff = TimeSpan.FromMilliseconds(20),
            MaxBackoff = TimeSpan.FromMilliseconds(20),
        };

        watcher.Start();

        await Eventually(() => Volatile.Read(ref attempts) >= 2, "a failed connection should be retried");
        await Eventually(() => watcher.IsRunning, "the watcher should be idling after reconnecting");
    }

    /// <summary>A session that fails to connect, for the reconnect path.</summary>
    private sealed class ThrowingImap : IImapSession
    {
        public bool IsConnected => false;
        public ImapFeatures Features => ImapFeatures.Idle;
        public event EventHandler? FolderChanged { add { } remove { } }

        public Task ConnectAsync(ServerSettings s, CancellationToken c)
            => throw new IOException("connection refused");

        public Task AuthenticateAsync(ServerSettings s, CancellationToken c) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken c) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken c) => Task.FromResult<IReadOnlyList<RemoteFolder>>([]);
        public Task<RemoteFolder> CreateFolderAsync(string name, CancellationToken c, string? parentPath = null) => throw new NotSupportedException();
        public Task<RemoteFolder> RenameFolderAsync(string path, string newName, CancellationToken c) => throw new NotSupportedException();
        public Task DeleteFolderAsync(string path, CancellationToken c) => throw new NotSupportedException();
        public Task<FolderState> OpenAsync(string path, CancellationToken c) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> SearchAllAsync(CancellationToken c) => Task.FromResult<IReadOnlyList<long>>([]);
        public Task<IReadOnlyList<long>> SearchByMessageIdAsync(string messageId, CancellationToken c) => Task.FromResult<IReadOnlyList<long>>([]);
        public Task<IReadOnlyList<RemoteMessageInfo>> FetchInfoAsync(IReadOnlyList<long> uids, CancellationToken c) => Task.FromResult<IReadOnlyList<RemoteMessageInfo>>([]);
        public Task<IReadOnlyList<RemoteMessageInfo>> FetchFlagsChangedSinceAsync(long modSeq, CancellationToken c) => Task.FromResult<IReadOnlyList<RemoteMessageInfo>>([]);
        public Task<MimeKit.MimeMessage?> GetMessageAsync(long uid, CancellationToken c) => Task.FromResult<MimeKit.MimeMessage?>(null);
        public Task StoreFlagsAsync(IReadOnlyList<long> uids, MailKit.MessageFlags flags, bool set, CancellationToken c) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<long, long>> MoveAsync(IReadOnlyList<long> uids, string destinationPath, CancellationToken c) => Task.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>());
        public Task ExpungeAsync(IReadOnlyList<long> uids, CancellationToken c) => Task.CompletedTask;
        public Task<long?> AppendAsync(string path, byte[] raw, MailKit.MessageFlags flags, DateTimeOffset? date, CancellationToken c) => Task.FromResult<long?>(null);
        public Task IdleAsync(CancellationToken done, CancellationToken c) => Task.CompletedTask;
        public void Dispose() { }
    }
}
