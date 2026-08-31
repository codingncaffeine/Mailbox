using System.IO.Compression;
using System.Text.Json;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Store;

/// <summary>What one archive holds, written beside the data so a restore knows what it has.</summary>
public sealed record ProfileManifest(
    DateTimeOffset Made,
    int MailSchema,
    int PimSchema,
    IReadOnlyList<string> Entries);

/// <summary>What writing an archive came to.</summary>
public sealed record ProfileArchiveResult(bool Ok, string Path, long Bytes, int Entries, string? Error = null)
{
    public static ProfileArchiveResult Failed(string path, string error) => new(false, path, 0, 0, error);
}

/// <summary>What a restore came to: what was written, and what was moved aside first.</summary>
public sealed record ProfileRestoreResult(
    bool Ok,
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> Displaced,
    string? Error = null)
{
    public static ProfileRestoreResult Failed(string error) => new(false, [], [], error);
}

/// <summary>
/// The whole profile in one archive, and back: every account's store, the calendars and
/// contacts, the settings, the themes and the plugin manifests.
/// </summary>
/// <remarks>
/// The stores go through SQLite's own online backup — never a file copy, because a WAL store
/// mid-write copies into something that opens cleanly and is quietly missing mail — and every
/// copied store is verified before the archive is called good, exactly as
/// <see cref="StoreBackup"/> taught for one file. Restore verifies everything first and then
/// displaces rather than overwrites: what was there is moved aside with a dated name, so a
/// restore from the wrong backup is recoverable rather than the second half of a disaster.
/// </remarks>
public static class ProfileBackup
{
    private const string ManifestName = "manifest.json";

    /// <summary>A dated archive name, so successive backups line up and never collide.</summary>
    public static string SuggestedName(DateTimeOffset when) => $"mailbox-backup-{when:yyyy-MM-dd-HHmm}.zip";

    /// <summary>
    /// Writes the archive: the account stores under <c>accounts/</c>, the PIM and feeds stores,
    /// then whatever plain files and directories the caller names — settings, themes, plugins.
    /// </summary>
    public static ProfileArchiveResult WriteArchive(
        string destination,
        string accountsDirectory,
        string? pimDb,
        string? feedsDb,
        IEnumerable<(string Path, string ArchiveName)> files,
        IEnumerable<(string Directory, string Prefix)> directories,
        DateTimeOffset now,
        IProgress<(int Done, int Total, string Item)>? progress = null)
    {
        var scratch = Directory.CreateTempSubdirectory("mailbox-backup-").FullName;
        var fileList = files.ToList();
        var directoryList = directories.ToList();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            if (File.Exists(destination)) File.Delete(destination);

            // Counted up front so the bar can be a fraction rather than a pulse.
            var planned =
                (Directory.Exists(accountsDirectory) ? Directory.EnumerateFiles(accountsDirectory, "*.db").Count() : 0)
                + (pimDb is { Length: > 0 } && File.Exists(pimDb) ? 1 : 0)
                + (feedsDb is { Length: > 0 } && File.Exists(feedsDb) ? 1 : 0)
                + fileList.Count(f => File.Exists(f.Path))
                + directoryList.Where(d => Directory.Exists(d.Directory))
                    .Sum(d => Directory.EnumerateFiles(d.Directory, "*", SearchOption.AllDirectories).Count());
            var done = 0;

            void Step(string item) => progress?.Report((++done, planned, item));

            var entries = new List<string>();
            using (var zip = ZipFile.Open(destination, ZipArchiveMode.Create))
            {
                if (Directory.Exists(accountsDirectory))
                {
                    foreach (var db in Directory.EnumerateFiles(accountsDirectory, "*.db").OrderBy(p => p))
                    {
                        var name = "accounts/" + Path.GetFileName(db);
                        Step(Path.GetFileName(db));
                        if (CopyStore(db, mail: true, scratch) is not { } copied)
                        {
                            return ProfileArchiveResult.Failed(destination,
                                $"“{Path.GetFileName(db)}” did not copy cleanly.");
                        }

                        zip.CreateEntryFromFile(copied, name);
                        entries.Add(name);
                    }
                }

                if (pimDb is { Length: > 0 } && File.Exists(pimDb))
                {
                    Step("pim.db");
                    if (CopyStore(pimDb, mail: false, scratch) is not { } copied)
                    {
                        return ProfileArchiveResult.Failed(destination, "The calendar store did not copy cleanly.");
                    }

                    zip.CreateEntryFromFile(copied, "pim.db");
                    entries.Add("pim.db");
                }

                if (feedsDb is { Length: > 0 } && File.Exists(feedsDb))
                {
                    Step("feeds.db");
                    if (CopyStore(feedsDb, mail: true, scratch) is not { } copied)
                    {
                        return ProfileArchiveResult.Failed(destination, "The feeds store did not copy cleanly.");
                    }

                    zip.CreateEntryFromFile(copied, "feeds.db");
                    entries.Add("feeds.db");
                }

                foreach (var (path, archiveName) in fileList)
                {
                    if (!File.Exists(path)) continue;
                    Step(archiveName);
                    zip.CreateEntryFromFile(path, archiveName);
                    entries.Add(archiveName);
                }

                foreach (var (directory, prefix) in directoryList)
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        var name = prefix + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/');
                        Step(name);
                        zip.CreateEntryFromFile(file, name);
                        entries.Add(name);
                    }
                }

                var manifest = new ProfileManifest(now, Schema.Migrations.Latest, Pim.PimMigrations.Latest, entries);
                var entry = zip.CreateEntry(ManifestName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }

            var bytes = new FileInfo(destination).Length;
            Log.Info($"Backed up the profile to {destination} ({bytes:N0} bytes, {entries.Count} entries).");
            return new ProfileArchiveResult(true, destination, bytes, entries.Count);
        }
        catch (Exception ex)
        {
            Log.Warn($"Profile backup to {destination} failed.", ex);
            return ProfileArchiveResult.Failed(destination, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // A scratch directory that would not delete is a leak, not a failure: the
                // backup or restore it served has already said how it went.
            }
        }
    }

    /// <summary>Reads the manifest out of an archive, or says why it cannot be one of ours.</summary>
    public static (ProfileManifest? Manifest, string? Error) Inspect(string archive)
    {
        try
        {
            if (!File.Exists(archive)) return (null, "There is no file there.");

            using var zip = ZipFile.OpenRead(archive);
            if (zip.GetEntry(ManifestName) is not { } entry)
            {
                return (null, "That is not a Mailbox backup — it carries no manifest.");
            }

            using var reader = new StreamReader(entry.Open());
            var manifest = JsonSerializer.Deserialize<ProfileManifest>(reader.ReadToEnd());
            return manifest is null
                ? (null, "The manifest could not be read.")
                : (manifest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Puts an archive back: every store verified first, then each target displaced with a
    /// dated name and the backup's copy written in. Nothing is touched until everything the
    /// archive holds has proven readable.
    /// </summary>
    public static ProfileRestoreResult Restore(
        string archive,
        string accountsDirectory,
        string? pimDb,
        string? feedsDb,
        IEnumerable<(string ArchiveName, string Path)> files,
        IEnumerable<(string Prefix, string Directory)> directories,
        DateTimeOffset now)
    {
        var scratch = Directory.CreateTempSubdirectory("mailbox-restore-").FullName;

        try
        {
            var (manifest, error) = Inspect(archive);
            if (manifest is null) return ProfileRestoreResult.Failed(error!);

            if (manifest.MailSchema > Schema.Migrations.Latest || manifest.PimSchema > Pim.PimMigrations.Latest)
            {
                return ProfileRestoreResult.Failed(
                    "That backup was made by a newer Mailbox than this one. Update first, then restore.");
            }

            ZipFile.ExtractToDirectory(archive, scratch);

            // Verify every store before touching anything: a restore that discovers a damaged
            // copy halfway through has already destroyed what it replaced.
            foreach (var name in manifest.Entries.Where(e => e.EndsWith(".db", StringComparison.OrdinalIgnoreCase)))
            {
                var extracted = Path.Combine(scratch, name);
                if (!File.Exists(extracted)) return ProfileRestoreResult.Failed($"“{name}” is missing from the archive.");

                var problems = Verify(extracted, mail: name != "pim.db");
                if (problems.Count > 0)
                {
                    return ProfileRestoreResult.Failed($"“{name}” in that backup is damaged: {string.Join("; ", problems)}");
                }
            }

            var stamp = $"replaced-{now:yyyyMMdd-HHmmss}";
            var restored = new List<string>();
            var displaced = new List<string>();

            // The account stores, wholesale: the directory is displaced and rebuilt, so an
            // account that exists now and not in the backup does not survive as a stray.
            if (manifest.Entries.Any(e => e.StartsWith("accounts/", StringComparison.Ordinal)))
            {
                DisplaceDirectory(accountsDirectory, stamp, displaced);
                Directory.CreateDirectory(accountsDirectory);
                foreach (var name in manifest.Entries.Where(e => e.StartsWith("accounts/", StringComparison.Ordinal)))
                {
                    var target = Path.Combine(accountsDirectory, Path.GetFileName(name));
                    File.Copy(Path.Combine(scratch, name), target);
                    restored.Add(target);
                }
            }

            RestoreFile("pim.db", pimDb);
            RestoreFile("feeds.db", feedsDb);

            foreach (var (archiveName, path) in files)
            {
                if (!manifest.Entries.Contains(archiveName)) continue;
                DisplaceFile(path, stamp, displaced);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.Copy(Path.Combine(scratch, archiveName), path);
                restored.Add(path);
            }

            foreach (var (prefix, directory) in directories)
            {
                var held = manifest.Entries.Where(e => e.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList();
                if (held.Count == 0) continue;

                DisplaceDirectory(directory, stamp, displaced);
                foreach (var name in held)
                {
                    var target = Path.Combine(directory, Path.GetRelativePath(prefix, name));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(scratch, name), target);
                }

                restored.Add(directory);
            }

            Log.Info($"Restored the profile from {archive}: {restored.Count} target(s), "
                     + $"{displaced.Count} displaced.");
            return new ProfileRestoreResult(true, restored, displaced);

            void RestoreFile(string name, string? path)
            {
                if (path is not { Length: > 0 } || !manifest.Entries.Contains(name)) return;
                DisplaceFile(path, stamp, displaced);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.Copy(Path.Combine(scratch, name), path);
                restored.Add(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Profile restore from {archive} failed.", ex);
            return ProfileRestoreResult.Failed(ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // A scratch directory that would not delete is a leak, not a failure: the
                // backup or restore it served has already said how it went.
            }
        }
    }

    /// <summary>Keeps the newest archives in a directory and deletes the rest. Says how many went.</summary>
    public static int Prune(string directory, int keep)
    {
        if (!Directory.Exists(directory)) return 0;

        var old = Directory.EnumerateFiles(directory, "mailbox-backup-*.zip")
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .Skip(Math.Max(1, keep))
            .ToList();

        foreach (var file in old)
        {
            File.Delete(file);
            Log.Info($"Pruned old backup {Path.GetFileName(file)}.");
        }

        return old.Count;
    }

    /// <summary>A consistent, verified copy of one store, or null when it does not verify.</summary>
    private static string? CopyStore(string source, bool mail, string scratch)
    {
        var copied = Path.Combine(scratch, Guid.NewGuid().ToString("n") + ".db");

        using (var target = new Microsoft.Data.Sqlite.SqliteConnection(
                   new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = copied }.ToString()))
        {
            target.Open();
            if (mail)
            {
                using var store = new MailStore(source);
                store.BackupTo(target);
            }
            else
            {
                using var store = new Pim.PimStore(source);
                store.BackupTo(target);
            }
        }

        return Verify(copied, mail).Count == 0 ? copied : null;
    }

    private static IReadOnlyList<string> Verify(string path, bool mail)
    {
        if (mail)
        {
            using var store = new MailStore(path);
            return store.CheckIntegrity();
        }

        using var pim = new Pim.PimStore(path);
        return pim.CheckIntegrity();
    }

    private static void DisplaceFile(string path, string stamp, List<string> displaced)
    {
        if (!File.Exists(path)) return;

        var aside = $"{path}.{stamp}";
        File.Move(path, aside);
        displaced.Add(aside);

        // WAL leaves two companions. Left behind, they would be applied over the restored
        // file and undo the restore.
        foreach (var suffix in (string[])["-wal", "-shm"])
        {
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void DisplaceDirectory(string path, string stamp, List<string> displaced)
    {
        if (!Directory.Exists(path)) return;
        var aside = $"{path}.{stamp}";
        Directory.Move(path, aside);
        displaced.Add(aside);
    }
}
