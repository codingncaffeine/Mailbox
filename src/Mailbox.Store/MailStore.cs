using Microsoft.Data.Sqlite;
using Mailbox.Core.Diagnostics;
using Mailbox.Store.Schema;

namespace Mailbox.Store;

/// <summary>
/// The mail store: one SQLite file holding accounts, folders, messages and the outbox.
/// </summary>
/// <remarks>
/// Opened in WAL mode with foreign keys on. WAL lets readers carry on while a poll writes,
/// which matters because a send/receive is long and the list must stay live through it.
/// <para>
/// This is the one thing in the application that holds mail which may exist nowhere else —
/// POP3 with delete-on-download is the ordinary case. So it is strict about integrity:
/// foreign keys enforced rather than assumed, migrations forward-only, and the raw message
/// kept verbatim beside the parsed columns so a parsing mistake is recoverable.
/// </para>
/// </remarks>
public sealed class MailStore : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Opens, creating and migrating the file if it is new or behind.</summary>
    public MailStore(string path)
    {
        Path = path;

        if (path != InMemory)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == InMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,

            // Private, deliberately. A shared cache makes every connection naming ":memory:"
            // the same database, so two in-memory stores alive at once — two tests, or a
            // preview beside the real thing — would silently be one.
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
        }.ToString());

        _connection.Open();
        Configure();
        Migrate();
    }

    /// <summary>Path that opens a store that never touches disk. For tests.</summary>
    public const string InMemory = ":memory:";

    public string Path { get; }

    /// <summary>Schema version of the open file.</summary>
    public int Version => (int)ScalarLong("PRAGMA user_version");

    /// <summary>An in-memory store, migrated and empty.</summary>
    public static MailStore Transient() => new(InMemory);

    /// <summary>Where the store lives when the user has not said otherwise.</summary>
    public static string DefaultPath()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }

        return System.IO.Path.Combine(data, "mailbox", "mail.db");
    }

    private void Configure()
    {
        // WAL is what lets the list keep reading while a poll writes. It is a property of the
        // file, not the connection, so setting it once is enough — but setting it is cheap and
        // an in-memory store rejects it, hence the tolerance.
        if (Path != InMemory) Execute("PRAGMA journal_mode = WAL");

        Execute("PRAGMA foreign_keys = ON");
        Execute("PRAGMA synchronous = NORMAL");
        Execute("PRAGMA busy_timeout = 5000");
    }

    private void Migrate()
    {
        var from = Version;
        if (from >= Migrations.Latest)
        {
            if (from > Migrations.Latest)
            {
                // A file from a newer build. Refuse rather than run against a schema this build
                // does not understand; the alternative is corrupting mail to look compatible.
                throw new InvalidOperationException(
                    $"{Path} is schema version {from}; this build understands {Migrations.Latest}. " +
                    "It was written by a newer version of Mailbox.");
            }

            return;
        }

        Log.Info($"Migrating store from schema {from} to {Migrations.Latest}.");

        for (var version = from; version < Migrations.Latest; version++)
        {
            using var transaction = _connection.BeginTransaction();
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Migrations.Steps[version];
                command.ExecuteNonQuery();
            }

            // user_version takes no parameter, and the value is ours rather than a caller's.
            using (var stamp = _connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = $"PRAGMA user_version = {version + 1}";
                stamp.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Runs a statement that returns nothing.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a statement and returns the first column of the first row as a long.</summary>
    public long ScalarLong(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>Runs a query and projects each row.</summary>
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> read,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();

        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    /// <summary>
    /// Wraps work in a transaction, rolling back if it throws. Re-entrant: nested calls join
    /// the outermost one rather than failing.
    /// </summary>
    /// <remarks>
    /// SQLite has no nested transactions, and the operations that want one are naturally
    /// composed — filing a message takes a transaction, and filing ten thousand of them on a
    /// first poll wants one around the lot. Without this the caller either fsyncs per message
    /// or has to know which repository methods already opened one, which is the kind of thing
    /// that is right until somebody adds a method.
    /// </remarks>
    public T InTransaction<T>(Func<T> work)
    {
        if (_depth > 0)
        {
            _depth++;
            try { return work(); }
            finally { _depth--; }
        }

        using var transaction = _connection.BeginTransaction();
        _depth = 1;

        try
        {
            var result = work();
            transaction.Commit();
            return result;
        }
        finally
        {
            _depth = 0;
        }
    }

    private int _depth;

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>
    /// Copies this store into another connection using SQLite's online backup, which is safe
    /// while the store is in use.
    /// </summary>
    public void BackupTo(SqliteConnection target) => _connection.BackupDatabase(target);

    /// <summary>Row id the last insert produced on this connection.</summary>
    public long LastInsertId => ScalarLong("SELECT last_insert_rowid()");

    /// <summary>
    /// Checks the file over. Runs on demand rather than at startup: it reads every page, which
    /// is the wrong thing to do to a large store on the way to showing an inbox.
    /// </summary>
    public IReadOnlyList<string> CheckIntegrity()
    {
        var problems = Query("PRAGMA integrity_check", r => r.GetString(0))
            .Where(line => line != "ok")
            .ToList();

        problems.AddRange(Query("PRAGMA foreign_key_check", r => $"foreign key: {r.GetString(0)}"));
        return problems;
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearPool(_connection);
    }
}
