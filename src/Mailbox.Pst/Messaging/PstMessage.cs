using System.Buffers.Binary;
using Mailbox.Pst.Ltp;
using Mailbox.Pst.Ndb;

namespace Mailbox.Pst.Messaging;

/// <summary>Who a message names, on which line ([MS-PST] §2.4.5.3's table, one row each).</summary>
public sealed record PstRecipient(int Type, string Name, string Address, string AddressType)
{
    public const int To = 1;
    public const int Cc = 2;
    public const int Bcc = 3;
}

/// <summary>
/// A message ([MS-PST] §2.4.5): its property context, and — inside its own subnode tree — the
/// recipient table at local NID 0x692 and the attachment table at 0x671.
/// </summary>
/// <remarks>
/// An embedded message arrives through the same class: an attachment whose method says
/// "embedded" carries the message as a subnode of the attachment object, and
/// <see cref="PstAttachment.EmbeddedMessage"/> hands it back here, recursion and all. The
/// subject needs one unweaving on the way out ([MS-PST] §2.5.3.1.1.1): a first character of
/// 0x01 means the second states the prefix length and the text proper starts at the third —
/// stored that way so "RE:" sorts with its conversation, and stripped here so no reader ever
/// sees the control characters.
/// </remarks>
public sealed class PstMessage : IStoredMessage
{
    IEnumerable<IStoredAttachment> IStoredMessage.Attachments() => Attachments();

    private readonly PstNode _node;
    private readonly PropertyContext _properties;

    public Nid Nid => _node.Nid;

    internal static PstMessage Open(PstNode node, System.Text.Encoding? inheritedString8 = null) => new(node, inheritedString8);

    private PstMessage(PstNode node, System.Text.Encoding? inheritedString8)
    {
        _node = node;
        _properties = PropertyContext.Read(node, inheritedString8);
    }

    /// <summary>Any property by id — the layer above decides what the rest mean.</summary>
    public PstProperty? Property(ushort id) => _properties.Find(id);

    /// <summary>A named property, through this file's own map — null when the file never stored the name or the value.</summary>
    public PstProperty? Named(PstNamedProperties names, Guid set, uint numericId) =>
        names.IdOf(set, numericId) is { } id ? _properties.Find(id) : null;

    public IReadOnlyDictionary<ushort, PstProperty> Properties => _properties.Properties;

    public string MessageClass => _properties.Find(Pid.MessageClass)?.AsString() ?? string.Empty;

    /// <summary>The whole subject as a reader knows it, prefix and all, with the stored metadata removed.</summary>
    public string Subject
    {
        get
        {
            var raw = _properties.Find(Pid.Subject)?.AsString() ?? string.Empty;
            return raw.Length >= 2 && raw[0] == '\x01' ? raw[2..] : raw;
        }
    }

    public string TransportHeaders => _properties.Find(Pid.TransportHeaders)?.AsString() ?? string.Empty;

    public string SenderName => _properties.Find(Pid.SenderName)?.AsString() ?? string.Empty;

    public string SenderAddress
    {
        get
        {
            // The SMTP form wins when the writer recorded one; the plain address is an X.500
            // path for mail that lived on an Exchange server, still worth keeping when it is
            // all there is.
            var smtp = _properties.Find(Pid.SenderSmtpAddress)?.AsString();
            return smtp is { Length: > 0 } ? smtp : _properties.Find(Pid.SenderEmailAddress)?.AsString() ?? string.Empty;
        }
    }

    public string InternetMessageId => _properties.Find(Pid.InternetMessageId)?.AsString() ?? string.Empty;

    public DateTimeOffset? Delivered => _properties.Find(Pid.MessageDeliveryTime)?.AsTime();

    public DateTimeOffset? Submitted => _properties.Find(Pid.ClientSubmitTime)?.AsTime();

    public string BodyText => _properties.Find(Pid.Body)?.AsString() ?? string.Empty;

    /// <summary>The HTML body's bytes as stored — their character set is the document's own affair.</summary>
    public byte[] HtmlBody => _properties.Find(Pid.Html) is { } html
        ? html.Type is PstPropertyType.Binary or PstPropertyType.Object ? html.AsBinary()
            : System.Text.Encoding.UTF8.GetBytes(html.AsString())
        : [];

    private int Flags => _properties.Find(Pid.MessageFlags)?.AsInteger32() ?? 0;

    public bool IsRead => (Flags & 0x1) != 0;

    public bool IsUnsent => (Flags & 0x8) != 0;

    /// <summary>The follow-up flag: PidTagFlagStatus is 2 when the flag stands, 1 when it is complete.</summary>
    public bool IsFlagged => (_properties.Find(Pid.FlagStatus)?.AsInteger32() ?? 0) == 2;

    public bool HasAttachments => (Flags & 0x10) != 0;

    private static readonly Nid AttachmentTableNid = new(0x671);
    private static readonly Nid RecipientTableNid = new(0x692);

    public IEnumerable<PstRecipient> Recipients()
    {
        var table = _node.Subnode(RecipientTableNid);
        if (table is null) yield break;

        foreach (var row in TableContext.Read(table, _properties.String8).Rows())
        {
            // The SMTP address wins over the address-book one when both are present: the
            // importer is bound for internet mail, and an X.500 path helps nobody there.
            var smtp = row.Property(Pid.SmtpAddress)?.AsString();
            yield return new PstRecipient(
                row.Property(Pid.RecipientType)?.AsInteger32() ?? PstRecipient.To,
                row.Property(Pid.DisplayName)?.AsString() ?? string.Empty,
                smtp is { Length: > 0 } ? smtp : row.Property(Pid.EmailAddress)?.AsString() ?? string.Empty,
                row.Property(Pid.AddressType)?.AsString() ?? string.Empty);
        }
    }

    public IEnumerable<PstAttachment> Attachments()
    {
        var table = _node.Subnode(AttachmentTableNid);
        if (table is null) yield break;

        foreach (var row in TableContext.Read(table).Rows())
        {
            var nid = new Nid(row.RowId);
            if (nid.Type != NidType.Attachment) continue;
            if (_node.Subnode(nid) is { } attachment)
                yield return new PstAttachment(attachment, _properties.String8);
        }
    }
}

/// <summary>
/// An attachment ([MS-PST] §2.4.6): a property context living as a subnode of its message, with
/// the payload behind one more indirection.
/// </summary>
public sealed class PstAttachment : IStoredAttachment
{
    IStoredMessage? IStoredAttachment.EmbeddedMessage => EmbeddedMessage;

    private readonly PstNode _node;
    private readonly PropertyContext _properties;

    private readonly System.Text.Encoding? _string8;

    internal PstAttachment(PstNode node, System.Text.Encoding? inheritedString8 = null)
    {
        _node = node;
        _properties = PropertyContext.Read(node, inheritedString8);
        _string8 = _properties.String8;
    }

    public PstProperty? Property(ushort id) => _properties.Find(id);

    /// <summary>How the payload is carried ([MS-OXCMSG]'s values): 1 is bytes in the file, 5 an embedded message, the rest references.</summary>
    public int Method => _properties.Find(Pid.AttachMethod)?.AsInteger32() ?? 0;

    public bool IsEmbeddedMessage => Method == 5;

    public string FileName
    {
        get
        {
            var name = _properties.Find(Pid.AttachLongFilename)?.AsString();
            return name is { Length: > 0 } ? name : _properties.Find(Pid.AttachFilename)?.AsString() ?? string.Empty;
        }
    }

    public string MimeType => _properties.Find(Pid.AttachMimeTag)?.AsString() ?? string.Empty;

    public string ContentId => _properties.Find(Pid.AttachContentId)?.AsString() ?? string.Empty;

    /// <summary>The attachment's bytes, when it is carried by value; empty for references and embedded messages.</summary>
    public byte[] Content
    {
        get
        {
            var data = _properties.Find(Pid.AttachData);
            return data is { Type: PstPropertyType.Binary } ? data.AsBinary() : [];
        }
    }

    /// <summary>
    /// The message inside, when there is one: the data property turns from bytes into a
    /// PtypObject naming a subnode of this attachment, and that subnode is a whole message —
    /// its own properties, recipients and attachments, however deep the nesting goes.
    /// </summary>
    public PstMessage? EmbeddedMessage
    {
        get
        {
            var data = _properties.Find(Pid.AttachData);
            if (data is not { Type: PstPropertyType.Object } || data.Raw.Length < 4) return null;

            var nid = new Nid(BinaryPrimitives.ReadUInt32LittleEndian(data.Raw));
            return _node.Subnode(nid) is { } inner ? PstMessage.Open(inner, _string8) : null;
        }
    }
}
