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
