namespace Mailbox.Pst.Messaging;

/// <summary>
/// A message as MAPI-descended storage keeps one — the surface a PST message and a .msg file
/// share, which is what lets one MIME assembly and one PIM mapping serve both. The two formats
/// differ in where bytes live, never in what a message is.
/// </summary>
public interface IStoredMessage
{
    string MessageClass { get; }

    string Subject { get; }

    string TransportHeaders { get; }

    string SenderName { get; }

    string SenderAddress { get; }

    string InternetMessageId { get; }

    DateTimeOffset? Delivered { get; }

    DateTimeOffset? Submitted { get; }

    string BodyText { get; }

    byte[] HtmlBody { get; }

    bool IsRead { get; }

    bool IsFlagged { get; }

    PstProperty? Property(ushort id);

    PstProperty? Named(PstNamedProperties names, Guid set, uint numericId);

    IEnumerable<PstRecipient> Recipients();

    IEnumerable<IStoredAttachment> Attachments();
}

/// <summary>An attachment as both formats carry one: a payload, its naming, or a whole message inside.</summary>
public interface IStoredAttachment
{
    string FileName { get; }

    string MimeType { get; }

    string ContentId { get; }

    /// <summary>The bytes, when carried by value; empty for references and embedded messages.</summary>
    byte[] Content { get; }

    IStoredMessage? EmbeddedMessage { get; }
}
