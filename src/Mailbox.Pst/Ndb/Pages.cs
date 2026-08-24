using System.Buffers.Binary;

namespace Mailbox.Pst.Ndb;

/// <summary>What a page trailer says its page is ([MS-PST] §2.2.2.7.1).</summary>
internal enum PageType : byte
{
    BlockBTree = 0x80,
    NodeBTree = 0x81,
    FreeMap = 0x82,
    PageMap = 0x83,
    AllocationMap = 0x84,
    FreePageMap = 0x85,
    DensityList = 0x86,
}

/// <summary>An intermediate BTree entry ([MS-PST] §2.2.2.7.7.2): the smallest key under a child page, and where that page is.</summary>
internal readonly record struct BtEntry(ulong Key, Bref Child);

/// <summary>A leaf node entry ([MS-PST] §2.2.2.7.7.4): where a node's data and subnode trees begin.</summary>
public readonly record struct NbtEntry(Nid Nid, Bid Data, Bid Subnode, Nid Parent);

/// <summary>A leaf block entry ([MS-PST] §2.2.2.7.7.3): where a block is, and how many raw bytes it holds.</summary>
public readonly record struct BbtEntry(Bref Bref, ushort Length, ushort ReferenceCount);

/// <summary>
/// One 512-byte BTree page ([MS-PST] §2.2.2.7.7.1), verified and pulled apart.
/// </summary>
/// <remarks>
/// Three verifications stand between the disk and the caller, because a BTree page is reached by
/// trusting an offset some other page stated: the trailer's type must be the type the descent
/// expected, its signature must fold the offset and block id this page was addressed by, and its
/// CRC must cover the page's content. Entries are then read at the stride the page itself states
/// (<c>cbEnt</c>) — §2.2.2.7.7.1 is explicit that the stride can exceed the entry's natural size,
/// and the specification's own leaf-NBT example writes 28-byte entries on a 32-byte stride.
/// </remarks>
internal sealed class BTreePage
{
    public const int Size = 512;

    public required PageType Type { get; init; }

    /// <summary>Zero at the leaves, counting up toward the root.</summary>
    public required byte Level { get; init; }

    public required byte EntryCount { get; init; }

    public required byte EntryStride { get; init; }

    public required byte[] Entries { get; init; }

    public static BTreePage Read(ReadOnlySpan<byte> page, Bref source, PstFormat format)
    {
        if (page.Length != Size)
            throw new PstException($"A BTree page must be {Size} bytes and {page.Length} arrived for the one at 0x{source.Ib:X}.");

        // Unicode: 488 bytes of entries, 4 of metadata, 4 of padding, a 16-byte trailer.
        // ANSI: 496 bytes of entries, 4 of metadata, a 12-byte trailer.
        var entryRegion = format == PstFormat.Unicode ? 488 : 496;
        var meta = page.Slice(entryRegion, 4);
        var trailer = PageTrailer.Read(page, format);

        if (trailer.Type is not (PageType.NodeBTree or PageType.BlockBTree))
            throw new PstException($"The page at 0x{source.Ib:X} is a 0x{(byte)trailer.Type:X2} page where a BTree page was expected.");

        // The page CRC covers everything before the trailer — the entries, the metadata, and on
        // the Unicode layout the padding word between them.
        trailer.Verify(page[..^(format == PstFormat.Unicode ? 16 : 12)], source, format);

        var count = meta[0];
        var stride = meta[2];
        if (stride == 0 || count * stride > entryRegion)
            throw new PstException(
                $"The BTree page at 0x{source.Ib:X} claims {count} entries of {stride} bytes, which do not fit in a page.");

        return new BTreePage
        {
            Type = trailer.Type,
            Level = meta[3],
            EntryCount = count,
            EntryStride = stride,
            Entries = page[..(count * stride)].ToArray(),
        };
    }

    private ReadOnlySpan<byte> Entry(int index) => Entries.AsSpan(index * EntryStride, EntryStride);

    public BtEntry Intermediate(int index, PstFormat format)
    {
        var entry = Entry(index);
        return format == PstFormat.Unicode
            ? new BtEntry(BinaryPrimitives.ReadUInt64LittleEndian(entry), PstHeader.ReadBref(entry[8..], format))
            : new BtEntry(BinaryPrimitives.ReadUInt32LittleEndian(entry), PstHeader.ReadBref(entry[4..], format));
    }

    public NbtEntry Node(int index, PstFormat format)
    {
        var entry = Entry(index);
        return format == PstFormat.Unicode
            ? new NbtEntry(
                new Nid((uint)BinaryPrimitives.ReadUInt64LittleEndian(entry)),
                new Bid(BinaryPrimitives.ReadUInt64LittleEndian(entry[8..])),
                new Bid(BinaryPrimitives.ReadUInt64LittleEndian(entry[16..])),
                new Nid(BinaryPrimitives.ReadUInt32LittleEndian(entry[24..])))
            : new NbtEntry(
                new Nid(BinaryPrimitives.ReadUInt32LittleEndian(entry)),
                new Bid(BinaryPrimitives.ReadUInt32LittleEndian(entry[4..])),
                new Bid(BinaryPrimitives.ReadUInt32LittleEndian(entry[8..])),
                new Nid(BinaryPrimitives.ReadUInt32LittleEndian(entry[12..])));
    }

    public BbtEntry Block(int index, PstFormat format)
    {
        var entry = Entry(index);
        var brefSize = format == PstFormat.Unicode ? 16 : 8;
        return new BbtEntry(
            PstHeader.ReadBref(entry, format),
            BinaryPrimitives.ReadUInt16LittleEndian(entry[brefSize..]),
            BinaryPrimitives.ReadUInt16LittleEndian(entry[(brefSize + 2)..]));
    }
}

/// <summary>The sixteen (or twelve) bytes that vouch for a page ([MS-PST] §2.2.2.7.1).</summary>
internal readonly record struct PageTrailer(PageType Type, ushort Signature, uint Crc, Bid Bid)
{
    public static PageTrailer Read(ReadOnlySpan<byte> page, PstFormat format)
    {
        var trailer = page[(format == PstFormat.Unicode ? 496 : 500)..];
        if (trailer[0] != trailer[1])
            throw new PstException($"A page trailer repeats its type byte and this one does not (0x{trailer[0]:X2} then 0x{trailer[1]:X2}): the page is damaged.");

        // The CRC and the block id swap places between the two layouts.
        return format == PstFormat.Unicode
            ? new PageTrailer((PageType)trailer[0], BinaryPrimitives.ReadUInt16LittleEndian(trailer[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]), new Bid(BinaryPrimitives.ReadUInt64LittleEndian(trailer[8..])))
            : new PageTrailer((PageType)trailer[0], BinaryPrimitives.ReadUInt16LittleEndian(trailer[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]), new Bid(BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..])));
    }

    /// <summary>Checks the CRC over the page's content and the signature over where the page sits.</summary>
    public void Verify(ReadOnlySpan<byte> content, Bref source, PstFormat format)
    {
        var computed = PstCrc.Compute(content);
        if (computed != Crc)
            throw new PstException($"The page at 0x{source.Ib:X} carries CRC 0x{Crc:X8} but its bytes come to 0x{computed:X8}: the page is damaged.");

        var signature = PstCrc.BlockSignature(source.Ib, Bid.Value);
        if (signature != Signature)
            throw new PstException(
                $"The page at 0x{source.Ib:X} carries signature 0x{Signature:X4} where 0x{signature:X4} belongs: the page is not the one that was written here.");

        if (source.Bid.Value != 0 && Bid.Value != source.Bid.Value)
            throw new PstException(
                $"The page at 0x{source.Ib:X} says it is block {Bid} but was reached as {source.Bid}: a BTree reference is stale.");
    }
}
