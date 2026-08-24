using Mailbox.Pst.Ndb;

namespace Mailbox.Pst.Ltp;

/// <summary>
/// What the LTP layer needs from a node: its identity, its data kept block by block, and its
/// subnodes. <see cref="PstNode"/> is the real one; the specification's worked examples stand in
/// as fake ones in tests, a single block with no file behind it.
/// </summary>
internal interface ILtpNode
{
    Nid Nid { get; }

    PstFormat Format { get; }

    byte[] Data();

    IReadOnlyList<byte[]> DataBlocks();

    ILtpNode? Subnode(Nid local);
}
