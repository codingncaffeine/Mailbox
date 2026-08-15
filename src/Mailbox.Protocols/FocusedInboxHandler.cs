using Mailbox.Core.Focus;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>
/// Sorts an arriving Inbox message into Focused or Other (§12), from what the message says
/// about itself, whether the reader has written to its sender, and what the reader has said
/// about that sender before.
/// </summary>
/// <remarks>
/// Runs whether or not Focused Inbox is switched on: the column is filled either way, so
/// turning the view on later has something to show rather than an Inbox that is all Focused
/// until new mail arrives. It never moves a message; it only marks it.
/// </remarks>
public sealed class FocusedInboxHandler : IArrivalHandler
{
    /// <inheritdoc />
    public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
    {
        if (folder.Role != FolderRole.Inbox) return folder.Id;

        var facts = FactsFor(mail, message);
        mail.SetFocused([messageId], FocusedInbox.IsFocused(facts));
        return folder.Id;
    }

    /// <summary>What the classifier is shown for a message.</summary>
    public static FocusFacts FactsFor(MailRepository mail, MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(message);

        var from = message.From.Mailboxes.FirstOrDefault();
        var address = from?.Address ?? string.Empty;
        var own = mail.OwnAddress();

        return new FocusFacts
        {
            FromAddress = address,
            FromName = from?.Name ?? string.Empty,
            HeaderNames = [.. message.Headers.Select(h => h.Field.ToLowerInvariant()).Distinct()],
            Precedence = message.Headers["Precedence"],
            AutoSubmitted = message.Headers["Auto-Submitted"],
            KnownCorrespondent = address.Length > 0 && mail.HasWrittenTo(address),
            AddressedToMe = own is not null && message.To.Mailboxes.Any(m => string.Equals(m.Address, own, StringComparison.OrdinalIgnoreCase)),
            Override = address.Length > 0 ? mail.FocusOverride(address) : null,
        };
    }
}
