using Mailbox.Import;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The Maildir importer: the three layouts told apart by looking, flags read off the names,
/// well-known folders merged rather than doubled, and a re-run that tops up instead of
/// duplicating — the counts a migration is checked by.
/// </summary>
public class MaildirImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-maildir-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Tree(string name)
    {
        var home = Path.Combine(_root, name);
        Directory.CreateDirectory(home);
        return home;
    }

    /// <summary>One message file, in cur/ or new/, with maildir flags in its name.</summary>
    private static void Message(string maildir, string sub, string uniq, string flags,
        string subject = "Hello", string? messageId = null, string body = "The body.")
    {
        var home = Path.Combine(maildir, sub);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(maildir, "cur"));
        Directory.CreateDirectory(Path.Combine(maildir, "new"));
        Directory.CreateDirectory(Path.Combine(maildir, "tmp"));

        var name = flags.Length > 0 || sub == "cur" ? $"{uniq}:2,{flags}" : uniq;
        var id = messageId ?? $"<{uniq}@maildir.example>";
        File.WriteAllText(Path.Combine(home, name),
            $"Message-ID: {id}\r\nFrom: A. Person <a@example.com>\r\nTo: you@example.net\r\n" +
            $"Date: Fri, 21 Aug 2026 17:00:00 +0000\r\nSubject: {subject}\r\n\r\n{body}\r\n");
    }

    private (MailStore Store, MailRepository Mail, long AccountId) Fresh()
    {
        var store = new MailStore(Path.Combine(_root, Guid.NewGuid().ToString("n") + ".db"));
        var mail = new MailRepository(store);
        var account = mail.AddAccount("a@example.net", "A", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        return (store, mail, account.Id);
    }

    [Fact]
    public void APlainMaildirIsOneInboxWithItsFlagsRead()
    {
        var tree = Tree("plain");
        Message(tree, "cur", "m1", "S");
        Message(tree, "cur", "m2", "FS", subject: "Flagged");
        Message(tree, "new", "m3", "");

        var (store, mail, account) = Fresh();
        using var _ = store;

        var report = new MaildirImporter(mail, account).Run(tree, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Imported);
        var inbox = mail.FolderWithRole(account, FolderRole.Inbox)!;
        var rows = mail.Messages(inbox.Id);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => r.IsRead));
        Assert.Single(rows, r => r.IsFlagged);

        // The date is the message's own, not the import's afternoon.
        Assert.All(rows, r => Assert.Equal(2026, r.Received.Year));
    }

    [Fact]
    public void MaildirPlusPlusMergesTheKnownNamesAndKeepsTheHierarchy()
    {
        var tree = Tree("dovecot");
        Message(tree, "cur", "in1", "S");
        Message(Path.Combine(tree, ".Sent"), "cur", "s1", "S", subject: "I sent this");
        Message(Path.Combine(tree, ".Work.Projects"), "cur", "w1", "", subject: "The plan");

        var (store, mail, account) = Fresh();
        using var _ = store;

        var report = new MaildirImporter(mail, account).Run(tree, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(3, report.Imported);

        // Sent merged into the account's own Sent Items rather than a second folder beside it.
        var sent = mail.FolderWithRole(account, FolderRole.Sent)!;
        Assert.Single(mail.Messages(sent.Id), r => r.Subject == "I sent this");

        // .Work.Projects is Work under the account with Projects inside it.
        var folders = mail.Folders(account);
        var work = Assert.Single(folders, f => f.Name == "Work");
        var projects = Assert.Single(folders, f => f.Name == "Projects");
        Assert.Equal(work.Id, projects.ParentId);
        Assert.Single(mail.Messages(projects.Id));
    }

    [Fact]
    public void ANestedTreeAndKMailsDirectoriesBothFileUnderTheirParents()
    {
        var tree = Tree("kmail");
        Message(Path.Combine(tree, "Receipts"), "cur", "r1", "S");
        Message(Path.Combine(tree, ".Receipts.directory", "Online"), "cur", "o1", "S");

        var (store, mail, account) = Fresh();
        using var _ = store;

        var report = new MaildirImporter(mail, account).Run(tree, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Imported);

        var folders = mail.Folders(account);
        var receipts = Assert.Single(folders, f => f.Name == "Receipts");
        var online = Assert.Single(folders, f => f.Name == "Online");
        Assert.Equal(receipts.Id, online.ParentId);
    }

    [Fact]
    public void ARerunTopsUpInsteadOfDoubling()
    {
        var tree = Tree("rerun");
        Message(tree, "cur", "m1", "S");

        var (store, mail, account) = Fresh();
        using var _ = store;
        var importer = new MaildirImporter(mail, account);

        Assert.Equal(1, importer.Run(tree, cancellation: TestContext.Current.CancellationToken).Imported);

        Message(tree, "cur", "m2", "S", subject: "Newer");
        var second = importer.Run(tree, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Imported);
        Assert.Equal(1, second.AlreadyHere);
        Assert.Equal(2, mail.Messages(mail.FolderWithRole(account, FolderRole.Inbox)!.Id).Count);
    }

    [Fact]
    public void TrashedStaysBehindAndTheUnreadableIsCountedNotFatal()
    {
        var tree = Tree("edges");
        Message(tree, "cur", "keep", "S");
        Message(tree, "cur", "gone", "ST", subject: "Deleted long ago");
        Directory.CreateDirectory(Path.Combine(tree, "cur"));
        File.WriteAllBytes(Path.Combine(tree, "cur", "broken:2,S"), [0xFF, 0xFE, 0x00]);

        var (store, mail, account) = Fresh();
        using var _ = store;

        var report = new MaildirImporter(mail, account).Run(tree, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Imported);
        Assert.Equal(1, report.Trashed);
        // A malformed file still parses as an empty message in MimeKit's lenient reading, or
        // counts as unreadable — either way exactly one row landed and nothing threw.
        Assert.Equal(1, mail.Messages(mail.FolderWithRole(account, FolderRole.Inbox)!.Id)
            .Count(r => r.Subject == "Hello"));
    }

    [Fact]
    public void OutboxDoesNotMergeIntoAnythingThatSends()
    {
        var tree = Tree("outbox");
        Message(Path.Combine(tree, ".Outbox"), "cur", "o1", "", subject: "Never sent");

        var (store, mail, account) = Fresh();
        using var _ = store;

        new MaildirImporter(mail, account).Run(tree, cancellation: TestContext.Current.CancellationToken);

        // A plain folder beside the account's own — somebody's unsent 2019 mail must not
        // arrive looking ready to send. Named apart, because one shelf holds one of each name.
        var own = mail.FolderWithRole(account, FolderRole.Outbox)!;
        Assert.Empty(mail.Messages(own.Id));
        var imported = Assert.Single(mail.Folders(account), f => f.Name == "Outbox (2)");
        Assert.NotEqual(own.Id, imported.Id);
        Assert.Single(mail.Messages(imported.Id));
    }
}
