using System.Buffers.Binary;
using Mailbox.Pst.Ndb;

namespace Mailbox.Pst.Ltp;

/// <summary>
/// An HID ([MS-PST] §2.3.1.1): which heap item, in which block of the node's data tree. The
/// eleven-bit index is 1-based; the five type bits must be zero for the value to be an HID at
/// all, which is exactly how an HNID tells the two apart.
/// </summary>
internal readonly record struct HeapId(uint Value)
{
    public byte Type => (byte)(Value & 0x1F);

    /// <summary>1-based index into the owning block's allocation table.</summary>
    public int Index => (int)((Value >> 5) & 0x7FF);

    public int BlockIndex => (int)(Value >> 16);

    public bool IsZero => Value == 0;

    public override string ToString() => $"0x{Value:X}";
}

/// <summary>
/// An HNID ([MS-PST] §2.3.3.2): one 32-bit slot that holds either an HID or an NID, told apart
/// by the type bits — zero means the data sits in the heap, anything else names the subnode
/// holding it.
/// </summary>
internal readonly record struct Hnid(uint Value)
{
    public bool IsZero => Value == 0;

    public bool IsHeap => (Value & 0x1F) == 0;

    public HeapId AsHeapId => new(Value);

    public Nid AsNid => new(Value);
}

/// <summary>
/// A Heap-on-Node ([MS-PST] §2.3.1): a node's data blocks reinterpreted as a heap of small
/// items, each addressed by an <see cref="HeapId"/>.
/// </summary>
/// <remarks>
/// The heap spans the node's data tree block by block — an HID names a block index and a slot,
/// so the blocks are kept apart here rather than concatenated. Each block ends in its own
/// allocation table, found through the header every block opens with: the full HNHDR on block
/// zero, a two-byte HNPAGEHDR on most others, and the 66-byte HNBITMAPHDR on block eight and
/// every 128th after it — a schedule fixed by the format, not recorded in the file.
/// </remarks>
internal sealed class HeapNode
{
    private readonly IReadOnlyList<byte[]> _blocks;
    private readonly ushort[][] _allocations;
    private readonly Nid _owner;

    public byte ClientSignature { get; }

    public HeapId UserRoot { get; }

    private HeapNode(IReadOnlyList<byte[]> blocks, ushort[][] allocations, Nid owner, byte clientSignature, HeapId userRoot)
    {
        _blocks = blocks;
        _allocations = allocations;
        _owner = owner;
        ClientSignature = clientSignature;
        UserRoot = userRoot;
    }

    public static HeapNode Parse(IReadOnlyList<byte[]> blocks, Nid owner)
    {
        if (blocks.Count == 0 || blocks[0].Length < 12)
            throw new PstException($"Node {owner} was asked for as a heap and does not begin with a heap header.");

        var first = blocks[0];
        if (first[2] != 0xEC)
            throw new PstException($"Node {owner} carries 0x{first[2]:X2} where a heap's 0xEC signature belongs: the node is not a heap.");

        var allocations = new ushort[blocks.Count][];
        for (var i = 0; i < blocks.Count; i++)
            allocations[i] = ReadAllocationTable(blocks[i], i, owner);

        return new HeapNode(blocks, allocations, owner, first[3], new HeapId(BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4))));
    }

    private static ushort[] ReadAllocationTable(byte[] block, int index, Nid owner)
    {
        var headerSize = index == 0 ? 12 : index >= 8 && (index - 8) % 128 == 0 ? 66 : 2;
        if (block.Length < headerSize + 4)
            throw new PstException($"Block {index} of the heap on node {owner} is too short to be part of one.");

        var mapAt = BinaryPrimitives.ReadUInt16LittleEndian(block);
        if (mapAt + 4 > block.Length)
            throw new PstException($"Block {index} of the heap on node {owner} puts its allocation map outside itself.");

        int count = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(mapAt));
        if (mapAt + 4 + (count + 1) * 2 > block.Length)
            throw new PstException($"Block {index} of the heap on node {owner} claims {count} allocations, whose table does not fit.");

        var offsets = new ushort[count + 1];
        for (var i = 0; i <= count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(mapAt + 4 + i * 2));
            if (offsets[i] > block.Length || (i > 0 && offsets[i] < offsets[i - 1]) || (i == 0 && offsets[0] < headerSize))
                throw new PstException($"Block {index} of the heap on node {owner} has an allocation table that walks outside the block.");
        }

        return offsets;
    }

    /// <summary>The bytes of one heap item.</summary>
    public ReadOnlyMemory<byte> Item(HeapId hid)
    {
        if (hid.Type != 0)
            throw new PstException($"Node {_owner} was asked for heap item {hid}, whose type bits say it is not a heap id.");

        if (hid.BlockIndex >= _blocks.Count)
            throw new PstException($"Node {_owner} was asked for heap item {hid} in block {hid.BlockIndex}, and the heap has {_blocks.Count}.");

        var offsets = _allocations[hid.BlockIndex];
        if (hid.Index < 1 || hid.Index >= offsets.Length)
            throw new PstException($"Node {_owner} was asked for heap item {hid}, and that block holds {offsets.Length - 1} items.");

        var start = offsets[hid.Index - 1];
        return _blocks[hid.BlockIndex].AsMemory(start, offsets[hid.Index] - start);
    }
}
