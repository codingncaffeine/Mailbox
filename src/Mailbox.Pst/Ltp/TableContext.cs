using System.Buffers.Binary;
using Mailbox.Pst.Ndb;

namespace Mailbox.Pst.Ltp;

/// <summary>One column of a table ([MS-PST] §2.3.4.2): its property tag, and where in a row its cell sits.</summary>
internal readonly record struct TableColumn(uint Tag, ushort Offset, byte Width, byte ExistenceBit)
{
    public ushort PropertyId => (ushort)(Tag >> 16);

    public PstPropertyType PropertyType => (PstPropertyType)(ushort)Tag;
}

/// <summary>
/// A Table Context ([MS-PST] §2.3.4): a node read as rows of columns — folder listings,
/// recipient tables, attachment tables.
/// </summary>
/// <remarks>
/// Three structures cooperate: the TCINFO header defines the columns and the row width, the
/// RowIndex (a BTree-on-heap) maps each row's 32-bit id to its position, and the Row Matrix
/// holds the packed rows. Two of the matrix's rules do all the damage when missed: rows never
/// straddle blocks, so each 8,192-byte block holds a whole number of rows and dead space the
/// reader must skip; and a cell is only real if its bit stands in the row's Cell Existence
/// Block — the space is reserved either way, so reading a cleared cell reads plausible garbage.
/// Values wider than eight bytes, and every variable-size value, sit behind an HNID exactly as
/// they would in a property context; the eight-byte inline allowance is the one place the two
/// diverge (§2.3.4.4.2).
/// </remarks>
internal sealed class TableContext
{
    private readonly ILtpNode _node;
    private readonly HeapNode _heap;
    private readonly byte[][] _rowBlocks;
    private readonly int _rowsPerBlock;
    private readonly int _rowWidth;
    private readonly int _bitmapAt;
    private readonly List<(uint RowId, uint RowIndex)> _index;

    public IReadOnlyList<TableColumn> Columns { get; }

    public int RowCount => _index.Count;

    public const byte ClientSignature = 0x7C;

    private TableContext(ILtpNode node, HeapNode heap, byte[][] rowBlocks, int rowsPerBlock, int rowWidth,
        int bitmapAt, List<(uint, uint)> index, IReadOnlyList<TableColumn> columns)
    {
        _node = node;
        _heap = heap;
        _rowBlocks = rowBlocks;
        _rowsPerBlock = rowsPerBlock;
        _rowWidth = rowWidth;
        _bitmapAt = bitmapAt;
        _index = index;
        Columns = columns;
    }

    public static TableContext Read(ILtpNode node)
    {
        var heap = HeapNode.Parse(node.DataBlocks(), node.Nid);
        if (heap.ClientSignature != ClientSignature)
            throw new PstException(
                $"Node {node.Nid} was read as a table and its heap says 0x{heap.ClientSignature:X2} where 0x{ClientSignature:X2} belongs.");

        var info = heap.Item(heap.UserRoot).Span;
        if (info.Length < 22 || info[0] != ClientSignature)
            throw new PstException($"Node {node.Nid} does not hold a table header where its heap's user root points.");

        int columnCount = info[1];

        // rgib: the four group-end offsets — 4-byte data, 2-byte data, 1-byte data, and the
        // cell existence bitmap, whose end is the row's whole width.
        var bitmapAt = BinaryPrimitives.ReadUInt16LittleEndian(info[6..]);
        var rowWidth = BinaryPrimitives.ReadUInt16LittleEndian(info[8..]);
        var rowIndexAt = new HeapId(BinaryPrimitives.ReadUInt32LittleEndian(info[10..]));
        var rows = new Hnid(BinaryPrimitives.ReadUInt32LittleEndian(info[14..]));

        if (info.Length < 22 + columnCount * 8)
            throw new PstException($"Node {node.Nid} declares {columnCount} table columns, which do not fit in its header.");
        if (rowWidth < 4 || bitmapAt > rowWidth)
            throw new PstException($"Node {node.Nid} declares table rows {rowWidth} bytes wide with the existence bitmap at {bitmapAt}, which cannot be.");

        var columns = new TableColumn[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var at = info[(22 + i * 8)..];
            columns[i] = new TableColumn(
                BinaryPrimitives.ReadUInt32LittleEndian(at),
                BinaryPrimitives.ReadUInt16LittleEndian(at[4..]),
                at[6], at[7]);
        }

        // The RowIndex: row id → zero-based position in the matrix. Its data half is four bytes
        // in the Unicode layout and two in the ANSI one — the one place a TC differs by format.
        var indexTree = BTreeOnHeap.Parse(heap, rowIndexAt, node.Nid);
        if (indexTree.KeySize != 4)
            throw new PstException($"Node {node.Nid}'s table keeps a row index of {indexTree.KeySize}-byte keys where 4 belong.");

        var index = new List<(uint, uint)>();
        foreach (var record in indexTree.Records())
        {
            var span = record.Span;
            var rowId = BinaryPrimitives.ReadUInt32LittleEndian(span);
            var rowIndex = indexTree.DataSize >= 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(span[4..])
                : BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            index.Add((rowId, rowIndex));
        }

        index.Sort((left, right) => left.Item2.CompareTo(right.Item2));

        // The Row Matrix: in the heap as one run when small, in a subnode's blocks when not.
        byte[][] rowBlocks;
        int rowsPerBlock;
        if (rows.IsZero)
        {
            rowBlocks = [];
            rowsPerBlock = 1;
        }
        else if (rows.IsHeap)
        {
            rowBlocks = [heap.Item(rows.AsHeapId).ToArray()];
            rowsPerBlock = rowBlocks[0].Length / rowWidth;
        }
        else
        {
            var subnode = node.Subnode(rows.AsNid)
                ?? throw new PstException($"Node {node.Nid} keeps its table rows in subnode {rows.AsNid}, which its subnode tree does not hold.");
            rowBlocks = [.. subnode.DataBlocks()];

            // §2.3.4.4: every block but the last is exactly 8,192 bytes on disk, so the rows per
            // block come from the usable size of a full block, not from any one block's length.
            rowsPerBlock = (8192 - BlockTrailer.Size(node.Format)) / rowWidth;
        }

        if (rowsPerBlock < 1) rowsPerBlock = 1;
        return new TableContext(node, heap, rowBlocks, rowsPerBlock, rowWidth, bitmapAt, index, columns);
    }

    /// <summary>The rows in matrix order, each a view that answers by property id.</summary>
    public IEnumerable<TableRow> Rows()
    {
        foreach (var (rowId, rowIndex) in _index)
        {
            var block = (int)(rowIndex / _rowsPerBlock);
            var within = (int)(rowIndex % _rowsPerBlock);
            if (block >= _rowBlocks.Length || (within + 1) * _rowWidth > _rowBlocks[block].Length)
                throw new PstException($"Node {_node.Nid}'s table index places row 0x{rowId:X} at position {rowIndex}, outside the row matrix.");

            var row = _rowBlocks[block].AsMemory(within * _rowWidth, _rowWidth);
            var stated = BinaryPrimitives.ReadUInt32LittleEndian(row.Span);
            if (stated != rowId)
                throw new PstException($"Node {_node.Nid}'s table row at position {rowIndex} says it is 0x{stated:X} where the index says 0x{rowId:X}.");

            yield return new TableRow(this, rowId, row);
        }
    }

    /// <summary>One row. The cell for a column, honouring the existence bitmap, or null when the cell is not set.</summary>
    public sealed class TableRow
    {
        private readonly TableContext _table;
        private readonly ReadOnlyMemory<byte> _row;

        public uint RowId { get; }

        internal TableRow(TableContext table, uint rowId, ReadOnlyMemory<byte> row)
        {
            _table = table;
            _row = row;
            RowId = rowId;
        }

        public PstProperty? Property(ushort propertyId)
        {
            foreach (var column in _table.Columns)
            {
                if (column.PropertyId == propertyId)
                    return Cell(column);
            }

            return null;
        }

        public IEnumerable<PstProperty> Properties()
        {
            foreach (var column in _table.Columns)
            {
                if (Cell(column) is { } property) yield return property;
            }
        }

        private PstProperty? Cell(TableColumn column)
        {
            // The existence bit walks most-significant-first: iBit 0 is the top bit of byte 0.
            var bitByte = column.ExistenceBit / 8;
            var span = _row.Span;
            if (_table._bitmapAt + bitByte >= span.Length) return null;
            if ((span[_table._bitmapAt + bitByte] & (1 << (7 - column.ExistenceBit % 8))) == 0) return null;

            if (column.Offset + column.Width > span.Length) return null;
            var cell = span.Slice(column.Offset, column.Width);
            var type = column.PropertyType;

            // §2.3.4.4.2: up to eight fixed bytes live in the row; everything else is an HNID
            // resolved exactly as a property context resolves one.
            if (PstProperty.FixedSize(type) is { } size && (((ushort)type) & PstProperty.MultiValuedFlag) == 0 && size <= 8)
                return new PstProperty(column.PropertyId, type, cell[..Math.Min(size, cell.Length)].ToArray());

            var hnid = new Hnid(BinaryPrimitives.ReadUInt32LittleEndian(cell));
            if (hnid.IsZero) return new PstProperty(column.PropertyId, type, []);
            if (hnid.IsHeap) return new PstProperty(column.PropertyId, type, _table._heap.Item(hnid.AsHeapId).ToArray());

            var subnode = _table._node.Subnode(hnid.AsNid)
                ?? throw new PstException(
                    $"Node {_table._node.Nid} keeps a table cell in subnode {hnid.AsNid}, which its subnode tree does not hold.");
            return new PstProperty(column.PropertyId, type, subnode.Data());
        }
    }
}
