using Mailbox.Pst.Ltp;
using Mailbox.Pst.Ndb;

namespace Mailbox.Pst.Messaging;

/// <summary>
/// A folder ([MS-PST] §2.4.4): its property context, and the three tables that ride beside it.
/// </summary>
/// <remarks>
/// A folder is four nodes wearing one nidIndex: the folder object itself, and its hierarchy,
/// contents and FAI contents tables, each under its own NID type — which is how this class
/// finds them without being told. The children come from the tables, whose row ids are the
/// children's NIDs; the counts come from the folder's own properties and are the writer's
/// claim, so the walk never trusts them for anything but display.
/// </remarks>
public sealed class PstFolder
{
    private readonly PstFile _file;
    private readonly PropertyContext _properties;

    public Nid Nid { get; }

    public string Name { get; }

    /// <summary>The writer's claimed message count — display information, not a bound the walk relies on.</summary>
    public int ContentCount { get; }

    public int UnreadCount { get; }

    private PstFolder(PstFile file, Nid nid, PropertyContext properties)
    {
        _file = file;
        _properties = properties;
        Nid = nid;
        Name = properties.Find(Pid.DisplayName)?.AsString() ?? string.Empty;
        ContentCount = properties.Find(Pid.ContentCount)?.AsInteger32() ?? 0;
        UnreadCount = properties.Find(Pid.ContentUnreadCount)?.AsInteger32() ?? 0;
    }

    /// <summary>The container class — "IPM.Note" mail, "IPM.Appointment" a calendar, and so on; empty on ordinary mail folders too.</summary>
    public string ContainerClass => _properties.Find(Pid.ContainerClass)?.AsString() ?? string.Empty;

    /// <summary>One of a folder's well-known property values, for what this layer does not interpret.</summary>
    public PstProperty? Property(ushort id) => _properties.Find(id);

    internal static PstFolder? Open(PstFile file, Nid nid)
    {
        if (nid.Type is not (NidType.NormalFolder or NidType.SearchFolder)) return null;
        var node = file.Node(nid);
        return node is null ? null : new PstFolder(file, nid, PropertyContext.Read(node));
    }

    private Nid Sibling(NidType type) => new((Nid.Index << 5) | (uint)type);

    /// <summary>The subfolders, from the hierarchy table's rows — each row id a child folder's NID.</summary>
    public IEnumerable<PstFolder> Subfolders()
    {
        foreach (var rowId in TableRowIds(Sibling(NidType.HierarchyTable)))
        {
            if (Open(_file, new Nid(rowId)) is { } child)
                yield return child;
        }
    }

    /// <summary>The messages, from the contents table's rows.</summary>
    public IEnumerable<PstMessage> Messages() => MessagesOf(Sibling(NidType.ContentsTable));

    /// <summary>
    /// The folder-associated messages — settings, views and rules carried as hidden messages.
    /// An importer reads mail from <see cref="Messages"/>; these exist so nothing is invisible.
    /// </summary>
    public IEnumerable<PstMessage> AssociatedMessages() => MessagesOf(Sibling(NidType.AssocContentsTable));

    private IEnumerable<PstMessage> MessagesOf(Nid table)
    {
        foreach (var rowId in TableRowIds(table))
        {
            var nid = new Nid(rowId);
            if (nid.Type is not (NidType.NormalMessage or NidType.AssocMessage)) continue;
            if (_file.Node(nid) is { } node)
                yield return PstMessage.Open(node);
        }
    }

    /// <summary>
    /// Row ids straight off the table's index, without materialising any cells: opening every
    /// message is the caller's decision, made one message at a time.
    /// </summary>
    private List<uint> TableRowIds(Nid table)
    {
        var node = _file.Node(table);
        if (node is null) return [];

        var rows = new List<uint>();
        foreach (var row in TableContext.Read(node).Rows())
            rows.Add(row.RowId);
        return rows;
    }
}
