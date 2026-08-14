using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// A backup that opens cleanly and is missing the last few messages is the worst outcome, so
/// what is checked here is that a copy taken while the store is live is complete, that a
/// damaged backup is refused before it can replace anything, and that a restore keeps what it
/// displaced.
/// </summary>
public class StoreBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mailbox-backup-tests", Guid.NewGuid().ToString("n"));

    private string Live => Path.Combine(_directory, "mail.db");
    private string Copy => Path.Combine(_directory, "backup", "mail-backup.db");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static long Seed(MailStore store, int messages)
    {
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;

        for (var i = 0; i < messages; i++)
        {
            repo.AddMessage(inbox.Id, new MessageSummary(
                0, 0, $"uid-{i}", null, "Alice", "alice@example.com", $"Message {i}",
                "Preview", null, DateTimeOffset.UnixEpoch, 100, false, false, false));
        }

        return inbox.Id;
    }

    [Fact]
    public void ABackupTakenWhileTheStoreIsOpenIsComplete()
    {
        using var live = new MailStore(Live);
        Seed(live, 25);

        var result = StoreBackup.To(live, Copy);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Bytes > 0);

        using var restored = new MailStore(Copy);
        Assert.Equal(25, restored.ScalarLong("SELECT count(*) FROM messages"));
    }

    /// <summary>Writes made after a backup belong to the live store, not the copy.</summary>
    [Fact]
    public void ABackupIsAPointInTime()
    {
        using var live = new MailStore(Live);
        var inbox = Seed(live, 5);
        StoreBackup.To(live, Copy);

        new MailRepository(live).AddMessage(inbox, new MessageSummary(
            0, 0, "later", null, "Bob", "bob@example.com", "After the backup", "",
            null, DateTimeOffset.UnixEpoch, 10, false, false, false));

        using var copy = new MailStore(Copy);

        Assert.Equal(6, live.ScalarLong("SELECT count(*) FROM messages"));
        Assert.Equal(5, copy.ScalarLong("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void RestoringPutsTheMailBackAndKeepsWhatItReplaced()
    {
        using (var live = new MailStore(Live))
        {
            Seed(live, 10);
            StoreBackup.To(live, Copy);
        }

        // Something happens to the live store: fewer messages than the backup holds.
        using (var live = new MailStore(Live))
        {
            live.Execute("DELETE FROM messages WHERE server_uid != 'uid-0'");
            Assert.Equal(1, live.ScalarLong("SELECT count(*) FROM messages"));
        }

        var (result, displaced) = StoreBackup.From(Copy, Live);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(displaced);
        Assert.True(File.Exists(displaced));

        using var restored = new MailStore(Live);
        Assert.Equal(10, restored.ScalarLong("SELECT count(*) FROM messages"));
    }

    /// <summary>
    /// The important refusal. Discovering a backup is unreadable halfway through a restore has
    /// already destroyed what it was replacing.
    /// </summary>
    [Fact]
    public void ADamagedBackupIsRefusedBeforeAnythingIsReplaced()
    {
        using (var live = new MailStore(Live)) Seed(live, 4);

        Directory.CreateDirectory(Path.GetDirectoryName(Copy)!);
        File.WriteAllText(Copy, "this is not a database");

        var (result, displaced) = StoreBackup.From(Copy, Live);

        Assert.False(result.Ok);
        Assert.Null(displaced);

        using var untouched = new MailStore(Live);
        Assert.Equal(4, untouched.ScalarLong("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void RestoringFromNothingSaysSo()
    {
        var (result, _) = StoreBackup.From(Path.Combine(_directory, "absent.db"), Live);

        Assert.False(result.Ok);
        Assert.Contains("no file", result.Error);
    }

    [Fact]
    public void BackupNamesAreDatedSoTheyDoNotOverwriteEachOther()
    {
        var first = StoreBackup.SuggestedName(new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero));
        var later = StoreBackup.SuggestedName(new DateTimeOffset(2026, 8, 14, 17, 30, 0, TimeSpan.Zero));

        Assert.Equal("mailbox-2026-08-14-0900.db", first);
        Assert.NotEqual(first, later);
    }
}
