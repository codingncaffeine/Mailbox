using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>
/// Files the newsletters a reader has asked to read as articles into their own feed folders.
/// </summary>
/// <remarks>
/// An arrival handler, so it runs on whatever protocol brought the message and behaves the same
/// on POP3 and IMAP — on IMAP the move is journalled to the server like any other, so the
/// newsletter lands in the same place on the reader's phone.
/// <para>
/// It moves only what the reader has already said to move, matched on the newsletter's identity
/// rather than on it looking like bulk mail. Everything else is left in the inbox: a handler that
/// swept up anything with a List-Unsubscribe header would file receipts, password resets and
/// calendar invitations as reading matter.
/// </para>
/// </remarks>
public sealed class NewsletterRouter(FeedSubscriptions feeds) : IArrivalHandler
{
    private readonly FeedSubscriptions _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));

    /// <summary>How many issues were filed this way, for the status line.</summary>
    public int Filed { get; private set; }

    public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(message);

        // Only what arrived; a message the reader moved into a feed folder themselves is theirs.
        if (folder.Role is not FolderRole.Inbox) return folder.Id;

        var raw = mail.LoadRaw(messageId);
        if (raw is null) return folder.Id;

        var marks = Newsletters.Marks(raw);
        if (!marks.IsNewsletter) return folder.Id;

        if (Routed(marks) is not { } subscription) return folder.Id;

        var destination = FeedFolder(mail, folder.AccountId, subscription);
        if (destination is null || destination.Id == folder.Id) return folder.Id;

        mail.MoveMessage(messageId, destination.Id);
        Filed++;

        Log.Info($"Newsletters: message {messageId} filed under {Mailbox.Protocols.FeedReceiver.RootFolder}/{subscription.FolderPath}.");
        Log.Debug($"Newsletters: message {messageId} is “{message.Subject}”.");
        return destination.Id;
    }

    /// <summary>
    /// The subscription this message belongs to, or null when the reader has not asked for it.
    /// </summary>
    /// <remarks>
    /// By the list's own identity first and the sending address second, which is the order they
    /// are stable in: a publication that changes which service sends it keeps its List-ID, and
    /// matching only on the address would lose the subscription the day they move.
    /// </remarks>
    private FeedSubscription? Routed(NewsletterMarks marks)
        => _feeds.Find(Newsletters.AddressFor(marks.Identity));

    /// <summary>The newsletter's folder under the feeds root, made if it is not there.</summary>
    private static Folder? FeedFolder(MailRepository mail, long accountId, FeedSubscription feed)
    {
        var folders = mail.Folders(accountId);

        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == FeedReceiver.RootFolder)
                   ?? mail.AddFolder(accountId, FeedReceiver.RootFolder);

        var parent = root;

        if (feed.Category is { Length: > 0 } category)
        {
            folders = mail.Folders(accountId);
            parent = folders.FirstOrDefault(f => f.ParentId == root.Id && f.Name == category)
                     ?? mail.AddFolder(accountId, category, parentId: root.Id);
        }

        folders = mail.Folders(accountId);
        var name = feed.Name is { Length: > 0 } named ? named : feed.Url;

        return folders.FirstOrDefault(f => f.ParentId == parent.Id && f.Name == name)
               ?? mail.AddFolder(accountId, name, parentId: parent.Id);
    }
}

/// <summary>One newsletter found in a mailbox, with how much of it is there.</summary>
/// <param name="Identity">What tells it from the others — its List-ID or its sending address.</param>
/// <param name="Issues">How many of its issues are in the folder that was looked at.</param>
public sealed record FoundNewsletter(string Identity, string Name, string From, int Issues, DateTimeOffset Latest)
{
    /// <summary>The address the subscription would be filed under.</summary>
    public string Address => Newsletters.AddressFor(Identity);
}

/// <summary>
/// Finds the newsletters already sitting in a mailbox.
/// </summary>
/// <remarks>
/// The counterpart of feed discovery, and the same idea: a reader should not have to know
/// anything. They do not remember what they have subscribed to over the years, and asking them
/// to type it in would get a fraction of it — so the inbox is read and what it holds is offered.
/// <para>
/// Reads headers only, off the raw bytes, without a MIME parse. A thousand messages parsed to
/// read one header of each is the difference between a question that answers itself and one that
/// spins for ten seconds.
/// </para>
/// </remarks>
public static class NewsletterScan
{
    /// <summary>How far back to look. A newsletter worth reading has published inside this many.</summary>
    public const int MostMessages = 600;

    /// <summary>The newsletters in a folder, most issues first.</summary>
    public static IReadOnlyList<FoundNewsletter> In(MailRepository mail, long folderId, int limit = MostMessages)
    {
        ArgumentNullException.ThrowIfNull(mail);

        var found = new Dictionary<string, FoundNewsletter>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in mail.Messages(folderId, limit))
        {
            var raw = mail.LoadRaw(message.Id);
            if (raw is null) continue;

            var marks = Newsletters.Marks(raw);
            if (!marks.IsNewsletter || marks.Identity.Length == 0) continue;

            if (found.TryGetValue(marks.Identity, out var already))
            {
                found[marks.Identity] = already with
                {
                    Issues = already.Issues + 1,
                    Latest = message.Received > already.Latest ? message.Received : already.Latest,
                };

                continue;
            }

            found[marks.Identity] = new FoundNewsletter(
                marks.Identity,
                marks.Name is { Length: > 0 } named ? named : message.DisplayFrom,
                message.FromAddress,
                1,
                message.Received);
        }

        return [.. found.Values.OrderByDescending(n => n.Issues).ThenByDescending(n => n.Latest)];
    }

    /// <summary>
    /// Moves a newsletter's issues out of a mail account's folder and into the feeds store, so
    /// subscribing to one brings its back numbers to where the module actually reads.
    /// </summary>
    /// <remarks>
    /// Cross-store, so its shape is the one <c>FeedStoreMove</c> set for exactly this: copy
    /// every issue with its raw message, count what landed, and only then delete the originals.
    /// A move that cannot account for every issue leaves both copies and says so — a duplicate
    /// is an annoyance and a deletion is not.
    /// </remarks>
    /// <returns>How many issues now stand in the feeds store's folder.</returns>
    public static int Gather(OpenAccount feeds, OpenAccount from, long fromFolderId, FeedSubscription feed, string identity)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(feed);

        var moving = new List<(MessageSummary Summary, byte[] Raw)>();

        foreach (var message in from.Mail.Messages(fromFolderId, MostMessages))
        {
            var raw = from.Mail.LoadRaw(message.Id);
            if (raw is null) continue;

            var marks = Newsletters.Marks(raw);
            if (marks.IsNewsletter && string.Equals(marks.Identity, identity, StringComparison.OrdinalIgnoreCase))
            {
                moving.Add((message, raw));
            }
        }

        if (moving.Count == 0) return 0;

        var destination = FeedReceiver.EnsureFolder(feeds, feed);
        var copied = 0;

        foreach (var (summary, raw) in moving)
        {
            if (feeds.Mail.AddMessage(
                    destination.Id, summary with { Id = 0, FolderId = destination.Id }, raw) is not null)
            {
                copied++;
            }
        }

        if (copied < moving.Count)
        {
            Log.Warn($"Newsletters: only {copied} of {moving.Count} issue(s) reached the feeds "
                + "store, so the originals have been left in the inbox. Nothing has been lost; "
                + "some are in both places.");
            return copied;
        }

        // To Deleted Items rather than gone: the copy in the feeds store is the reading copy,
        // and the originals stay recoverable the way any deletion is.
        var ids = moving.Select(m => m.Summary.Id).ToList();
        if (from.Mail.FolderWithRole(from.Account.Id, FolderRole.Deleted) is { } deleted)
        {
            from.Mail.MoveMessages(ids, deleted.Id);
        }
        else
        {
            from.Mail.DeleteMessages(ids);
        }

        Log.Info($"Newsletters: {copied} issue(s) of “{feed.Name}” moved into the feeds store.");
        return copied;
    }
}
