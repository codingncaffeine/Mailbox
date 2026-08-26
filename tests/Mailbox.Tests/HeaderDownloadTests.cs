using MailStore = Mailbox.Store.MailStore;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Send/Receive's Server group: headers without their messages, and the messages behind the ones
/// the reader marks.
/// </summary>
/// <remarks>
/// What matters here is that a header is an ordinary row from the moment it lands — it can be
/// flagged and filed like any other — and that the message arriving under it keeps that row
/// rather than making a second one. The rest is the store's own bookkeeping: only a header can
/// be marked, and a marked header stops being marked once its message is here.
/// </remarks>
public class HeaderDownloadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Imap()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Imap);
        repo.CreateStandardFolders(account.Id);

        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;
        repo.MapFolder(inbox.Id, "INBOX", inbox.Name, inbox.ParentId);
        return (store, repo, repo.GetFolder(inbox.Id)!);
    }

    private static AccountConnection Connection() => new(
        1, "you@example.com",
        new ServerSettings("imap.example.com", 993),
        new ServerSettings("smtp.example.com", 587))
    {
        Protocol = MailProtocol.Imap,
    };

    private static HeaderDownloader Downloader(MailRepository repo, FakeImap server)
        => new(repo, () => Now) { ImapSessionFactory = () => server };

    [Fact]
    public async Task HeadersArriveWithoutTheirMessages()
    {
        var (store, repo, inbox) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "The quarterly figures");
        server.Deliver("INBOX", "Lunch?");

        var written = await Downloader(repo, server).HeadersAsync(Connection(), inbox, Ct);

        Assert.Equal(2, written);

        var rows = repo.Messages(inbox.Id, int.MaxValue);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.HeaderOnly));
        Assert.All(rows, row => Assert.Null(repo.LoadRaw(row.Id)));

        // The sender and subject are what makes a header worth having: a row that said nothing
        // would leave the reader no way to choose.
        Assert.Contains(rows, r => r.Subject == "The quarterly figures");
        Assert.Contains(rows, r => r.FromAddress == "sender@example.com");

        // Asking twice writes nothing the second time: the store already has these UIDs.
        Assert.Equal(0, await Downloader(repo, server).HeadersAsync(Connection(), inbox, Ct));
    }

    [Fact]
    public async Task OnlyTheMarkedHeadersAreFetched()
    {
        var (store, repo, inbox) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Wanted");
        server.Deliver("INBOX", "Not wanted");

        var downloader = Downloader(repo, server);
        await downloader.HeadersAsync(Connection(), inbox, Ct);

        var wanted = repo.Messages(inbox.Id, int.MaxValue).Single(m => m.Subject == "Wanted");
        Assert.Equal(1, repo.MarkForDownload([wanted.Id], marked: true));

        var filled = await downloader.ProcessMarkedAsync(Connection(), Ct);

        Assert.Equal(1, filled);

        // The same row, with a message under it now — not a second row.
        Assert.Equal(2, repo.Messages(inbox.Id, int.MaxValue).Count);

        var arrived = repo.GetMessage(wanted.Id)!;
        Assert.False(arrived.HeaderOnly);
        Assert.False(arrived.MarkedForDownload);
        Assert.NotNull(repo.LoadRaw(wanted.Id));
        Assert.Contains("Body of Wanted", System.Text.Encoding.UTF8.GetString(repo.LoadRaw(wanted.Id)!));

        // The one nobody asked for is still a header.
        var other = repo.Messages(inbox.Id, int.MaxValue).Single(m => m.Subject == "Not wanted");
        Assert.True(other.HeaderOnly);
        Assert.Null(repo.LoadRaw(other.Id));
    }

    [Fact]
    public async Task WhatWasPutOnTheHeaderSurvivesTheMessageArriving()
    {
        var (store, repo, inbox) = Imap();
        using var _ = store;

        var server = new FakeImap();
        server.Deliver("INBOX", "Keep my flag");

        var downloader = Downloader(repo, server);
        await downloader.HeadersAsync(Connection(), inbox, Ct);

        var header = repo.Messages(inbox.Id, int.MaxValue).Single();
        repo.SetFlagged([header.Id], flagged: true);
        repo.MarkForDownload([header.Id], marked: true);

        await downloader.ProcessMarkedAsync(Connection(), Ct);

        var arrived = repo.GetMessage(header.Id)!;
        Assert.True(arrived.IsFlagged);
        Assert.False(arrived.HeaderOnly);
    }

    [Fact]
    public void OnlyAHeaderCanBeMarkedForDownload()
    {
        var (store, repo, inbox) = Imap();
        using var _ = store;

        // An ordinary message, stored with its bytes: there is nothing left to fetch, and a mark
        // on it would be a promise the next send/receive could not keep.
        var whole = repo.AddMessage(inbox.Id, new MessageSummary(
            0, 0, "9", "<whole@example.com>", "Alice", "alice@example.com", "Here already",
            "Preview", Now, Now, 10, false, false, false), [1, 2, 3])!.Value;

        Assert.Equal(0, repo.MarkForDownload([whole], marked: true));
        Assert.False(repo.GetMessage(whole)!.MarkedForDownload);
    }

    [Fact]
    public async Task MarkedHeadersAreListedAcrossTheAccount()
    {
        var (store, repo, inbox) = Imap();
        using var _ = store;

        var archive = repo.FolderWithRole(inbox.AccountId, FolderRole.Archive)!;
        repo.MapFolder(archive.Id, "Archive", archive.Name, archive.ParentId);

        var server = new FakeImap();
        server.Folder("Archive", FolderRole.Archive);
        server.Deliver("INBOX", "In the inbox");
        server.Deliver("Archive", "In the archive");

        var downloader = Downloader(repo, server);
        await downloader.HeadersAsync(Connection(), inbox, Ct);
        await downloader.HeadersAsync(Connection(), repo.GetFolder(archive.Id)!, Ct);

        foreach (var folder in (long[])[inbox.Id, archive.Id])
        {
            repo.MarkForDownload([.. repo.Headers(folder).Select(m => m.Id)], marked: true);
        }

        var marked = repo.MarkedForDownload(inbox.AccountId);

        Assert.Equal(2, marked.Count);
        Assert.Contains(marked, m => m.Folder.Id == inbox.Id);
        Assert.Contains(marked, m => m.Folder.Id == archive.Id);

        // And one press fetches both, wherever they were marked.
        Assert.Equal(2, await downloader.ProcessMarkedAsync(Connection(), Ct));
        Assert.Empty(repo.MarkedForDownload(inbox.AccountId));
    }
}
