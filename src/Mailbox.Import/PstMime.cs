using System.Text;
using Mailbox.Pst;
using Mailbox.Pst.Messaging;
using MimeKit;
using MimeKit.Utils;

namespace Mailbox.Import;

/// <summary>
/// Turns a PST message back into MIME. Received internet mail still carries its original header
/// section, which is used verbatim; everything else — bodies, attachments, the messages nested
/// inside attachments — is rebuilt from the properties, because the original MIME body was not
/// kept and pretending otherwise would be invention.
/// </summary>
internal static class PstMime
{
    public static MimeMessage Assemble(IStoredMessage source, string? syntheticMessageId = null)
    {
        var message = new MimeMessage();
        message.Headers.Remove(HeaderId.Date);
        message.Headers.Remove(HeaderId.MessageId);

        Address(message.From, source.SenderName, source.SenderAddress);
        foreach (var recipient in source.Recipients())
        {
            var line = recipient.Type switch
            {
                PstRecipient.Cc => message.Cc,
                PstRecipient.Bcc => message.Bcc,
                _ => message.To,
            };
            Address(line, recipient.Name, recipient.Address);
        }

        message.Subject = source.Subject;
        if ((source.Delivered ?? source.Submitted) is { } date) message.Date = date;

        var builder = new BodyBuilder();
        if (source.BodyText.Length > 0) builder.TextBody = source.BodyText;

        var html = source.HtmlBody;
        if (html.Length > 0) builder.HtmlBody = DecodeHtml(html);

        // The body of last resort: a message whose only body is compressed RTF
        // (PidTagRtfCompressed, 0x1009) gets the HTML the RTF encapsulates, or its text.
        if (builder.TextBody is null && builder.HtmlBody is null
            && source.Property(0x1009) is { Type: PstPropertyType.Binary } rtf && rtf.Raw.Length > 0)
        {
            var (fromHtml, fromText) = RtfBody.FromCompressed(rtf.Raw);
            if (fromHtml is { Length: > 0 }) builder.HtmlBody = fromHtml;
            else if (fromText is { Length: > 0 }) builder.TextBody = fromText;
        }

        foreach (var attachment in source.Attachments())
        {
            if (attachment.EmbeddedMessage is { } inner)
            {
                var part = new MessagePart { Message = Assemble(inner) };
                part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment);
                if (attachment.FileName is { Length: > 0 } name) part.ContentDisposition.FileName = name;
                builder.Attachments.Add(part);
                continue;
            }

            var content = attachment.Content;
            if (content.Length == 0) continue; // a reference to a file on the original machine

            var mime = ContentType.TryParse(attachment.MimeType, out var stated)
                ? stated
                : new ContentType("application", "octet-stream");
            var filePart = new MimePart(mime)
            {
                Content = new MimeContent(new MemoryStream(content)),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = attachment.FileName is { Length: > 0 } file ? file : "attachment",
            };

            // An inline image the HTML refers to by cid rides in the related root; anything
            // else is an ordinary attachment.
            if (attachment.ContentId is { Length: > 0 } cid && builder.HtmlBody is not null)
            {
                filePart.ContentId = cid.Trim('<', '>');
                filePart.ContentDisposition = new ContentDisposition(ContentDisposition.Inline);
                builder.LinkedResources.Add(filePart);
            }
            else
            {
                builder.Attachments.Add(filePart);
            }
        }

        message.Body = builder.ToMessageBody();

        OverlayTransportHeaders(message, source.TransportHeaders);

        // An id from the wire wins; then the stored property; then — for locally posted mail
        // that never had one — a deterministic synthetic id, so a re-run of the same import
        // meets the same id and tops up instead of doubling. Only a message with no id at all
        // gets a random one, and only when the caller offers nothing better.
        if (message.MessageId is not { Length: > 0 })
        {
            var stored = source.InternetMessageId is { Length: > 0 } id
                ? MimeUtils.EnumerateReferences(id).FirstOrDefault()
                : null;
            message.MessageId = stored ?? syntheticMessageId ?? MimeUtils.GenerateMessageId();
        }

        return message;
    }

    private static void Address(InternetAddressList line, string name, string address)
    {
        // Nothing is invented and nothing invalid is written. A parseable address becomes an
        // ordinary mailbox; a name with no usable address — locally posted mail, or a sender
        // known only by an X.500 path — becomes a group with no members, the one header shape
        // that carries a bare display name and stays valid mail (RFC 6854 allows it even in
        // From). The transport headers overwrite all of this for mail that came off the wire.
        if (MailboxAddress.TryParse(address, out var mailbox))
        {
            if (name.Length > 0) mailbox.Name = name;
            line.Add(mailbox);
        }
        else if (name.Length > 0 || address.Length > 0)
        {
            line.Add(new GroupAddress(name.Length > 0 ? name : address));
        }
    }

    private static string DecodeHtml(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: read it byte-for-byte. The document's own charset declaration, if it
            // has one, still names the truth for anything that renders it.
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Lays the stored header section over the assembled message — the original Received chain,
    /// addressing and ids beat anything rebuilt from properties. The structural headers stay
    /// ours: the stored Content-Type describes a body this file did not keep.
    /// </summary>
    private static void OverlayTransportHeaders(MimeMessage message, string headers)
    {
        if (headers.Length == 0) return;

        HeaderList stored;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(headers));
            stored = HeaderList.Load(stream);
        }
        catch (Exception)
        {
            return; // damaged header text: the rebuilt headers stand
        }

        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in stored)
        {
            if (header.Field.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            if (header.Id == HeaderId.MimeVersion) continue;

            if (replaced.Add(header.Field))
                message.Headers.RemoveAll(header.Field);
            message.Headers.Add(header);
        }
    }
}
