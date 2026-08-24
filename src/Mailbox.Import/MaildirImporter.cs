using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Import;

/// <summary>What an import came to: the counts a reader checks a migration by.</summary>
public sealed record ImportReport(
    int Folders,
    int Imported,
    int AlreadyHere,
    int Trashed,
    int Unreadable,
    IReadOnlyList<string> Notes)
{
    public string Summary =>
        $"{Imported:N0} message(s) into {Folders:N0} folder(s)"
        + (AlreadyHere > 0 ? $"; {AlreadyHere:N0} already here" : string.Empty)
        + (Trashed > 0 ? $"; {Trashed:N0} marked deleted and left behind" : string.Empty)
        + (Unreadable > 0 ? $"; {Unreadable:N0} could not be read" : string.Empty)
        + ".";
}

/// <summary>
/// Files a maildir tree into an account, through the same repository the receivers use.
/// </summary>
/// <remarks>
/// Four decisions, each the conservative one:
/// <list type="bullet">
/// <item><b>The source is never written to</b> — not a flag, not a rename. A migration that
/// edits what it migrates from cannot be re-run after a doubt.</item>
/// <item><b>Well-known folder names merge into the account's own folders</b> — Inbox into
/// Inbox, Sent into Sent Items, the spellings every source uses — and everything else is
/// created by name under the account, hierarchy kept. Outbox deliberately does not map:
/// somebody's unsent 2019 mail must not arrive looking ready to send.</item>
/// <item><b>A message the folder already holds is skipped</b>, known by its Message-ID —
/// re-running an interrupted import tops up instead of doubling. A message without one cannot
/// be told from its twin and is imported; the report says how many were skipped.</item>
/// <item><b>Nothing acts on what arrives</b>: no junk filter, no rules, no reminders. Import
/// is furniture moving in, not mail arriving.</item>
/// </list>
/// Received times come from the message's own Date, falling back to the file's write time —
/// import day would sort a decade of mail into one afternoon.
/// </remarks>
public sealed class MaildirImporter(MailRepository mail, long accountId)
{
    private readonly MailRepository _mail = mail ?? throw new ArgumentNullException(nameof(mail));

    /// <summary>Runs the import. Progress is (done, total), for a dialog's counter.</summary>
    public ImportReport Run(string root, Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        var messages = Maildir.Scan(root);
        var filer = new MessageFiler(_mail);
        var trashed = 0;
        var done = 0;

        var folders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in messages)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(done++, messages.Count);

            if (message.IsTrashed)
            {
                trashed++;
                continue;
            }

            var folderId = Folder(folders, message.Folder, filer.Notes);

            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(message.Path);
            }
            catch (Exception ex)
            {
                filer.Notes.Add($"Could not read {Path.GetFileName(message.Path)}: {ex.Message}");
                continue;
            }

            filer.File(folderId, raw, message.IsRead, message.IsFlagged,
                fallbackDate: new DateTimeOffset(File.GetLastWriteTimeUtc(message.Path), TimeSpan.Zero),
                name: Path.GetFileName(message.Path));
        }

        progress?.Invoke(messages.Count, messages.Count);
        Log.Info($"Maildir import: {filer.Imported} in, {filer.AlreadyHere} already here, {trashed} trashed, "
                 + $"{filer.Unreadable} unreadable, from {root}.");

        return new ImportReport(folders.Count, filer.Imported, filer.AlreadyHere, trashed, filer.Unreadable, filer.Notes);
    }

    /// <summary>The folder a path of segments files into, made on first meeting.</summary>
    private long Folder(Dictionary<string, long> known, IReadOnlyList<string> path, List<string> notes)
    {
        var key = string.Join("/", path);
        if (known.TryGetValue(key, out var id)) return id;

        // One segment with a well-known name merges into the account's own folder — the point
        // of a migration is that Sent mail is in Sent Items, not in a second folder beside it.
        if (path.Count == 1 && WellKnownFolders.RoleFor(path[0]) is { } role
            && _mail.FolderWithRole(accountId, role) is { } existing)
        {
            known[key] = existing.Id;
            if (!string.Equals(existing.Name, path[0], StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"“{path[0]}” merged into {existing.Name}.");
            }

            return existing.Id;
        }

        long? parent = null;
        for (var i = 0; i < path.Count; i++)
        {
            var partial = string.Join("/", path.Take(i + 1));
            if (!known.TryGetValue(partial, out var levelId))
            {
                levelId = _mail.AddFolder(accountId, path[i], parentId: parent).Id;
                known[partial] = levelId;
            }

            parent = levelId;
        }

        return known[key];
    }

}
