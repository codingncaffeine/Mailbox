namespace Mailbox.Store.Pim;

/// <summary>
/// The PIM store: one SQLite file for every calendar, task list, note list and address book —
/// local ones and the ones a DAV account brings — beside the per-account mail stores.
/// </summary>
/// <remarks>
/// One file rather than one per account, on purpose: the To-Do Bar, the reminders window and
/// the day's appointments on the front page read across every collection at once, and a local
/// calendar belongs to no mail account at all. Recorded as a departure from the schema sketch's
/// <c>collections.account_id</c> in the plan.
/// </remarks>
public sealed class PimStore : SqliteStore
{
    public PimStore(string path) : base(path, PimMigrations.Steps)
    {
    }

    /// <summary>An in-memory store, migrated and empty.</summary>
    public static PimStore Transient() => new(InMemory);

    /// <summary>Where the store lives: <c>$XDG_DATA_HOME/mailbox/pim.db</c>, beside the mail stores.</summary>
    public static string DefaultPath()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        if (string.IsNullOrWhiteSpace(data))
        {
            data = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }

        return System.IO.Path.Combine(data, "mailbox", "pim.db");
    }
}
