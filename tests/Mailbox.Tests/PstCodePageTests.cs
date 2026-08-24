using System.Buffers.Binary;
using System.Text;
using Mailbox.Pst;
using Mailbox.Pst.Ltp;
using Mailbox.Pst.Messaging;
using Mailbox.Pst.Ndb;

namespace Mailbox.Tests;

/// <summary>
/// String8 code pages: the message's own PidTagMessageCodepage decides how its narrow strings
/// decode. The end-to-end case builds a real single-block property context by hand — heap
/// header, BTree-on-heap, records, page map — holding a Cyrillic display name beside the code
/// page that names it, because no file in the corpus carries anything beyond ASCII.
/// </summary>
public class PstCodePageTests
{
    [Fact]
    public void TheResolverAnswersKnownPagesAndRefusesTheRest()
    {
        Assert.Equal("windows-1251", PstCodePage.Resolve(1251)!.WebName);
        Assert.Equal("utf-8", PstCodePage.Resolve(65001)!.WebName);
        Assert.Null(PstCodePage.Resolve(null));
        Assert.Null(PstCodePage.Resolve(0));
        Assert.Null(PstCodePage.Resolve(1200));
        Assert.Null(PstCodePage.Resolve(999999));
    }

    [Fact]
    public void AString8PropertyDecodesByItsStampedEncodingAndItsElementsInherit()
    {
        var shiftJis = PstCodePage.Resolve(932)!;
        var bytes = shiftJis.GetBytes("日本語");

        var single = new PstProperty(0x3001, PstPropertyType.String8, bytes) { String8Encoding = shiftJis };
        Assert.Equal("日本語", single.AsString());

        // Unstamped stays Latin-1 — the reading that never widens a byte.
        Assert.NotEqual("日本語", new PstProperty(0x3001, PstPropertyType.String8, bytes).AsString());

        var packed = new byte[4 + 4 + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packed, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(4), 8);
        bytes.CopyTo(packed, 8);
        var multi = new PstProperty(0x3001, (PstPropertyType)(0x001E | PstProperty.MultiValuedFlag), packed)
        {
            String8Encoding = shiftJis,
        };
        Assert.Equal("日本語", Assert.Single(multi.Elements()).AsString());
    }

    /// <summary>A node that is just its one block, as the LTP fixtures use.</summary>
    private sealed class FakeNode(byte[] block) : ILtpNode
    {
        public Nid Nid => new(0x200024);

        public PstFormat Format => PstFormat.Unicode;

        public byte[] Data() => block;

        public IReadOnlyList<byte[]> DataBlocks() => [block];

        public ILtpNode? Subnode(Nid local) => null;
    }

    /// <summary>
    /// One heap block holding a property context: HNHDR, the BTH header at HID 0x20, the record
    /// array at 0x40, one heap value per extra allocation, and the page map last.
    /// </summary>
    private static byte[] BuildPc(IReadOnlyList<(ushort Id, ushort Type, byte[] Slot)> records, IReadOnlyList<byte[]> heapValues)
    {
        var allocations = new List<byte[]>();

        var header = new byte[8];
        header[0] = 0xB5;
        header[1] = 2;
        header[2] = 6;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x40);
        allocations.Add(header);

        var table = new byte[records.Count * 8];
        for (var i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(i * 8), records[i].Id);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(i * 8 + 2), records[i].Type);
            records[i].Slot.CopyTo(table.AsSpan(i * 8 + 4, 4));
        }

        allocations.Add(table);
        allocations.AddRange(heapValues);

        var offsets = new List<ushort> { 12 };
        foreach (var allocation in allocations)
            offsets.Add((ushort)(offsets[^1] + allocation.Length));

        var mapAt = offsets[^1] + (offsets[^1] % 2); // the map starts on a two-byte boundary
        var block = new byte[mapAt + 4 + offsets.Count * 2];
        block[0] = (byte)mapAt;
        block[1] = (byte)(mapAt >> 8);
        block[2] = 0xEC;
        block[3] = 0xBC;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0x20);

        var at = 12;
        foreach (var allocation in allocations)
        {
            allocation.CopyTo(block.AsSpan(at));
            at += allocation.Length;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(mapAt), (ushort)allocations.Count);
        for (var i = 0; i < offsets.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(mapAt + 4 + i * 2), offsets[i]);

        return block;
    }

    [Fact]
    public void APropertyContextStampsItsStringsWithItsOwnCodePage()
    {
        var cp1251 = PstCodePage.Resolve(1251)!;
        var name = cp1251.GetBytes("Привет, мир");

        var codepageSlot = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(codepageSlot, 1251);
        var nameSlot = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(nameSlot, 0x60); // third allocation

        var pc = PropertyContext.Read(new FakeNode(BuildPc(
            [(0x3FFD, 0x0003, codepageSlot), (0x3001, 0x001E, nameSlot)],
            [name])));

        Assert.Equal("Привет, мир", pc.Find(0x3001)!.AsString());
        Assert.Equal("windows-1251", pc.String8!.WebName);
    }

    [Fact]
    public void AContextWithoutItsOwnCodePageInheritsItsMessages()
    {
        var cp1251 = PstCodePage.Resolve(1251)!;
        var name = cp1251.GetBytes("Вложение");
        var nameSlot = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(nameSlot, 0x60);

        var block = BuildPc([(0x3707, 0x001E, nameSlot)], [name]);

        Assert.Equal("Вложение", PropertyContext.Read(new FakeNode(block), cp1251).Find(0x3707)!.AsString());
        Assert.NotEqual("Вложение", PropertyContext.Read(new FakeNode(block)).Find(0x3707)!.AsString());
    }
}
