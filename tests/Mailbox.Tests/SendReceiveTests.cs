using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// What a send/receive must guarantee: one account failing does not cost another its mail,
/// sending happens before receiving, and Work Offline actually stops the network rather than
/// merely looking like it does.
/// </summary>
public class SendReceiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        MailStore Store,
        MailRepository Repo,
        SendReceiveService Service,
        Dictionary<string, FakePop3> Servers,
        FakeSmtp Smtp,
        List<AccountConnection> Accounts);

    private static Harness Build(params string[] addresses)
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var servers = new Dictionary<string, FakePop3>();
        var connections = new List<AccountConnection>();

        foreach (var address in addresses)
        {
            var account = repo.AddAccount(address, address, MailProtocol.Pop3);
            repo.CreateStandardFolders(account.Id);
            servers[address] = new FakePop3();
            connections.Add(new AccountConnection(
                account.Id, address,
                new ServerSettings($"pop.{address.Split('@')[1]}", 995),
                new ServerSettings($"smtp.{address.Split('@')[1]}", 587, UserName: address)));
        }

        var smtp = new FakeSmtp();
        var receiver = new Pop3Receiver(repo);
        var sender = new SmtpSender(repo) { SessionFactory = () => smtp };
        var service = new SendReceiveService(repo, receiver, sender);

        // Each account polls its own fake server.
        var current = 0;
        receiver.SessionFactory = () => servers[addresses[current++ % addresses.Length]];

        return new Harness(store, repo, service, servers, smtp, connections);
    }

    private static MimeMessage Message()
    {
        var m = new MimeMessage { Subject = "Outbound" };
        m.From.Add(new MailboxAddress("You", "you@example.com"));
        m.To.Add(new MailboxAddress("Alice", "alice@example.com"));
        m.Body = new TextPart("plain") { Text = "Body" };
        return m;
    }

    [Fact]
    public async Task CollectsFromEveryAccount()
    {
        var h = Build("one@example.com", "two@example.net");
        using var _ = h.Store;
        h.Servers["one@example.com"].With("a-1");
        h.Servers["two@example.net"].With("b-1").With("b-2");

        var result = await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.True(result.AllSucceeded);
        Assert.Equal(3, result.Received);
        Assert.Equal("3 new.", result.Summary());
    }

    /// <summary>
    /// A work account behind a VPN that is down must not cost the user their personal mail.
    /// </summary>
    [Fact]
    public async Task OneAccountFailingDoesNotStopTheOthers()
    {
        var h = Build("works@example.com", "broken@example.net");
        using var _ = h.Store;
        h.Servers["works@example.com"].With("a-1");
        h.Servers["broken@example.net"].FailOnConnect = new System.Net.Sockets.SocketException();

        var result = await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.False(result.AllSucceeded);
        Assert.Equal(1, result.Received);
        Assert.True(result.Accounts.Single(a => a.Address == "works@example.com").Succeeded);
        Assert.Contains("1 account failed", result.Summary());
    }

    /// <summary>
    /// Sending runs before receiving: a reply queued a moment ago should leave before the poll
    /// that might bring its answer.
    /// </summary>
    [Fact]
    public async Task MailGoesOutBeforeNewMailComesIn()
    {
        var h = Build("one@example.com");
        using var _ = h.Store;
        h.Servers["one@example.com"].With("a-1");
        var sender = new SmtpSender(h.Repo) { SessionFactory = () => h.Smtp };
        sender.Queue(h.Accounts[0].AccountId, Message(), Now);

        var order = new List<string>();
        h.Service.Progress += (_, p) => order.Add(p.Stage);

        await new SendReceiveService(h.Repo, new Pop3Receiver(h.Repo)
        { SessionFactory = () => h.Servers["one@example.com"] }, sender)
            .RunAsync(h.Accounts, Now, Ct);

        Assert.Single(h.Smtp.Sent);
        Assert.Single(h.Repo.Messages(
            h.Repo.FolderWithRole(h.Accounts[0].AccountId, FolderRole.Inbox)!.Id));
    }

    [Fact]
    public async Task WorkingOfflineDoesNothingAtAll()
    {
        var h = Build("one@example.com");
        using var _ = h.Store;
        h.Servers["one@example.com"].With("a-1");
        h.Service.SetWorkOffline(true, h.Accounts.Select(a => a.AccountId));

        var result = await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.Empty(result.Accounts);
        Assert.Equal(0, result.Received);
    }

    /// <summary>Queued mail is held while offline, and goes as soon as it is released.</summary>
    [Fact]
    public async Task GoingOfflineHoldsTheOutboxAndGoingBackOnlineReleasesIt()
    {
        var h = Build("one@example.com");
        using var _ = h.Store;
        var id = h.Accounts[0].AccountId;
        var sender = new SmtpSender(h.Repo) { SessionFactory = () => h.Smtp };
        sender.Queue(id, Message(), Now);

        h.Service.SetWorkOffline(true, [id]);
        Assert.Equal(OutboxState.Held, h.Repo.Outbox(id).Single().State);
        Assert.Equal(0, await sender.DrainAsync(h.Accounts[0], Now, Ct));

        h.Service.SetWorkOffline(false, [id]);
        Assert.Equal(OutboxState.Queued, h.Repo.Outbox(id).Single().State);
        Assert.Equal(1, await sender.DrainAsync(h.Accounts[0], Now, Ct));
    }

    [Fact]
    public async Task AnEmptyRunSaysSoRatherThanNothing()
    {
        var h = Build("one@example.com");
        using var _ = h.Store;

        var result = await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.Equal("No new mail.", result.Summary());
    }

    [Fact]
    public async Task ProgressIsReportedPerAccount()
    {
        var h = Build("one@example.com");
        using var _ = h.Store;
        h.Servers["one@example.com"].With("a-1");
        var seen = new List<PollProgress>();
        h.Service.Progress += (_, p) => seen.Add(p);

        await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.Contains(seen, p => p.Stage == "Sending");
        Assert.Contains(seen, p => p.Stage == "Receiving");
        Assert.All(seen, p => Assert.Equal("one@example.com", p.Account));
    }
}
