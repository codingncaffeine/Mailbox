using Mailbox.Core.Diagnostics;
using Mailbox.Pst;
using Mailbox.Pst.Messaging;
using Mailbox.Store;

namespace Mailbox.Import;

/// <summary>
/// Files a PST's mail into an account, through the same repository the receivers use and under
/// the same four rules every importer here follows: the source is never written, well-known
/// folder names merge, Message-ID skips what is already here, and nothing acts on what arrives.
/// </summary>
/// <remarks>
/// A PST carries every module, and this is deliberately the mail half only: a folder whose
/// container class says calendar, contacts, tasks, notes or journal is left where it is and
/// named once in the report — those items become appointments and cards, not messages, and
/// pretending a vCard is an email would technically import everything while losing all of it.
/// The same test guards item by item inside mail folders, where a stray contact can live.
/// </remarks>
public sealed class PstImporter(MailRepository mail, long accountId)
{
    private readonly MailRepository _mail = mail ?? throw new ArgumentNullException(nameof(mail));

    /// <summary>The container classes that belong to the PIM importer, when it arrives — folders say IPF where their items say IPM.</summary>
    private static readonly string[] PimContainers =
        ["IPF.Appointment", "IPF.Contact", "IPF.Task", "IPF.StickyNote", "IPF.Journal"];

    private static readonly string[] PimItemClasses =
        ["IPM.Appointment", "IPM.Contact", "IPM.DistList", "IPM.Task", "IPM.StickyNote", "IPM.Activity"];

    public ImportReport Run(string path, Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        using var file = PstFile.Open(path);
        var store = PstStore.Open(file);
        var storeUid = store.RecordKey is { Length: > 0 } key ? Convert.ToHexString(key).ToLowerInvariant() : "unknown";

        var filer = new MessageFiler(_mail);
        var folders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var skippedPim = 0;
        var leftBehind = new List<string>();
        var done = 0;

        // Two passes so the progress counter has a total: the first only counts.
        var total = 0;
        Walk(store.MailRoot, [], (_, _) => total++, ref skippedPim, leftBehind, cancellation);
        skippedPim = 0;
        leftBehind.Clear();

        Walk(store.MailRoot, [], (folderPath, message) =>
        {
            progress?.Invoke(done++, total);

            var folderId = Folder(folders, folderPath, filer.Notes);
            byte[] raw;
            try
            {
                using var stream = new MemoryStream();
                PstMime.Assemble(message, $"{message.Nid.Value:x}.{storeUid}@pst.import.invalid")
                    .WriteTo(stream, cancellation);
                raw = stream.ToArray();
            }
            catch (Exception ex)
            {
                filer.Notes.Add($"Could not rebuild “{message.Subject}”: {ex.Message}");
                return;
            }

            filer.File(folderId, raw, message.IsRead, message.IsFlagged,
                fallbackDate: message.Delivered ?? message.Submitted,
                name: message.Subject is { Length: > 0 } subject ? subject : "a message");
        }, ref skippedPim, leftBehind, cancellation);

        progress?.Invoke(total, total);

        if (leftBehind.Count > 0)
        {
            filer.Notes.Add("Left for the calendar, contacts and tasks importer: "
                + string.Join(", ", leftBehind.Distinct()) + ".");
        }

        if (skippedPim > 0)
            filer.Notes.Add($"{skippedPim} non-mail item(s) in mail folders left behind with them.");

        Log.Info($"PST import: {filer.Imported} in, {filer.AlreadyHere} already here, {filer.Unreadable} unreadable, "
                 + $"{skippedPim} non-mail, from {path}.");

        return new ImportReport(folders.Count, filer.Imported, filer.AlreadyHere, 0, filer.Unreadable, filer.Notes);
    }

    private static void Walk(PstFolder folder, IReadOnlyList<string> path, Action<IReadOnlyList<string>, PstMessage> visit,
        ref int skippedPim, List<string> leftBehind, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        // Mail sitting directly in the top folder files into a folder wearing that folder's
        // own name — the root is the account, and the account itself holds no messages.
        var into = path.Count > 0
            ? path
            : [folder.Name is { Length: > 0 } rootName ? rootName : "Imported"];

        foreach (var message in folder.Messages())
        {
            var itemClass = message.MessageClass;
            if (PimItemClasses.Any(pim => itemClass.StartsWith(pim, StringComparison.OrdinalIgnoreCase)))
            {
                skippedPim++;
                continue;
            }

            visit(into, message);
        }

        foreach (var child in folder.Subfolders())
        {
            if (PimContainers.Any(pim => child.ContainerClass.StartsWith(pim, StringComparison.OrdinalIgnoreCase)))
            {
                leftBehind.Add(child.Name is { Length: > 0 } name ? name : child.ContainerClass);
                continue;
            }

            var childPath = new List<string>(path) { child.Name is { Length: > 0 } name2 ? name2 : "Unnamed" };
            Walk(child, childPath, visit, ref skippedPim, leftBehind, cancellation);
        }
    }

    /// <summary>The folder a path files into, made on first meeting — the Maildir importer's own rule.</summary>
    private long Folder(Dictionary<string, long> known, IReadOnlyList<string> path, List<string> notes)
    {
        var key = string.Join("/", path);
        if (known.TryGetValue(key, out var id)) return id;

        if (path.Count == 1 && WellKnownFolders.RoleFor(path[0]) is { } role
            && _mail.FolderWithRole(accountId, role) is { } existing)
        {
            known[key] = existing.Id;
            if (!string.Equals(existing.Name, path[0], StringComparison.OrdinalIgnoreCase))
                notes.Add($"“{path[0]}” merged into {existing.Name}.");
            return existing.Id;
        }

        long? parent = null;
        for (var i = 0; i < path.Count; i++)
        {
            var partial = string.Join("/", path.Take(i + 1));
            if (!known.TryGetValue(partial, out var levelId))
            {
                // Find before make: a re-run meets the folders its first run created, and a
                // second "Projects" beside the first would hide the dedupe from every message
                // filed into it. Only ordinary folders are found this way — a folder wearing a
                // role is reachable solely through the explicit merge above, which is what keeps
                // a source's Outbox out of anything that sends.
                var found = _mail.Folders(accountId).FirstOrDefault(f =>
                    f.Role == Store.FolderRole.None && f.ParentId == parent
                    && string.Equals(f.Name, path[i], StringComparison.OrdinalIgnoreCase));
                levelId = found?.Id ?? _mail.AddFolder(accountId, path[i], parentId: parent).Id;
                known[partial] = levelId;
            }

            parent = levelId;
        }

        return known[key];
    }
}
