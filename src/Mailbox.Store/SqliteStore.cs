using Microsoft.Data.Sqlite;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Store;

/// <summary>
/// One SQLite file with a forward-only, append-only schema: the connection, its pragmas, the
/// migration walk, and the handful of helpers every repository is written against. The mail
/// store and the PIM store are both this, with their own schemas.
/// </summary>
/// <remarks>
/// Opened in WAL mode with foreign keys on. WAL lets readers carry on while a poll writes,
/// which matters because a send/receive is long and the list must stay live through it.
/// Migrations are forward-only and additive, and a file from a newer build is refused rather
/// than run against a schema this build does not understand.
/// <para>
/// <b>One writer, a reader per thread.</b> A poll runs on a thread-pool thread while the list,
/// the folder counts and the reading pane read from the interface thread, and the two used to
/// share a single connection with nothing between them. Two things came of that: a connection
/// used from two threads at once, which is not something the provider promises to survive, and
/// — worse — a write from the interface thread that started while the poll had a transaction
/// open would see the re-entrancy counter above zero, skip opening one of its own, and quietly
/// join the poll's. If the poll then threw, the reader's flag or move was rolled back with it,
/// with nothing said.
/// </para>
/// <para>
/// So: every write and every transaction goes through one writer connection under a lock that a
/// transaction holds for its whole life — a second thread wanting to write waits rather than
/// joining — and reads go to a connection belonging to the calling thread, which under WAL means
/// the interface never waits on the poll to draw a list. A read made by the thread that is
/// inside a transaction goes to the writer instead, because it has to see what that transaction
/// has done so far. An in-memory store has no second connection to give: <c>:memory:</c> with a
/// private cache means a second connection is a second database, so those serialise on the same
/// lock, which is what a store that exists for the length of one test wants anyway.
/// </para>
/// </remarks>
public abstract class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IReadOnlyList<string> _steps;
    private readonly string _connectionString;
    private readonly Lock _gate = new();

    /// <summary>One connection per thread that reads, or null for a store that has only the one.</summary>
    private readonly ThreadLocal<SqliteConnection>? _readers;

    /// <summary>Transaction depth, and the thread that owns it. Both are written under the gate.</summary>
    private volatile int _depth;
    private volatile int _owner;

    /// <summary>
    /// The row id of the last insert this thread made in this store.
    /// </summary>
    /// <remarks>
    /// Captured inside the same lock as the insert, rather than asked of the connection
    /// afterwards: the connection's own <c>last_insert_rowid()</c> belongs to whichever thread
    /// wrote last, so an insert on the poll thread landing between another thread's insert and
    /// its read of the id would hand back the wrong row.
    /// </remarks>
    [ThreadStatic]
    private static (SqliteStore? Store, long Id) _lastInsert;

    /// <summary>Path that opens a store that never touches disk. For tests.</summary>
    public const string InMemory = ":memory:";

    /// <summary>Opens, creating and migrating the file if it is new or behind.</summary>
    protected SqliteStore(string path, IReadOnlyList<string> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        Path = path;
        _steps = migrations;

        if (path != InMemory)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == InMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            // Private, deliberately. A shared cache makes every connection naming ":memory:"
            // the same database, so two in-memory stores alive at once — two tests, or a
            // preview beside the real thing — would silently be one.
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
        }.ToString();

        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
        Configure();
        Migrate();

        // After the migration, so that everything the constructor does goes through the writer:
        // a reader opened against a file that has not been created yet would have nothing to
        // read, and a migration read on another connection would not see the step in flight.
        _readers = path == InMemory
            ? null
            : new ThreadLocal<SqliteConnection>(OpenReader, trackAllValues: true);
    }

    public string Path { get; }

    /// <summary>Schema version of the open file.</summary>
    public int Version => (int)ScalarLong("PRAGMA user_version");

    /// <summary>The newest schema this build writes.</summary>
    public int LatestVersion => _steps.Count;

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

        if (from >= _steps.Count)
        {
            if (from > _steps.Count)
            {
                // A file from a newer build. Refuse rather than run against a schema this build
                // does not understand; the alternative is corrupting mail to look compatible.
                throw new InvalidOperationException(
                    $"{Path} is schema version {from}; this build understands {_steps.Count}. " +
                    "It was written by a newer version of Mailbox.");
            }

            return;
        }

        Log.Info($"Migrating {System.IO.Path.GetFileName(Path)} from schema {from} to {_steps.Count}.");

        for (var version = from; version < _steps.Count; version++)
        {
            using var transaction = _connection.BeginTransaction();

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = _steps[version];
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

    /// <summary>Runs a statement that returns nothing. Always on the writer, under the gate.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(_connection, sql, parameters);
            var rows = command.ExecuteNonQuery();

            // Read back inside the same lock as the insert that produced it, so that
            // LastInsertId belongs to this thread's row and not to whatever the poll wrote
            // between the two calls.
            if (Inserts(sql))
            {
                using var id = Command(_connection, "SELECT last_insert_rowid()");
                _lastInsert = (this, Convert.ToInt64(id.ExecuteScalar()));
            }

            return rows;
        }
    }

    /// <summary>Runs a statement and returns the first column of the first row as a long.</summary>
    public long ScalarLong(string sql, params (string Name, object? Value)[] parameters)
        => Read(connection =>
        {
            using var command = Command(connection, sql, parameters);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? 0 : Convert.ToInt64(value);
        });

    /// <summary>Runs a query and projects each row.</summary>
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> read,
        params (string Name, object? Value)[] parameters)
        => Read(connection =>
        {
            using var command = Command(connection, sql, parameters);
            using var reader = command.ExecuteReader();

            var rows = new List<T>();
            while (reader.Read()) rows.Add(read(reader));
            return rows;
        });

    /// <summary>
    /// Runs a read on the connection this thread should be reading from: the writer while this
    /// thread is inside a transaction, or where there is no second connection to use, and this
    /// thread's own reader otherwise.
    /// </summary>
    private T Read<T>(Func<SqliteConnection, T> work)
    {
        if (_readers is null || (_depth > 0 && _owner == Environment.CurrentManagedThreadId))
        {
            // Re-entrant for the thread already holding it, which is what a read inside a
            // transaction is.
            lock (_gate) return work(_connection);
        }

        return work(_readers.Value!);
    }

    /// <summary>Whether a statement can move <c>last_insert_rowid()</c>.</summary>
    private static bool Inserts(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        return text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A connection of this thread's own, for reading while another thread writes.</summary>
    private SqliteConnection OpenReader()
    {
        var reader = new SqliteConnection(_connectionString);
        reader.Open();

        // The pragmas that are the connection's rather than the file's. WAL is the file's and is
        // already set; a reader that met a checkpoint would otherwise fail instead of waiting.
        using (var foreignKeys = Command(reader, "PRAGMA foreign_keys = ON")) foreignKeys.ExecuteNonQuery();
        using (var busy = Command(reader, "PRAGMA busy_timeout = 5000")) busy.ExecuteNonQuery();

        return reader;
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
        // Already inside one, on this thread: join it, which is what nesting means. Another
        // thread's transaction is not ours to join — it waits for the gate below instead.
        if (_depth > 0 && _owner == Environment.CurrentManagedThreadId)
        {
            _depth++;
            try { return work(); }
            finally { _depth--; }
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            _depth = 1;
            _owner = Environment.CurrentManagedThreadId;

            try
            {
                var result = work();
                transaction.Commit();
                return result;
            }
            finally
            {
                _depth = 0;
                _owner = 0;
            }
        }
    }

    private static SqliteCommand Command(
        SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
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
    public void BackupTo(SqliteConnection target)
    {
        lock (_gate) _connection.BackupDatabase(target);
    }

    /// <summary>Row id of the last insert this thread made in this store.</summary>
    /// <remarks>
    /// This thread's rather than this connection's: see <see cref="_lastInsert"/>. A thread that
    /// has inserted nothing here asks the writer, which is what the property used to do for
    /// everybody.
    /// </remarks>
    public long LastInsertId
    {
        get
        {
            if (_lastInsert.Store == this) return _lastInsert.Id;

            lock (_gate)
            {
                using var command = Command(_connection, "SELECT last_insert_rowid()");
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
    }

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

    /// <summary>
    /// Gives back the space that deleted mail left behind: rebuilds the file without its free
    /// pages and folds the write-ahead log into it. The reference's Compact Now. Slow on a
    /// large store, so it is only ever run when asked for.
    /// </summary>
    /// <returns>The file's size afterwards, in bytes.</returns>
    public long Compact()
    {
        // VACUUM cannot run inside a transaction, and there is never one open between calls.
        Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        Execute("VACUUM");
        Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        return Path == InMemory || !File.Exists(Path) ? 0 : new FileInfo(Path).Length;
    }

    public void Dispose()
    {
        if (_readers is not null)
        {
            foreach (var reader in _readers.Values) reader.Dispose();
            _readers.Dispose();
        }

        _connection.Dispose();
        SqliteConnection.ClearPool(_connection);
        GC.SuppressFinalize(this);
    }
}
