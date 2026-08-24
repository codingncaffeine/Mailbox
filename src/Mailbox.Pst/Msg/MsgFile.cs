using System.Buffers.Binary;
using System.Text;
using Mailbox.Pst.Cfb;
using Mailbox.Pst.Messaging;

namespace Mailbox.Pst.Msg;

/// <summary>
/// A .msg file ([MS-OXMSG]): one message, saved as a compound file whose stream names carry the
/// property tags. Everything read here comes out wearing the same types the PST reader hands
/// over, so one MIME assembly and one PIM mapping serve both formats.
/// </summary>
public sealed class MsgFile
{
    public MsgMessage Message { get; }

    /// <summary>The file's named-property map — one per file, at the top level, embedded messages included.</summary>
    public PstNamedProperties Names { get; }

    private MsgFile(MsgMessage message, PstNamedProperties names)
    {
        Message = message;
        Names = names;
    }

    public static MsgFile Open(string path) => FromBytes(File.ReadAllBytes(path));

    public static MsgFile FromBytes(byte[] bytes)
    {
        var file = CompoundFile.Parse(bytes);
        var names = PstNamedProperties.Empty;

        if (file.Find(file.Root, "__nameid_version1.0") is { } nameid)
        {
            names = PstNamedProperties.FromMsgStreams(
                Stream(file, nameid, "__substg1.0_00030102"),
                Stream(file, nameid, "__substg1.0_00020102"),
                Stream(file, nameid, "__substg1.0_00040102"));
        }

        return new MsgFile(new MsgMessage(file, file.Root, MsgPropertySet.TopLevelHeader), names);

        static byte[] Stream(CompoundFile file, CfbEntry parent, string name) =>
            file.Find(parent, name) is { } entry ? file.ReadStream(entry) : [];
    }
}

/// <summary>
/// One storage's properties ([MS-OXMSG] §2.4): the fixed-length values inline in the property
/// stream, everything else in a stream named after its tag.
/// </summary>
internal sealed class MsgPropertySet
{
    public const int TopLevelHeader = 32;
    public const int EmbeddedHeader = 24;
    public const int RowHeader = 8;

    private readonly Dictionary<ushort, PstProperty> _properties = [];

    public PstProperty? Find(ushort id) => _properties.GetValueOrDefault(id);

    /// <summary>The String8 encoding this set's values decode by — its own code page, or its message's.</summary>
    public Encoding? String8 { get; }

    public MsgPropertySet(CompoundFile file, CfbEntry storage, int headerSize, Encoding? inheritedString8 = null)
    {
        String8 = inheritedString8;
        if (file.Find(storage, "__properties_version1.0") is not { } propertyStream) return;

        var entries = file.ReadStream(propertyStream);
        var substreams = file.Children(storage)
            .Where(child => child.Type == CfbEntry.Stream && child.Name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(child => child.Name[12..], child => child, StringComparer.OrdinalIgnoreCase);

        for (var at = headerSize; at + 16 <= entries.Length; at += 16)
        {
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(at));
            var id = (ushort)(tag >> 16);
            var type = (PstPropertyType)(ushort)tag;
            var slot = entries.AsSpan(at + 8, 8);

            if (PstProperty.FixedSize(type) is { } size && (((ushort)type) & PstProperty.MultiValuedFlag) == 0)
            {
                _properties[id] = new PstProperty(id, type, slot[..size].ToArray());
                continue;
            }

            // A variable, GUID, or multi-valued value lives in its own stream, named by the tag.
            _properties[id] = new PstProperty(id, type, Resolve(file, substreams, tag, type));
        }

        // The code page is one of the properties just read; String8 values are stamped after.
        String8 = PstCodePage.Resolve(
            _properties.GetValueOrDefault((ushort)0x3FFD) is { Type: PstPropertyType.Integer32 } codepage
                ? codepage.AsInteger32()
                : null) ?? inheritedString8;

        if (String8 is not null)
        {
            foreach (var id in _properties.Keys.ToList())
            {
                if (_properties[id].BaseType == PstPropertyType.String8)
                    _properties[id] = _properties[id] with { String8Encoding = String8 };
            }
        }
    }

    private static byte[] Resolve(CompoundFile file, Dictionary<string, CfbEntry> substreams, uint tag, PstPropertyType type)
    {
        var isMultiValued = (((ushort)type) & PstProperty.MultiValuedFlag) != 0;
        var baseType = (PstPropertyType)(((ushort)type) & ~PstProperty.MultiValuedFlag);

        if (!isMultiValued)
        {
            // A PtypObject's "value" is a substorage, which the attachment layer resolves.
            if (type == PstPropertyType.Object) return [];
            return Trimmed(Value(file, substreams, $"{tag:X8}"), baseType);
        }

        if (PstProperty.FixedSize(baseType) is not null)
        {
            // Fixed-size elements are one stream, already the contiguous array PstProperty reads.
            return Value(file, substreams, $"{tag:X8}");
        }

        // Variable-size elements are N value streams behind a length stream; they are repacked
        // into the PST's own multi-value layout so one Elements() serves both formats.
        var lengths = Value(file, substreams, $"{tag:X8}");
        var entrySize = baseType == PstPropertyType.Binary ? 8 : 4;
        var count = lengths.Length / entrySize;
        var values = new byte[count][];
        for (var i = 0; i < count; i++)
            values[i] = Trimmed(Value(file, substreams, $"{tag:X8}-{i:X8}"), baseType);

        var total = 4 + count * 4 + values.Sum(value => value.Length);
        var packed = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(packed, (uint)count);
        var offset = 4 + count * 4;
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(4 + i * 4), (uint)offset);
            values[i].CopyTo(packed.AsSpan(offset));
            offset += values[i].Length;
        }

        return packed;
    }

    private static byte[] Value(CompoundFile file, Dictionary<string, CfbEntry> substreams, string suffix) =>
        substreams.TryGetValue(suffix, out var entry) ? file.ReadStream(entry) : [];

    /// <summary>
    /// A string stream ends at its first null: some writers store fixed-width buffers whose
    /// tail is null fill, and a "padded" address with invisible characters in it fails address
    /// parsing while looking perfectly clean in every log.
    /// </summary>
    private static byte[] Trimmed(byte[] value, PstPropertyType type)
    {
        if (type == PstPropertyType.String)
        {
            for (var i = 0; i + 1 < value.Length; i += 2)
            {
                if (value[i] == 0 && value[i + 1] == 0) return value[..i];
            }

            return value.Length % 2 == 0 ? value : value[..^1];
        }

        if (type == PstPropertyType.String8)
        {
            var terminator = Array.IndexOf(value, (byte)0);
            return terminator >= 0 ? value[..terminator] : value;
        }

        return value;
    }
}

/// <summary>The message inside a .msg — the top level, or an embedded one any depth down.</summary>
public sealed class MsgMessage : IStoredMessage
{
    private readonly CompoundFile _file;
    private readonly CfbEntry _storage;
    private readonly MsgPropertySet _properties;

    internal MsgMessage(CompoundFile file, CfbEntry storage, int headerSize, Encoding? inheritedString8 = null)
    {
        _file = file;
        _storage = storage;
        _properties = new MsgPropertySet(file, storage, headerSize, inheritedString8);
    }

    public PstProperty? Property(ushort id) => _properties.Find(id);

    public PstProperty? Named(PstNamedProperties names, Guid set, uint numericId) =>
        names.IdOf(set, numericId) is { } id ? _properties.Find(id) : null;

    public string MessageClass => _properties.Find(Pid.MessageClass)?.AsString() ?? string.Empty;

    public string Subject
    {
        get
        {
            var raw = _properties.Find(Pid.Subject)?.AsString() ?? string.Empty;
            return raw.Length >= 2 && raw[0] == '\x01' ? raw[2..] : raw;
        }
    }

    public string TransportHeaders => _properties.Find(Pid.TransportHeaders)?.AsString() ?? string.Empty;

    // Some writers pad names and addresses to fixed widths; the spaces are storage, not meaning.
    public string SenderName => (_properties.Find(Pid.SenderName)?.AsString() ?? string.Empty).Trim();

    public string SenderAddress
    {
        get
        {
            var smtp = _properties.Find(Pid.SenderSmtpAddress)?.AsString();
            return (smtp is { Length: > 0 } ? smtp : _properties.Find(Pid.SenderEmailAddress)?.AsString() ?? string.Empty).Trim();
        }
    }

    public string InternetMessageId => _properties.Find(Pid.InternetMessageId)?.AsString() ?? string.Empty;

    public DateTimeOffset? Delivered => _properties.Find(Pid.MessageDeliveryTime)?.AsTime();

    public DateTimeOffset? Submitted => _properties.Find(Pid.ClientSubmitTime)?.AsTime();

    public string BodyText => _properties.Find(Pid.Body)?.AsString() ?? string.Empty;

    public byte[] HtmlBody => _properties.Find(Pid.Html) is { } html
        ? html.Type is PstPropertyType.Binary or PstPropertyType.Object ? html.AsBinary()
            : System.Text.Encoding.UTF8.GetBytes(html.AsString())
        : [];

    private int Flags => _properties.Find(Pid.MessageFlags)?.AsInteger32() ?? 0;

    public bool IsRead => (Flags & 0x1) != 0;

    public bool IsFlagged => (_properties.Find(Pid.FlagStatus)?.AsInteger32() ?? 0) == 2;

    public IEnumerable<PstRecipient> Recipients()
    {
        foreach (var storage in Substorages("__recip_version1.0_#"))
        {
            var row = new MsgPropertySet(_file, storage, MsgPropertySet.RowHeader, _properties.String8);
            var smtp = row.Find(Pid.SmtpAddress)?.AsString();
            yield return new PstRecipient(
                row.Find(Pid.RecipientType)?.AsInteger32() ?? PstRecipient.To,
                (row.Find(Pid.DisplayName)?.AsString() ?? string.Empty).Trim(),
                (smtp is { Length: > 0 } ? smtp : row.Find(Pid.EmailAddress)?.AsString() ?? string.Empty).Trim(),
                (row.Find(Pid.AddressType)?.AsString() ?? string.Empty).Trim());
        }
    }

    IEnumerable<IStoredAttachment> IStoredMessage.Attachments() => Attachments();

    public IEnumerable<MsgAttachment> Attachments()
    {
        foreach (var storage in Substorages("__attach_version1.0_#"))
            yield return new MsgAttachment(_file, storage, _properties.String8);
    }

    private IEnumerable<CfbEntry> Substorages(string prefix) => _file.Children(_storage)
        .Where(child => child.Type == CfbEntry.Storage && child.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>An attachment storage ([MS-OXMSG] §2.2.2): its properties, its bytes, or the message inside.</summary>
public sealed class MsgAttachment : IStoredAttachment
{
    private readonly CompoundFile _file;
    private readonly CfbEntry _storage;
    private readonly MsgPropertySet _properties;

    private readonly Encoding? _string8;

    internal MsgAttachment(CompoundFile file, CfbEntry storage, Encoding? inheritedString8 = null)
    {
        _file = file;
        _storage = storage;
        _properties = new MsgPropertySet(file, storage, MsgPropertySet.RowHeader, inheritedString8);
        _string8 = _properties.String8;
    }

    public PstProperty? Property(ushort id) => _properties.Find(id);

    public int Method => _properties.Find(Pid.AttachMethod)?.AsInteger32() ?? 0;

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

    public byte[] Content => _properties.Find(Pid.AttachData) is { Type: PstPropertyType.Binary } data ? data.AsBinary() : [];

    IStoredMessage? IStoredAttachment.EmbeddedMessage => EmbeddedMessage;

    /// <summary>The message inside: a substorage named for the data tag with the object type, itself a whole message.</summary>
    public MsgMessage? EmbeddedMessage =>
        _file.Find(_storage, "__substg1.0_3701000D") is { Type: CfbEntry.Storage } inner
            ? new MsgMessage(_file, inner, MsgPropertySet.EmbeddedHeader, _string8)
            : null;
}
