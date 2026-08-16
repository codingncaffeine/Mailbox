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
        List<MailStore> Stores,
        SendReceiveService Service,
        Dictionary<string, FakePop3> Servers,
        FakeSmtp Smtp,
        List<TransferTarget> Accounts) : IDisposable
    {
        public MailRepository Repo => Accounts[0].Mail;

        public void Dispose()
        {
            foreach (var store in Stores) store.Dispose();
        }
    }

    /// <summary>
    /// One store per account, as the application has. The fakes are keyed by address so a
    /// per-account server can be set up and the run's routing checked, not assumed.
    /// </summary>
    private static Harness Build(params string[] addresses)
    {
        var stores = new List<MailStore>();
        var servers = new Dictionary<string, FakePop3>();
        var targets = new List<TransferTarget>();
        var byRepository = new Dictionary<MailRepository, string>();

        foreach (var address in addresses)
        {
            var store = MailStore.Transient();
            stores.Add(store);

            var repo = new MailRepository(store);
            var account = repo.AddAccount(address, address, MailProtocol.Pop3);
            repo.CreateStandardFolders(account.Id);

            servers[address] = new FakePop3();
            byRepository[repo] = address;

            targets.Add(new TransferTarget(
                new AccountConnection(
                    account.Id, address,
                    new ServerSettings($"pop.{address.Split('@')[1]}", 995),
                    new ServerSettings($"smtp.{address.Split('@')[1]}", 587, UserName: address)),
                repo));
        }

        var smtp = new FakeSmtp();
        var service = new SendReceiveService(
            mail => new Pop3Receiver(mail) { SessionFactory = () => servers[byRepository[mail]] },
            mail => new SmtpSender(mail) { SessionFactory = () => smtp });

        return new Harness(stores, service, servers, smtp, targets);
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
        using var _ = h;
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
        using var _ = h;
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
        using var _ = h;
        h.Servers["one@example.com"].With("a-1");
        var sender = new SmtpSender(h.Repo) { SessionFactory = () => h.Smtp };
        sender.Queue(h.Accounts[0].Connection.AccountId, Message(), Now);

        await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.Single(h.Smtp.Sent);
        Assert.Single(h.Repo.Messages(
            h.Repo.FolderWithRole(h.Accounts[0].Connection.AccountId, FolderRole.Inbox)!.Id));
    }

    [Fact]
    public async Task WorkingOfflineDoesNothingAtAll()
    {
        var h = Build("one@example.com");
        using var _ = h;
        h.Servers["one@example.com"].With("a-1");
        h.Service.SetWorkOffline(true, h.Accounts);

        var result = await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.Empty(result.Accounts);
        Assert.Equal(0, result.Received);
    }

    /// <summary>Queued mail is held while offline, and goes as soon as it is released.</summary>
    [Fact]
    public async Task GoingOfflineHoldsTheOutboxAndGoingBackOnlineReleasesIt()
    {
        var h = Build("one@example.com");
        using var _ = h;
        var id = h.Accounts[0].Connection.AccountId;
        var sender = new SmtpSender(h.Repo) { SessionFactory = () => h.Smtp };
        sender.Queue(id, Message(), Now);

        h.Service.SetWorkOffline(true, h.Accounts);
        Assert.Equal(OutboxState.Held, h.Repo.Outbox(id).Single().State);
        Assert.Equal(0, await sender.DrainAsync(h.Accounts[0].Connection, Now, Ct));

        h.Service.SetWorkOffline(false, h.Accounts);
        Assert.Equal(OutboxState.Queued, h.Repo.Outbox(id).Single().State);
        Assert.Equal(1, await sender.DrainAsync(h.Accounts[0].Connection, Now, Ct));
    }

    [Fact]
    public async Task AnEmptyRunSaysSoRatherThanNothing()
    {
        var h = Build("one@example.com");
        using var _ = h;

        var result = await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.Equal("No new mail.", result.Summary());
    }

    [Fact]
    public async Task ProgressIsReportedPerAccount()
    {
        var h = Build("one@example.com");
        using var _ = h;
        h.Servers["one@example.com"].With("a-1");
        var seen = new List<PollProgress>();
        h.Service.Progress += (_, p) => seen.Add(p);

        await h.Service.RunAsync(h.Accounts, Now, Ct);

        Assert.Contains(seen, p => p.Stage == "Sending");
        Assert.Contains(seen, p => p.Stage == "Receiving");
        Assert.All(seen, p => Assert.Equal("one@example.com", p.Account));
    }

    /// <summary>
    /// Change Folder: a POP3 account can be told to deliver somewhere other than its Inbox, and
    /// the poll files there. A folder that has since gone falls back to the Inbox — mail with
    /// nowhere to go is lost mail.
    /// </summary>
    [Fact]
    public async Task ADeliveryFolderIsWhereThePollFiles()
    {
        var h = Build("one@example.com");
        using var _ = h;
        var id = h.Accounts[0].Connection.AccountId;
        var receipts = h.Repo.AddFolder(id, "Receipts");
        var inbox = h.Repo.FolderWithRole(id, FolderRole.Inbox)!;

        var routed = new TransferTarget(
            h.Accounts[0].Connection with { Policy = new Pop3Policy { DeliveryFolderId = receipts.Id } },
            h.Repo);
        h.Servers["one@example.com"].With("a-1").With("a-2");

        var result = await h.Service.RunAsync([routed], Now, Ct);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, h.Repo.Folders(id).Single(f => f.Id == receipts.Id).Total);
        Assert.Equal(0, h.Repo.Folders(id).Single(f => f.Id == inbox.Id).Total);

        // The folder goes; the next poll lands in the Inbox rather than nowhere.
        h.Repo.RemoveFolder(receipts.Id);
        h.Servers["one@example.com"].With("a-3");
        result = await h.Service.RunAsync([routed], Now, Ct);

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, h.Repo.Folders(id).Single(f => f.Id == inbox.Id).Total);
    }
}
