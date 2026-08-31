using Mailbox.Store;
using Mailbox.Store.Pim;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// The whole profile into one archive and back: stores copied consistently and verified,
/// plain files and directories carried, restore displacing rather than overwriting, and the
/// retention prune. The contract throughout: nothing is touched until everything has proven
/// readable, and nothing that was there is destroyed — only moved aside with a dated name.
/// </summary>
public class ProfileBackupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A small real profile on disk: one account with a message, one calendar, extras.</summary>
    private static string MakeProfile(string root)
    {
        var accounts = Path.Combine(root, "accounts");
        Directory.CreateDirectory(accounts);

        using (var store = new MailStore(Path.Combine(accounts, "you@example.com.db")))
        {
            var repo = new MailRepository(store);
            var account = repo.AddAccount("you@example.com", "You", MailProtocol.Imap);
            repo.CreateStandardFolders(account.Id);
            var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;

            var message = new MimeMessage { Subject = "Kept safe" };
            message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
            message.To.Add(new MailboxAddress("You", "you@example.com"));
            message.Body = new TextPart("plain") { Text = "The one message the backup must carry." };

            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();
            repo.AddMessage(inbox.Id, Mailbox.Protocols.MessageMapper.ToSummary(message, "1", raw.Length, Now), raw);
        }

        using (var pim = new PimStore(Path.Combine(root, "pim.db")))
        {
            new PimRepository(pim).AddCollection(CollectionKind.Events, "Kept Calendar", "#0078D4");
        }

        File.WriteAllText(Path.Combine(root, "settings.json"), """{"theme":"darkgray"}""");

        var themes = Path.Combine(root, "themes");
        Directory.CreateDirectory(themes);
        File.WriteAllText(Path.Combine(themes, "mine.mailbox-theme.json"), """{"id":"mine"}""");

        return root;
    }

    private static ProfileArchiveResult Archive(string profile, string zip) => ProfileBackup.WriteArchive(
        zip,
        Path.Combine(profile, "accounts"),
        Path.Combine(profile, "pim.db"),
        feedsDb: null,
        files: [(Path.Combine(profile, "settings.json"), "settings.json")],
        directories: [(Path.Combine(profile, "themes"), "themes")],
        Now);

    private static ProfileRestoreResult Put(string zip, string profile) => ProfileBackup.Restore(
        zip,
        Path.Combine(profile, "accounts"),
        Path.Combine(profile, "pim.db"),
        feedsDb: null,
        files: [("settings.json", Path.Combine(profile, "settings.json"))],
        directories: [("themes", Path.Combine(profile, "themes"))],
        Now);

    [Fact]
    public void TheWholeProfileRoundTrips()
    {
        var root = Directory.CreateTempSubdirectory("backup-test-").FullName;
        try
        {
            var profile = MakeProfile(Path.Combine(root, "old"));
            var zip = Path.Combine(root, ProfileBackup.SuggestedName(Now));

            var wrote = Archive(profile, zip);
            Assert.True(wrote.Ok, wrote.Error);
            Assert.True(wrote.Bytes > 0);

            var (manifest, error) = ProfileBackup.Inspect(zip);
            Assert.Null(error);
            Assert.Contains("accounts/you@example.com.db", manifest!.Entries);
            Assert.Contains("pim.db", manifest.Entries);
            Assert.Contains("settings.json", manifest.Entries);
            Assert.Contains("themes/mine.mailbox-theme.json", manifest.Entries);

            var fresh = Path.Combine(root, "new");
            var restored = Put(zip, fresh);
            Assert.True(restored.Ok, restored.Error);
            Assert.Empty(restored.Displaced);

            using var store = new MailStore(Path.Combine(fresh, "accounts", "you@example.com.db"));
            var repo = new MailRepository(store);
            var account = Assert.Single(repo.Accounts());
            var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;
            Assert.Equal("Kept safe", Assert.Single(repo.Messages(inbox.Id)).Subject);

            using var pim = new PimStore(Path.Combine(fresh, "pim.db"));
            Assert.Contains(new PimRepository(pim).Collections(), c => c.DisplayName == "Kept Calendar");

            Assert.Equal("""{"theme":"darkgray"}""", File.ReadAllText(Path.Combine(fresh, "settings.json")));
            Assert.True(File.Exists(Path.Combine(fresh, "themes", "mine.mailbox-theme.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreDisplacesWhatWasThereRatherThanOverwritingIt()
    {
        var root = Directory.CreateTempSubdirectory("backup-test-").FullName;
        try
        {
            var zip = Path.Combine(root, "backup.zip");
            Assert.True(Archive(MakeProfile(Path.Combine(root, "old")), zip).Ok);

            // A live profile with its own different mail is already at the target.
            var live = MakeProfile(Path.Combine(root, "live"));

            var restored = Put(zip, live);
            Assert.True(restored.Ok, restored.Error);
            Assert.NotEmpty(restored.Displaced);
            Assert.All(restored.Displaced, aside => Assert.Contains("replaced-", aside));

            // What was there survives, aside — the second half of a disaster is impossible.
            Assert.Contains(restored.Displaced, aside => Directory.Exists(aside) || File.Exists(aside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ADamagedArchiveIsRefusedBeforeAnythingIsTouched()
    {
        var root = Directory.CreateTempSubdirectory("backup-test-").FullName;
        try
        {
            var zip = Path.Combine(root, "backup.zip");
            Assert.True(Archive(MakeProfile(Path.Combine(root, "old")), zip).Ok);

            // Corrupt the account store inside the archive, manifest intact.
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("accounts/you@example.com.db")!;
                entry.Delete();
                var broken = archive.CreateEntry("accounts/you@example.com.db");
                using var stream = broken.Open();
                stream.Write("not a database"u8);
            }

            var live = MakeProfile(Path.Combine(root, "live"));
            var before = File.ReadAllBytes(Path.Combine(live, "accounts", "you@example.com.db"));

            var restored = Put(zip, live);
            Assert.False(restored.Ok);
            Assert.Empty(restored.Displaced);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(live, "accounts", "you@example.com.db")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ABackupFromANewerMailboxIsRefusedWithTheReason()
    {
        var root = Directory.CreateTempSubdirectory("backup-test-").FullName;
        try
        {
            var zip = Path.Combine(root, "backup.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("manifest.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(System.Text.Json.JsonSerializer.Serialize(new ProfileManifest(
                    Now, Mailbox.Store.Schema.Migrations.Latest + 1, PimMigrations.Latest, [])));
            }

            var restored = Put(zip, Path.Combine(root, "new"));
            Assert.False(restored.Ok);
            Assert.Contains("newer Mailbox", restored.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PruneKeepsTheNewestAndSaysHowManyWent()
    {
        var root = Directory.CreateTempSubdirectory("backup-test-").FullName;
        try
        {
            foreach (var day in Enumerable.Range(1, 5))
            {
                File.WriteAllText(Path.Combine(root, $"mailbox-backup-2026-08-0{day}-1000.zip"), "x");
            }

            File.WriteAllText(Path.Combine(root, "unrelated.zip"), "x");

            Assert.Equal(3, ProfileBackup.Prune(root, keep: 2));
            var left = Directory.EnumerateFiles(root, "mailbox-backup-*.zip").OrderBy(p => p).ToList();
            Assert.Equal(2, left.Count);
            Assert.EndsWith("2026-08-04-1000.zip", left[0]);
            Assert.EndsWith("2026-08-05-1000.zip", left[1]);
            Assert.True(File.Exists(Path.Combine(root, "unrelated.zip")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
