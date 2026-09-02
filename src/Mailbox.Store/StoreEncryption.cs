using Mailbox.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Mailbox.Store;

/// <summary>What turning encryption on or off came to.</summary>
/// <param name="Changed">The files that were rewritten.</param>
/// <param name="Problem">Why it stopped, or null when it did not.</param>
public sealed record StoreEncryptionResult(IReadOnlyList<string> Changed, string? Problem = null)
{
    public bool Worked => Problem is null;
}

/// <summary>
/// Encrypting a profile's databases, and decrypting them again.
/// </summary>
/// <remarks>
/// <b>Never in place.</b> These files are somebody's mail, and rewriting every page of one is the
/// single most destructive thing this application can do to it: a crash, a full disk or a
/// pulled plug halfway through leaves a file that is neither readable as plaintext nor openable
/// with the key. So each file is copied, the <em>copy</em> is rewritten, the copy is opened and
/// asked whether it still holds what it held, and only then does it take the original's place —
/// with the original kept beside it under a dated name rather than deleted. Nothing is lost by a
/// failure at any point; the worst case is a directory with a spare copy in it.
/// <para>
/// It runs with every store closed, which is why it is a step the application takes on its way
/// down rather than something a button does underneath a running interface. A WAL file open on a
/// database being rewritten is the other way to corrupt one.
/// </para>
/// </remarks>
public static class StoreEncryption
{
    /// <summary>The databases a profile keeps, wherever they are.</summary>
    /// <remarks>
    /// Every one of them, not just the mail: <c>pim.db</c> holds the calendar, the contacts and
    /// the journal, and <c>feeds.db</c> holds what has been read. Encrypting the mail and leaving
    /// somebody's address book in the clear beside it would be the kind of half-measure that
    /// reads as a feature and is not one.
    /// </remarks>
    public static IReadOnlyList<string> Databases(string profileDirectory)
    {
        if (!Directory.Exists(profileDirectory)) return [];

        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(profileDirectory, "*.db", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            // The journal and shared-memory files travel with their database and are rebuilt;
            // a stale copy left from an earlier attempt is not one of the profile's stores.
            if (file.EndsWith("-wal", StringComparison.Ordinal)
                || file.EndsWith("-shm", StringComparison.Ordinal))
            {
                continue;
            }

            found.Add(file);
        }

        return found;
    }

    /// <summary>Encrypts every database in a profile with this key.</summary>
    public static StoreEncryptionResult Encrypt(string profileDirectory, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Rewrite(profileDirectory, from: null, to: Convert.ToHexString(key));
    }

    /// <summary>Decrypts every database in a profile, leaving ordinary SQLite behind.</summary>
    public static StoreEncryptionResult Decrypt(string profileDirectory, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Rewrite(profileDirectory, from: Convert.ToHexString(key), to: null);
    }

    /// <summary>
    /// Whether a file is encrypted — asked by opening it without a key and seeing what happens.
    /// </summary>
    /// <remarks>
    /// The header would do it more cheaply, and would be a guess: an encrypted file's first
    /// sixteen bytes are ciphertext and could be anything, including the sixteen a plain database
    /// starts with. Asking the library is the answer that cannot be wrong.
    /// </remarks>
    public static bool IsEncrypted(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            using var connection = new SqliteConnection(Connection(path));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_schema";
            command.ExecuteScalar();
            return false;
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    private static StoreEncryptionResult Rewrite(string profileDirectory, string? from, string? to)
    {
        var changed = new List<string>();

        foreach (var path in Databases(profileDirectory))
        {
            // Already the way it is being asked to be. Turning encryption on over a profile that
            // is half done — an earlier attempt that stopped — finishes it rather than failing.
            if (IsEncrypted(path) == (to is not null)) continue;

            var working = path + ".rewriting";
            var kept = path + ".before-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

            try
            {
                Discard(working);

                // The WAL has to be folded back into the file before it is copied, or the copy is
                // missing whatever had not been checkpointed.
                Checkpoint(path, from);
                File.Copy(path, working, overwrite: true);

                Rekey(working, from, to);

                if (Reads(working, to) is { } why)
                {
                    Discard(working);
                    return new StoreEncryptionResult(changed, $"{Path.GetFileName(path)} could not be rewritten: {why}");
                }

                // The original is moved aside rather than deleted, and only once the replacement
                // has been read back. Two files on disk beats none.
                File.Move(path, kept, overwrite: true);
                File.Move(working, path, overwrite: true);
                Discard(path + "-wal");
                Discard(path + "-shm");

                changed.Add(path);
                Log.Info($"Store: rewrote {Path.GetFileName(path)}; the previous file is {Path.GetFileName(kept)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                Log.Warn($"Store: {Path.GetFileName(path)} could not be rewritten.", ex);
                Discard(working);
                return new StoreEncryptionResult(changed, $"{Path.GetFileName(path)} could not be rewritten: {ex.Message}");
            }
        }

        return new StoreEncryptionResult(changed);
    }

    /// <summary>Folds the write-ahead log back into the file, so a copy of it is complete.</summary>
    private static void Checkpoint(string path, string? key)
    {
        using var connection = new SqliteConnection(Connection(path));
        connection.Open();
        Key(connection, key);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
    }

    /// <summary>Changes what a file is encrypted with — including to and from nothing.</summary>
    private static void Rekey(string path, string? from, string? to)
    {
        using var connection = new SqliteConnection(Connection(path));
        connection.Open();
        Key(connection, from);

        using var rekey = connection.CreateCommand();
        rekey.CommandText = to is null ? "PRAGMA rekey = \"\"" : $"PRAGMA rekey = \"x'{to}'\"";
        rekey.ExecuteNonQuery();
    }

    /// <summary>
    /// Whether the rewritten file still holds what it held: null when it does, or what went
    /// wrong.
    /// </summary>
    /// <remarks>
    /// An integrity check rather than a row count. The question is not "did some rows survive"
    /// but "is this a database", and SQLite's own <c>integrity_check</c> walks every page,
    /// every index and every constraint to answer it — which is exactly the damage a half-written
    /// rewrite would do. The schema version is read too, because a file that passes its integrity
    /// check and has lost its version would be migrated from the beginning on the next start.
    /// </remarks>
    private static string? Reads(string path, string? key)
    {
        try
        {
            using var connection = new SqliteConnection(Connection(path));
            connection.Open();
            Key(connection, key);

            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            var answer = check.ExecuteScalar()?.ToString();
            if (!string.Equals(answer, "ok", StringComparison.Ordinal)) return answer ?? "it would not answer";

            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            return Convert.ToInt64(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0
                ? null
                : "it came back with no schema version";
        }
        catch (SqliteException ex)
        {
            return ex.Message.Split('\n')[0];
        }
    }

    private static void Key(SqliteConnection connection, string? hex)
    {
        if (hex is not { Length: > 0 }) return;

        using var key = connection.CreateCommand();
        key.CommandText = $"PRAGMA key = \"x'{hex}'\"";
        key.ExecuteNonQuery();
    }

    private static string Connection(string path)
        => new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,

            // For the reason the stores' own connections turn it off: a pooled handle comes back
            // already keyed, which would make this unable to tell an encrypted file from a plain
            // one and unable to prove a rewritten one opens.
            Pooling = false,
        }.ToString();

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover working file is untidy, not dangerous: it is never read.
        }
    }
}
