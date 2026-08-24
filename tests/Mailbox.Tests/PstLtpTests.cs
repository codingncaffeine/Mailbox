using Mailbox.Pst;
using Mailbox.Pst.Ltp;
using Mailbox.Pst.Ndb;

namespace Mailbox.Tests;

/// <summary>
/// The LTP layer against [MS-PST]'s worked examples: the heap and BTree-on-heap of §3.8–3.9,
/// the fully annotated message-store property context of §3.10, and the three-row table context
/// of §3.11. Every value asserted is one the specification prints beside its dump.
/// </summary>
public class PstLtpTests
{
    /// <summary>A node that is just its bytes — the examples have no file around them.</summary>
    private sealed class FakeNode(byte[] block, Nid nid) : ILtpNode
    {
        public Nid Nid => nid;

        public PstFormat Format => PstFormat.Unicode;

        public byte[] Data() => block;

        public IReadOnlyList<byte[]> DataBlocks() => [block];

        public ILtpNode? Subnode(Nid local) => null;
    }

    [Fact]
    public void SampleHeapParsesToItsAnnotatedShape()
    {
        var heap = HeapNode.Parse([PstSpecExamples.MessageStoreBlock()], new Nid(0x21));

        Assert.Equal(PropertyContext.ClientSignature, heap.ClientSignature);
        Assert.Equal(new HeapId(0x20), heap.UserRoot);

        // §3.8's allocation table: eight items, whose starts the text lists one by one.
        Assert.Equal(8, heap.Item(new HeapId(0x20)).Length);
        Assert.Equal(0x58, heap.Item(new HeapId(0x40)).Length);
        Assert.Equal(16, heap.Item(new HeapId(0x60)).Length);
    }

    [Fact]
    public void SampleBTreeOnHeapWalksItsElevenRecords()
    {
        var heap = HeapNode.Parse([PstSpecExamples.MessageStoreBlock()], new Nid(0x21));
        var tree = BTreeOnHeap.Parse(heap, heap.UserRoot, new Nid(0x21));

        Assert.Equal(2, tree.KeySize);
        Assert.Equal(6, tree.DataSize);
        Assert.Equal(11, tree.Records().Count());
    }

    [Fact]
    public void SampleMessageStorePropertiesDecodeToTheirPrintedValues()
    {
        var pc = PropertyContext.Read(new FakeNode(PstSpecExamples.MessageStoreBlock(), new Nid(0x21)));

        // Eleven records; §3.10 prints nine of them and both of the others are asserted too.
        Assert.Equal(11, pc.Properties.Count);

        Assert.Equal("UNICODE1", pc.Find(0x3001)!.AsString());
        Assert.Equal(0, pc.Find(0x0E38)!.AsInteger32());
        Assert.Equal(0x89, pc.Find(0x35DF)!.AsInteger32());
        Assert.Equal(0, pc.Find(0x67FF)!.AsInteger32());
        Assert.True(pc.Find(0x6633)!.AsBoolean());
        Assert.Equal(0x000E000D, pc.Find(0x66FA)!.AsInteger32());

        Assert.Equal(
            Convert.FromHexString("229DB50ADCD9944385DE90AEB07D1270"),
            pc.Find(0x0FF9)!.AsBinary());

        var replVersionHistory = pc.Find(0x0E34)!.AsBinary();
        Assert.Equal(24, replVersionHistory.Length);
        Assert.Equal(Convert.FromHexString("01000000F55EF666"), replVersionHistory[..8]);

        var ipmSubTree = pc.Find(0x35E0)!.AsBinary();
        Assert.Equal(24, ipmSubTree.Length);
        Assert.Equal(Convert.FromHexString("22800000"), ipmSubTree[20..]);
    }

    [Fact]
    public void SampleTableContextReadsItsThreeRows()
    {
        var tc = TableContext.Read(new FakeNode(PstSpecExamples.TableContextBlock(), new Nid(0x122)));

        Assert.Equal(13, tc.Columns.Count);
        Assert.Equal(3, tc.RowCount);

        // The display-name column as §3.11's TCOLDESC dump states it.
        var name = tc.Columns.Single(column => column.PropertyId == 0x3001);
        Assert.Equal(PstPropertyType.String, name.PropertyType);
        Assert.Equal(8, name.Offset);
        Assert.Equal(4, name.Width);
        Assert.Equal(2, name.ExistenceBit);

        // Rows in matrix order, each name resolved through the heap.
        var rows = tc.Rows().ToList();
        Assert.Equal(0x8022u, rows[0].RowId);
        Assert.Equal(0x8042u, rows[1].RowId);
        Assert.Equal(0x2223u, rows[2].RowId);
        Assert.Equal("Top of Personal Folders", rows[0].Property(0x3001)!.AsString());
        Assert.Equal("Search Root", rows[1].Property(0x3001)!.AsString());
        Assert.Equal("SPAM Search Folder 2", rows[2].Property(0x3001)!.AsString());
    }

    [Fact]
    public void ACellWhoseExistenceBitIsClearIsNotThere()
    {
        var tc = TableContext.Read(new FakeNode(PstSpecExamples.TableContextBlock(), new Nid(0x122)));
        var rows = tc.Rows().ToList();

        // Every row's bitmap is FC 00 — bits 0 through 5 stand, 6 and up are clear — so the two
        // columns on bits 6 and 7 must read as absent even though their row space exists.
        Assert.All(rows, row => Assert.Null(row.Property(0x0E30)));
        Assert.All(rows, row => Assert.Null(row.Property(0x0E33)));

        // And three cells that are set, with the values the dump holds: the row id surfaced as
        // its own column, and the has-subfolders flag true only on the root row.
        Assert.Equal(0x8022, rows[0].Property(0x67F2)!.AsInteger32());
        Assert.True(rows[0].Property(0x360A)!.AsBoolean());
        Assert.False(rows[1].Property(0x360A)!.AsBoolean());
        Assert.False(rows[2].Property(0x360A)!.AsBoolean());
    }

    [Fact]
    public void PropertyReadingsRefuseTypeConfusion()
    {
        var pc = PropertyContext.Read(new FakeNode(PstSpecExamples.MessageStoreBlock(), new Nid(0x21)));

        // Asking a binary for its integer is the reader's bug, and the refusal says so instead
        // of manufacturing a number out of the first four bytes.
        Assert.Throws<PstException>(() => pc.Find(0x0FF9)!.AsInteger32());
        Assert.Throws<PstException>(() => pc.Find(0x3001)!.AsBinary());
    }

    [Fact]
    public void MultiValuedElementsSplitByTheirOffsetTable()
    {
        // §2.3.3.4.2's layout built by hand: two strings behind a count and offset table.
        var first = "one"u8.ToArray().SelectMany(b => new byte[] { b, 0 }).ToArray();
        var second = "two!"u8.ToArray().SelectMany(b => new byte[] { b, 0 }).ToArray();
        var raw = new byte[4 + 8 + first.Length + second.Length];
        BitConverter.GetBytes(2u).CopyTo(raw, 0);
        BitConverter.GetBytes(12u).CopyTo(raw, 4);
        BitConverter.GetBytes(12u + (uint)first.Length).CopyTo(raw, 8);
        first.CopyTo(raw, 12);
        second.CopyTo(raw, 12 + first.Length);

        var property = new PstProperty(0x1234, (PstPropertyType)(0x001F | PstProperty.MultiValuedFlag), raw);
        var elements = property.Elements();

        Assert.Equal(2, elements.Count);
        Assert.Equal("one", elements[0].AsString());
        Assert.Equal("two!", elements[1].AsString());

        // And the fixed-size shape: bytes divided evenly, no table.
        var numbers = new PstProperty(0x1234, (PstPropertyType)(0x0003 | PstProperty.MultiValuedFlag),
            [1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0]);
        Assert.Equal([1, 2, 3], numbers.Elements().Select(element => element.AsInteger32()));
    }
}
