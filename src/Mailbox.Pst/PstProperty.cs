using System.Buffers.Binary;
using System.Text;

namespace Mailbox.Pst;

/// <summary>The property types a PST stores, as [MS-OXCDATA] §2.11.1 numbers them.</summary>
public enum PstPropertyType : ushort
{
    Integer16 = 0x0002,
    Integer32 = 0x0003,
    Floating32 = 0x0004,
    Floating64 = 0x0005,
    Currency = 0x0006,
    FloatingTime = 0x0007,
    ErrorCode = 0x000A,
    Boolean = 0x000B,
    Object = 0x000D,
    Integer64 = 0x0014,
    String8 = 0x001E,
    String = 0x001F,
    Time = 0x0040,
    Guid = 0x0048,
    ServerId = 0x00FB,
    Restriction = 0x00FD,
    RuleAction = 0x00FE,
    Binary = 0x0102,
}

/// <summary>
/// One property as a PST stores it: id, type, and the raw bytes, with the readings a consumer
/// wants layered on top rather than baked in.
/// </summary>
/// <remarks>
/// The raw bytes are kept and the typed readings are computed, because the same bytes answer
/// different questions — a time is also its integer, a string is also its bytes — and because a
/// file that lies about a value's width should shortchange one property, not fail the object it
/// sits on. Readings are forgiving of short values (a missing byte reads as zero) but never of
/// type confusion: asking a binary for its integer is a caller's bug and throws.
/// <para>
/// <c>String8</c> values decode as Latin-1 here. Their real encoding is the writing program's
/// own code page, which is a per-message fact the messaging layer resolves; this layer does not
/// guess beyond the bytes.
/// </para>
/// </remarks>
public sealed record PstProperty(ushort Id, PstPropertyType Type, byte[] Raw)
{
    /// <summary>The multi-valued flag: any type ORed with it is an array of that type.</summary>
    public const ushort MultiValuedFlag = 0x1000;

    public bool IsMultiValued => (((ushort)Type) & MultiValuedFlag) != 0;

    /// <summary>The element type with the multi-valued flag stripped.</summary>
    public PstPropertyType BaseType => (PstPropertyType)(((ushort)Type) & ~MultiValuedFlag);

    /// <summary>How many bytes one value of a fixed-size type occupies, or null for the variable-size types.</summary>
    public static int? FixedSize(PstPropertyType type) => type switch
    {
        PstPropertyType.Integer16 => 2,
        PstPropertyType.Integer32 or PstPropertyType.Floating32 or PstPropertyType.ErrorCode => 4,
        PstPropertyType.Floating64 or PstPropertyType.Currency or PstPropertyType.FloatingTime
            or PstPropertyType.Integer64 or PstPropertyType.Time => 8,
        PstPropertyType.Boolean => 1,
        PstPropertyType.Guid => 16,
        _ => null,
    };

    public int AsInteger32() => Type is PstPropertyType.Integer32 or PstPropertyType.ErrorCode
        ? (int)ReadUnsigned(4)
        : throw Wrong("a 32-bit integer");

    public long AsInteger64() => Type switch
    {
        PstPropertyType.Integer64 or PstPropertyType.Currency => (long)ReadUnsigned(8),
        PstPropertyType.Integer32 or PstPropertyType.ErrorCode => (int)ReadUnsigned(4),
        PstPropertyType.Integer16 => (short)ReadUnsigned(2),
        _ => throw Wrong("an integer"),
    };

    public bool AsBoolean() => Type == PstPropertyType.Boolean ? ReadUnsigned(1) != 0 : throw Wrong("a boolean");

    /// <summary>
    /// A FILETIME read as a moment, in UTC. Zero — how "no date" travels — reads as null, and so
    /// does a value outside what a calendar can hold, some writers using a saturated FILETIME as
    /// their own "never".
    /// </summary>
    public DateTimeOffset? AsTime()
    {
        if (Type != PstPropertyType.Time) throw Wrong("a time");
        var ticks = (long)ReadUnsigned(8);
        if (ticks <= 0) return null;
        try
        {
            return DateTimeOffset.FromFileTime(ticks).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public string AsString() => Type switch
    {
        PstPropertyType.String => Encoding.Unicode.GetString(Raw),
        PstPropertyType.String8 => Encoding.Latin1.GetString(Raw),
        _ => throw Wrong("a string"),
    };

    public byte[] AsBinary() => Type is PstPropertyType.Binary or PstPropertyType.Object ? Raw : throw Wrong("binary data");

    public Guid AsGuid() => Type == PstPropertyType.Guid && Raw.Length == 16 ? new Guid(Raw) : throw Wrong("a GUID");

    /// <summary>Every element of a multi-valued property, each wearing the base type.</summary>
    public IReadOnlyList<PstProperty> Elements()
    {
        if (!IsMultiValued) throw Wrong("a multi-valued property");

        var one = FixedSize(BaseType);
        var elements = new List<PstProperty>();

        if (one is { } size)
        {
            // Fixed-size elements are the bytes divided evenly ([MS-PST] §2.3.3.4.1).
            for (var at = 0; at + size <= Raw.Length; at += size)
                elements.Add(new PstProperty(Id, BaseType, Raw.AsSpan(at, size).ToArray()));
            return elements;
        }

        // Variable-size elements are delimited by a count and an offset table ([MS-PST] §2.3.3.4.2).
        if (Raw.Length < 4) return elements;
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(Raw);
        if (count < 0 || 4 + (long)count * 4 > Raw.Length) return elements;

        for (var i = 0; i < count; i++)
        {
            var start = BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(4 + i * 4));
            var end = i + 1 < count ? BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(4 + (i + 1) * 4)) : (uint)Raw.Length;
            if (start > end || end > Raw.Length) break;
            elements.Add(new PstProperty(Id, BaseType, Raw.AsSpan((int)start, (int)(end - start)).ToArray()));
        }

        return elements;
    }

    private ulong ReadUnsigned(int width)
    {
        var value = 0UL;
        for (var i = 0; i < width && i < Raw.Length; i++)
            value |= (ulong)Raw[i] << (8 * i);
        return value;
    }

    private PstException Wrong(string wanted) =>
        new($"Property 0x{Id:X4} is a {Type} and was read as {wanted}: the reader's expectation is wrong, not the file.");
}
