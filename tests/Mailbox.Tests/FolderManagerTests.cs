using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>New Folder, Rename Folder and Delete Folder — on the store alone for POP3, on the server first for IMAP.</summary>
public class FolderManagerTests
{
    private static (MailStore Store, MailRepository Repo, long AccountId) Fresh(MailProtocol protocol)
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", protocol);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, account.Id);
    }

    private static AccountConnection Imap() => new(
        1, "you@example.com", new ServerSettings("imap.example.com", 993), new ServerSettings("smtp.example.com", 587))
    { Protocol = MailProtocol.Imap };

    [Fact]
    public async Task APop3FolderIsMadeRenamedAndDeletedHere()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Pop3);
        using var _ = store;
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        var manager = new FolderManager(repo);

        var made = await manager.CreateAsync(null, accountId, "Receipts", inbox.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Receipts", made.Name);
        Assert.Equal(inbox.Id, made.ParentId);
        Assert.Null(made.ImapPath);

        var child = await manager.CreateAsync(null, accountId, "2026", made.Id, TestContext.Current.CancellationToken);
        await manager.RenameAsync(null, made, "Shopping", TestContext.Current.CancellationToken);
        Assert.Equal("Shopping", repo.GetFolder(made.Id)!.Name);
        Assert.Equal(made.Id, repo.GetFolder(child.Id)!.ParentId);

        await manager.DeleteAsync(null, repo.GetFolder(made.Id)!, TestContext.Current.CancellationToken);
        Assert.Null(repo.GetFolder(made.Id));
        Assert.Null(repo.GetFolder(child.Id));

        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateAsync(null, accountId, "  ", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnImapFolderIsMadeOnTheServerFirstAndRenamedWithItsChildren()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Imap);
        using var _ = store;
        var server = new FakeImap();

        // Map the Inbox as a sync would, so a folder can be made under it.
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        repo.MapFolder(inbox.Id, "INBOX", "Inbox", null);

        var manager = new FolderManager(repo) { SessionFactory = () => server };
        var made = await manager.CreateAsync(Imap(), accountId, "Receipts", inbox.Id, TestContext.Current.CancellationToken);
        Assert.Equal("INBOX/Receipts", made.ImapPath);
        Assert.Contains((await server.ListFoldersAsync(TestContext.Current.CancellationToken)), f => f.Path == "INBOX/Receipts");

        var child = await manager.CreateAsync(Imap(), accountId, "2026", made.Id, TestContext.Current.CancellationToken);
        Assert.Equal("INBOX/Receipts/2026", child.ImapPath);

        await manager.RenameAsync(Imap(), repo.GetFolder(made.Id)!, "Shopping", TestContext.Current.CancellationToken);
        Assert.Equal("INBOX/Shopping", repo.GetFolder(made.Id)!.ImapPath);
        Assert.Equal("Shopping", repo.GetFolder(made.Id)!.Name);
        Assert.Equal("INBOX/Shopping/2026", repo.GetFolder(child.Id)!.ImapPath);
        var listed = await server.ListFoldersAsync(TestContext.Current.CancellationToken);
        Assert.Contains(listed, f => f.Path == "INBOX/Shopping/2026");
        Assert.DoesNotContain(listed, f => f.Path == "INBOX/Receipts");

        await manager.DeleteAsync(Imap(), repo.GetFolder(made.Id)!, TestContext.Current.CancellationToken);
        Assert.Null(repo.GetFolder(made.Id));
        Assert.Null(repo.GetFolder(child.Id));
        listed = await server.ListFoldersAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(listed, f => f.Path.StartsWith("INBOX/Shopping", StringComparison.Ordinal));
    }
}
