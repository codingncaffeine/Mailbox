using Microsoft.Data.Sqlite;
using Mailbox.Store.Schema;

namespace Mailbox.Store;

/// <summary>
/// The mail store: one SQLite file per account holding its folders, messages and outbox.
/// </summary>
/// <remarks>
/// This is the one thing in the application that holds mail which may exist nowhere else —
/// POP3 with delete-on-download is the ordinary case. So it is strict about integrity:
/// foreign keys enforced rather than assumed, migrations forward-only, and the raw message
/// kept verbatim beside the parsed columns so a parsing mistake is recoverable. The connection,
/// the pragmas and the migration walk are <see cref="SqliteStore"/>'s; the schema is
/// <see cref="Migrations"/>.
/// </remarks>
public sealed class MailStore : SqliteStore
{
    /// <summary>Opens, creating and migrating the file if it is new or behind.</summary>
    public MailStore(string path) : base(path, Migrations.Steps)
    {
    }

    /// <summary>An in-memory store, migrated and empty.</summary>
    public static MailStore Transient() => new(InMemory);

    /// <summary>
    /// Whether a file is one of ours, asked before it is opened.
    /// </summary>
    /// <remarks>
    /// Opening a store migrates it, and a migration commits as it goes — so a file that is not a
    /// mail store at all, dropped into the accounts directory, used to be half-rewritten with
    /// this schema before anything noticed it held no accounts, and left at a version neither
    /// schema recognises. A <c>pim.db</c> put there by a mis-posed harness run is exactly how
    /// that was found. A file that has never been stamped is a new store and is opened as one;
    /// anything already stamped has to carry the table this schema is built on.
    /// </remarks>
    public static bool LooksLikeOurs(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return true;

        try
        {
            // Through the shared opener, so an encrypted store is opened with its key rather
            // than reported as somebody else's file.
            using var connection = SqliteStore.Connect(path, SqliteOpenMode.ReadOnly);

            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt64(version.ExecuteScalar()) == 0) return true;

            using var accounts = connection.CreateCommand();
            accounts.CommandText =
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'accounts'";

            return Convert.ToInt64(accounts.ExecuteScalar()) > 0;
        }
        catch (SqliteException)
        {
            // Not a database, or not a readable one. Whatever it is, it is not ours to migrate.
            return false;
        }
    }

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
}
