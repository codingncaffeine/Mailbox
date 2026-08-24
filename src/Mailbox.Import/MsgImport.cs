using System.Security.Cryptography;
using Mailbox.Contacts;
using Mailbox.Pst.Messaging;
using Mailbox.Pst.Msg;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Import;

/// <summary>
/// Files one .msg into this application, routed by what the message is: mail into the account's
/// Inbox through the shared filer, an appointment, contact, task, note or journal entry into
/// its kind's default collection through the same mappers the PST importer uses. A saved .msg
/// is one item, so the whole report is one sentence.
/// </summary>
public static class MsgImport
{
    public static string Run(string path, MailRepository? mail, long accountId,
        PimRepository? pim, Action<PimItem>? queuePut)
    {
        var msg = MsgFile.Open(path);
        var message = msg.Message;

        // The uid is the file's own bytes: importing the same file twice meets the same uid,
        // and two different saves of one message are honestly two items.
        var uid = $"msg-{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..32].ToLowerInvariant()}";

        var kind = message.MessageClass;
        if (kind.StartsWith("IPM.Appointment", StringComparison.OrdinalIgnoreCase))
        {
            if (pim is null) return "an appointment; add it once the calendar import is asked for.";
            var notes = new List<string>();
            if (PstPim.ToEvents(message, msg.Names, uid, notes) is not { } events)
                return "an appointment with no start and end; not imported.";

            var collection = Default(pim, CollectionKind.Events, "Calendar");
            var added = 0;
            foreach (var calendarEvent in events)
            {
                if (pim.ItemsByUid(collection, calendarEvent.Uid).Any(row => row.IsOverride == calendarEvent.IsOverride)) continue;
                queuePut?.Invoke(pim.AddItem(PimEventCodec.ToItem(calendarEvent, collection)));
                added++;
            }

            return added > 0 ? $"{added} appointment item(s) into the calendar." : "already in the calendar.";
        }

        if (kind.StartsWith("IPM.Contact", StringComparison.OrdinalIgnoreCase)
            || kind.StartsWith("IPM.DistList", StringComparison.OrdinalIgnoreCase))
        {
            if (pim is null) return "a contact; add it once the address book import is asked for.";
            var book = new ContactBook(pim);
            var home = book.Default();
            if (pim.ItemsByUid(home.Id, uid).Count > 0) return "already in the address book.";
            queuePut?.Invoke(book.Save(PstPim.ToContact(message, msg.Names, uid), home.Id));
            return "1 contact into the address book.";
        }

        if (kind.StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase))
        {
            if (pim is null) return "a task; add it once the task import is asked for.";
            var collection = Default(pim, CollectionKind.Tasks, "Tasks");
            if (pim.ItemsByUid(collection, uid).Count > 0) return "already in the task list.";
            queuePut?.Invoke(pim.AddItem(PimTodoCodec.ToItem(PstPim.ToTask(message, msg.Names, uid, []), collection)));
            return "1 task into the task list.";
        }

        if (kind.StartsWith("IPM.StickyNote", StringComparison.OrdinalIgnoreCase)
            || kind.StartsWith("IPM.Activity", StringComparison.OrdinalIgnoreCase))
        {
            if (pim is null) return "a note; add it once the notes import is asked for.";
            var isNote = kind.StartsWith("IPM.StickyNote", StringComparison.OrdinalIgnoreCase);
            var collection = Default(pim, CollectionKind.Journal, isNote ? "Notes" : "Journal");
            if (pim.ItemsByUid(collection, uid).Count > 0) return "already here.";
            var entry = isNote ? PstPim.ToNote(message, msg.Names, uid) : PstPim.ToJournal(message, msg.Names, uid);
            queuePut?.Invoke(pim.AddItem(PimJournalCodec.ToItem(entry, collection)));
            return isNote ? "1 note onto the wall." : "1 journal entry into the timeline.";
        }

        // Mail, and anything wearing an unknown class: a message is the honest reading.
        if (mail is null) return "add an account first.";
        var inbox = mail.FolderWithRole(accountId, FolderRole.Inbox);
        if (inbox is null) return "the account has no Inbox.";

        var filer = new MessageFiler(mail);
        using var stream = new MemoryStream();
        PstMime.Assemble(message, $"{uid}@msg.import.invalid").WriteTo(stream);
        filer.File(inbox.Id, stream.ToArray(), message.IsRead, message.IsFlagged,
            fallbackDate: message.Delivered ?? message.Submitted, name: Path.GetFileName(path));

        return filer.Imported > 0 ? "1 message into the Inbox."
            : filer.AlreadyHere > 0 ? "already in the Inbox."
            : string.Join("; ", filer.Notes.DefaultIfEmpty("could not be read."));
    }

    private static long Default(PimRepository pim, CollectionKind kind, string name)
    {
        var collections = pim.Collections(kind);
        return (collections.FirstOrDefault(c => c.IsDefault) ?? collections.FirstOrDefault())?.Id
               ?? pim.AddCollection(kind, name).Id;
    }
}
