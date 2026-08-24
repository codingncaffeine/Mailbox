using Mailbox.Pst;
using Mailbox.Pst.Ndb;

namespace Mailbox.Tests;

/// <summary>
/// The NDB layer against [MS-PST]'s own worked examples. Every assertion here restates a number
/// printed in the specification's §3 — parsing one of its dumps and reproducing its annotated
/// values, its CRCs and its signatures is the closest thing to being checked by the format's
/// author that an implementation with no reference build can get.
/// </summary>
public class PstSpecExampleTests
{
    [Fact]
    public void SampleHeaderParsesToItsAnnotatedValues()
    {
        // Parsing at all means both header CRCs reproduced — §3.2's dwCRCPartial 0x379AA90E and
        // dwCRCFull 0x1FD283D6 are checked against the bytes before anything is returned.
        var header = PstHeader.Parse(PstSpecExamples.Header());

        Assert.Equal(PstFormat.Unicode, header.Format);
        Assert.Equal(23, header.Version);
        Assert.Equal(PstCryptMethod.Permute, header.CryptMethod);
        Assert.Equal(0x9F2400UL, header.Root.FileEof);
        Assert.Equal(new Bref(new Bid(0x24B), 0x905200), header.Root.NodeBTree);
        Assert.Equal(new Bref(new Bid(0x253), 0x900A00), header.Root.BlockBTree);
    }

    [Fact]
    public void ADamagedHeaderIsRefusedByItsOwnCrc()
    {
        var bytes = PstSpecExamples.Header();
        bytes[0x30] ^= 0x01;

        var refusal = Assert.Throws<PstException>(() => PstHeader.Parse(bytes));
        Assert.Contains("CRC", refusal.Message);
    }

    [Fact]
    public void SampleIntermediatePageParsesToItsAnnotatedValues()
    {
        // §3.3: three BTENTRYs of 0x18 bytes on an NBT page of level 1, at 0x8200 as block 0x206.
        var page = BTreePage.Read(PstSpecExamples.IntermediatePage(),
            new Bref(new Bid(0x206), 0x8200), PstFormat.Unicode);

        Assert.Equal(PageType.NodeBTree, page.Type);
        Assert.Equal(1, page.Level);
        Assert.Equal(3, page.EntryCount);
        Assert.Equal(0x18, page.EntryStride);
        Assert.Equal(new BtEntry(0x21, new Bref(new Bid(0x205), 0x7E00)), page.Intermediate(0, PstFormat.Unicode));
        Assert.Equal(new BtEntry(0x60F, new Bref(new Bid(0x141), 0x7000)), page.Intermediate(1, PstFormat.Unicode));
        Assert.Equal(new BtEntry(0x8022, new Bref(new Bid(0xFD), 0x8400)), page.Intermediate(2, PstFormat.Unicode));
    }

    [Fact]
    public void SampleLeafNodePageReadsItsEntriesOnTheStatedStride()
    {
        // §3.4's own point: the entries are 28 bytes but cbEnt says 32, and a reader that walks
        // the natural size instead of the stride shears off the page after a few entries.
        var page = BTreePage.Read(PstSpecExamples.LeafNodePage(),
            new Bref(new Bid(0x6B), 0x7000), PstFormat.Unicode);

        Assert.Equal(PageType.NodeBTree, page.Type);
        Assert.Equal(0, page.Level);
        Assert.Equal(0x0E, page.EntryCount);
        Assert.Equal(0x20, page.EntryStride);

        Assert.Equal(new NbtEntry(new Nid(0x60F), new Bid(0x0C), default, default), page.Node(0, PstFormat.Unicode));

        // The eleventh entry is the first with a parent folder recorded (NID 0x122).
        var folderChild = page.Node(10, PstFormat.Unicode);
        Assert.Equal(new Nid(0x8022), folderChild.Nid);
        Assert.Equal(new Bid(0x54), folderChild.Data);
        Assert.Equal(new Nid(0x122), folderChild.Parent);
        Assert.Equal(NidType.NormalFolder, folderChild.Nid.Type);
    }

    [Fact]
    public void SampleLeafBlockPageTrailerReproducesItsSignatureButNotItsCrc()
    {
        // §3.5 is the specification's one defective sample: it zero-fills the page's unused
        // space for print, and its own note — "the unused space can contain any value, as long
        // as the dwCRC ... match its contents" — is exactly the condition the zero-filling
        // breaks. The trailer's fields and its offset-folding signature still reproduce, and a
        // reader given the page as printed must refuse it, which is asserted rather than worked
        // around. Leaf BBT parsing itself is proven by the corpus files, where every block
        // lookup crosses a real one.
        var page = PstSpecExamples.LeafBlockPage();
        var trailer = PageTrailer.Read(page, PstFormat.Unicode);

        Assert.Equal(PageType.BlockBTree, trailer.Type);
        Assert.Equal(new Bid(0x246), trailer.Bid);
        Assert.Equal(0xA1F6A02F, trailer.Crc);
        Assert.Equal(PstCrc.BlockSignature(0x900200, 0x246), trailer.Signature);

        var refusal = Assert.Throws<PstException>(() =>
            BTreePage.Read(page, new Bref(new Bid(0x246), 0x900200), PstFormat.Unicode));
        Assert.Contains("CRC", refusal.Message);
    }

    [Fact]
    public void ADamagedPageIsRefusedByItsOwnCrc()
    {
        var bytes = PstSpecExamples.LeafNodePage();
        bytes[0x10] ^= 0x01;

        var refusal = Assert.Throws<PstException>(() =>
            BTreePage.Read(bytes, new Bref(new Bid(0x6B), 0x7000), PstFormat.Unicode));
        Assert.Contains("CRC", refusal.Message);
    }

    [Fact]
    public void APageReadFromTheWrongOffsetFailsItsSignature()
    {
        // The signature folds the offset in, which is exactly what catches a stale reference
        // pointing at a page that was itself copied intact.
        var refusal = Assert.Throws<PstException>(() =>
            BTreePage.Read(PstSpecExamples.LeafNodePage(), new Bref(new Bid(0x6B), 0x7200), PstFormat.Unicode));
        Assert.Contains("signature", refusal.Message);
    }

    [Fact]
    public void SampleDataTreeParsesToItsAnnotatedValues()
    {
        // §3.6: an XBLOCK of 0x35 children totalling 0x69C49 bytes, at 0x5A6600 as block 0x162.
        var block = PstSpecExamples.XBlock();
        var trailer = BlockTrailer.Read(block.AsSpan(^16..), PstFormat.Unicode);

        Assert.Equal(0x1B0, trailer.Length);
        Assert.Equal(new Bid(0x162), trailer.Bid);
        Assert.Equal(PstCrc.BlockSignature(0x5A6600, 0x162), trailer.Signature);
        Assert.Equal(trailer.Crc, PstCrc.Compute(block.AsSpan(0, trailer.Length)));

        var (level, total, children) = InternalBlocks.ReadDataTree(
            block.AsSpan(0, trailer.Length), new Bid(0x162), PstFormat.Unicode);

        Assert.Equal(1, level);
        Assert.Equal(0x69C49u, total);
        Assert.Equal(0x35, children.Count);
        Assert.Equal(new Bid(0x15C), children[0]);
        Assert.Equal(new Bid(0x230), children[^1]);
    }

    [Fact]
    public void SampleSubnodeBlockParsesToItsAnnotatedValues()
    {
        // §3.7: the smallest possible SLBLOCK — one entry in 64 bytes, at 0x594D80 as block 0x1386.
        var block = PstSpecExamples.SlBlock();
        var trailer = BlockTrailer.Read(block.AsSpan(^16..), PstFormat.Unicode);

        Assert.Equal(0x20, trailer.Length);
        Assert.Equal(new Bid(0x1386), trailer.Bid);
        Assert.Equal(PstCrc.BlockSignature(0x594D80, 0x1386), trailer.Signature);
        Assert.Equal(trailer.Crc, PstCrc.Compute(block.AsSpan(0, trailer.Length)));

        var (level, entries, children) = InternalBlocks.ReadSubnodeBlock(
            block.AsSpan(0, trailer.Length), new Bid(0x1386), PstFormat.Unicode);

        Assert.Equal(0, level);
        Assert.Empty(children);
        var entry = Assert.Single(entries);
        Assert.Equal(new SlEntry(new Nid(0x817F), new Bid(0x1380), default), entry);
    }

    [Fact]
    public void PermutativeEncodingRoundTrips()
    {
        var data = new byte[256];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;
        var original = data.ToArray();

        BlockEncoding.EncodePermute(data);
        Assert.NotEqual(original, data);
        BlockEncoding.DecodePermute(data);
        Assert.Equal(original, data);
    }

    [Fact]
    public void CyclicEncodingIsItsOwnInverse()
    {
        var data = new byte[300];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
        var original = data.ToArray();

        BlockEncoding.Cyclic(data, 0x1234_5678);
        Assert.NotEqual(original, data);
        BlockEncoding.Cyclic(data, 0x1234_5678);
        Assert.Equal(original, data);
    }
}
