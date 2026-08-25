using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Import;

/// <summary>
/// The single-file mail importers: an mbox into a folder, .eml files into a folder. Both ride
/// <see cref="MessageFiler"/>, so the skip-by-identity and the counts mean the same thing they
/// mean for a maildir.
/// </summary>
public static class MailFileImport
{
    /// <summary>Files an mbox into one folder.</summary>
    public static ImportReport Mbox(MailRepository mail, long folderId, string path,
        Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        var filer = new MessageFiler(mail);

        using var stream = File.OpenRead(path);
        var messages = Import.Mbox.Read(stream);

        var done = 0;
        foreach (var message in messages)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(done++, messages.Count);
            filer.File(folderId, message.Raw, message.IsRead, message.IsFlagged,
                fallbackDate: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                name: System.IO.Path.GetFileName(path));
        }

        Log.Info($"mbox import: {filer.Imported} in, {filer.AlreadyHere} already here, "
                 + $"{filer.Unreadable} unreadable, from {path}.");
        return new ImportReport(1, filer.Imported, filer.AlreadyHere, 0, filer.Unreadable, filer.Notes);
    }

    /// <summary>Files .eml files into one folder. Read, unflagged — an .eml carries no flags.</summary>
    public static ImportReport Eml(MailRepository mail, long folderId, IReadOnlyList<string> paths,
        CancellationToken cancellation = default)
    {
        var filer = new MessageFiler(mail);

        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();

            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                filer.Notes.Add($"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
                continue;
            }

            filer.File(folderId, raw, read: true, flagged: false,
                fallbackDate: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                name: System.IO.Path.GetFileName(path));
        }

        Log.Info($".eml import: {filer.Imported} in, {filer.AlreadyHere} already here, "
                 + $"{filer.Unreadable} unreadable, of {paths.Count} file(s).");
        return new ImportReport(1, filer.Imported, filer.AlreadyHere, 0, filer.Unreadable, filer.Notes);
    }

    /// <summary>Writes one folder out as mboxrd, every message its stored bytes.</summary>
    public static int ExportMbox(MailRepository mail, long folderId, string path, CancellationToken cancellation = default)
    {
        using var stream = File.Create(path);
        var written = 0;

        foreach (var summary in mail.Messages(folderId, limit: int.MaxValue))
        {
            cancellation.ThrowIfCancellationRequested();
            if (mail.LoadRaw(summary.Id) is not { } raw) continue;

            Import.Mbox.Append(stream, raw, summary.Received, summary.FromAddress is { Length: > 0 } from ? from : null);
            written++;
        }

        Log.Info($"mbox export: {written} message(s) to {path}.");
        return written;
    }

    /// <summary>Writes one message as .eml — its stored bytes, verbatim, which is §7.6a's promise.</summary>
    public static bool ExportEml(MailRepository mail, long messageId, string path)
    {
        if (mail.LoadRaw(messageId) is not { } raw) return false;
        File.WriteAllBytes(path, raw);
        Log.Info($".eml export: message {messageId} to {path}.");
        return true;
    }
}

/// <summary>What a PIM import came to, counted the way the mail reports count.</summary>
public sealed record PimImportReport(int Events, int Tasks, int Journal, int Contacts, int AlreadyHere, IReadOnlyList<string> Notes)
{
    public int Imported => Events + Tasks + Journal + Contacts;

    public string Summary =>
        string.Join(", ", new[]
        {
            Events > 0 ? $"{Events:N0} appointment(s)" : null,
            Tasks > 0 ? $"{Tasks:N0} task(s)" : null,
            Journal > 0 ? $"{Journal:N0} journal entr(ies)" : null,
            Contacts > 0 ? $"{Contacts:N0} contact(s)" : null,
        }.Where(p => p is not null)) is { Length: > 0 } counts
            ? counts + (AlreadyHere > 0 ? $"; {AlreadyHere:N0} already here" : string.Empty) + "."
            : "Nothing to import.";
}

/// <summary>
/// The .ics and .vcf importers: components routed to the default collection of their kind,
/// written through the same codecs the editors use, skipped by UID when already here, and
/// queued to their servers exactly as an edit is.
/// </summary>
public sealed class PimFileImporter(PimRepository pim, Action<PimItem>? queuePut = null)
{
    private readonly PimRepository _pim = pim ?? throw new ArgumentNullException(nameof(pim));

    /// <summary>Imports an .ics file's events, tasks and journal entries.</summary>
    /// <param name="text">The file.</param>
    /// <param name="intoEvents">
    /// The calendar the events go on. Null for the default one, which is what importing means;
    /// Open Calendar names a calendar of its own instead, so a file opened to be looked at does
    /// not land among the reader's own appointments with no way to tell them apart again.
    /// </param>
    public PimImportReport Ics(string text, Collection? intoEvents = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var notes = new List<string>();
        int events = 0, tasks = 0, journal = 0, already = 0;

        foreach (var calendarEvent in Safe(() => ICalendarCodec.Parse(text), notes, "events"))
        {
            var collection = intoEvents ?? Default(CollectionKind.Events, "Calendar");
            if (Save(PimEventCodec.ToItem(calendarEvent, collection.Id, Existing(collection.Id, calendarEvent.Uid)), out var wrote) && wrote) events++;
            else already++;
        }

        foreach (var task in Safe(() => TodoCodec.Parse(text), notes, "tasks"))
        {
            var collection = Default(CollectionKind.Tasks, "Tasks");
            if (Save(PimTodoCodec.ToItem(task, collection.Id, Existing(collection.Id, task.Uid)), out var wrote) && wrote) tasks++;
            else already++;
        }

        foreach (var entry in Safe(() => JournalCodec.Parse(text), notes, "journal"))
        {
            var collection = Default(CollectionKind.Journal, "Journal");
            if (Save(PimJournalCodec.ToItem(entry, collection.Id, Existing(collection.Id, entry.Uid)), out var wrote) && wrote) journal++;
            else already++;
        }

        Log.Info($".ics import: {events} event(s), {tasks} task(s), {journal} journal, {already} already here.");
        return new PimImportReport(events, tasks, journal, 0, already, notes);
    }

    /// <summary>Imports a .vcf file's cards into the default address book.</summary>
    public PimImportReport Vcf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var notes = new List<string>();
        int contacts = 0, already = 0;

        var book = new ContactBook(_pim);
        var home = book.Default();

        foreach (var contact in Safe(() => VCardCodec.Parse(text), notes, "cards"))
        {
            if (contact.Uid is { Length: > 0 } && _pim.ItemsByUid(home.Id, contact.Uid).Count > 0)
            {
                already++;
                continue;
            }

            var written = book.Save(contact, home.Id);
            queuePut?.Invoke(written);
            contacts++;
        }

        Log.Info($".vcf import: {contacts} contact(s), {already} already here.");
        return new PimImportReport(0, 0, 0, contacts, already, notes);
    }

    private static IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>> parse, List<string> notes, string kind)
    {
        try
        {
            return parse();
        }
        catch (Exception ex)
        {
            notes.Add($"Could not read the {kind}: {ex.Message}");
            return [];
        }
    }

    private PimItem? Existing(long collectionId, string uid)
        => uid.Length == 0 ? null : _pim.ItemsByUid(collectionId, uid).FirstOrDefault(i => !i.IsOverride);

    private Collection Default(CollectionKind kind, string name)
    {
        var collections = _pim.Collections(kind);
        return collections.FirstOrDefault(c => c.IsDefault)
               ?? collections.FirstOrDefault()
               ?? _pim.AddCollection(kind, name);
    }

    /// <summary>New rows import; an existing UID in the same collection is left alone.</summary>
    private bool Save(PimItem row, out bool wrote)
    {
        if (row.Id != 0)
        {
            // ToItem was handed an existing row: the UID is already here. Left alone — an
            // import must not overwrite what the reader may have edited since.
            wrote = false;
            return false;
        }

        var written = _pim.AddItem(row);
        queuePut?.Invoke(written);
        wrote = true;
        return true;
    }
}
