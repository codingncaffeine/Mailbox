using Mailbox.Core.Archive;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.App;

/// <summary>What one AutoArchive run did.</summary>
public sealed record ArchiveOutcome(int Moved, int Deleted, int Expired)
{
    public int Total => Moved + Deleted + Expired;

    public string Summary => Total == 0
        ? "Nothing was old enough to archive."
        : string.Join(", ", new[]
        {
            Moved > 0 ? $"{Moved} moved to Archive" : null,
            Deleted > 0 ? $"{Deleted} deleted" : null,
            Expired > 0 ? $"{Expired} expired and deleted" : null,
        }.Where(s => s is not null)) + ".";
}

/// <summary>
/// AutoArchive's runs: over every account by the settings and each folder's own choice, or
/// over one folder and its subfolders from the Archive dialog. Old mail goes to the account's
/// Archive folder — moved on the server for IMAP, like any move — or is deleted for good.
/// </summary>
/// <remarks>
/// The reference archives into a data file; the Archive folder is the same idea in a place
/// the folder pane already shows. Drafts, the Outbox, the Archive itself and Deleted Items are
/// never archived out of: the first two are not filed mail, and the last two are where archived
/// and deleted mail already is.
/// </remarks>
public static class Archiver
{
    /// <summary>The whole AutoArchive pass, as the timer or the Options button runs it.</summary>
    public static ArchiveOutcome RunAll(IReadOnlyList<OpenAccount> accounts, AutoArchiveOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(options);

        var moved = 0;
        var deleted = 0;
        var expired = 0;
        var defaults = options.DefaultPolicy;

        foreach (var account in accounts)
        {
            if (options.DeleteExpired)
            {
                var gone = account.Mail.ExpiredMessages(now).Select(m => m.Id).ToList();
                if (gone.Count > 0) expired += account.Mail.DeleteMessages(gone);
            }

            if (!options.ArchiveOld) continue;

            foreach (var folder in Archivable(account))
            {
                var own = account.Mail.FolderAutoArchive(folder.Id) is { } json ? FolderArchivePolicy.FromJson(json) : null;
                if (AutoArchive.Effective(own, defaults) is not { } policy) continue;

                var cutoff = AutoArchive.Cutoff(policy.OlderThan, policy.Unit, now);
                var (m, d) = ArchiveFolder(account, folder, cutoff, policy.Action);
                moved += m;
                deleted += d;
            }
        }

        var outcome = new ArchiveOutcome(moved, deleted, expired);
        Log.Info($"AutoArchive: {outcome.Summary}");
        return outcome;
    }

    /// <summary>The Archive dialog: one folder — and its subfolders when asked — older than a date.</summary>
    public static ArchiveOutcome ArchiveFolderTree(OpenAccount account, long folderId, bool subfolders, DateTimeOffset olderThan, bool includeDoNotArchive)
    {
        ArgumentNullException.ThrowIfNull(account);

        var all = account.Mail.Folders(account.Account.Id);
        var chosen = new List<Folder>();
        var frontier = new Queue<long>([folderId]);
        while (frontier.TryDequeue(out var id))
        {
            if (all.FirstOrDefault(f => f.Id == id) is not { } folder) continue;
            chosen.Add(folder);
            if (!subfolders) break;
            foreach (var child in all.Where(f => f.ParentId == id)) frontier.Enqueue(child.Id);
        }

        var moved = 0;
        foreach (var folder in chosen.Where(f => IsArchivable(f)))
        {
            if (!includeDoNotArchive && account.Mail.FolderAutoArchive(folder.Id) is { } json && FolderArchivePolicy.FromJson(json).Mode == FolderArchiveMode.Off) continue;
            var (m, _) = ArchiveFolder(account, folder, olderThan, ArchiveAction.Move);
            moved += m;
        }

        var outcome = new ArchiveOutcome(moved, 0, 0);
        Log.Info($"Archive: {outcome.Summary}");
        return outcome;
    }

    /// <summary>The folders AutoArchive looks at: filed mail, not the Outbox, Drafts, Archive or Deleted Items.</summary>
    private static IEnumerable<Folder> Archivable(OpenAccount account)
        => account.Mail.Folders(account.Account.Id).Where(IsArchivable);

    private static bool IsArchivable(Folder folder)
        => folder.Role is not (FolderRole.Outbox or FolderRole.Drafts or FolderRole.Archive or FolderRole.Deleted);

    /// <summary>Moves or deletes what is older than the cutoff in one folder. Returns (moved, deleted).</summary>
    private static (int Moved, int Deleted) ArchiveFolder(OpenAccount account, Folder folder, DateTimeOffset cutoff, ArchiveAction action)
    {
        var old = account.Mail.MessagesOlderThan(folder.Id, cutoff).Select(m => m.Id).ToList();
        if (old.Count == 0) return (0, 0);

        if (action == ArchiveAction.Delete)
        {
            return (0, account.Mail.DeleteMessages(old));
        }

        var archive = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Archive)
                      ?? account.Mail.AddFolder(account.Account.Id, "Archive", FolderRole.Archive);
        if (archive.Id == folder.Id) return (0, 0);
        return (account.Mail.MoveMessages(old, archive.Id), 0);
    }
}
