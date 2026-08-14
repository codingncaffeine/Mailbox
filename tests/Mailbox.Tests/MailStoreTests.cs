using Mailbox.Store;
using Mailbox.Store.Schema;

namespace Mailbox.Tests;

/// <summary>
/// The store holds mail that may exist nowhere else, so what is tested here is not that it can
/// round-trip a row but that it cannot quietly lose or duplicate one: migrations reach the
/// current version, a re-poll of the same message is refused, deletes do not orphan, and a file
/// from a newer build is not opened and written to.
/// </summary>
public class MailStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mailbox-store-tests", Guid.NewGuid().ToString("n"));

    private string File_ => Path.Combine(_directory, "mail.db");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static long SeedFolder(MailStore store, string address = "you@example.com")
    {
        store.Execute(
            "INSERT INTO accounts (address, protocol, created_utc) VALUES ($a, 'pop3', 0)",
            ("$a", address));
        var account = store.LastInsertId;

        store.Execute(
            "INSERT INTO folders (account_id, name, role) VALUES ($a, 'Inbox', 'inbox')",
            ("$a", account));
        return store.LastInsertId;
    }

    private static void Add(MailStore store, long folder, string uid, string subject = "Hello")
        => store.Execute(
            """
            INSERT INTO messages (folder_id, server_uid, subject, received_utc)
            VALUES ($f, $u, $s, 0)
            """,
            ("$f", folder), ("$u", uid), ("$s", subject));

    [Fact]
    public void ANewStoreIsMigratedToTheCurrentSchema()
    {
        using var store = new MailStore(File_);
        Assert.Equal(Migrations.Latest, store.Version);
        Assert.Empty(store.CheckIntegrity());
    }

    [Fact]
    public void ReopeningRunsNothingAndKeepsTheData()
    {
        long folder;
        using (var first = new MailStore(File_))
        {
            folder = SeedFolder(first);
            Add(first, folder, "uid-1");
        }

        using var second = new MailStore(File_);

        Assert.Equal(Migrations.Latest, second.Version);
        Assert.Equal(1, second.ScalarLong("SELECT count(*) FROM messages"));
    }

    /// <summary>
    /// The guard that stops a re-poll duplicating an inbox. POP3 hands back the same UIDL every
    /// time; without this, every send/receive would deliver the mail again.
    /// </summary>
    [Fact]
    public void TheSameServerIdCannotArriveTwiceInAFolder()
    {
        using var store = MailStore.Transient();
        var folder = SeedFolder(store);
        Add(store, folder, "uid-1");

        Assert.ThrowsAny<Exception>(() => Add(store, folder, "uid-1"));
        Assert.Equal(1, store.ScalarLong("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void TheSameServerIdMayAppearInDifferentFolders()
    {
        using var store = MailStore.Transient();
        var inbox = SeedFolder(store);
        store.Execute(
            "INSERT INTO folders (account_id, name) VALUES ((SELECT id FROM accounts), 'Archive')");
        var archive = store.LastInsertId;

        Add(store, inbox, "uid-1");
        Add(store, archive, "uid-1");

        Assert.Equal(2, store.ScalarLong("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void DeletingAnAccountTakesItsFoldersAndMessagesWithIt()
    {
        using var store = MailStore.Transient();
        var folder = SeedFolder(store);
        Add(store, folder, "uid-1");

        store.Execute("DELETE FROM accounts");

        Assert.Equal(0, store.ScalarLong("SELECT count(*) FROM folders"));
        Assert.Equal(0, store.ScalarLong("SELECT count(*) FROM messages"));
        Assert.Empty(store.CheckIntegrity());
    }

    [Fact]
    public void AMessageCannotBeFiledInAFolderThatDoesNotExist()
    {
        using var store = MailStore.Transient();
        Assert.ThrowsAny<Exception>(() => Add(store, folder: 999, uid: "uid-1"));
    }

    [Fact]
    public void SearchFindsAMessageBySubjectAndForgetsItWhenDeleted()
    {
        using var store = MailStore.Transient();
        var folder = SeedFolder(store);
        Add(store, folder, "uid-1", "Quarterly numbers");
        Add(store, folder, "uid-2", "Lunch on Thursday");

        long Hits(string term) => store.ScalarLong(
            "SELECT count(*) FROM messages_fts WHERE messages_fts MATCH $t", ("$t", term));

        Assert.Equal(1, Hits("quarterly"));
        Assert.Equal(0, Hits("biscuits"));

        store.Execute("DELETE FROM messages WHERE server_uid = 'uid-1'");
        Assert.Equal(0, Hits("quarterly"));
    }

    /// <summary>Diacritics are folded, so a search for "resume" finds "résumé".</summary>
    [Fact]
    public void SearchIgnoresAccents()
    {
        using var store = MailStore.Transient();
        var folder = SeedFolder(store);
        Add(store, folder, "uid-1", "Votre résumé");

        Assert.Equal(1, store.ScalarLong(
            "SELECT count(*) FROM messages_fts WHERE messages_fts MATCH 'resume'"));
    }

    [Fact]
    public void AStoreFromANewerBuildIsRefusedRatherThanMigrated()
    {
        using (var store = new MailStore(File_))
        {
            store.Execute($"PRAGMA user_version = {Migrations.Latest + 1}");
        }

        var refusal = Assert.Throws<InvalidOperationException>(() => new MailStore(File_));
        Assert.Contains("newer version", refusal.Message);
    }

    [Fact]
    public void EveryMigrationStepIsAppendOnly()
    {
        // Not a behavioural test: a reminder in executable form. Editing a shipped step leaves
        // stores already migrated past it differing from a fresh one, undetectably.
        Assert.Equal(4, Migrations.Steps.Count);
        Assert.Equal(Migrations.Steps.Count, Migrations.Latest);
    }
}
