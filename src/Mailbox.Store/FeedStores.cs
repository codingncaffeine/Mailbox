using Mailbox.Core.Diagnostics;

namespace Mailbox.Store;

/// <summary>
/// The store the feed reader files into: one file of its own, beside the account files.
/// </summary>
/// <remarks>
/// Feeds used to be filed into whichever mail account happened to sort first, and that was
/// wrong in two ways. It was wrong in principle — a subscription belongs to the reader, not to
/// one of their mail accounts, and nothing about a feed has anything to do with the server that
/// carries their post. And it was wrong in practice: "whichever sorts first" is not a stable
/// answer, so adding an account that sorted ahead of the old one pointed the whole module at a
/// store with no feed folders in it, and a reader's subscriptions appeared to empty themselves
/// while their articles sat in a file nothing was looking at any more.
/// <para>
/// A file of its own also buys what a file per account buys: the feeds can be backed up, moved
/// or deleted on their own, and a store that goes bad costs the feeds rather than somebody's
/// mail.
/// </para>
/// <para>
/// It carries an account row because a folder belongs to an account and the repository is built
/// that way — but the address is deliberately one nothing can send to, and this store is not in
/// <see cref="AccountStores"/>, so nothing that walks the reader's accounts finds it: not
/// Send/Receive, not the unified inbox, not Account Settings, not the compose From line.
/// </para>
/// </remarks>
public sealed class FeedStores : IDisposable
{
    /// <summary>What the store calls itself, and what the folder pane shows.</summary>
    public const string DisplayName = "RSS Feeds";

    /// <summary>
    /// The address on the account row.
    /// </summary>
    /// <remarks>
    /// <c>.invalid</c> is reserved by RFC 2606 precisely so that a name which must never resolve
    /// cannot one day belong to somebody. Nothing sends to this and nothing shows it.
    /// </remarks>
    public const string Address = "feeds@mailbox.invalid";

    private readonly MailStore _store;

    public FeedStores(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _store = new MailStore(path);
        var mail = new MailRepository(_store);

        var record = mail.Accounts().FirstOrDefault()
                     ?? mail.AddAccount(Address, DisplayName, MailProtocol.Pop3);

        // No standard folders: there is no Inbox, no Sent and no Drafts here. What this store
        // holds is one tree of feeds and the things saved out of them.
        Account = new OpenAccount(record, _store, mail);

        Log.Info($"Feeds store: {path}");
    }

    /// <summary>The feeds store, in the shape everything else already understands.</summary>
    public OpenAccount Account { get; }

    /// <summary>Where the file goes, given where the account files go.</summary>
    /// <remarks>
    /// Beside the accounts directory rather than inside it, so nothing that opens every file in
    /// there as a mail account ever opens this one.
    /// </remarks>
    public static string PathBeside(string accountsDirectory)
    {
        var parent = Path.GetDirectoryName(accountsDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(parent ?? accountsDirectory, "feeds.db");
    }

    public void Dispose() => _store.Dispose();
}
