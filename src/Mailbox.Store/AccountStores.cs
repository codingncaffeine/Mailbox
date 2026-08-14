using Mailbox.Core.Diagnostics;

namespace Mailbox.Store;

/// <summary>One account, its store file, and typed access to it.</summary>
public sealed record OpenAccount(Account Account, MailStore Store, MailRepository Mail)
{
    public string Path => Store.Path;

    /// <summary>Size of this account's file on disk.</summary>
    public long Bytes => File.Exists(Path) ? new FileInfo(Path).Length : 0;

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

    private void OpenExisting()
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.db").OrderBy(p => p))
        {
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
