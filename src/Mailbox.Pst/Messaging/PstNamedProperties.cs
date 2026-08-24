using System.Buffers.Binary;
using System.Text;
using Mailbox.Pst.Ltp;

namespace Mailbox.Pst.Messaging;

/// <summary>The property sets named properties live in, as [MS-OXPROPS] §1.3.2 registers them.</summary>
public static class PstPropertySets
{
    public static readonly Guid Mapi = new("00020328-0000-0000-C000-000000000046");
    public static readonly Guid PublicStrings = new("00020329-0000-0000-C000-000000000046");
    public static readonly Guid InternetHeaders = new("00020386-0000-0000-C000-000000000046");
    public static readonly Guid Common = new("00062008-0000-0000-C000-000000000046");
    public static readonly Guid Appointment = new("00062002-0000-0000-C000-000000000046");
    public static readonly Guid Meeting = new("6ED8DA90-450B-101B-98DA-00AA003F1305");
    public static readonly Guid Address = new("00062004-0000-0000-C000-000000000046");
    public static readonly Guid Task = new("00062003-0000-0000-C000-000000000046");
    public static readonly Guid Log = new("0006200A-0000-0000-C000-000000000046");
    public static readonly Guid Note = new("0006200E-0000-0000-C000-000000000046");
}

/// <summary>A named property's name: which set it belongs to, and a number or a string within it.</summary>
public sealed record PstPropertyName(Guid Set, uint? NumericId, string? StringName);

/// <summary>
/// The Name-to-ID map ([MS-PST] §2.4.7): what the property ids from 0x8000 up mean in this
/// particular file. Everything a PST stores about an appointment, a task or a contact's email
/// addresses travels as named properties, so this map is the gate on reading any of them —
/// the same (set, id) name can wear a different 16-bit id in every file.
/// </summary>
/// <remarks>
/// On disk it is a property context at NID 0x61 whose "properties" are really three streams —
/// entries, GUIDs, strings — plus a hash table this reader never touches: the hash exists to
/// make writers fast, and a reader that scans the entry stream once holds the whole answer.
/// A record that points outside its streams is dropped alone rather than failing the map:
/// it costs one property its name, not the file its import.
/// </remarks>
public sealed class PstNamedProperties
{
    private readonly Dictionary<ushort, PstPropertyName> _byId = [];
    private readonly Dictionary<(Guid, uint), ushort> _byNumber = [];
    private readonly Dictionary<(Guid, string), ushort> _byString = [];

    /// <summary>An empty map — what a file with no name node gets, every lookup answering null.</summary>
    public static PstNamedProperties Empty { get; } = new([], [], []);

    public int Count => _byId.Count;

    public static PstNamedProperties Open(PstFile file)
    {
        var node = file.Node(PstStore.NameToIdMapNid);
        if (node is null) return Empty;

        var properties = PropertyContext.Read(node);
        return new PstNamedProperties(
            properties.Find(0x0003)?.Raw ?? [],
            properties.Find(0x0002)?.Raw ?? [],
            properties.Find(0x0004)?.Raw ?? []);
    }

    internal PstNamedProperties(byte[] entryStream, byte[] guidStream, byte[] stringStream)
    {
        // Each NAMEID is eight bytes: the name value, then a word whose low bit says whether
        // the name is a string, over a fifteen-bit GUID index, then the ordinal that makes the
        // property id by adding 0x8000.
        for (var at = 0; at + 8 <= entryStream.Length; at += 8)
        {
            var nameValue = BinaryPrimitives.ReadUInt32LittleEndian(entryStream.AsSpan(at));
            var kindAndGuid = BinaryPrimitives.ReadUInt16LittleEndian(entryStream.AsSpan(at + 4));
            var propertyId = (ushort)(0x8000 + BinaryPrimitives.ReadUInt16LittleEndian(entryStream.AsSpan(at + 6)));

            var guidIndex = kindAndGuid >> 1;
            Guid set;
            if (guidIndex == 0) set = Guid.Empty;
            else if (guidIndex == 1) set = PstPropertySets.Mapi;
            else if (guidIndex == 2) set = PstPropertySets.PublicStrings;
            else
            {
                var offset = (guidIndex - 3) * 16;
                if (offset + 16 > guidStream.Length) continue;
                set = new Guid(guidStream.AsSpan(offset, 16));
            }

            if ((kindAndGuid & 1) == 0)
            {
                _byId[propertyId] = new PstPropertyName(set, nameValue, null);
                _byNumber[(set, nameValue)] = propertyId;
                continue;
            }

            // A string name: the value is the byte offset of a length-prefixed UTF-16 string,
            // Unicode even in an ANSI file.
            if (nameValue + 4 > stringStream.Length) continue;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(stringStream.AsSpan((int)nameValue));
            if (nameValue + 4 + length > (ulong)stringStream.Length) continue;

            var name = Encoding.Unicode.GetString(stringStream.AsSpan((int)(nameValue + 4), (int)length));
            _byId[propertyId] = new PstPropertyName(set, null, name);
            _byString[(set, name)] = propertyId;
        }
    }

    /// <summary>What a property id from 0x8000 up means here, or null for one the map does not know.</summary>
    public PstPropertyName? NameOf(ushort propertyId) => _byId.GetValueOrDefault(propertyId);

    /// <summary>This file's id for a numeric name — [MS-OXPROPS]'s "property long ID" — or null when the file never stored one.</summary>
    public ushort? IdOf(Guid set, uint numericId) =>
        _byNumber.TryGetValue((set, numericId), out var id) ? id : null;

    /// <summary>This file's id for a string name, or null.</summary>
    public ushort? IdOf(Guid set, string name) =>
        _byString.TryGetValue((set, name), out var id) ? id : null;
}
