using Mailbox.Pst.Ndb;
using Microsoft.Win32.SafeHandles;

namespace Mailbox.Pst;

/// <summary>
/// A PST file open for reading — the NDB layer of [MS-PST]: the header, the node and block
/// BTrees, block integrity, the obfuscation, data trees and subnode trees. What this layer hands
/// out is each node's bytes; what the bytes mean belongs to the LTP layer above it.
/// </summary>
/// <remarks>
/// Everything read is verified before it is believed — a page or block must carry the id it was
/// looked up by, sit where its reference said, and match its own CRC — because an importer's
/// input is by definition a file some other program wrote and this application cannot repair
/// (§19: the parsers are the attack surface). Every structural lie a file can tell surfaces as a
/// <see cref="PstException"/> naming the disagreement, never as a runtime fault, and no claimed
/// length is allocated until the blocks backing it have been counted.
/// </remarks>
public sealed class PstFile : IDisposable
{
    private readonly SafeFileHandle? _handle;
    private readonly byte[]? _bytes;
    private readonly long _length;
    private readonly PstHeader _header;

    // BTree pages are re-read constantly during an import — every node lookup descends the same
    // few hundred pages — so verified ones are kept, bounded, and simply forgotten when full.
    private readonly Dictionary<ulong, BTreePage> _pages = [];
    private const int PageCacheLimit = 4096;

    private const int MaxBTreeDepth = 8;

    internal PstFormat Format => _header.Format;

    internal PstCryptMethod CryptMethod => _header.CryptMethod;

    /// <summary>Opens and verifies the header; the rest of the file is read as it is asked for.</summary>
    public static PstFile Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new PstFile(handle, RandomAccess.GetLength(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private PstFile(SafeFileHandle handle, long length)
    {
        _handle = handle;
        _length = length;
        _header = ReadHeader();
    }

    /// <summary>A file already in memory — the specification's worked examples arrive this way in tests.</summary>
    internal PstFile(byte[] bytes)
    {
        _bytes = bytes;
        _length = bytes.Length;
        _header = ReadHeader();
    }

    public void Dispose() => _handle?.Dispose();

    private PstHeader ReadHeader()
    {
        var header = new byte[Math.Min(PstHeader.UnicodeLength, _length)];
        ReadExactly(0, header);
        return PstHeader.Parse(header);
    }

    private void ReadExactly(long offset, Span<byte> into)
    {
        if (offset < 0 || offset + into.Length > _length)
            throw new PstException(
                $"The file asks its reader to look at bytes 0x{offset:X}–0x{offset + into.Length:X} and is only 0x{_length:X} long: a reference points outside the file.");

        if (_bytes is not null)
        {
            _bytes.AsSpan((int)offset, into.Length).CopyTo(into);
            return;
        }

        var read = RandomAccess.Read(_handle!, into, offset);
        if (read != into.Length)
            throw new PstException($"The file ended after {read} of the {into.Length} bytes wanted at 0x{offset:X}.");
    }

    // ---- The two BTrees -----------------------------------------------------------------

    /// <summary>Every node in the file, in node-id order — the walk an importer starts from.</summary>
    public IEnumerable<NbtEntry> Nodes() => LeavesOf(_header.Root.NodeBTree, PageType.NodeBTree, 0)
        .SelectMany(page => Enumerable.Range(0, page.EntryCount).Select(i => page.Node(i, Format)));

    /// <summary>The node with this id as a <see cref="PstNode"/>, or null.</summary>
    public PstNode? Node(Nid nid) => FindNode(nid) is { } entry ? NodeOf(entry) : null;

    /// <summary>A found entry wrapped with its context.</summary>
    public PstNode NodeOf(NbtEntry entry) => new(this, entry.Nid, entry.Data, entry.Subnode);

    /// <summary>The node with this id, or null — absence is an answer here, not a fault.</summary>
    public NbtEntry? FindNode(Nid nid)
    {
        var page = LeafFor(_header.Root.NodeBTree, PageType.NodeBTree, nid.Value);
        if (page is null) return null;

        for (var i = 0; i < page.EntryCount; i++)
        {
            var node = page.Node(i, Format);
            if (node.Nid == nid) return node;
        }

        return null;
    }

    internal BbtEntry? FindBlock(Bid bid)
    {
        var page = LeafFor(_header.Root.BlockBTree, PageType.BlockBTree, bid.SearchKey);
        if (page is null) return null;

        for (var i = 0; i < page.EntryCount; i++)
        {
            var block = page.Block(i, Format);
            if (block.Bref.Bid.SearchKey == bid.SearchKey) return block;
        }

        return null;
    }

    private BTreePage ReadPage(Bref source, PageType expected)
    {
        if (_pages.TryGetValue(source.Ib, out var cached))
        {
            if (cached.Type != expected)
                throw new PstException($"The page at 0x{source.Ib:X} is referenced as both BTrees at once: a BTree reference is wrong.");
            return cached;
        }

        Span<byte> buffer = stackalloc byte[4096];
        var bytes = buffer[..BTreePage.PageSize(Format)];
        ReadExactly((long)source.Ib, bytes);
        var page = BTreePage.Read(bytes, source, Format);
        if (page.Type != expected)
            throw new PstException(
                $"The page at 0x{source.Ib:X} is a {(page.Type == PageType.NodeBTree ? "node" : "block")}-BTree page where the {(expected == PageType.NodeBTree ? "node" : "block")} BTree was being walked.");

        if (_pages.Count >= PageCacheLimit) _pages.Clear();
        _pages[source.Ib] = page;
        return page;
    }

    /// <summary>Descends one BTree to the leaf page whose range covers the key, or null when the key sorts before everything.</summary>
    private BTreePage? LeafFor(Bref root, PageType expected, ulong key)
    {
        var at = root;
        for (var depth = 0; depth <= MaxBTreeDepth; depth++)
        {
            var page = ReadPage(at, expected);
            if (page.Level == 0) return page;

            // Intermediate entries carry the smallest key beneath them; the child to follow is
            // the last one at or under the key being looked for.
            var found = -1;
            for (var i = 0; i < page.EntryCount; i++)
            {
                if (page.Intermediate(i, Format).Key <= key) found = i;
                else break;
            }

            if (found < 0) return null;
            at = page.Intermediate(found, Format).Child;
        }

        throw new PstException($"The BTree under 0x{root.Ib:X} is deeper than the {MaxBTreeDepth} levels [MS-PST] allows: the tree loops.");
    }

    private IEnumerable<BTreePage> LeavesOf(Bref root, PageType expected, int depth)
    {
        if (depth > MaxBTreeDepth)
            throw new PstException($"The BTree under 0x{root.Ib:X} is deeper than the {MaxBTreeDepth} levels [MS-PST] allows: the tree loops.");

        var page = ReadPage(root, expected);
        if (page.Level == 0)
        {
            yield return page;
            yield break;
        }

        for (var i = 0; i < page.EntryCount; i++)
        {
            foreach (var leaf in LeavesOf(page.Intermediate(i, Format).Child, expected, depth + 1))
                yield return leaf;
        }
    }

    // ---- Blocks and the data they assemble into -----------------------------------------

    /// <summary>The bytes of one block exactly as stored, verified against its own trailer but not yet decoded.</summary>
    private byte[] ReadStoredBlock(Bid bid, BbtEntry entry)
    {
        var trailerSize = BlockTrailer.Size(Format);
        var maxBlock = BlockTrailer.MaxSize(Format);
        if (entry.Length > maxBlock - trailerSize)
            throw new PstException($"Block {bid} claims {entry.Length} bytes of data, and no block holds more than {maxBlock - trailerSize}.");

        var stored = BlockTrailer.StoredSize(entry.Length, Format);
        var buffer = new byte[stored];
        ReadExactly((long)entry.Bref.Ib, buffer);

        var trailer = BlockTrailer.Read(buffer.AsSpan(stored - trailerSize), Format);
        if (trailer.Length != entry.Length)
            throw new PstException(
                $"Block {bid} says it holds {trailer.Length} bytes where its BTree entry says {entry.Length}: the file disagrees with itself.");

        if (trailer.Bid.SearchKey != bid.SearchKey)
            throw new PstException($"The block at 0x{entry.Bref.Ib:X} says it is {trailer.Bid} but was reached as {bid}: a block reference is stale.");

        var signature = PstCrc.BlockSignature(entry.Bref.Ib, trailer.Bid.Value);
        if (signature != trailer.Signature)
            throw new PstException(
                $"The block at 0x{entry.Bref.Ib:X} carries signature 0x{trailer.Signature:X4} where 0x{signature:X4} belongs: the block is not the one that was written here.");

        // The CRC covers the bytes as stored — for an encoded or compressed block, those.
        var crc = PstCrc.Compute(buffer.AsSpan(0, entry.Length));
        if (crc != trailer.Crc)
            throw new PstException($"Block {bid} carries CRC 0x{trailer.Crc:X8} but its bytes come to 0x{crc:X8}: the block is damaged.");

        // The 4K layout can deflate a block's data; the trailer's uncompressed size differing
        // from the stored size is the flag, and the wrapper is zlib — learnt from real files,
        // the documentation saying bare deflate.
        if (Format == PstFormat.Unicode4K && trailer.UncompressedLength != (ushort)entry.Length)
            return Inflate(buffer.AsSpan(0, entry.Length), trailer.UncompressedLength, bid);

        Array.Resize(ref buffer, entry.Length);
        return buffer;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, ushort statedLength, Bid bid)
    {
        try
        {
            using var source = new MemoryStream(compressed.ToArray());
            using var inflater = new System.IO.Compression.ZLibStream(source, System.IO.Compression.CompressionMode.Decompress);
            using var inflated = new MemoryStream();

            // The stated size is sixteen bits, so it is checked modulo rather than trusted as a
            // bound; the block's own ceiling is the real one.
            var buffer = new byte[8192];
            int read;
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                inflated.Write(buffer, 0, read);
                if (inflated.Length > BlockTrailer.MaxSize(PstFormat.Unicode4K))
                    throw new PstException($"Block {bid} inflates past the largest block the format allows: the block is damaged.");
            }

            var result = inflated.ToArray();
            if ((ushort)result.Length != statedLength)
                throw new PstException(
                    $"Block {bid} says it inflates to {statedLength} bytes and came to {result.Length}: the block is damaged.");

            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new PstException($"Block {bid} is marked compressed and would not inflate: the block is damaged.", ex);
        }
    }

    private byte[] ReadBlock(Bid bid)
    {
        var entry = FindBlock(bid)
            ?? throw new PstException($"The file references block {bid}, which its block BTree does not hold.");

        var data = ReadStoredBlock(bid, entry);

        // Only external data blocks are ever obfuscated; the internal ones are NDB metadata.
        if (!bid.IsInternal)
        {
            switch (CryptMethod)
            {
                case PstCryptMethod.Permute:
                    BlockEncoding.DecodePermute(data);
                    break;
                case PstCryptMethod.Cyclic:
                    BlockEncoding.Cyclic(data, (uint)bid.Value);
                    break;
            }
        }

        return data;
    }

    /// <summary>
    /// The data behind a block reference kept as its constituent blocks, in order — one entry
    /// for a plain block, the leaf blocks of the tree otherwise. The heap and the table row
    /// matrix are addressed per block, which is why the pieces survive here and concatenation
    /// is the caller's move.
    /// </summary>
    internal IReadOnlyList<byte[]> ReadDataBlocks(Bid bid)
    {
        if (bid.IsZero) return [];
        if (!bid.IsInternal) return [ReadBlock(bid)];

        var chunks = new List<byte[]>();
        var (level, claimed, children) = InternalBlocks.ReadDataTree(ReadBlock(bid), bid, Format);
        long total = 0;

        foreach (var child in children)
        {
            if (level == 1)
            {
                var chunk = ReadLeafChunk(child, bid);
                chunks.Add(chunk);
                total += chunk.Length;
                continue;
            }

            // An XXBLOCK fans out to XBLOCKs, each with its own claimed total to hold it to.
            var (childLevel, childClaimed, grandchildren) = InternalBlocks.ReadDataTree(ReadBlock(child), child, Format);
            if (childLevel != 1)
                throw new PstException($"Block {child} sits under a level-2 data tree and must be level 1, not level {childLevel}.");

            long childTotal = 0;
            foreach (var grandchild in grandchildren)
            {
                var chunk = ReadLeafChunk(grandchild, child);
                chunks.Add(chunk);
                childTotal += chunk.Length;
            }

            if (childTotal != childClaimed)
                throw new PstException($"The data tree under block {child} claims {childClaimed} bytes and its blocks hold {childTotal}.");
            total += childTotal;
        }

        if (total != claimed)
            throw new PstException($"The data tree under block {bid} claims {claimed} bytes and its blocks hold {total}.");

        return chunks;
    }

    /// <summary>
    /// The full data behind a block reference: a single block's bytes, or a whole data tree
    /// assembled in order. A zero id is an empty answer — nodes without data exist.
    /// </summary>
    public byte[] ReadData(Bid bid)
    {
        var chunks = ReadDataBlocks(bid);
        if (chunks.Count == 1) return chunks[0];

        var total = chunks.Sum(chunk => (long)chunk.Length);
        if (total > int.MaxValue)
            throw new PstException($"The data tree under block {bid} assembles to {total} bytes, which is more than any one value can be.");

        var assembled = new byte[total];
        var at = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(assembled.AsSpan(at));
            at += chunk.Length;
        }

        return assembled;
    }

    private byte[] ReadLeafChunk(Bid bid, Bid parent)
    {
        if (bid.IsInternal)
            throw new PstException($"Block {bid} sits at the bottom of the data tree under {parent} and must be a plain data block.");
        return ReadBlock(bid);
    }

    /// <summary>A node's own bytes — the thing the NDB layer exists to hand over.</summary>
    public byte[] ReadNodeData(NbtEntry node) => ReadData(node.Data);

    // ---- Subnodes ------------------------------------------------------------------------

    /// <summary>
    /// The subnode entries under a node's subnode tree, flattened in on-disk order. Subnodes are
    /// how a message carries its recipient table and attachments: nodes addressable only from
    /// their parent.
    /// </summary>
    public IReadOnlyList<SlEntry> Subnodes(Bid subnodeBid)
    {
        if (subnodeBid.IsZero) return [];

        var (level, entries, children) = InternalBlocks.ReadSubnodeBlock(ReadBlock(subnodeBid), subnodeBid, Format);
        if (level == 0) return entries;

        var all = new List<SlEntry>();
        foreach (var child in children)
        {
            var (childLevel, childEntries, _) = InternalBlocks.ReadSubnodeBlock(ReadBlock(child), child, Format);
            if (childLevel != 0)
                throw new PstException($"Block {child} sits under an intermediate subnode block and must be a leaf, not level {childLevel}.");
            all.AddRange(childEntries);
        }

        return all;
    }
}
