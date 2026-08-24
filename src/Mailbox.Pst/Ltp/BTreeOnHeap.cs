using System.Buffers.Binary;
using Mailbox.Pst.Ndb;

namespace Mailbox.Pst.Ltp;

/// <summary>
/// A BTree-on-Heap ([MS-PST] §2.3.2): the header states the record shape, and the records live
/// in heap items — one item per index or leaf array.
/// </summary>
internal sealed class BTreeOnHeap
{
    private readonly HeapNode _heap;
    private readonly HeapId _root;
    private readonly byte _levels;

    public byte KeySize { get; }

    public byte DataSize { get; }

    public int RecordSize => KeySize + DataSize;

    private BTreeOnHeap(HeapNode heap, HeapId root, byte levels, byte keySize, byte dataSize)
    {
        _heap = heap;
        _root = root;
        _levels = levels;
        KeySize = keySize;
        DataSize = dataSize;
    }

    public static BTreeOnHeap Parse(HeapNode heap, HeapId at, Nid owner)
    {
        var header = heap.Item(at).Span;
        if (header.Length < 8 || header[0] != 0xB5)
            throw new PstException($"Node {owner} does not hold a BTree-on-heap where its structure says one starts.");

        var keySize = header[1];
        var dataSize = header[2];
        if (keySize is not (2 or 4 or 8 or 16) || dataSize == 0 || dataSize > 32)
            throw new PstException($"Node {owner} holds a BTree-on-heap of {keySize}-byte keys and {dataSize}-byte data, which [MS-PST] does not allow.");

        return new BTreeOnHeap(heap, new HeapId(BinaryPrimitives.ReadUInt32LittleEndian(header[4..])), header[3], keySize, dataSize);
    }

    /// <summary>
    /// Every leaf record, in key order, as raw <see cref="RecordSize"/>-byte slices — what a key
    /// or datum means belongs to whoever built the tree.
    /// </summary>
    public IEnumerable<ReadOnlyMemory<byte>> Records() => _root.IsZero ? [] : RecordsUnder(_root, _levels);

    private IEnumerable<ReadOnlyMemory<byte>> RecordsUnder(HeapId at, int level)
    {
        var item = _heap.Item(at);

        if (level == 0)
        {
            for (var offset = 0; offset + RecordSize <= item.Length; offset += RecordSize)
                yield return item.Slice(offset, RecordSize);
            yield break;
        }

        // An index record is the child level's first key and the heap item holding that level.
        var indexSize = KeySize + 4;
        for (var offset = 0; offset + indexSize <= item.Length; offset += indexSize)
        {
            var child = new HeapId(BinaryPrimitives.ReadUInt32LittleEndian(item.Span[(offset + KeySize)..]));
            foreach (var record in RecordsUnder(child, level - 1))
                yield return record;
        }
    }
}
