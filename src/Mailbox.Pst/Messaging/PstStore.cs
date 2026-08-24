using Mailbox.Pst.Ltp;
using Mailbox.Pst.Ndb;

namespace Mailbox.Pst.Messaging;

/// <summary>
/// The messaging layer's front door: the message store object of NID 0x21, which every PST
/// carries, and the way from it to the folder tree.
/// </summary>
/// <remarks>
/// The interesting resolution is the mail root. The store names the "IPM subtree" — the folder
/// under which everything a mail program shows lives — by EntryID, a 24-byte structure whose
/// last four bytes are the folder's NID and whose middle sixteen are the store's own record key.
/// The key is compared before the NID is believed ([MS-PST] §2.4.3.2): an EntryID from a
/// different file names a node this file also happens to have, and following it silently would
/// hand back somebody else's folder. The root folder object of NID 0x122 always exists and is
/// the fallback for a file whose store is missing the pointer.
/// </remarks>
public sealed class PstStore
{
    private readonly PstFile _file;
    private readonly PropertyContext _properties;

    /// <summary>The node ids the format fixes ([MS-PST] §2.4.1).</summary>
    public static readonly Nid MessageStoreNid = new(0x21);
    public static readonly Nid NameToIdMapNid = new(0x61);
    public static readonly Nid RootFolderNid = new(0x122);

    public string DisplayName { get; }

    /// <summary>The store's own sixteen-byte uid — what its EntryIDs carry, and a stable name for the file's contents.</summary>
    public byte[] RecordKey => _properties.Find(Pid.RecordKey)?.Raw ?? [];

    private PstStore(PstFile file, PropertyContext properties, string displayName)
    {
        _file = file;
        _properties = properties;
        DisplayName = displayName;
    }

    public static PstStore Open(PstFile file)
    {
        var node = file.Node(MessageStoreNid)
            ?? throw new PstException("The file has no message store node, which every PST carries: it is damaged or empty.");

        var properties = PropertyContext.Read(node);
        var name = properties.Find(Pid.DisplayName)?.AsString() ?? string.Empty;
        return new PstStore(file, properties, name);
    }

    /// <summary>The root folder object — the true top of the tree, above what a mail program shows.</summary>
    public PstFolder RootFolder =>
        PstFolder.Open(_file, RootFolderNid)
        ?? throw new PstException("The file has no root folder node, which every PST carries: it is damaged.");

    /// <summary>
    /// The folder a mail program treats as the top — "Top of Personal Folders" and its
    /// translations — or the root folder itself when the store does not point to one.
    /// </summary>
    public PstFolder MailRoot =>
        FolderByEntryId(_properties.Find(Pid.IpmSubTreeEntryId)) ?? OstSubtree() ?? RootFolder;

    /// <summary>
    /// The 4K layout's answer when the store carries no subtree pointer at all, which real OST
    /// files do: the mail lives under a folder literally named IPM_SUBTREE — a MAPI constant,
    /// not display text, so the name is safe to look for — and the file keeps an empty second
    /// one for public folders, which is why the populated one wins.
    /// </summary>
    private PstFolder? OstSubtree()
    {
        if (_file.Format != Ndb.PstFormat.Unicode4K) return null;

        PstFolder? best = null;
        var bestChildren = 0;
        foreach (var branch in RootFolder.Subfolders())
        {
            foreach (var candidate in new[] { branch }.Concat(branch.Subfolders()))
            {
                if (!string.Equals(candidate.Name, "IPM_SUBTREE", StringComparison.OrdinalIgnoreCase)) continue;
                var children = candidate.Subfolders().Count();
                if (children > bestChildren)
                {
                    best = candidate;
                    bestChildren = children;
                }
            }
        }

        return best;
    }

    private PstFolder? FolderByEntryId(PstProperty? entryId)
    {
        if (entryId is null || entryId.Raw.Length < 24) return null;

        // The sixteen bytes in the middle are the store's uid; a mismatch means the EntryID
        // belongs to some other file and its NID means nothing here. The 4K layout is exempt:
        // real OST files stamp their EntryIDs with a mapping uid that is not the record key,
        // and the check that protects a PST from a foreign pointer just blinds an OST to its
        // own — the target still has to be a real folder in this file either way.
        var recordKey = _properties.Find(Pid.RecordKey)?.Raw;
        if (_file.Format != Ndb.PstFormat.Unicode4K
            && recordKey is { Length: 16 } && !recordKey.AsSpan().SequenceEqual(entryId.Raw.AsSpan(4, 16)))
        {
            return null;
        }

        var nid = new Nid(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entryId.Raw.AsSpan(20)));
        return PstFolder.Open(_file, nid);
    }
}
