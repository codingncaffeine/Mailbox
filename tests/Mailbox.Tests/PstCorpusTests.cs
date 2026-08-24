using Mailbox.Pst;
using Mailbox.Pst.Ltp;
using Mailbox.Pst.Ndb;

namespace Mailbox.Tests;

/// <summary>
/// The NDB layer against real files this project did not write. The corpus is not in the
/// repository — point <c>MAILBOX_PST_CORPUS</c> at a directory of PST files, or keep one at
/// <c>specs/pst-corpus</c> beside the solution; the tests skip when neither exists, the same
/// bargain <c>RealDavTests</c> strikes with its server.
/// </summary>
/// <remarks>
/// The walk is deliberately total: every node's data tree is assembled and every subnode's
/// behind it, transitively, which drags every reachable block through the trailer, signature and
/// CRC checks. A fixture can prove a structure parses; only a file written by the reference
/// implementation proves the assumptions between the structures — which bytes the block CRC
/// covers, which blocks the obfuscation applies to — because getting one wrong fails thousands
/// of checks at once here and none anywhere else.
/// </remarks>
public class PstCorpusTests
{
    private static string? CorpusDirectory()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_PST_CORPUS") is { Length: > 0 } posed)
            return Directory.Exists(posed) ? posed : null;

        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Mailbox.slnx")))
            {
                var corpus = Path.Combine(at.FullName, "specs", "pst-corpus");
                return Directory.Exists(corpus) ? corpus : null;
            }
        }

        return null;
    }

    private static string[] CorpusFiles()
    {
        var directory = CorpusDirectory();
        Assert.SkipWhen(directory is null, "Set MAILBOX_PST_CORPUS, or keep files in specs/pst-corpus, to run against real PST files.");
        var files = Directory.GetFiles(directory!, "*.pst");
        Assert.SkipWhen(files.Length == 0, $"No .pst files in {directory}.");
        return files;
    }

    [Fact]
    public void EveryCorpusFileOpensAndStatesItsShape()
    {
        var layouts = new HashSet<PstFormat>();
        foreach (var path in CorpusFiles())
        {
            using var file = PstFile.Open(path);

            // The classification must agree with the version word read straight off the disk —
            // an independent five-line read, so the parser is not grading its own work.
            var version = new byte[2];
            using var raw = File.OpenRead(path);
            raw.Position = 10;
            raw.ReadExactly(version);
            var expected = version[0] is 14 or 15 && version[1] == 0 ? PstFormat.Ansi : PstFormat.Unicode;

            Assert.Equal(expected, file.Format);
            layouts.Add(file.Format);
        }

        // The known corpus carries both layouts (sample2 and test_ansi are ANSI); a corpus that
        // has quietly lost one of them is not testing half the reader.
        Assert.Equal(2, layouts.Count);
    }

    [Fact]
    public void EveryReachableByteOfEveryCorpusFileVerifies()
    {
        foreach (var path in CorpusFiles())
        {
            using var file = PstFile.Open(path);

            var nodes = 0;
            var subnodes = 0;
            long bytes = 0;

            foreach (var node in file.Nodes())
            {
                nodes++;
                bytes += file.ReadNodeData(node).Length;
                subnodes += WalkSubnodes(file, node.Subnode, ref bytes);
            }

            // Floors, not counts: even a freshly created file carries the minimal-PST node set,
            // and a corpus file with none of its data reachable would sail through a walk that
            // silently visited nothing.
            Assert.True(nodes > 20, $"{Path.GetFileName(path)} yielded only {nodes} nodes.");
            Assert.True(bytes > 10_000, $"{Path.GetFileName(path)} yielded only {bytes} bytes of node data.");
        }
    }

    private static int WalkSubnodes(PstFile file, Bid subnodeTree, ref long bytes)
    {
        var count = 0;
        foreach (var entry in file.Subnodes(subnodeTree))
        {
            count++;
            bytes += file.ReadData(entry.Data).Length;
            count += WalkSubnodes(file, entry.Subnode, ref bytes);
        }

        return count;
    }

    [Fact]
    public void EveryPropertyAndTableInEveryCorpusFileDecodes()
    {
        foreach (var path in CorpusFiles())
        {
            using var file = PstFile.Open(path);

            var contexts = 0;
            var tables = 0;
            var values = 0;

            foreach (var entry in file.Nodes())
                Decode(file.NodeOf(entry), entry.Nid.Type, 0, ref contexts, ref tables, ref values);

            // Floors again: any PST at all carries the store and name-map contexts, a handful
            // of folder contexts, and each folder's three tables.
            Assert.True(contexts >= 5, $"{Path.GetFileName(path)} yielded only {contexts} property contexts.");
            Assert.True(tables >= 3, $"{Path.GetFileName(path)} yielded only {tables} tables.");
            Assert.True(values > 50, $"{Path.GetFileName(path)} yielded only {values} property values.");
        }
    }

    /// <summary>
    /// Reads a node as whatever its id type says it is — property bag or table — and follows a
    /// message's subnodes, where its recipient and attachment tables and each attachment's own
    /// context live. Every value is materialised, strings decoded and multi-values split, so a
    /// wrong offset anywhere fails here rather than lurking.
    /// </summary>
    private static void Decode(PstNode node, NidType type, int depth, ref int contexts, ref int tables, ref int values)
    {
        if (depth > 4) return;

        var isContext = type is NidType.NormalFolder or NidType.SearchFolder or NidType.NormalMessage
            or NidType.AssocMessage or NidType.Attachment
            || node.Nid.Value is 0x21 or 0x61;
        // Search-folder contents tables are left out on purpose: real files write them with the
        // reserved heap signature 0xAC, a shape [MS-PST] does not define — and a search folder
        // is a computed view, not mail an importer would carry over.
        var isTable = type is NidType.HierarchyTable or NidType.ContentsTable or NidType.AssocContentsTable
            or NidType.AttachmentTable or NidType.RecipientTable;

        if (isContext)
        {
            contexts++;
            foreach (var property in PropertyContext.Read(node).Properties.Values)
            {
                values++;
                Materialise(property);
            }
        }
        else if (isTable)
        {
            tables++;
            foreach (var row in TableContext.Read(node).Rows())
            {
                foreach (var property in row.Properties())
                {
                    values++;
                    Materialise(property);
                }
            }
        }
        else
        {
            return;
        }

        if (type is NidType.NormalMessage or NidType.AssocMessage or NidType.Attachment)
        {
            foreach (var subnode in node.Subnodes())
                Decode(subnode, subnode.Nid.Type, depth + 1, ref contexts, ref tables, ref values);
        }
    }

    private static void Materialise(PstProperty property)
    {
        if (property.IsMultiValued)
        {
            foreach (var element in property.Elements()) Materialise(element);
            return;
        }

        switch (property.Type)
        {
            case PstPropertyType.String or PstPropertyType.String8:
                property.AsString();
                break;
            case PstPropertyType.Time:
                property.AsTime();
                break;
            case PstPropertyType.Integer32:
                property.AsInteger32();
                break;
            case PstPropertyType.Boolean:
                property.AsBoolean();
                break;
            default:
                _ = property.Raw.Length;
                break;
        }
    }

    [Fact]
    public void NodeLookupAgreesWithTheFullWalk()
    {
        foreach (var path in CorpusFiles())
        {
            using var file = PstFile.Open(path);

            foreach (var node in file.Nodes())
            {
                var found = file.FindNode(node.Nid);
                Assert.Equal(node, found);
            }

            // A node id no file uses: index far past anything allocated, and absence is an
            // answer rather than an exception.
            Assert.Null(file.FindNode(new Nid(0x7FFFFF42)));
        }
    }
}
