using System.Buffers.Binary;

namespace Mailbox.Pst.Ndb;

/// <summary>The file state a reader needs out of ROOT ([MS-PST] §2.2.2.5): where the two BTrees start, and how big the file claims to be.</summary>
public sealed record PstRoot(ulong FileEof, Bref NodeBTree, Bref BlockBTree);

/// <summary>
/// The header at offset zero ([MS-PST] §2.2.2.6): which of the two layouts the file uses, how its
/// data blocks are encoded, and the ROOT that locates everything else.
/// </summary>
/// <remarks>
/// The two layouts differ in field widths <em>and</em> in field order — ANSI keeps its next-block
/// counter where Unicode keeps eight bytes of padding — so this is two parses that share their
/// first sixteen bytes, not one parse with two widths. Both CRCs are verified before anything is
/// believed: a header is the one structure with no trailer to vouch for it, and every offset in
/// the file descends from what it says.
/// </remarks>
public sealed record PstHeader
{
    public required PstFormat Format { get; init; }

    public required PstCryptMethod CryptMethod { get; init; }

    public required PstRoot Root { get; init; }

    /// <summary>wVer as written. 14 and 15 are the ANSI layout, 23 the Unicode one; 36 and up are the 4K-page variant this reader refuses by name.</summary>
    public required ushort Version { get; init; }

    internal const int UnicodeLength = 564;
    internal const int AnsiLength = 512;

    /// <summary>
    /// Parses and verifies a header. The span must hold at least <see cref="UnicodeLength"/>
    /// bytes for a Unicode file, <see cref="AnsiLength"/> for an ANSI one.
    /// </summary>
    /// <exception cref="PstException">The bytes are not a PST header, or fail their own CRCs.</exception>
    public static PstHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < AnsiLength)
            throw new PstException("The file is shorter than the smallest possible header, so it is not a PST file.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x4E444221)
            throw new PstException("The file does not start with the PST magic number, so it is not a PST file.");

        if (BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != 0x4D53)
            throw new PstException("The header's client magic is wrong: the file is damaged, or something else wearing a PST extension.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        var format = version switch
        {
            14 or 15 => PstFormat.Ansi,
            23 => PstFormat.Unicode,
            >= 36 => throw new PstException(
                $"The file uses the 4K-page layout (version {version}), which is written by cached-mode OST "
                + "files rather than PST exports. Reading it is not supported."),
            _ => throw new PstException($"The header names format version {version}, which [MS-PST] does not define."),
        };

        // dwCRCPartial covers the 471 bytes from wMagicClient in both layouts; the Unicode layout
        // adds dwCRCFull over the 516 bytes reaching through its relocated bidNextB.
        var crcPartial = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var computedPartial = PstCrc.Compute(header.Slice(8, 471));
        if (crcPartial != computedPartial)
            throw new PstException(
                $"The header's partial CRC is 0x{crcPartial:X8} but its bytes come to 0x{computedPartial:X8}: the header is damaged.");

        if (format == PstFormat.Unicode)
        {
            if (header.Length < UnicodeLength)
                throw new PstException("The file ends inside its own header.");

            var crcFull = BinaryPrimitives.ReadUInt32LittleEndian(header[0x20C..]);
            var computedFull = PstCrc.Compute(header.Slice(8, 516));
            if (crcFull != computedFull)
                throw new PstException(
                    $"The header's full CRC is 0x{crcFull:X8} but its bytes come to 0x{computedFull:X8}: the header is damaged.");
        }

        var cryptByte = format == PstFormat.Unicode ? header[0x201] : header[0x1CD];
        var crypt = cryptByte switch
        {
            0x00 => PstCryptMethod.None,
            0x01 => PstCryptMethod.Permute,
            0x02 => PstCryptMethod.Cyclic,
            0x10 => throw new PstException(
                "The file is encrypted with Windows Information Protection, which only the machine it "
                + "belongs to can decrypt. Export an unprotected copy and import that."),
            _ => throw new PstException($"The header names encoding method 0x{cryptByte:X2}, which [MS-PST] does not define."),
        };

        var root = format == PstFormat.Unicode ? ReadUnicodeRoot(header[0xB4..]) : ReadAnsiRoot(header[0xA4..]);

        return new PstHeader { Format = format, CryptMethod = crypt, Root = root, Version = version };
    }

    private static PstRoot ReadUnicodeRoot(ReadOnlySpan<byte> root) => new(
        FileEof: BinaryPrimitives.ReadUInt64LittleEndian(root[0x04..]),
        NodeBTree: ReadBref(root[0x24..], PstFormat.Unicode),
        BlockBTree: ReadBref(root[0x34..], PstFormat.Unicode));

    private static PstRoot ReadAnsiRoot(ReadOnlySpan<byte> root) => new(
        FileEof: BinaryPrimitives.ReadUInt32LittleEndian(root[0x04..]),
        NodeBTree: ReadBref(root[0x14..], PstFormat.Ansi),
        BlockBTree: ReadBref(root[0x1C..], PstFormat.Ansi));

    internal static Bref ReadBref(ReadOnlySpan<byte> bytes, PstFormat format) => format == PstFormat.Unicode
        ? new Bref(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(bytes)), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]))
        : new Bref(new Bid(BinaryPrimitives.ReadUInt32LittleEndian(bytes)), BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]));
}
