using System.Buffers.Binary;

namespace Mailbox.Pst.Ndb;

/// <summary>
/// The trailer every block ends on ([MS-PST] §2.2.2.8.1), aligned to the block's last byte. The
/// 4K layout's is eight bytes longer and carries the uncompressed size, which doubles as the
/// compression flag: a value that differs from the stored length means the data is zlib-deflated.
/// </summary>
internal readonly record struct BlockTrailer(ushort Length, ushort Signature, uint Crc, Bid Bid, ushort UncompressedLength = 0)
{
    public static int Size(PstFormat format) => format == PstFormat.Unicode4K ? 24 : format.IsWide() ? 16 : 12;

    /// <summary>
    /// A block on disk occupies its data rounded up with its trailer to the next boundary — 64
    /// bytes, or 512 in the 4K layout — and never more than <see cref="MaxSize"/>.
    /// </summary>
    public static int StoredSize(int dataLength, PstFormat format)
    {
        var align = format == PstFormat.Unicode4K ? 512 : 64;
        return (dataLength + Size(format) + align - 1) & ~(align - 1);
    }

    public static int MaxSize(PstFormat format) => format == PstFormat.Unicode4K ? 65536 : 8192;

    public static BlockTrailer Read(ReadOnlySpan<byte> trailer, PstFormat format)
    {
        // The CRC and the block id swap places between the two layouts, as they do on pages;
        // the 4K trailer is the wide one plus the uncompressed size at offset 18.
        return format.IsWide()
            ? new BlockTrailer(BinaryPrimitives.ReadUInt16LittleEndian(trailer), BinaryPrimitives.ReadUInt16LittleEndian(trailer[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]), new Bid(BinaryPrimitives.ReadUInt64LittleEndian(trailer[8..])),
                format == PstFormat.Unicode4K ? BinaryPrimitives.ReadUInt16LittleEndian(trailer[18..]) : (ushort)0)
            : new BlockTrailer(BinaryPrimitives.ReadUInt16LittleEndian(trailer), BinaryPrimitives.ReadUInt16LittleEndian(trailer[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]), new Bid(BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..])));
    }
}

/// <summary>A subnode leaf entry ([MS-PST] §2.2.2.8.3.3.1.1): a node that exists only inside its parent node.</summary>
public readonly record struct SlEntry(Nid Nid, Bid Data, Bid Subnode);

/// <summary>
/// The two tree shapes hidden inside internal blocks: data trees that spread one node's bytes
/// over many blocks ([MS-PST] §2.2.2.8.3.2), and subnode BTrees ([MS-PST] §2.2.2.8.3.3).
/// </summary>
internal static class InternalBlocks
{
    /// <summary>
    /// Reads an XBLOCK or XXBLOCK and hands back the block ids it fans out to, with the byte
    /// total the tree claims so the assembled data can be held to it.
    /// </summary>
    public static (byte Level, uint TotalLength, IReadOnlyList<Bid> Children) ReadDataTree(
        ReadOnlySpan<byte> data, Bid bid, PstFormat format)
    {
        if (data.Length < 8)
            throw new PstException($"Block {bid} is an internal block of {data.Length} bytes, too short to say what it is.");

        if (data[0] != 0x01)
            throw new PstException($"Block {bid} was referenced as a data tree but its type byte is 0x{data[0]:X2} where 0x01 belongs.");

        var level = data[1];
        if (level is not (1 or 2))
            throw new PstException($"Block {bid} is a data-tree block of level {level}, and [MS-PST] defines only levels 1 and 2.");

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var total = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var width = format.IsWide() ? 8 : 4;
        if (8 + count * width > data.Length)
            throw new PstException($"Block {bid} claims {count} child blocks, which do not fit in its {data.Length} bytes.");

        var children = new Bid[count];
        for (var i = 0; i < count; i++)
        {
            var at = 8 + i * width;
            children[i] = new Bid(format.IsWide()
                ? BinaryPrimitives.ReadUInt64LittleEndian(data[at..])
                : BinaryPrimitives.ReadUInt32LittleEndian(data[at..]));
        }

        return (level, total, children);
    }

    /// <summary>
    /// Reads a subnode block. A leaf (SLBLOCK) yields entries and no children; an intermediate
    /// (SIBLOCK) yields the SLBLOCK ids to follow and no entries.
    /// </summary>
    public static (byte Level, IReadOnlyList<SlEntry> Entries, IReadOnlyList<Bid> Children) ReadSubnodeBlock(
        ReadOnlySpan<byte> data, Bid bid, PstFormat format)
    {
        if (data.Length < 8)
            throw new PstException($"Block {bid} is an internal block of {data.Length} bytes, too short to say what it is.");

        if (data[0] != 0x02)
            throw new PstException($"Block {bid} was referenced as a subnode tree but its type byte is 0x{data[0]:X2} where 0x02 belongs.");

        var level = data[1];
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);

        // The Unicode layout aligns its entries on an eight-byte boundary with four bytes of
        // padding after the count; the ANSI layout starts them straight after it.
        var start = format.IsWide() ? 8 : 4;
        var width = format.IsWide() ? 8 : 4;

        if (level == 0)
        {
            var stride = 3 * width;
            if (start + count * stride > data.Length)
                throw new PstException($"Block {bid} claims {count} subnode entries, which do not fit in its {data.Length} bytes.");

            var entries = new SlEntry[count];
            for (var i = 0; i < count; i++)
            {
                var entry = data[(start + i * stride)..];
                entries[i] = format.IsWide()
                    ? new SlEntry(
                        new Nid((uint)BinaryPrimitives.ReadUInt64LittleEndian(entry)),
                        new Bid(BinaryPrimitives.ReadUInt64LittleEndian(entry[8..])),
                        new Bid(BinaryPrimitives.ReadUInt64LittleEndian(entry[16..])))
                    : new SlEntry(
                        new Nid(BinaryPrimitives.ReadUInt32LittleEndian(entry)),
                        new Bid(BinaryPrimitives.ReadUInt32LittleEndian(entry[4..])),
                        new Bid(BinaryPrimitives.ReadUInt32LittleEndian(entry[8..])));
            }

            return (level, entries, []);
        }

        if (level != 1)
            throw new PstException($"Block {bid} is a subnode block of level {level}, and [MS-PST] defines only levels 0 and 1.");

        var childStride = 2 * width;
        if (start + count * childStride > data.Length)
            throw new PstException($"Block {bid} claims {count} subnode child blocks, which do not fit in its {data.Length} bytes.");

        var children = new Bid[count];
        for (var i = 0; i < count; i++)
        {
            var entry = data[(start + i * childStride)..];
            children[i] = new Bid(format.IsWide()
                ? BinaryPrimitives.ReadUInt64LittleEndian(entry[8..])
                : BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]));
        }

        return (level, [], children);
    }
}
