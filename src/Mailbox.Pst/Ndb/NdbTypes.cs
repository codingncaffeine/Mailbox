namespace Mailbox.Pst.Ndb;

/// <summary>
/// Which of the two on-disk layouts the file uses. Every fixed structure in the format has two
/// widths: ANSI keeps 32-bit block ids and file offsets, Unicode 64-bit ones.
/// </summary>
public enum PstFormat
{
    /// <summary>32-bit layout (wVer 14 or 15). The name is the specification's, not an encoding claim.</summary>
    Ansi,

    /// <summary>64-bit layout (wVer 23).</summary>
    Unicode,

    /// <summary>
    /// The 64-bit layout on 4096-byte pages (wVer 36 and 37) — what cached-mode OST files use.
    /// Outside [MS-PST]; read best-effort from the libpff project's documentation and the real
    /// files themselves. Same entry shapes as Unicode; bigger pages, bigger blocks, and block
    /// data that can arrive zlib-compressed.
    /// </summary>
    Unicode4K,
}

/// <summary>The one distinction most of the reader cares about: 32-bit fields or 64-bit ones.</summary>
public static class PstFormatExtensions
{
    /// <summary>True for both 64-bit layouts — ANSI is the narrow one everywhere.</summary>
    public static bool IsWide(this PstFormat format) => format != PstFormat.Ansi;
}

/// <summary>How the data blocks are obfuscated ([MS-PST] §2.2.2.8.3.1.1 — the header names it once for the whole file).</summary>
public enum PstCryptMethod : byte
{
    None = 0x00,
    Permute = 0x01,
    Cyclic = 0x02,

    /// <summary>Encrypted with Windows Information Protection. Actual encryption, not obfuscation — unreadable without the key.</summary>
    EdpCrypted = 0x10,
}

/// <summary>The five bits of a NID that say what kind of thing the node is ([MS-PST] §2.2.2.1).</summary>
public enum NidType : byte
{
    Hid = 0x00,
    Internal = 0x01,
    NormalFolder = 0x02,
    SearchFolder = 0x03,
    NormalMessage = 0x04,
    Attachment = 0x05,
    SearchUpdateQueue = 0x06,
    SearchCriteria = 0x07,
    AssocMessage = 0x08,
    ContentsTableIndex = 0x0A,
    ReceiveFolderTable = 0x0B,
    OutgoingQueueTable = 0x0C,
    HierarchyTable = 0x0D,
    ContentsTable = 0x0E,
    AssocContentsTable = 0x0F,
    SearchContentsTable = 0x10,
    AttachmentTable = 0x11,
    RecipientTable = 0x12,
    SearchTableIndex = 0x13,
    Ltp = 0x1F,
}

/// <summary>
/// A node id ([MS-PST] §2.2.2.1): five type bits under a 27-bit index. Always 32 bits — the
/// Unicode format stores it zero-extended to eight bytes in BTree entries, but the value itself
/// never grows.
/// </summary>
public readonly record struct Nid(uint Value)
{
    public NidType Type => (NidType)(Value & 0x1F);

    public uint Index => Value >> 5;

    public override string ToString() => $"0x{Value:X}";
}

/// <summary>
/// A block id ([MS-PST] §2.2.2.2). The two low bits are flags, which is why block ids advance in
/// fours: bit 0 is reserved and ignored when searching the block BTree, bit 1 says the block is
/// internal — metadata about where data lives rather than the data itself.
/// </summary>
public readonly record struct Bid(ulong Value)
{
    public bool IsInternal => (Value & 0x2) != 0;

    /// <summary>
    /// The value to compare when searching the BBT: the reserved bit is read as zero on both
    /// sides, exactly as §2.2.2.2 instructs a reader to do before the lookup.
    /// </summary>
    public ulong SearchKey => Value & ~0x1UL;

    public bool IsZero => Value == 0;

    public override string ToString() => $"0x{Value:X}";
}

/// <summary>A block id paired with the absolute file offset it lives at ([MS-PST] §2.2.2.4).</summary>
public readonly record struct Bref(Bid Bid, ulong Ib);
