using MimeKit;

namespace Mailbox.Protocols;

/// <summary>
/// Builds the answer to a read-receipt request: the RFC 8098 message disposition notification,
/// addressed to whoever <c>Disposition-Notification-To</c> names.
/// </summary>
/// <remarks>
/// The MDN is a <c>multipart/report</c> of two parts — a human-readable sentence, and the
/// machine-readable <c>message/disposition-notification</c> — because the requester may be a
/// person reading mail or a system counting confirmations, and the two halves serve one each.
/// The disposition is <c>manual-action/MDN-sent-manually; displayed</c> even when Options says
/// to always send: the setting is the person's standing decision, which is still a person
/// deciding, and <c>automatic-action</c> would claim the software decided alone.
/// </remarks>
public static class ReadReceipt
{
    /// <summary>The addresses a receipt for this message should go to, or empty for none asked.</summary>
    public static IReadOnlyList<MailboxAddress> RequestedBy(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var header = message.Headers["Disposition-Notification-To"];
        if (string.IsNullOrWhiteSpace(header)) return [];

        return InternetAddressList.TryParse(header, out var parsed)
            ? [.. parsed.Mailboxes]
            : [];
    }

    /// <summary>
    /// The receipt itself, or null when the message never asked for one. <paramref name="from"/>
    /// is the account the message was displayed in — the receipt's sender and final recipient.
    /// </summary>
    public static MimeMessage? Build(MimeMessage original, MailboxAddress from, DateTimeOffset displayed)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(from);

        var requested = RequestedBy(original);
        if (requested.Count == 0) return null;

        var receipt = new MimeMessage();
        receipt.From.Add(from);
        receipt.To.AddRange(requested);
        receipt.Subject = $"Read: {original.Subject ?? string.Empty}".TrimEnd();

        var sentence = new TextPart("plain")
        {
            Text = $"This is a read receipt for the message you sent to {from.Address}.\n"
                   + $"It was displayed on {displayed.ToLocalTime():f}.\n\n"
                   + "This says the message was shown, not that it was read or acted on.",
        };

        var disposition = new MessageDispositionNotification();
        disposition.Fields.Add("Reporting-UA", "Mailbox");
        disposition.Fields.Add("Final-Recipient", $"rfc822;{from.Address}");
        if (!string.IsNullOrWhiteSpace(original.MessageId))
        {
            disposition.Fields.Add("Original-Message-ID", $"<{original.MessageId}>");
        }

        disposition.Fields.Add("Disposition", "manual-action/MDN-sent-manually; displayed");

        receipt.Body = new MultipartReport("disposition-notification") { sentence, disposition };
        return receipt;
    }
}
