using Mailbox.Core.Diagnostics;

namespace Mailbox.Store;

/// <summary>One account, its store file, and typed access to it.</summary>
public sealed record OpenAccount(Account Account, MailStore Store, MailRepository Mail)
{
    public string Path => Store.Path;

    /// <summary>How much room this account takes on disk.</summary>
    /// <remarks>
    /// The file <em>and</em> its write-ahead log. A store in WAL mode is two files, and until a
    /// checkpoint folds one into the other the newest mail is in the log rather than in the
    /// database — so the file on its own is not the size of the account and is not what a backup
    /// of it would have to carry. Reading the file alone had the mailbox that held 602 messages
    /// reported at 524 KB while it occupied 4.6 MB, and had Compact Now, which folds the log in as
    /// its first act, report a store it had just shrunk by four megabytes as "already compact":
    /// the size before was the file, the size after was the whole of it, and the difference came
    /// out negative.
    /// </remarks>
    public long Bytes
    {
        get
        {
            long Of(string suffix) => File.Exists(Path + suffix) ? new FileInfo(Path + suffix).Length : 0;

            // The shared-memory file is not counted: it is a fixed-size index into the log rather
            // than data, and it does not survive the last connection closing.
            return Of(string.Empty) + Of("-wal");
        }
    }

    public bool IsDefault { get; init; }
}

/// <summary>
/// The set of accounts, one SQLite file each.
/// </summary>
/// <remarks>
/// A file per account rather than one store for all of them. It costs a little duplication —
/// every file carries the schema — and buys the things that matter when mail is the only copy:
/// an account can be backed up, moved to another machine or deleted on its own, and a file that
/// goes bad takes one account with it rather than all of them. It is also what the reference
/// does, and what anyone arriving from it will expect to find on disk.
/// <para>
/// Each file is self-describing: it holds the single account row it belongs to, so a file
/// copied somewhere else still knows whose it is. What cannot live in any one file is which
/// account is the default and what order they come in — those are facts about the set, and they
/// live in the settings file.
/// </para>
/// </remarks>
public sealed class AccountStores : IDisposable
{
    private readonly string _directory;
    private readonly List<OpenAccount> _open = [];

    /// <summary>Which address is the default, and the order. Supplied by the settings file.</summary>
    private readonly IAccountOrder _order;

    public AccountStores(string directory, IAccountOrder order)
    {
        _directory = directory;
        _order = order;
        Directory.CreateDirectory(_directory);
        OpenExisting();
    }

    /// <summary>Where the per-account files live.</summary>
    public string Directory_ => _directory;

    public static string DefaultDirectory()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }

        return Path.Combine(data, "mailbox", "accounts");
    }

    /// <summary>Every account, in the user's order, the default first-class among them.</summary>
    public IReadOnlyList<OpenAccount> All
    {
        get
        {
            var defaultAddress = _order.DefaultAddress;
            return
            [
                .. _open
                    .OrderBy(a => _order.IndexOf(a.Account.Address))
                    .ThenBy(a => a.Account.Address, StringComparer.OrdinalIgnoreCase)
                    .Select(a => a with
                    {
                        IsDefault = string.Equals(
                            a.Account.Address, defaultAddress, StringComparison.OrdinalIgnoreCase),
                    }),
            ];
        }
    }

    public OpenAccount? Default => All.FirstOrDefault(a => a.IsDefault) ?? All.FirstOrDefault();

    public OpenAccount? Find(string address) => All.FirstOrDefault(
        a => string.Equals(a.Account.Address, address, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creates an account and the file behind it. Refuses an address already present rather
    /// than opening a second file that would collect the same mail twice.
    /// </summary>
    public OpenAccount Add(string address, string displayName, MailProtocol protocol)
    {
        if (Find(address) is not null)
        {
            throw new InvalidOperationException($"{address} has already been added.");
        }

        var path = Path.Combine(_directory, FileNameFor(address));
        var store = new MailStore(path);
        var mail = new MailRepository(store);

        var account = mail.AddAccount(address, displayName, protocol);
        mail.CreateStandardFolders(account.Id);

        var opened = new OpenAccount(account, store, mail);
        _open.Add(opened);
        _order.Register(address);

        Log.Info($"Created the store for {address} at {path}.");
        return opened;
    }

    /// <summary>
    /// Closes an account and deletes its file, along with WAL's two companions — left behind,
    /// they would be applied to whatever took the name next.
    /// </summary>
    public void Remove(string address)
    {
        if (Find(address) is not { } account) return;

        var path = account.Path;
        account.Store.Dispose();
        _open.RemoveAll(a => a.Account.Id == account.Account.Id && a.Path == path);
        _order.Forget(address);

        foreach (var suffix in (string[])[string.Empty, "-wal", "-shm"])
        {
            try
            {
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete {path}{suffix}.", ex);
            }
        }

        Log.Info($"Removed {address} and deleted {path}.");
    }

    /// <summary>
    /// Opens an account file from somewhere else — a backup, or a file detached earlier — by
    /// copying it into the directory under its own address's name and opening it. The
    /// reference's Add on the Data Files tab. The original is left where it was: it may be
    /// the only backup its owner has.
    /// </summary>
    /// <returns>The opened account, or the reason the file could not be taken.</returns>
    public (OpenAccount? Account, string? Error) Attach(string source)
    {
        if (!File.Exists(source)) return (null, "There is no file there.");

        // Look inside without opening it as a store: opening would migrate the file in place,
        // and the file may be somebody's only backup. Read-only, and refused before anything
        // is copied when it does not open, is damaged, or holds no account.
        string address;
        try
        {
            using var peek = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = source,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                }.ToString());
            peek.Open();

            using (var check = peek.CreateCommand())
            {
                check.CommandText = "PRAGMA integrity_check";
                using var reader = check.ExecuteReader();
                var problems = new List<string>();
                while (reader.Read())
                {
                    var line = reader.GetString(0);
                    if (line != "ok") problems.Add(line);
                }
                if (problems.Count > 0) return (null, $"That file is damaged: {string.Join("; ", problems)}");
            }

            using var who = peek.CreateCommand();
            who.CommandText = "SELECT address FROM accounts ORDER BY ordinal, id LIMIT 1";
            address = who.ExecuteScalar() as string ?? string.Empty;
            if (address.Length == 0) return (null, "That file holds no account.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {source}.", ex);
            return (null, $"That file could not be read as an account file: {ex.Message}");
        }

        if (Find(address) is not null)
        {
            return (null, $"{address} is already open. Remove it first to replace it with this file.");
        }

        var destination = Path.Combine(_directory, FileNameFor(address));
        if (File.Exists(destination))
        {
            return (null, $"There is already a file named {Path.GetFileName(destination)} in the accounts folder.");
        }

        try
        {
            File.Copy(source, destination);
            var store = new MailStore(destination);
            var mail = new MailRepository(store);
            var opened = new OpenAccount(mail.Accounts().First(), store, mail);
            _open.Add(opened);
            _order.Register(address);
            Log.Info($"Attached {source} as {destination}.");
            return (opened, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not attach {source}.", ex);
            try { if (File.Exists(destination)) File.Delete(destination); } catch { /* the copy is the casualty, not the source */ }
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Closes an account and moves its file out of the directory without deleting it — the
    /// reference's Remove on the Data Files tab, which closes a data file and leaves it on
    /// disk. The file goes to a <c>detached</c> folder beside the accounts, from where
    /// <see cref="Attach"/> can bring it back.
    /// </summary>
    /// <returns>Where the file went, or null when there was no such account.</returns>
    public string? Detach(string address)
    {
        if (Find(address) is not { } account) return null;

        var path = account.Path;
        account.Store.Dispose();
        _open.RemoveAll(a => a.Account.Id == account.Account.Id && a.Path == path);
        _order.Forget(address);

        var folder = Path.Combine(Path.GetDirectoryName(_directory) ?? _directory, "detached");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Path.GetFileName(path));
        if (File.Exists(destination))
        {
            destination = Path.Combine(folder,
                $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        }

        // WAL's two companions were folded in on close; anything left is stale and would be
        // applied over whatever takes the name next.
        File.Move(path, destination);
        foreach (var suffix in (string[])["-wal", "-shm"])
        {
            try
            {
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete {path}{suffix}.", ex);
            }
        }

        Log.Info($"Detached {address}: moved {path} to {destination}.");
        return destination;
    }

    private void OpenExisting()
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.db").OrderBy(p => p))
        {
            // Asked before it is opened, because opening migrates: see MailStore.LooksLikeOurs.
            if (!MailStore.LooksLikeOurs(path))
            {
                Log.Warn($"{path} is not a mail store and was left alone.");
                continue;
            }

            try
            {
                var store = new MailStore(path);
                var mail = new MailRepository(store);
                var account = mail.Accounts().FirstOrDefault();

                if (account is null)
                {
                    // A file with no account in it describes nothing. Left alone rather than
                    // deleted: it may be a backup somebody dropped in the wrong directory.
                    Log.Warn($"{path} holds no account and was skipped.");
                    store.Dispose();
                    continue;
                }

                _open.Add(new OpenAccount(account, store, mail));
                _order.Register(account.Address);
            }
            catch (Exception ex)
            {
                // One unreadable file must not stop the others opening.
                Log.Warn($"Could not open {path}.", ex);
            }
        }

        Log.Info($"Opened {_open.Count} account store(s) from {_directory}.");
    }

    /// <summary>
    /// A file name that says whose it is. The address is kept readable — it is the label the
    /// user looks for when backing one up — with only the characters a path cannot hold
    /// replaced.
    /// </summary>
    public static string FileNameFor(string address)
    {
        var safe = new string([.. address.Select(c =>
            c == '/' || c == '\\' || c == ':' || char.IsControl(c) ? '_' : c)]).Trim();

        if (safe.Length == 0) safe = "account";
        return safe + ".db";
    }

    public void Dispose()
    {
        foreach (var account in _open) account.Store.Dispose();
        _open.Clear();
    }
}

/// <summary>
/// Which account is the default, and what order they come in. Facts about the set of accounts
/// rather than about any one of them, so they live outside the stores.
/// </summary>
public interface IAccountOrder
{
    string? DefaultAddress { get; set; }

    /// <summary>Position in the folder pane, or a large number when unranked.</summary>
    int IndexOf(string address);

    void Register(string address);

    void Forget(string address);

    void Move(string address, int direction);
}
