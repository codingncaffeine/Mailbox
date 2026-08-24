using System.Buffers.Binary;
using System.Text;

namespace Mailbox.Pst.Cfb;

/// <summary>One directory entry ([MS-CFB] §2.6.1): a storage, a stream, or the root.</summary>
internal sealed record CfbEntry(int Id, string Name, byte Type, uint Child, uint LeftSibling, uint RightSibling,
    uint StartSector, long Length)
{
    public const byte Storage = 0x01;
    public const byte Stream = 0x02;
    public const byte Root = 0x05;
}

/// <summary>
/// A compound file ([MS-CFB]): the container .msg files — and half of the last thirty years of
/// Windows documents — are built on. A FAT of sector chains, a directory of storages and
/// streams, and a mini stream inside the root for anything under 4,096 bytes.
/// </summary>
/// <remarks>
/// Read entirely from memory: a .msg is one message and fits. Every chain walk is bounded by
/// the count of sectors the file can hold, so a cyclic FAT ends in a refusal instead of a spin;
/// the same parser posture as the PST side, this being the same job — somebody else's binary
/// format on the way in (§19).
/// </remarks>
internal sealed class CompoundFile
{
    private readonly byte[] _bytes;
    private readonly int _sectorSize;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly List<CfbEntry> _entries;

    public CfbEntry Root => _entries[0];

    private CompoundFile(byte[] bytes, int sectorSize, uint[] fat, uint[] miniFat, byte[] miniStream, List<CfbEntry> entries)
    {
        _bytes = bytes;
        _sectorSize = sectorSize;
        _fat = fat;
        _miniFat = miniFat;
        _miniStream = miniStream;
        _entries = entries;
    }

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint MaxRegular = 0xFFFFFFFA;
    private const uint NoStream = 0xFFFFFFFF;

    public static CompoundFile Parse(byte[] bytes)
    {
        if (bytes.Length < 512 || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != 0xE11AB1A1E011CFD0)
            throw new PstException("The file does not begin with a compound file's signature, so it is not one.");

        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x1A));
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x1E));
        if (major is not (3 or 4) || sectorShift is not (9 or 12))
            throw new PstException($"The compound file names version {major} with sector shift {sectorShift}, which [MS-CFB] does not define.");

        var sectorSize = 1 << sectorShift;
        var sectorCount = bytes.Length / sectorSize + 1;

        // The DIFAT locates the FAT: 109 entries in the header, the rest in their own chain.
        var fatSectors = new List<uint>();
        for (var i = 0; i < 109; i++)
        {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x4C + i * 4));
            if (sector <= MaxRegular) fatSectors.Add(sector);
        }

        var difat = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x44));
        for (var hops = 0; difat <= MaxRegular; hops++)
        {
            if (hops > sectorCount)
                throw new PstException("The compound file's DIFAT chain is longer than the file: it loops.");

            var sector = SectorSpan(bytes, sectorSize, difat);
            for (var i = 0; i < sectorSize / 4 - 1; i++)
            {
                var location = BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]);
                if (location <= MaxRegular) fatSectors.Add(location);
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector[^4..]);
        }

        var fat = new uint[fatSectors.Count * (sectorSize / 4)];
        for (var i = 0; i < fatSectors.Count; i++)
        {
            var sector = SectorSpan(bytes, sectorSize, fatSectors[i]);
            for (var j = 0; j < sectorSize / 4; j++)
                fat[i * (sectorSize / 4) + j] = BinaryPrimitives.ReadUInt32LittleEndian(sector[(j * 4)..]);
        }

        // The directory chain, walked flat; entry 0 is the root.
        var directoryStart = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x30));
        var directoryBytes = ReadChain(bytes, sectorSize, fat, directoryStart, long.MaxValue, sectorCount);
        var entries = new List<CfbEntry>(directoryBytes.Length / 128);
        for (var at = 0; at + 128 <= directoryBytes.Length; at += 128)
        {
            var entry = directoryBytes.AsSpan(at, 128);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[64..]);
            var name = nameLength >= 2 && nameLength <= 64
                ? Encoding.Unicode.GetString(entry[..(nameLength - 2)])
                : string.Empty;

            entries.Add(new CfbEntry(
                entries.Count, name, entry[66],
                BinaryPrimitives.ReadUInt32LittleEndian(entry[76..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[68..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[72..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[116..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[120..])));
        }

        if (entries.Count == 0 || entries[0].Type != CfbEntry.Root)
            throw new PstException("The compound file's directory does not begin with its root entry: the file is damaged.");

        // Version 3 writers leave garbage in the size's high half; the root's own size bounds it.
        entries[0] = entries[0] with { Length = Math.Min(entries[0].Length, bytes.Length) };

        // The mini FAT, and the mini stream it allocates — the root entry's own chain.
        var miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x3C));
        var miniFatBytes = miniFatStart <= MaxRegular
            ? ReadChain(bytes, sectorSize, fat, miniFatStart, long.MaxValue, sectorCount)
            : [];
        var miniFat = new uint[miniFatBytes.Length / 4];
        for (var i = 0; i < miniFat.Length; i++)
            miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));

        var miniStream = entries[0].StartSector <= MaxRegular
            ? ReadChain(bytes, sectorSize, fat, entries[0].StartSector, entries[0].Length, sectorCount)
            : [];

        return new CompoundFile(bytes, sectorSize, fat, miniFat, miniStream, entries);
    }

    private static ReadOnlySpan<byte> SectorSpan(byte[] bytes, int sectorSize, uint sector)
    {
        var offset = (long)(sector + 1) * sectorSize;
        if (offset + sectorSize > bytes.Length)
            throw new PstException($"The compound file points at sector {sector}, which lies past its own end.");
        return bytes.AsSpan((int)offset, sectorSize);
    }

    private static byte[] ReadChain(byte[] bytes, int sectorSize, uint[] fat, uint start, long limit, int sectorCount)
    {
        using var chain = new MemoryStream();
        var sector = start;
        for (var hops = 0; sector <= MaxRegular; hops++)
        {
            if (hops > sectorCount)
                throw new PstException("A sector chain in the compound file is longer than the file: it loops.");

            chain.Write(SectorSpan(bytes, sectorSize, sector));
            if (sector >= fat.Length)
                throw new PstException($"The compound file's FAT has no entry for sector {sector}: the file is damaged.");
            sector = fat[sector];
        }

        if (sector != EndOfChain && sector != FreeSector)
            throw new PstException($"A sector chain ends on 0x{sector:X8} where an end-of-chain marker belongs.");

        var result = chain.ToArray();
        return limit < result.Length ? result[..(int)limit] : result;
    }

    /// <summary>The bytes of a stream entry — from the mini stream under the cutoff, sector chains above it.</summary>
    public byte[] ReadStream(CfbEntry entry)
    {
        if (entry.Type != CfbEntry.Stream)
            throw new PstException($"“{entry.Name}” is not a stream and cannot be read as one.");
        if (entry.Length == 0 || entry.StartSector > MaxRegular) return [];

        if (entry.Length >= 4096)
            return ReadChain(_bytes, _sectorSize, _fat, entry.StartSector, entry.Length, _bytes.Length / _sectorSize + 1);

        // Mini sectors are 64 bytes inside the mini stream, chained by the mini FAT.
        using var data = new MemoryStream();
        var sector = entry.StartSector;
        for (var hops = 0; sector <= MaxRegular; hops++)
        {
            if (hops > _miniStream.Length / 64 + 1)
                throw new PstException("A mini-stream chain in the compound file is longer than the mini stream: it loops.");

            var offset = (long)sector * 64;
            if (offset + 64 > _miniStream.Length)
                throw new PstException($"The compound file points at mini sector {sector}, past the mini stream's end.");
            data.Write(_miniStream.AsSpan((int)offset, 64));

            if (sector >= _miniFat.Length)
                throw new PstException($"The compound file's mini FAT has no entry for sector {sector}: the file is damaged.");
            sector = _miniFat[sector];
        }

        var result = data.ToArray();
        return entry.Length < result.Length ? result[..(int)entry.Length] : result;
    }

    /// <summary>
    /// The children of a storage, by walking its red-black tree flat — order is not preserved
    /// and does not matter, the names carrying all the meaning in a .msg file.
    /// </summary>
    public IReadOnlyList<CfbEntry> Children(CfbEntry parent)
    {
        var children = new List<CfbEntry>();
        var seen = new HashSet<uint>();
        var stack = new Stack<uint>();
        if (parent.Child != NoStream) stack.Push(parent.Child);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id >= _entries.Count || !seen.Add(id))
                continue;

            var entry = _entries[(int)id];
            if (entry.Type is CfbEntry.Storage or CfbEntry.Stream) children.Add(entry);
            if (entry.LeftSibling != NoStream) stack.Push(entry.LeftSibling);
            if (entry.RightSibling != NoStream) stack.Push(entry.RightSibling);
        }

        return children;
    }

    /// <summary>A child by name — the uppercase comparison [MS-CFB] §2.6.4 prescribes.</summary>
    public CfbEntry? Find(CfbEntry parent, string name) =>
        Children(parent).FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
}
