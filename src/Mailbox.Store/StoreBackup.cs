using Microsoft.Data.Sqlite;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Store;

/// <summary>What a backup or restore did.</summary>
public sealed record BackupResult(bool Ok, string Path, long Bytes, string? Error = null)
{
    public static BackupResult Failed(string path, string error) => new(false, path, 0, error);
}

/// <summary>
/// Copying the store somewhere safe, and putting it back.
/// </summary>
/// <remarks>
/// Uses SQLite's own online backup rather than copying the file. A store in WAL mode is two
/// files and a shared-memory region, and copying the main one while anything is writing
/// produces something that opens cleanly and is missing the last however-many messages — the
/// worst kind of broken, because it looks like it worked.
/// <para>
/// Restore refuses to overwrite in place. The existing store is moved aside first, so a restore
/// from the wrong backup is recoverable rather than the second half of a disaster.
/// </para>
/// </remarks>
public static class StoreBackup
{
    /// <summary>Copies a live store to a file, safe to run while it is in use.</summary>
    public static BackupResult To(MailStore store, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) File.Delete(destination);

            using var target = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
            target.Open();

            store.BackupTo(target);

            // Everything a backup should be able to say for itself before it is trusted.
            using var check = new MailStore(destination);
            var problems = check.CheckIntegrity();
            if (problems.Count > 0)
            {
                return BackupResult.Failed(destination,
                    $"The copy did not verify: {string.Join("; ", problems)}");
            }

            var bytes = new FileInfo(destination).Length;
            Log.Info($"Backed up the store to {destination} ({bytes:N0} bytes).");
            return new BackupResult(true, destination, bytes);
        }
        catch (Exception ex)
        {
            Log.Warn($"Backup to {destination} failed.", ex);
            return BackupResult.Failed(destination, ex.Message);
        }
    }

    /// <summary>
    /// Puts a backup back, moving whatever is there now aside first.
    /// </summary>
    /// <returns>Where the previous store was moved to, or null when there was not one.</returns>
    public static (BackupResult Result, string? Displaced) From(string backup, string destination)
    {
        try
        {
            if (!File.Exists(backup))
            {
                return (BackupResult.Failed(backup, "There is no file there."), null);
            }

            // Verify before touching anything. A restore that discovers the backup is unreadable
            // halfway through has already destroyed what it replaced.
            using (var candidate = new MailStore(backup))
            {
                var problems = candidate.CheckIntegrity();
                if (problems.Count > 0)
                {
                    return (BackupResult.Failed(backup,
                        $"That backup is damaged: {string.Join("; ", problems)}"), null);
                }
            }

            string? displaced = null;
            if (File.Exists(destination))
            {
                displaced = $"{destination}.replaced-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(destination, displaced);

                // WAL leaves two companions. Left behind, they would be applied over the
                // restored file and undo the restore.
                foreach (var suffix in (string[])["-wal", "-shm"])
                {
                    if (File.Exists(destination + suffix)) File.Delete(destination + suffix);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(backup, destination);

            var bytes = new FileInfo(destination).Length;
            Log.Info($"Restored the store from {backup}.");
            return (new BackupResult(true, destination, bytes), displaced);
        }
        catch (Exception ex)
        {
            Log.Warn($"Restore from {backup} failed.", ex);
            return (BackupResult.Failed(backup, ex.Message), null);
        }
    }

    /// <summary>A dated name, so successive backups do not overwrite each other.</summary>
    public static string SuggestedName(DateTimeOffset when)
        => $"mailbox-{when:yyyy-MM-dd-HHmm}.db";
}
