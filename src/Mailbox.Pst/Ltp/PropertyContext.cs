using System.Buffers.Binary;

namespace Mailbox.Pst.Ltp;

/// <summary>
/// A Property Context ([MS-PST] §2.3.3): a node read as a bag of properties — a BTree-on-heap
/// of 2-byte property ids over 6-byte records, each record a type and a value slot.
/// </summary>
/// <remarks>
/// The value slot is the whole trick of the structure, and §2.3.3.3's table is restated here
/// because every reading below implements one of its rows: a fixed-size value of four bytes or
/// fewer sits in the slot itself; a longer fixed value sits in the heap; a variable-size value
/// sits in the heap up to 3,580 bytes and in one of the node's own subnodes past that. A zero
/// slot for a variable value is an empty value, not a missing property — the property exists,
/// with nothing in it.
/// </remarks>
internal sealed class PropertyContext
{
    private readonly Dictionary<ushort, PstProperty> _properties;

    public IReadOnlyDictionary<ushort, PstProperty> Properties => _properties;

    private PropertyContext(Dictionary<ushort, PstProperty> properties) => _properties = properties;

    public PstProperty? Find(ushort id) => _properties.GetValueOrDefault(id);

    public const byte ClientSignature = 0xBC;

    public static PropertyContext Read(ILtpNode node)
    {
        var heap = HeapNode.Parse(node.DataBlocks(), node.Nid);
        if (heap.ClientSignature != ClientSignature)
            throw new PstException(
                $"Node {node.Nid} was read as a property context and its heap says 0x{heap.ClientSignature:X2} where 0x{ClientSignature:X2} belongs.");

        var tree = BTreeOnHeap.Parse(heap, heap.UserRoot, node.Nid);
        if (tree.KeySize != 2 || tree.DataSize != 6)
            throw new PstException(
                $"Node {node.Nid} holds a property tree of {tree.KeySize}-byte keys and {tree.DataSize}-byte data where 2 and 6 belong.");

        var properties = new Dictionary<ushort, PstProperty>();
        foreach (var record in tree.Records())
        {
            var span = record.Span;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(span);
            var type = (PstPropertyType)BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
            var slot = span[4..8];

            properties[id] = new PstProperty(id, type, Resolve(type, slot, heap, node));
        }

        return new PropertyContext(properties);
    }

    /// <summary>§2.3.3.3's table, one row per branch.</summary>
    private static byte[] Resolve(PstPropertyType type, ReadOnlySpan<byte> slot, HeapNode heap, ILtpNode node)
    {
        if (PstProperty.FixedSize(type) is { } size && (((ushort)type) & PstProperty.MultiValuedFlag) == 0)
        {
            if (size <= 4) return slot[..size].ToArray();

            var hid = new Hnid(BinaryPrimitives.ReadUInt32LittleEndian(slot));
            return hid.IsZero ? new byte[size] : heap.Item(hid.AsHeapId).ToArray();
        }

        var hnid = new Hnid(BinaryPrimitives.ReadUInt32LittleEndian(slot));
        if (hnid.IsZero) return [];
        if (hnid.IsHeap) return heap.Item(hnid.AsHeapId).ToArray();

        var subnode = node.Subnode(hnid.AsNid)
            ?? throw new PstException($"Node {node.Nid} keeps a property in subnode {hnid.AsNid}, which its subnode tree does not hold.");
        return subnode.Data();
    }
}
