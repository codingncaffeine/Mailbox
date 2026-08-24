using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;
using Mailbox.Pst;
using Mailbox.Pst.Messaging;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Import;

/// <summary>What a PST import came to: the mail half and the PIM half, each checked by its own counts.</summary>
public sealed record PstImportReport(ImportReport Mail, PimImportReport Pim)
{
    public string Summary => Pim.Imported > 0 || Pim.AlreadyHere > 0
        ? $"{Mail.Summary} {Pim.Summary}"
        : Mail.Summary;
}

/// <summary>
/// Files a whole PST into this application: mail through the same repository the receivers use,
/// and — when a PIM store is given — appointments, contacts, tasks, notes and journal entries
/// through the same repositories the editors use, under the shared importer rules: the source
/// is never written, well-known names merge, what is already here is skipped, nothing acts on
/// what arrives.
/// </summary>
/// <remarks>
/// Items are routed by what they are, not where they sat — a contact filed into a mail folder
/// is still a contact — and the target collection follows the folder only when the folder is of
/// the item's own kind. The first folder of each kind merges into the kind's default collection:
/// its name is the writer's own language, so the position is the tell, not the spelling. Every
/// later folder of that kind becomes a collection wearing its own name. Without a PIM store the
/// mail-only behaviour stands and the report names what stayed behind.
/// </remarks>
public sealed class PstImporter(MailRepository mail, long accountId, PimRepository? pim = null, Action<PimItem>? queuePut = null)
{
    private readonly MailRepository _mail = mail ?? throw new ArgumentNullException(nameof(mail));

    private enum ItemKind
    {
        Mail,
        Event,
        Contact,
        Task,
        Note,
        Journal,
    }

    /// <summary>Folder container classes, by the kind their items are — folders say IPF where their items say IPM.</summary>
    private static ItemKind FolderKind(string containerClass) =>
        containerClass.StartsWith("IPF.Appointment", StringComparison.OrdinalIgnoreCase) ? ItemKind.Event
        : containerClass.StartsWith("IPF.Contact", StringComparison.OrdinalIgnoreCase) ? ItemKind.Contact
        : containerClass.StartsWith("IPF.Task", StringComparison.OrdinalIgnoreCase) ? ItemKind.Task
        : containerClass.StartsWith("IPF.StickyNote", StringComparison.OrdinalIgnoreCase) ? ItemKind.Note
        : containerClass.StartsWith("IPF.Journal", StringComparison.OrdinalIgnoreCase) ? ItemKind.Journal
        : ItemKind.Mail;

    private static ItemKind MessageKind(string messageClass) =>
        messageClass.StartsWith("IPM.Appointment", StringComparison.OrdinalIgnoreCase) ? ItemKind.Event
        : messageClass.StartsWith("IPM.Contact", StringComparison.OrdinalIgnoreCase)
          || messageClass.StartsWith("IPM.DistList", StringComparison.OrdinalIgnoreCase) ? ItemKind.Contact
        : messageClass.StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase) ? ItemKind.Task
        : messageClass.StartsWith("IPM.StickyNote", StringComparison.OrdinalIgnoreCase) ? ItemKind.Note
        : messageClass.StartsWith("IPM.Activity", StringComparison.OrdinalIgnoreCase) ? ItemKind.Journal
        : ItemKind.Mail;

    public PstImportReport Run(string path, Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        using var file = PstFile.Open(path);
        var store = PstStore.Open(file);
        var names = PstNamedProperties.Open(file);
        var storeUid = store.RecordKey is { Length: > 0 } key ? Convert.ToHexString(key).ToLowerInvariant() : "unknown";

        var filer = new MessageFiler(_mail);
        var mailFolders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var pimSide = new PimSide(pim, queuePut);
        var leftBehind = new List<string>();
        var skippedPim = 0;
        var done = 0;

        // Two passes so the progress counter has a total: the first only counts.
        var total = 0;
        Walk(store.MailRoot, [], ItemKind.Mail, (_, _, _) => total++, leftBehind, cancellation);
        leftBehind.Clear();

        Walk(store.MailRoot, [], ItemKind.Mail, (folderPath, folderKind, message) =>
        {
            progress?.Invoke(done++, total);

            var byClass = MessageKind(message.MessageClass);
            var kind = byClass != ItemKind.Mail ? byClass : folderKind == ItemKind.Mail ? ItemKind.Mail : folderKind;
            if (kind == ItemKind.Mail)
            {
                FileMail(message, folderPath, mailFolders, filer, storeUid, cancellation);
                return;
            }

            if (pim is null)
            {
                skippedPim++;
                return;
            }

            var uid = $"pst-{storeUid}-{message.Nid.Value:x}";
            pimSide.Add(kind, message, names, uid, folderKind == kind ? folderPath[^1] : null);
        }, leftBehind, cancellation);

        progress?.Invoke(total, total);

        if (leftBehind.Count > 0)
        {
            filer.Notes.Add("Left for the calendar, contacts and tasks importer: "
                + string.Join(", ", leftBehind.Distinct()) + ".");
        }

        if (skippedPim > 0)
            filer.Notes.Add($"{skippedPim} non-mail item(s) left behind with them.");

        var mailReport = new ImportReport(mailFolders.Count, filer.Imported, filer.AlreadyHere, 0, filer.Unreadable, filer.Notes);
        var pimReport = pimSide.Report();
        Log.Info($"PST import: {filer.Imported} mail in, {filer.AlreadyHere} already here, {filer.Unreadable} unreadable; "
                 + $"{pimReport.Imported} PIM item(s) in, {pimReport.AlreadyHere} already here; from {path}.");

        return new PstImportReport(mailReport, pimReport);
    }

    private void FileMail(PstMessage message, IReadOnlyList<string> folderPath,
        Dictionary<string, long> known, MessageFiler filer, string storeUid, CancellationToken cancellation)
    {
        var folderId = Folder(known, folderPath, filer.Notes);
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
    }

    private void Walk(PstFolder folder, IReadOnlyList<string> path, ItemKind kind,
        Action<IReadOnlyList<string>, ItemKind, PstMessage> visit, List<string> leftBehind, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        // Mail sitting directly in the top folder files into a folder wearing that folder's
        // own name — the root is the account, and the account itself holds no messages.
        var into = path.Count > 0
            ? path
            : [folder.Name is { Length: > 0 } rootName ? rootName : "Imported"];

        foreach (var message in folder.Messages())
            visit(into, kind, message);

        foreach (var child in folder.Subfolders())
        {
            var childKind = FolderKind(child.ContainerClass);
            if (childKind != ItemKind.Mail && pim is null)
            {
                leftBehind.Add(child.Name is { Length: > 0 } name ? name : child.ContainerClass);
                continue;
            }

            var childPath = new List<string>(path) { child.Name is { Length: > 0 } name2 ? name2 : "Unnamed" };
            Walk(child, childPath, childKind, visit, leftBehind, cancellation);
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

    /// <summary>
    /// The PIM half's state: which collections have been chosen, the books, and the counts.
    /// </summary>
    private sealed class PimSide(PimRepository? pim, Action<PimItem>? queuePut)
    {
        private readonly Dictionary<(CollectionKind, string), long> _collections = [];
        private readonly HashSet<CollectionKind> _defaultTaken = [];
        private ContactBook? _book;
        private int _events;
        private int _tasks;
        private int _journal;
        private int _contacts;
        private int _already;
        private readonly List<string> _notes = [];

        public PimImportReport Report() => new(_events, _tasks, _journal, _contacts, _already, _notes);

        public void Add(ItemKind kind, IStoredMessage message, PstNamedProperties names, string uid, string? folderName)
        {
            if (pim is null) return;
            try
            {
                switch (kind)
                {
                    case ItemKind.Event:
                        AddEvents(message, names, uid, folderName);
                        break;
                    case ItemKind.Task:
                        AddTask(message, names, uid, folderName);
                        break;
                    case ItemKind.Contact:
                        AddContact(message, names, uid, folderName);
                        break;
                    case ItemKind.Note:
                        AddJournal(PstPim.ToNote(message, names, uid), "Notes", folderName, ref _journal);
                        break;
                    case ItemKind.Journal:
                        AddJournal(PstPim.ToJournal(message, names, uid), "Journal", folderName, ref _journal);
                        break;
                }
            }
            catch (PstException ex)
            {
                _notes.Add($"Could not read “{message.Subject}”: {ex.Message}");
            }
        }

        private void AddEvents(IStoredMessage message, PstNamedProperties names, string uid, string? folderName)
        {
            if (PstPim.ToEvents(message, names, uid, _notes) is not { } events)
            {
                _notes.Add($"“{message.Subject}” has no start and end and was not imported.");
                return;
            }

            var collection = Collection(CollectionKind.Events, folderName, "Calendar");
            foreach (var calendarEvent in events)
            {
                var rows = pim!.ItemsByUid(collection, calendarEvent.Uid);
                var exists = calendarEvent.IsOverride
                    ? rows.Any(row => row.IsOverride && row.RecurrenceId == ICalendarCodec.RecurrenceIdText(calendarEvent.RecurrenceId!))
                    : rows.Any(row => !row.IsOverride);
                if (exists)
                {
                    _already++;
                    continue;
                }

                var written = pim.AddItem(PimEventCodec.ToItem(calendarEvent, collection));
                queuePut?.Invoke(written);
                _events++;
            }
        }

        private void AddTask(IStoredMessage message, PstNamedProperties names, string uid, string? folderName)
        {
            var collection = Collection(CollectionKind.Tasks, folderName, "Tasks");
            if (pim!.ItemsByUid(collection, uid).Count > 0)
            {
                _already++;
                return;
            }

            var written = pim.AddItem(PimTodoCodec.ToItem(PstPim.ToTask(message, names, uid, _notes), collection));
            queuePut?.Invoke(written);
            _tasks++;
        }

        private void AddContact(IStoredMessage message, PstNamedProperties names, string uid, string? folderName)
        {
            _book ??= new ContactBook(pim!);
            var collection = Collection(CollectionKind.Contacts, folderName, "Contacts");
            if (pim!.ItemsByUid(collection, uid).Count > 0)
            {
                _already++;
                return;
            }

            var written = _book.Save(PstPim.ToContact(message, names, uid), collection);
            queuePut?.Invoke(written);
            _contacts++;
        }

        private void AddJournal(JournalEntry entry, string defaultName, string? folderName, ref int count)
        {
            var collection = Collection(CollectionKind.Journal, folderName, defaultName);
            if (pim!.ItemsByUid(collection, entry.Uid).Count > 0)
            {
                _already++;
                return;
            }

            var written = pim.AddItem(PimJournalCodec.ToItem(entry, collection));
            queuePut?.Invoke(written);
            count++;
        }

        /// <summary>
        /// The collection a folder's items land in. The kind's first folder — and any stray
        /// item with no folder of its kind — takes the default collection; later folders are
        /// found by name before being made, so a re-run lands where the first run did.
        /// </summary>
        private long Collection(CollectionKind kind, string? folderName, string defaultName)
        {
            var key = (kind, folderName ?? string.Empty);
            if (_collections.TryGetValue(key, out var id)) return id;

            var all = pim!.Collections(kind);
            long resolved;
            if (folderName is null || _defaultTaken.Add(kind))
            {
                resolved = (all.FirstOrDefault(c => c.IsDefault) ?? all.FirstOrDefault())?.Id
                           ?? pim.AddCollection(kind, folderName ?? defaultName).Id;
            }
            else
            {
                resolved = all.FirstOrDefault(c => string.Equals(c.DisplayName, folderName, StringComparison.OrdinalIgnoreCase))?.Id
                           ?? pim.AddCollection(kind, folderName).Id;
            }

            _collections[key] = resolved;
            return resolved;
        }
    }
}
