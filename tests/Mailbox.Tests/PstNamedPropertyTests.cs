using System.Buffers.Binary;
using System.Text;
using Mailbox.Pst;
using Mailbox.Pst.Messaging;

namespace Mailbox.Tests;

/// <summary>
/// The Name-to-ID map ([MS-PST] §2.4.7): streams built by hand against the record layout, and
/// the real corpus proving that a file's own appointments resolve through its own map.
/// </summary>
public class PstNamedPropertyTests
{
    private static byte[] Entry(uint nameValue, bool isString, int guidIndex, ushort ordinal)
    {
        var record = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(record, nameValue);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)((guidIndex << 1) | (isString ? 1 : 0)));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), ordinal);
        return record;
    }

    [Fact]
    public void NumericAndStringNamesBothResolveBothWays()
    {
        // One numeric name in PSETID_Common (via the GUID stream at index 3), one string name
        // with no set at all — the two shapes §2.4.7.6's own diagram draws.
        var name = "x-mailbox-test";
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var stringStream = new byte[4 + nameBytes.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(stringStream, (uint)nameBytes.Length);
        nameBytes.CopyTo(stringStream, 4);

        var map = new PstNamedProperties(
            [.. Entry(0x8503, isString: false, guidIndex: 3, ordinal: 0),
             .. Entry(0, isString: true, guidIndex: 0, ordinal: 1)],
            PstPropertySets.Common.ToByteArray(),
            stringStream);

        Assert.Equal(2, map.Count);

        Assert.Equal((ushort)0x8000, map.IdOf(PstPropertySets.Common, 0x8503));
        Assert.Equal(new PstPropertyName(PstPropertySets.Common, 0x8503, null), map.NameOf(0x8000));

        Assert.Equal((ushort)0x8001, map.IdOf(Guid.Empty, name));
        Assert.Equal(new PstPropertyName(Guid.Empty, null, name), map.NameOf(0x8001));

        Assert.Null(map.IdOf(PstPropertySets.Task, 0x8503));
        Assert.Null(map.NameOf(0x8002));
    }

    [Fact]
    public void ARecordPointingOutsideItsStreamsCostsItselfAlone()
    {
        var map = new PstNamedProperties(
            [.. Entry(9999, isString: true, guidIndex: 0, ordinal: 0),   // string offset past the stream
             .. Entry(0x1234, isString: false, guidIndex: 7, ordinal: 1), // GUID index past the stream
             .. Entry(0x8205, isString: false, guidIndex: 3, ordinal: 2)],
            PstPropertySets.Appointment.ToByteArray(),
            []);

        var survivor = Assert.Single(
            Enumerable.Range(0x8000, 4).Select(id => map.NameOf((ushort)id)),
            name => name is not null);
        Assert.Equal(new PstPropertyName(PstPropertySets.Appointment, 0x8205, null), survivor);
    }

    [Fact]
    public void EveryCorpusMapOpensAndItsAppointmentsResolveThroughIt()
    {
        var posed = Environment.GetEnvironmentVariable("MAILBOX_PST_CORPUS");
        var directory = posed is { Length: > 0 } && Directory.Exists(posed) ? posed : Beside();
        Assert.SkipWhen(directory is null, "Set MAILBOX_PST_CORPUS, or keep files in specs/pst-corpus, to run against real PST files.");

        var resolvedAppointments = 0;
        foreach (var path in PstCorpusTests.CorpusScan(directory!))
        {
            using var file = PstFile.Open(path);
            var names = PstNamedProperties.Open(file);
            Assert.True(names.Count > 0, $"{Path.GetFileName(path)} has an empty name map, which no real file does.");

            // Round trip every entry: what the map says an id means must map back to that id.
            foreach (var id in Enumerable.Range(0x8000, names.Count + 64))
            {
                if (names.NameOf((ushort)id) is not { } name) continue;
                var back = name.StringName is { } text ? names.IdOf(name.Set, text) : names.IdOf(name.Set, name.NumericId!.Value);
                Assert.Equal((ushort)id, back);
            }

            // And the point of the map: the calendar folder's appointment gives up its start
            // and end through this file's own ids.
            var store = PstStore.Open(file);
            foreach (var folder in store.MailRoot.Subfolders())
            {
                if (!folder.ContainerClass.StartsWith("IPF.Appointment", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var appointment in folder.Messages())
                {
                    var start = appointment.Named(names, PstPropertySets.Appointment, 0x820D)?.AsTime();
                    var end = appointment.Named(names, PstPropertySets.Appointment, 0x820E)?.AsTime();
                    if (start is null || end is null) continue;
                    Assert.True(end >= start, $"{Path.GetFileName(path)}: an appointment ends before it starts.");
                    resolvedAppointments++;
                }
            }
        }

        Assert.True(resolvedAppointments > 0, "No corpus appointment resolved its start and end — the map is not being consulted.");

        static string? Beside()
        {
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
    }
}
