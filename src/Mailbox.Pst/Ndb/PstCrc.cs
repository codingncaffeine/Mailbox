namespace Mailbox.Pst.Ndb;

/// <summary>
/// The two integrity numbers every page and block carries ([MS-PST] §5.3, §5.5).
/// </summary>
/// <remarks>
/// The CRC is the ordinary reflected CRC-32 table walk with two differences from the everyday
/// checksum: it starts from zero and is never inverted, so it matches neither zlib nor anything
/// else off the shelf and has to be computed here. The table is the one printed in §5.3
/// (<c>CrcTableOffset32</c>); it is generated rather than transcribed because it is exactly the
/// standard CRC-32 table, and the specification's own worked examples hold the proof — every
/// sample header and page trailer CRC in the fixtures reproduces from this code. The seven other
/// tables in §5.3 belong to its slicing optimisation and change no answers.
/// </remarks>
internal static class PstCrc
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var crc = n;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            table[n] = crc;
        }

        return table;
    }

    /// <summary>The CRC of a run of bytes, seeded at zero as §5.3 requires everywhere in this format.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0u;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>
    /// The page or block signature ([MS-PST] §5.5): the file offset XORed with the block id,
    /// truncated to 32 bits, then folded to 16. Cheap, and enough to catch a block that was
    /// copied to the wrong place — the offset is an input, so the same bytes elsewhere fail.
    /// </summary>
    public static ushort BlockSignature(ulong ib, ulong bid)
    {
        var folded = (uint)(ib ^ bid);
        return (ushort)((folded >> 16) ^ folded);
    }
}
