using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The store's engines held to what they claim: a backup taken while something is writing, a
/// compaction that has to leave every row and the search index behind it standing, and the
/// retention window on Recover Deleted Items.
/// </summary>
/// <remarks>
/// Sizes are read as a file <em>and</em> its write-ahead log throughout. A store in WAL mode is
/// two files, and every size the application shows is the first of them — which is how a
/// compaction that folded four megabytes of log into the file came to report that the file "was
/// already compact". A test that measured the same way would agree with the bug.
/// </remarks>
public class StoreEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mailbox-store-engines", Guid.NewGuid().ToString("n"));

    private string Live => Path.Combine(_directory, "mail.db");
    private string Copy => Path.Combine(_directory, "backup", "mail-backup.db");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The file and the write-ahead log beside it, which are one store between them.</summary>
    private static long Footprint(string path)
    {
        long Of(string suffix) => File.Exists(path + suffix) ? new FileInfo(path + suffix).Length : 0;
        return Of(string.Empty) + Of("-wal");
    }

    private static long Seed(MailStore store, int messages, string tag = "seed")
    {
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;
        Fill(repo, inbox.Id, messages, tag);
        return inbox.Id;
    }

    private static void Fill(MailRepository repo, long folderId, int messages, string tag)
    {
        for (var i = 0; i < messages; i++)
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(
                $"From: A. Person <a.person@example.com>\r\nSubject: {tag} {i}\r\n\r\nThe body of {tag} {i} mentions kestrel.\r\n");

            repo.AddMessage(folderId, new MessageSummary(
                0, 0, $"{tag}-{i}", $"<{tag}-{i}@example.com>", "A. Person", "a.person@example.com",
                $"{tag} {i}", "Preview", null, DateTimeOffset.UnixEpoch.AddDays(i), raw.Length,
                false, false, false) { BodyText = $"The body of {tag} {i} mentions kestrel." }, raw);
        }
    }

    private static long Matches(MailStore store, string term) => store.ScalarLong(
        "SELECT count(*) FROM messages m JOIN messages_fts ON messages_fts.rowid = m.id WHERE messages_fts MATCH $t",
        ("$t", term));

    // ---- The consistent-copy backup ------------------------------------------------------------

    /// <summary>
    /// The engine's central claim, and the one nothing had exercised: a copy taken while the
    /// store is being written is a point in time rather than a torn file.
    /// </summary>
    /// <remarks>
    /// SQLite's online backup restarts when the source is written during the copy, so the answer
    /// is not "the count at the moment it started" — it is a count that is <em>some</em> consistent
    /// moment between the start and the end, with every row of it present and its search index
    /// agreeing. That is what is asserted: never fewer than what was there before the writer
    /// began, never more than what is there when it stops, integrity clean, and the FTS index
    /// holding exactly as many documents as the copy holds messages.
    /// </remarks>
    [Fact]
    public async Task ACopyTakenWhileSomethingIsWritingIsWholeAtSomeMoment()
    {
        using var live = new MailStore(Live);
        var inbox = Seed(live, 200);
        var repo = new MailRepository(live);

        var before = live.ScalarLong("SELECT count(*) FROM messages");

        using var writing = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 400 && !writing.IsCancellationRequested; i++)
            {
                Fill(repo, inbox, 1, $"during-{i}");
            }
        }, TestContext.Current.CancellationToken);

        var result = StoreBackup.To(live, Copy);
        await writer;
        var after = live.ScalarLong("SELECT count(*) FROM messages");

        Assert.True(result.Ok, result.Error);
        Assert.True(after > before, "the writer wrote nothing, so this proves nothing");

        // The copy carried away on its own, with nothing beside it — which is what a backup is.
        var carried = Path.Combine(_directory, "carried.db");
        File.Copy(Copy, carried);

        using var opened = new MailStore(carried);
        var held = opened.ScalarLong("SELECT count(*) FROM messages");

        Assert.Empty(opened.CheckIntegrity());
        Assert.InRange(held, before, after);
        Assert.Equal(held, opened.ScalarLong("SELECT count(*) FROM messages_fts_docsize"));
        Assert.Equal(held, opened.ScalarLong("SELECT count(*) FROM blobs"));
        Assert.Equal(held, Matches(opened, "kestrel"));
    }

    /// <summary>
    /// A backup is one file. Anything the engine left in a companion beside it would not travel
    /// with the copy a reader carries away, and the message that was not in the main file would
    /// be the one nobody notices until they need it.
    /// </summary>
    [Fact]
    public void ABackupIsOneFileWithNothingBesideIt()
    {
        using var live = new MailStore(Live);
        Seed(live, 60);

        var result = StoreBackup.To(live, Copy);
        Assert.True(result.Ok, result.Error);

        var beside = Directory.GetFiles(Path.GetDirectoryName(Copy)!)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([Path.GetFileName(Copy)], beside);
        Assert.Equal(new FileInfo(Copy).Length, result.Bytes);
    }

    /// <summary>
    /// What a restore has to put back: every row, every blob, and a search index that still finds
    /// them. A restore that only moves bytes has restored a file, not a mailbox.
    /// </summary>
    [Fact]
    public void ARestoredStoreIsSearchableAgain()
    {
        long before;
        using (var live = new MailStore(Live))
        {
            Seed(live, 40);
            before = Matches(live, "kestrel");
            Assert.Equal(40, before);
            Assert.True(StoreBackup.To(live, Copy).Ok);
        }

        using (var live = new MailStore(Live))
        {
            live.Execute("DELETE FROM messages WHERE server_uid != 'seed-0'");
        }

        var (result, displaced) = StoreBackup.From(Copy, Live);
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(displaced);

        using var restored = new MailStore(Live);
        Assert.Equal(40, restored.ScalarLong("SELECT count(*) FROM messages"));
        Assert.Equal(40, Matches(restored, "kestrel"));
        Assert.Empty(restored.CheckIntegrity());
    }

    // ---- Compact ---------------------------------------------------------------------------

    /// <summary>
    /// A compaction gives back the space a delete left and loses nothing doing it — proven with
    /// row counts and a search, because a file that got smaller is not evidence that what was in
    /// it survived.
    /// </summary>
    [Fact]
    public void CompactingGivesBackSpaceAndKeepsEverythingLeft()
    {
        using var store = new MailStore(Live);
        var inbox = Seed(store, 400);
        var repo = new MailRepository(store);

        // Big blobs, so the free pages a delete leaves behind are worth reclaiming.
        for (var i = 0; i < 200; i++)
        {
            var raw = new byte[8192];
            Array.Fill(raw, (byte)'x');
            repo.AddMessage(inbox, new MessageSummary(
                0, 0, $"fat-{i}", null, "A. Person", "a.person@example.com", $"fat {i}", "",
                null, DateTimeOffset.UnixEpoch, raw.Length, false, false, false), raw);
        }

        store.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        var full = Footprint(Live);

        var fat = store.Query("SELECT id FROM messages WHERE server_uid LIKE 'fat-%'", r => r.GetInt64(0));
        Assert.Equal(200, repo.DeleteMessages(fat));

        // Purge the holding area too: a delete keeps the bytes recoverable, so nothing is free
        // until the retention window has let them go.
        Assert.Equal(200, repo.Purge([.. store.Query("SELECT id FROM recoverable", r => r.GetInt64(0))]));

        var after = store.Compact();

        Assert.Equal(400, store.ScalarLong("SELECT count(*) FROM messages"));
        Assert.Equal(400, store.ScalarLong("SELECT count(*) FROM blobs"));
        Assert.Equal(400, store.ScalarLong("SELECT count(*) FROM messages_fts_docsize"));
        Assert.Equal(400, Matches(store, "kestrel"));
        Assert.Empty(store.CheckIntegrity());
        Assert.Equal(0, store.ScalarLong("PRAGMA freelist_count"));
        Assert.True(after < full, $"compacting freed nothing: {full:N0} → {after:N0}");
        Assert.Equal(after, Footprint(Live));
    }

    /// <summary>
    /// The arithmetic behind "N recovered": the size before a compaction has to be the store's,
    /// not the main file's.
    /// </summary>
    /// <remarks>
    /// Everything sitting in the write-ahead log is part of the store, and the compaction's first
    /// act is to fold the log into the file — so a "before" that ignores it is measuring a
    /// different thing from the "after", and the difference between them is not a saving. It came
    /// out negative on a real run (a 536 KB file with a 4.19 MB log, compacted to 553 KB), and
    /// negative clamps to zero, which is the sentence "was already compact" over a store that had
    /// just given back four megabytes.
    /// </remarks>
    [Fact]
    public void TheSizeOfAStoreIsItsFileAndItsWriteAheadLog()
    {
        var accounts = Path.Combine(_directory, "accounts");
        using var stores = new AccountStores(accounts, new NoOrder());
        var account = stores.Add("you@example.com", "You", MailProtocol.Pop3);
        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox)!;
        Fill(account.Mail, inbox.Id, 600, "seed");

        var mainFileOnly = new FileInfo(account.Path).Length;

        Assert.True(Footprint(account.Path) > mainFileOnly,
            "this store has no write-ahead log, so it cannot show the difference");

        // What the two dialogs ask the account for, which has to be the whole of it.
        Assert.Equal(Footprint(account.Path), account.Bytes);

        var before = account.Bytes;
        var after = account.Store.Compact();

        Assert.Equal(after, account.Bytes);
        Assert.True(before - after > 0,
            $"the compaction reported no saving: {before:N0} → {after:N0}");
        Assert.Equal(600, account.Store.ScalarLong("SELECT count(*) FROM messages"));
    }

    /// <summary>An account order for a store with no settings behind it.</summary>
    private sealed class NoOrder : IAccountOrder
    {
        public string? DefaultAddress { get; set; }

        public int IndexOf(string address) => 0;

        public void Register(string address) { }

        public void Forget(string address) { }

        public void Move(string address, int direction) { }
    }

    // ---- Recover Deleted Items ----------------------------------------------------------------

    /// <summary>
    /// What a permanent delete leaves recoverable, and that recovering it puts it back in the
    /// folder it came from rather than in whichever folder the dialog was standing in.
    /// </summary>
    [Fact]
    public void RecoveringPutsAMessageBackInTheFolderItCameFrom()
    {
        using var store = new MailStore(Live);
        var inbox = Seed(store, 3);
        var repo = new MailRepository(store);
        var accountId = repo.Accounts()[0].Id;
        var project = repo.AddFolder(accountId, "Project", FolderRole.None);
        var deleted = repo.FolderWithRole(accountId, FolderRole.Deleted)!;

        Fill(repo, project.Id, 2, "project");
        var fromProject = repo.Messages(project.Id).Select(m => m.Id).ToList();
        repo.SetRead(fromProject[0], true);
        repo.SetFlagged(fromProject[1], true);

        Assert.Equal(2, repo.DeleteMessages(fromProject));

        var recoverable = repo.Recoverable();
        Assert.Equal(2, recoverable.Count);
        Assert.All(recoverable, r => Assert.Equal("Project", r.OriginalFolderName));

        // Restored with the Deleted Items folder offered as the fallback, which is what the dialog
        // hands it — so a message that lands there is one whose own folder was not found.
        Assert.Equal(2, repo.Restore([.. recoverable.Select(r => r.Id)], deleted.Id));

        var back = repo.Messages(project.Id);
        Assert.Equal(2, back.Count);
        Assert.Empty(repo.Messages(deleted.Id));
        Assert.Contains(back, m => m.IsRead && !m.IsFlagged);
        Assert.Contains(back, m => m.IsFlagged && !m.IsRead);
        Assert.Empty(repo.Recoverable());

        // And findable again: a message that comes back unsearchable has not come back.
        Assert.Equal(5, Matches(store, "kestrel"));
    }

    /// <summary>
    /// The retention window: what was deleted longer ago than the setting keeps goes for good,
    /// and what was deleted inside it stays. The boundary is walked — one either side and one
    /// exactly on it.
    /// </summary>
    [Fact]
    public void TheRetentionWindowKeepsWhatIsInsideItAndPurgesWhatIsNot()
    {
        using var store = new MailStore(Live);
        var inboxId = Seed(store, 0);
        var repo = new MailRepository(store);

        var now = new DateTimeOffset(2026, 8, 16, 14, 30, 0, TimeSpan.Zero);
        var cutoff = now.AddDays(-30);

        // Three deletions, dated by hand at the boundary: a day older, exactly on it, a day newer.
        foreach (var (tag, when) in new[]
                 {
                     ("older", cutoff.AddDays(-1)),
                     ("exactly", cutoff),
                     ("newer", cutoff.AddDays(1)),
                 })
        {
            Fill(repo, inboxId, 1, tag);
            var id = repo.Messages(inboxId).Single(m => m.Subject.StartsWith(tag, StringComparison.Ordinal)).Id;
            repo.DeleteMessages([id]);
            store.Execute(
                "UPDATE recoverable SET deleted_utc = $when WHERE id = (SELECT max(id) FROM recoverable)",
                ("$when", when.ToUnixTimeSeconds()));
        }

        Assert.Equal(3, repo.RecoverableCount());

        var purged = repo.PurgeRecoverableOlderThan(cutoff);

        // Strictly older goes; the one exactly on the boundary is kept, which is the reading that
        // matches "kept for N days" — the last day is still inside the window.
        Assert.Equal(1, purged);
        Assert.Equal(
            ["exactly 0", "newer 0"],
            repo.Recoverable().Select(r => r.Subject).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A message with no stored bytes leaves nothing behind when it is deleted — there is no
    /// recoverable row for it at all, so Recover Deleted Items cannot offer it and the delete is
    /// final however long the retention window is.
    /// </summary>
    [Fact]
    public void AMessageWithNoStoredBytesIsNotRecoverable()
    {
        using var store = new MailStore(Live);
        var inboxId = Seed(store, 0);
        var repo = new MailRepository(store);

        // A summary with no raw bytes: what a header-only download files.
        var id = repo.AddMessage(inboxId, new MessageSummary(
            0, 0, "headers-only", "<headers-only@example.com>", "A. Person", "a.person@example.com",
            "Headers only", "Preview", null, DateTimeOffset.UnixEpoch, 400, false, false, false));

        Assert.NotNull(id);
        Assert.Equal(1, repo.DeleteMessages([id!.Value]));
        Assert.Equal(0, repo.RecoverableCount());
    }
}
