using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>A POP3 server in memory.</summary>
internal sealed class FakePop3 : IPop3Session
{
    private readonly List<(string Uid, MimeMessage Message)> _messages = [];

    public List<string> Deleted { get; } = [];
    public int Fetches { get; private set; }
    public Exception? FailOnConnect { get; set; }
    public bool IsConnected { get; private set; }
    public int Count => _messages.Count;

    public FakePop3 With(string uid, string subject = "Hello")
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.MessageId = $"{uid}@example.com";
        message.Body = new TextPart("plain") { Text = $"Body of {subject}" };
        _messages.Add((uid, message));
        return this;
    }

    public Task ConnectAsync(ServerSettings s, CancellationToken c)
    {
        if (FailOnConnect is { } ex) throw ex;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task AuthenticateAsync(ServerSettings s, CancellationToken c) => Task.CompletedTask;

    public Task<IList<string>> GetUidsAsync(CancellationToken c)
        => Task.FromResult<IList<string>>([.. _messages.Select(m => m.Uid)]);

    public Task<MimeMessage> GetMessageAsync(int index, CancellationToken c)
    {
        Fetches++;
        return Task.FromResult(_messages[index].Message);
    }

    public Task DeleteAsync(IList<int> indexes, CancellationToken c)
    {
        Deleted.AddRange(indexes.Select(i => _messages[i].Uid));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken c)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

public class Pop3ReceiverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static AccountConnection Connection(Pop3Policy? policy = null) => new(
        1, "you@example.com",
        new ServerSettings("pop.example.com", 995),
        new ServerSettings("smtp.example.com", 587))
    { Policy = policy ?? new Pop3Policy() };

    private static Pop3Receiver Receiver(MailRepository repo, FakePop3 server)
        => new(repo) { SessionFactory = () => server };

    [Fact]
    public async Task DownloadsWhatIsThereAndFilesIt()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1", "First").With("uid-2", "Second");

        var result = await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(2, repo.Messages(inbox.Id).Count);
    }

    /// <summary>
    /// The behaviour POP3 lives or dies on. A second poll of an unchanged mailbox must download
    /// nothing — not re-file, not re-fetch. Fetching again would work but would cost the user
    /// their whole mailbox in bandwidth on every poll.
    /// </summary>
    [Fact]
    public async Task ASecondPollDownloadsNothingAndFetchesNothing()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1").With("uid-2");
        var receiver = Receiver(repo, server);

        await receiver.PollAsync(Connection(), inbox, null, Ct);
        var second = await receiver.PollAsync(Connection(), inbox, null, Ct);

        Assert.Equal(0, second.Downloaded);
        Assert.Equal(2, second.AlreadyHad);
        Assert.Equal(2, server.Fetches);           // not four
        Assert.Equal(2, repo.Messages(inbox.Id).Count);
    }

    [Fact]
    public async Task NewMailArrivingBesideOldIsTheOnlyThingDownloaded()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1");
        var receiver = Receiver(repo, server);
        await receiver.PollAsync(Connection(), inbox, null, Ct);

        server.With("uid-2", "Newly arrived");
        var second = await receiver.PollAsync(Connection(), inbox, null, Ct);

        Assert.Equal(1, second.Downloaded);
        Assert.Equal(1, second.AlreadyHad);
        Assert.Equal(2, server.Fetches);
    }

    /// <summary>
    /// The default has to be to leave mail alone. Someone trying Mailbox beside an existing
    /// client must not find their mailbox emptied.
    /// </summary>
    [Fact]
    public async Task NothingIsRemovedFromTheServerByDefault()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1").With("uid-2");

        var result = await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);

        Assert.Empty(server.Deleted);
        Assert.Equal(0, result.RemovedFromServer);
        Assert.True(Connection().Policy.LeaveOnServer);
    }

    [Fact]
    public async Task MailIsRemovedOnlyWhenAskedFor()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1").With("uid-2");
        var policy = new Pop3Policy { LeaveOnServer = false };

        var result = await Receiver(repo, server).PollAsync(Connection(policy), inbox, null, Ct);

        Assert.Equal(["uid-1", "uid-2"], server.Deleted);
        Assert.Equal(2, result.RemovedFromServer);
        Assert.Equal(2, repo.Messages(inbox.Id).Count);   // filed before removal
    }

    [Fact]
    public async Task APollStopsAtTheCap()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3();
        for (var i = 0; i < 10; i++) server.With($"uid-{i}");

        var result = await Receiver(repo, server)
            .PollAsync(Connection(new Pop3Policy { MaxPerPoll = 4 }), inbox, null, Ct);

        Assert.Equal(4, result.Downloaded);
        Assert.Equal(4, server.Fetches);
    }

    /// <summary>
    /// A poll that fails reports rather than throws: a send/receive runs several accounts and
    /// one unreachable server must not stop the rest.
    /// </summary>
    [Fact]
    public async Task AFailedPollIsReportedNotThrown()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3
        {
            FailOnConnect = new System.Net.Sockets.SocketException(),
        };

        var result = await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("Could not reach the server", result.Error);
    }

    [Fact]
    public async Task CancellingAPollStopsIt()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3();
        for (var i = 0; i < 5; i++) server.With($"uid-{i}");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Receiver(repo, server).PollAsync(Connection(), inbox, null, cancel.Token));
    }

    [Fact]
    public async Task TheRawMessageIsKeptAlongsideTheParsedRow()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var server = new FakePop3().With("uid-1", "Keep me");

        await Receiver(repo, server).PollAsync(Connection(), inbox, null, Ct);
        var filed = repo.Messages(inbox.Id).Single();
        var raw = repo.LoadRaw(filed.Id)!;

        Assert.Contains("Keep me", System.Text.Encoding.UTF8.GetString(raw));
        Assert.Equal("alice@example.com", filed.FromAddress);
    }

    [Fact]
    public async Task AnEmptyMailboxIsNotAnError()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var result = await Receiver(repo, new FakePop3()).PollAsync(Connection(), inbox, null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Downloaded);
    }
}
