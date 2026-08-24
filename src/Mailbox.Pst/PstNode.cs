using Mailbox.Pst.Ltp;
using Mailbox.Pst.Ndb;

namespace Mailbox.Pst;

/// <summary>
/// One node with its context: its data, and the subnodes that exist only inside it. This is the
/// unit the LTP layer works on — a property or table context is a node read a particular way,
/// and its overflow values live in the node's own subnode tree, never anywhere else.
/// </summary>
public sealed class PstNode : ILtpNode
{
    private readonly PstFile _file;
    private readonly Bid _data;
    private readonly Bid _subnodeTree;
    private Dictionary<uint, SlEntry>? _subnodes;

    public Nid Nid { get; }

    internal PstFormat Format => _file.Format;

    PstFormat ILtpNode.Format => Format;

    ILtpNode? ILtpNode.Subnode(Nid local) => Subnode(local);

    internal PstNode(PstFile file, Nid nid, Bid data, Bid subnodeTree)
    {
        _file = file;
        _data = data;
        _subnodeTree = subnodeTree;
        Nid = nid;
    }

    public byte[] Data() => _file.ReadData(_data);

    IReadOnlyList<byte[]> ILtpNode.DataBlocks() => DataBlocks();

    internal IReadOnlyList<byte[]> DataBlocks() => _file.ReadDataBlocks(_data);

    private Dictionary<uint, SlEntry> SubnodeTable()
    {
        if (_subnodes is null)
        {
            _subnodes = [];
            foreach (var entry in _file.Subnodes(_subnodeTree))
                _subnodes[entry.Nid.Value] = entry;
        }

        return _subnodes;
    }

    /// <summary>The subnodes this node carries, in their own right — each a node whose id means something only here.</summary>
    public IEnumerable<PstNode> Subnodes() =>
        SubnodeTable().Values.Select(entry => new PstNode(_file, entry.Nid, entry.Data, entry.Subnode));

    /// <summary>The subnode a local id names, or null — a reference into a sibling's tree finds nothing here.</summary>
    public PstNode? Subnode(Nid local) =>
        SubnodeTable().TryGetValue(local.Value, out var entry)
            ? new PstNode(_file, entry.Nid, entry.Data, entry.Subnode)
            : null;
}
