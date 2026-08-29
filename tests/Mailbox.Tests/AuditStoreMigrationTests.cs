using System.Text;
using Microsoft.Data.Sqlite;
using Mailbox.Store;
using Mailbox.Store.Pim;
using Mailbox.Store.Schema;

namespace Mailbox.Tests;

/// <summary>
/// A store at every schema this application has ever written, opened by the real store class and
/// migrated forward, then compared object for object and column for column against a store the
/// same build created from nothing.
/// </summary>
/// <remarks>
/// "It opened without throwing" is not the claim. A migration that adds a column but forgets an
/// index, or writes a default a fresh store does not write, leaves two stores that behave
/// differently under the same version number with nothing able to tell them apart — and the one
/// that has been through the migrations is the one holding the reader's mail.
/// <para>
/// The historical schemas come from <c>tests/fixtures/schema/</c>, one .sql per step, taken from
/// the commit that introduced it by <c>tools/make-schema-fixtures.py</c>. They are history's copy
/// rather than the tree's on purpose: comparing the tree against itself would pass however badly
/// a shipped step had been edited, and editing one is the single change this schema cannot
/// survive.
/// </para>
/// </remarks>
public class AuditStoreMigrationTests
{
    /// <summary>Everything sqlite_master, table_info, index_list and the FTS config say.</summary>
    private static string Fingerprint(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        connection.Open();
        var text = new StringBuilder();

        // Objects: tables, indexes (the implicit ones too), triggers, views and the virtual
        // table's shadow tables, with their SQL normalised for whitespace only.
        foreach (var row in Rows(connection,
            "SELECT type, name, COALESCE(tbl_name,''), COALESCE(sql,'') FROM sqlite_master " +
            "ORDER BY type, name"))
        {
            text.AppendLine($"object {row[0]} {row[1]} on {row[2]}: {Squash(row[3])}");
        }

        var tables = Rows(connection,
                "SELECT name FROM sqlite_master WHERE type IN ('table') ORDER BY name")
            .Select(r => r[0]).ToList();

        foreach (var table in tables)
        {
            foreach (var column in Rows(connection, $"PRAGMA table_info(\"{table}\")"))
            {
                text.AppendLine(
                    $"column {table}.{column[1]} #{column[0]} {column[2]} " +
                    $"notnull={column[3]} default={(column[4].Length == 0 ? "<none>" : column[4])} pk={column[5]}");
            }

            foreach (var index in Rows(connection, $"PRAGMA index_list(\"{table}\")"))
            {
                var members = Rows(connection, $"PRAGMA index_info(\"{index[1]}\")")
                    .Select(i => $"{i[0]}:{i[2]}");
                text.AppendLine(
                    $"index {table}.{index[1]} unique={index[2]} origin={index[3]} " +
                    $"partial={index[4]} ({string.Join(",", members)})");
            }

            foreach (var key in Rows(connection, $"PRAGMA foreign_key_list(\"{table}\")"))
            {
                text.AppendLine(
                    $"foreignkey {table} -> {key[2]}({key[4]}) from {key[3]} " +
                    $"onupdate={key[5]} ondelete={key[6]}");
            }
        }

        // The FTS5 configuration lives in a shadow table of its own, and is the part of an index
        // a schema dump does not show: change the tokenizer and every store written before the
        // change answers a different set of searches.
        foreach (var fts in tables.Where(t => t.EndsWith("_config", StringComparison.Ordinal)))
        {
            foreach (var setting in Rows(connection, $"SELECT k, v FROM \"{fts}\" ORDER BY k"))
            {
                text.AppendLine($"ftsconfig {fts}.{setting[0]} = {setting[1]}");
            }
        }

        return text.ToString();
    }

    private static List<string[]> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<string[]>();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString() ?? string.Empty;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string Squash(string sql)
        => string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }

    private static string FixtureDirectory(string store)
        => Path.Combine(RepoRoot(), "tests", "fixtures", "schema", store);

    private static IReadOnlyList<string> Fixtures(string store)
        => Directory.GetFiles(FixtureDirectory(store), "*.sql").OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The lowest version any build ever wrote: the first commit shipped several steps at once,
    /// and a store could never rest between them.
    /// </summary>
    private static int FirstShippedVersion(string store)
    {
        var files = Fixtures(store);
        var earliest = Introducer(files[0]);
        var shipped = 1;
        while (shipped < files.Count && Introducer(files[shipped]) == earliest) shipped++;
        return shipped;

        // "-- mail schema step 7, as introduced at 07e97988 on 2026-08-15" — the commit, not the
        // step number, which is what tells the several steps of one commit apart from the rest.
        static string Introducer(string file)
        {
            var header = File.ReadLines(file).First();
            var at = header.IndexOf(" at ", StringComparison.Ordinal);
            return at < 0 ? header : header[(at + 4)..];
        }
    }

    /// <summary>Builds a store file at a historical version, from history's own steps.</summary>
    private static void Build(string store, string path, int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString());

        connection.Open();
        Run(connection, "PRAGMA journal_mode = WAL");

        // Exactly what SqliteStore.Migrate does: one step per transaction, user_version stamped
        // inside it, so a fixture is indistinguishable from a store a build of that day left.
        foreach (var file in Fixtures(store).Take(version))
        {
            using var transaction = connection.BeginTransaction();
            using (var step = connection.CreateCommand())
            {
                step.Transaction = transaction;
                step.CommandText = File.ReadAllText(file);
                step.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Run(connection, $"PRAGMA user_version = {version}");

        static void Run(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Mail a store of that vintage would be holding: an account, three folders and four
    /// messages, written with the columns schema 1 already had so one seed serves every version.
    /// </summary>
    private static void SeedMail(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO accounts (id, address, display_name, protocol, created_utc)
                VALUES (1, 'you@example.com', 'A. Person', 'pop3', 1000);
            INSERT INTO folders (id, account_id, parent_id, name, role, ordinal) VALUES
                (1, 1, NULL, 'Inbox', 'inbox', 0),
                (2, 1, NULL, 'Archive', 'none', 1),
                (3, 1, NULL, 'Deleted Items', 'deleted', 2);
            INSERT INTO blobs (id, bytes, byte_length) VALUES
                (1, x'4869', 2), (2, x'4869', 2), (3, x'4869', 2), (4, x'4869', 2);
            INSERT INTO messages
                (id, folder_id, blob_id, server_uid, message_id, from_name, from_address,
                 subject, preview, received_utc, size_bytes, is_read)
            VALUES
                (1, 1, 1, 'uidl-aardvark', '<1@example.com>', 'A. Person', 'a.person@example.com',
                 'The damson harvest', 'damsons everywhere', 1000, 2, 0),
                (2, 1, 2, 'uidl-basilisk', '<2@example.com>', 'B. Person', 'b.person@example.com',
                 'Quarterly quince', 'quinces are late', 2000, 2, 1),
                (3, 2, 3, 'uidl-cormorant', '<3@example.com>', 'C. Person', 'c.person@example.com',
                 'Archived apricot', 'apricot stones', 3000, 2, 1),
                (4, 3, 4, 'uidl-dromedary', '<4@example.com>', 'D. Person', 'd.person@example.com',
                 'Discarded sloe', 'sloe gin', 4000, 2, 0);
            """;
        command.ExecuteNonQuery();
    }

    private static string Scratch([System.Runtime.CompilerServices.CallerMemberName] string name = "")
        => Path.Combine(Path.GetTempPath(), "mailbox-migration-tests", name, Guid.NewGuid().ToString("n"));

    // ---- mail ---------------------------------------------------------------------------------

    public static TheoryData<int> MailVersions()
    {
        var data = new TheoryData<int>();
        for (var v = FirstShippedVersion("mail"); v < Migrations.Latest; v++) data.Add(v);
        return data;
    }

    public static TheoryData<int> PimVersions()
    {
        var data = new TheoryData<int>();
        for (var v = FirstShippedVersion("pim"); v < PimMigrations.Latest; v++) data.Add(v);
        return data;
    }

    [Fact]
    public void TheFixturesCoverEveryStepTheTreeCarries()
    {
        Assert.Equal(Migrations.Latest, Fixtures("mail").Count);
        Assert.Equal(PimMigrations.Latest, Fixtures("pim").Count);
    }

    [Theory]
    [MemberData(nameof(MailVersions))]
    public void AMailStoreAtEveryHistoricalSchemaMigratesToOneIdenticalToAFreshStore(int from)
    {
        var directory = Scratch();
        var old = Path.Combine(directory, $"from-{from}", "you@example.com.db");
        var fresh = Path.Combine(directory, "fresh", "you@example.com.db");

        try
        {
            Build("mail", old, from);
            SeedMail(old);

            // The real path: the store class opens it and walks its own steps.
            using (var store = new MailStore(old)) Assert.Equal(Migrations.Latest, store.Version);
            using (var store = new MailStore(fresh)) Assert.Equal(Migrations.Latest, store.Version);

            Assert.Equal(Fingerprint(fresh), Fingerprint(old));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(MailVersions))]
    public void MigratingAMailStoreKeepsItsMailAndItsSearchIndex(int from)
    {
        var directory = Scratch();
        var path = Path.Combine(directory, "you@example.com.db");

        try
        {
            Build("mail", path, from);
            SeedMail(path);

            using var store = new MailStore(path);
            var repository = new MailRepository(store);

            Assert.Equal(4, store.ScalarLong("SELECT count(*) FROM messages"));
            Assert.Equal(2, repository.Messages(1).Count);
            Assert.Equal("The damson harvest", repository.Messages(1).Last().Subject);

            // The index is external-content, so a migration that rewrote a row without telling it
            // would leave a search that finds nothing or finds the wrong thing.
            Assert.Equal([1L], Ids(repository.Search("damson")));
            Assert.Equal([3L], Ids(repository.Search("apricot")));
            Assert.Empty(repository.Search("marmalade"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Step 14's backfill: what a POP3 poll had already collected. It only means anything for a
    /// store that held mail when the step ran, which is exactly what a migration fixture is for
    /// and what a fresh store can never prove.
    /// </summary>
    [Theory]
    [MemberData(nameof(MailVersions))]
    public void TheSeenListIsBackfilledForAStoreThatPredatesIt(int from)
    {
        var directory = Scratch();
        var path = Path.Combine(directory, "you@example.com.db");

        try
        {
            Build("mail", path, from);
            SeedMail(path);
            using var store = new MailStore(path);

            var seen = store.Query("SELECT uidl FROM pop3_seen ORDER BY uidl", r => r.GetString(0));

            if (from < 14)
            {
                Assert.Equal(
                    ["uidl-aardvark", "uidl-basilisk", "uidl-cormorant", "uidl-dromedary"], seen);
            }
            else
            {
                // The step ran before this mail was here; nothing to backfill, and nothing
                // pretending it was collected by a poll that never happened.
                Assert.Empty(seen);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The six shipped categories arrive once, however far back the store starts.</summary>
    [Theory]
    [MemberData(nameof(MailVersions))]
    public void TheShippedCategoriesArriveExactlyOnce(int from)
    {
        var directory = Scratch();
        var path = Path.Combine(directory, "you@example.com.db");

        try
        {
            Build("mail", path, from);
            SeedMail(path);
            using var store = new MailStore(path);

            Assert.Equal(6, store.ScalarLong("SELECT count(*) FROM categories"));
            Assert.Equal(6, store.ScalarLong("SELECT count(DISTINCT name) FROM categories"));
            Assert.Equal(1, store.ScalarLong("SELECT count(*) FROM junk_corpus"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // ---- pim ----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(PimVersions))]
    public void APimStoreAtEveryHistoricalSchemaMigratesToOneIdenticalToAFreshStore(int from)
    {
        var directory = Scratch();
        var old = Path.Combine(directory, $"from-{from}", "pim.db");
        var fresh = Path.Combine(directory, "fresh", "pim.db");

        try
        {
            Build("pim", old, from);
            SeedPim(old);

            using (var store = new PimStore(old)) Assert.Equal(PimMigrations.Latest, store.Version);
            using (var store = new PimStore(fresh)) Assert.Equal(PimMigrations.Latest, store.Version);

            Assert.Equal(Fingerprint(fresh), Fingerprint(old));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(PimVersions))]
    public void MigratingAPimStoreKeepsItsItems(int from)
    {
        var directory = Scratch();
        var path = Path.Combine(directory, "pim.db");

        try
        {
            Build("pim", path, from);
            SeedPim(path);

            using var store = new PimStore(path);
            var pim = new PimRepository(store);

            var items = pim.Items(1);
            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.Summary == "Dentist");
            Assert.Contains(items, i => i.Summary == "Buy damsons");
            Assert.Contains("BEGIN:VEVENT", items.First(i => i.Summary == "Dentist").RawPayload);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void SeedPim(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO collections (id, account, kind, display_name, is_default, created_utc)
                VALUES (1, '', 'vevent', 'Calendar', 1, 1000);
            INSERT INTO pim_items
                (id, collection_id, uid, kind, raw_payload, summary, starts_utc, ends_utc, last_modified)
            VALUES
                (1, 1, 'uid-dentist', 'vevent',
                 'BEGIN:VEVENT
            UID:uid-dentist
            SUMMARY:Dentist
            END:VEVENT', 'Dentist', 1000, 2000, 1000),
                (2, 1, 'uid-damsons', 'vevent',
                 'BEGIN:VEVENT
            UID:uid-damsons
            SUMMARY:Buy damsons
            END:VEVENT', 'Buy damsons', 3000, 4000, 1000);
            """;
        command.ExecuteNonQuery();
    }

    private static long[] Ids(IEnumerable<MessageSummary> rows) => rows.Select(r => r.Id).OrderBy(i => i).ToArray();
}
