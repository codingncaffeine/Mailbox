using Avalonia.Controls;
using Mailbox.App.Views;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.App;

/// <summary>
/// Answers an arriving read-receipt request when a message is first displayed, per the
/// Tracking radios: always, never, or ask each time.
/// </summary>
/// <remarks>
/// The question is settled once per message — sent or declined, recorded on the row — so a
/// message re-opened tomorrow is not asked about again. The receipt goes out through the
/// ordinary outbox: it is mail, and it leaves on mail's own schedule.
/// <para>
/// Never asked about mail this application would not honestly call "displayed to its reader":
/// anything in Junk (a tracked display is exactly what junk senders request receipts for),
/// and anything in Drafts, Sent or the Outbox, which the reader wrote rather than received.
/// </para>
/// </remarks>
public static class ReadReceipts
{
    public static async Task MaybeAnswerAsync(
        Window owner, OpenAccount account, long messageId, MimeMessage message, FolderRole shownIn)
    {
        if (shownIn is FolderRole.Junk or FolderRole.Drafts or FolderRole.Sent or FolderRole.Outbox) return;
        if (App.MailOptions.ReadReceiptAnswer == ReadReceiptAnswer.Never) return;
        if (ReadReceipt.RequestedBy(message) is not { Count: > 0 } requested) return;

        // A request from the reader's own address answers nothing: it is their own sent copy,
        // or a loop.
        var self = account.Account.Address;
        if (requested.All(r => string.Equals(r.Address, self, StringComparison.OrdinalIgnoreCase))) return;

        if (account.Mail.ReceiptSettled(messageId)) return;

        if (App.MailOptions.ReadReceiptAnswer == ReadReceiptAnswer.Ask)
        {
            var sender = message.From.Mailboxes.FirstOrDefault()?.Address ?? requested[0].Address;
            var agreed = await Confirm.AskAsync(owner, "Read receipt requested",
                $"The sender of this message ({sender}) asked to be told when it is read.\n"
                + "Send a read receipt?", "Send Receipt", destructive: false);

            if (!agreed)
            {
                // Declined is settled too: the reference asks once, not on every open.
                account.Mail.SetReceiptSettled(messageId);
                Log.Info($"Read receipt: declined for {self}/{messageId}.");
                return;
            }
        }

        var from = new MailboxAddress(account.Account.DisplayName ?? string.Empty, self);
        if (ReadReceipt.Build(message, from, DateTimeOffset.Now) is not { } receipt) return;

        new SmtpSender(account.Mail).Queue(account.Account.Id, receipt);
        account.Mail.SetReceiptSettled(messageId);
        Log.Info($"Read receipt: queued to {string.Join(", ", requested.Select(r => r.Address))} "
                 + $"for {self}/{messageId}.");
    }
}
