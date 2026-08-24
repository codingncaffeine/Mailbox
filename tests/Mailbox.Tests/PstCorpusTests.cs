using Mailbox.Pst;
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
