using Mailbox.Store;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>
/// Ignore Conversation's arrival half: a message in a conversation the reader has ignored goes
/// to Deleted Items as it arrives, journalled on IMAP like any move made here.
/// </summary>
public sealed class IgnoreHandler : IArrivalHandler
{
    /// <inheritdoc />
    public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
    {
        if (folder.Role != FolderRole.Inbox) return folder.Id;

        // The stored key, not the subject's shape of it: the reply headers decide what
        // conversation an arrival belongs to, and the ignore list holds those keys.
        var key = mail.GetMessage(messageId)?.ThreadKey ?? string.Empty;
        if (key.Length == 0 || !mail.IsIgnored(key)) return folder.Id;

        if (mail.FolderWithRole(folder.AccountId, FolderRole.Deleted) is not { } deleted) return folder.Id;

        mail.MoveMessages([messageId], deleted.Id);
        return deleted.Id;
    }
}
