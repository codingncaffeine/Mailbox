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

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static long Message(MailRepository repo, long folderId, string subject)
        => repo.AddMessage(folderId, new MessageSummary(0, folderId, Guid.NewGuid().ToString("n"), null, "A", "a@example.com",
            subject, "Body", Now, Now, 10, false, false, false),
            System.Text.Encoding.ASCII.GetBytes("From: a@example.com\r\nSubject: " + subject + "\r\n\r\nBody"))!.Value;

    [Fact]
    public async Task APop3FolderMovesUnderAnotherWithItsChildrenAndMail()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Pop3);
        using var _ = store;
        var manager = new FolderManager(repo);

        var projects = await manager.CreateAsync(null, accountId, "Projects", null, TestContext.Current.CancellationToken);
        var receipts = await manager.CreateAsync(null, accountId, "Receipts", null, TestContext.Current.CancellationToken);
        var year = await manager.CreateAsync(null, accountId, "2026", receipts.Id, TestContext.Current.CancellationToken);
        var id = Message(repo, receipts.Id, "Invoice");

        Assert.True(await manager.MoveAsync(null, receipts, projects.Id, TestContext.Current.CancellationToken));

        Assert.Equal(projects.Id, repo.GetFolder(receipts.Id)!.ParentId);
        Assert.Equal(receipts.Id, repo.GetFolder(year.Id)!.ParentId);
        Assert.Contains(repo.Messages(receipts.Id), m => m.Id == id);

        // Not into itself, nor under one of its own.
        Assert.False(await manager.MoveAsync(null, repo.GetFolder(receipts.Id)!, receipts.Id, TestContext.Current.CancellationToken));
        Assert.False(await manager.MoveAsync(null, repo.GetFolder(receipts.Id)!, year.Id, TestContext.Current.CancellationToken));
        Assert.Equal(projects.Id, repo.GetFolder(receipts.Id)!.ParentId);

        // And back to the top.
        Assert.True(await manager.MoveAsync(null, repo.GetFolder(receipts.Id)!, null, TestContext.Current.CancellationToken));
        Assert.Null(repo.GetFolder(receipts.Id)!.ParentId);
    }

    [Fact]
    public async Task AnImapFolderMovesOnTheServerFirstAndIsRePathedWithItsChildren()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Imap);
        using var _ = store;
        var server = new FakeImap();
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        repo.MapFolder(inbox.Id, "INBOX", "Inbox", null);
        var manager = new FolderManager(repo) { SessionFactory = () => server };

        var projects = await manager.CreateAsync(Imap(), accountId, "Projects", null, TestContext.Current.CancellationToken);
        var receipts = await manager.CreateAsync(Imap(), accountId, "Receipts", inbox.Id, TestContext.Current.CancellationToken);
        var year = await manager.CreateAsync(Imap(), accountId, "2026", receipts.Id, TestContext.Current.CancellationToken);
        Assert.Equal("INBOX/Receipts/2026", year.ImapPath);

        Assert.True(await manager.MoveAsync(Imap(), repo.GetFolder(receipts.Id)!, projects.Id, TestContext.Current.CancellationToken));

        Assert.Equal("Projects/Receipts", repo.GetFolder(receipts.Id)!.ImapPath);
        Assert.Equal(projects.Id, repo.GetFolder(receipts.Id)!.ParentId);
        Assert.Equal("Projects/Receipts/2026", repo.GetFolder(year.Id)!.ImapPath);
        var listed = await server.ListFoldersAsync(TestContext.Current.CancellationToken);
        Assert.Contains(listed, f => f.Path == "Projects/Receipts/2026");
        Assert.DoesNotContain(listed, f => f.Path == "INBOX/Receipts");
    }

    [Fact]
    public async Task ACopiedFolderIsANewTreeOverTheSameBytesAndOnImapItsMailIsAppended()
    {
        var (store, repo, accountId) = Fresh(MailProtocol.Imap);
        using var _ = store;
        var server = new FakeImap();
        var inbox = repo.FolderWithRole(accountId, FolderRole.Inbox)!;
        repo.MapFolder(inbox.Id, "INBOX", "Inbox", null);
        var manager = new FolderManager(repo) { SessionFactory = () => server };

        var receipts = await manager.CreateAsync(Imap(), accountId, "Receipts", null, TestContext.Current.CancellationToken);
        var year = await manager.CreateAsync(Imap(), accountId, "2026", receipts.Id, TestContext.Current.CancellationToken);
        repo.SetFolderSynced(receipts.Id, true);
        repo.SetFolderSynced(year.Id, true);
        Message(repo, receipts.Id, "Invoice");
        Message(repo, year.Id, "Statement");
        var before = repo.PendingOps().Count;

        var copy = await manager.CopyAsync(Imap(), accountId, receipts, inbox.Id, TestContext.Current.CancellationToken);

        Assert.Equal("Receipts", copy.Name);
        Assert.Equal(inbox.Id, copy.ParentId);
        Assert.Equal("INBOX/Receipts", copy.ImapPath);
        var copiedYear = repo.Folders(accountId).Single(f => f.ParentId == copy.Id);
        Assert.Equal("2026", copiedYear.Name);
        Assert.Equal("INBOX/Receipts/2026", copiedYear.ImapPath);
        Assert.Single(repo.Messages(copy.Id));
        Assert.Single(repo.Messages(copiedYear.Id));
        // The originals are still where they were.
        Assert.Single(repo.Messages(receipts.Id));
        Assert.Single(repo.Messages(year.Id));

        // Made on the server, and the copies journalled to be appended there.
        var listed = await server.ListFoldersAsync(TestContext.Current.CancellationToken);
        Assert.Contains(listed, f => f.Path == "INBOX/Receipts/2026");
        Assert.Equal(2, repo.PendingOps().Count(o => o.Kind == SyncOpKind.Append) - repo.PendingOps().Take(before).Count(o => o.Kind == SyncOpKind.Append));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.CopyAsync(Imap(), accountId, repo.GetFolder(receipts.Id)!, year.Id, TestContext.Current.CancellationToken));
    }
}
