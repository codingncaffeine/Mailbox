namespace Mailbox.Store;

/// <summary>How an account talks to its server.</summary>
public enum MailProtocol
{
    Pop3,
    Imap,
}

/// <summary>
/// What a folder is for, independent of what the server calls it.
/// </summary>
/// <remarks>
/// Servers name these in whatever language they were configured in, and POP3 has no folders at
/// all, so the shell asks for a role rather than matching on "Inbox".
/// </remarks>
public enum FolderRole
{
    None,
    Inbox,
    Drafts,
    Outbox,
    Sent,
    Deleted,
    Junk,
    Archive,
}

public sealed record Account(
    long Id,
    string Address,
    string DisplayName,
    MailProtocol Protocol,
    int Ordinal,
    DateTimeOffset Created)
{
    /// <summary>What the account list's Type column shows.</summary>
    public string TypeLabel => Protocol == MailProtocol.Imap ? "IMAP/SMTP" : "POP/SMTP";
}

public sealed record Folder(
    long Id,
    long AccountId,
    long? ParentId,
    string Name,
    FolderRole Role,
    int Ordinal)
{
    /// <summary>Unread count, filled in by the repository when asked for.</summary>
    public int Unread { get; init; }

    public int Total { get; init; }

    /// <summary>
    /// The server folder this one stands for, as IMAP names it, or null for a folder that
    /// exists only here — every folder of a POP3 account, and the Outbox of any.
    /// </summary>
    public string? ImapPath { get; init; }

    /// <summary>True when the server folder is pulled as well as listed.</summary>
    public bool Synced { get; init; } = true;

    /// <summary>The server's UIDVALIDITY when this folder was last synced, or null before the first.</summary>
    public long? UidValidity { get; init; }

    /// <summary>The server's UIDNEXT as of the last sync.</summary>
    public long? UidNext { get; init; }

    /// <summary>The highest MODSEQ seen, for asking only what changed since.</summary>
    public long? HighestModSeq { get; init; }

    /// <summary>A folder that stands for one on the server.</summary>
    public bool IsMapped => ImapPath is not null;
}

/// <summary>What one entry in the sync journal asks the server to do.</summary>
public enum SyncOpKind
{
    /// <summary>Set or clear one flag on a message.</summary>
    Flags,

    /// <summary>Move a message from one server folder to another.</summary>
    Move,

    /// <summary>Remove a message from the server for good.</summary>
    Delete,

    /// <summary>Put a message that exists only here onto the server.</summary>
    Append,
}

/// <summary>Which flag a <see cref="SyncOpKind.Flags"/> op sets or clears.</summary>
public enum SyncFlag
{
    Seen,
    Flagged,
}

/// <summary>
/// One entry in the sync journal: a local change waiting to be played to the server.
/// </summary>
/// <param name="FolderId">The folder the message is in on the server when the op is played.</param>
/// <param name="ServerUid">Its UID there; null for an append, which has none yet.</param>
/// <param name="MessageId">The local row, or null once it has gone.</param>
/// <param name="TargetFolderId">Where a move goes.</param>
public sealed record SyncOp(
    long Id,
    SyncOpKind Kind,
    long FolderId,
    string? ServerUid,
    long? MessageId,
    long? TargetFolderId,
    SyncFlag? Flag,
    bool? Value,
    DateTimeOffset Created,
    int Attempts,
    string? LastError);

/// <summary>
/// A message as the list needs it: enough to draw a row, without the body.
/// </summary>
/// <remarks>
/// The raw message is a blob loaded separately. A folder of ten thousand messages is ten
/// thousand of these and no megabytes of MIME.
/// </remarks>
public sealed record MessageSummary(
    long Id,
    long FolderId,
    string? ServerUid,
    string? MessageId,
    string FromName,
    string FromAddress,
    string Subject,
    string Preview,
    DateTimeOffset? Sent,
    DateTimeOffset Received,
    long SizeBytes,
    bool IsRead,
    bool IsFlagged,
    bool HasAttachment) : Lists.IArrangeable
{
    /// <summary>Who the row shows: the display name when there is one, else the address.</summary>
    public string DisplayFrom => FromName.Length > 0 ? FromName : FromAddress;

    /// <summary>
    /// The message's plain text, for the search index. Not shown anywhere and not the preview —
    /// the whole body, so a search reaches a word buried in it. Empty for a row built by hand.
    /// </summary>
    public string BodyText { get; init; } = string.Empty;

    /// <summary>When a follow-up is due, or null for a flag with no date or no flag at all.</summary>
    public DateTimeOffset? FollowUpDue { get; init; }

    /// <summary>Whether a follow-up has been marked complete — a check where the flag was.</summary>
    public bool FollowUpComplete { get; init; }

    /// <summary>What the flag says — "Follow up", "Call", "Review" — or null for the plain flag.</summary>
    public string? FollowUpType { get; init; }

    /// <summary>When the follow-up starts, if a start was set.</summary>
    public DateTimeOffset? FollowUpStart { get; init; }

    /// <summary>When to be reminded, or null for no reminder or one already dismissed.</summary>
    public DateTimeOffset? Reminder { get; init; }

    /// <summary>When a snoozed message comes back, or null for one that is not snoozed (§12).</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }

    /// <summary>0 low, 1 normal, 2 high — the message's own Importance/X-Priority, for the list's column and the searches.</summary>
    public int Importance { get; init; } = 1;

    /// <summary>
    /// A feed item's own address, and the picture to show beside it. Both empty for a message
    /// that did not come from a feed, which is nearly all of them.
    /// </summary>
    /// <remarks>
    /// Columns rather than headers because the article list draws a thumbnail on every visible
    /// row: reading these out of the MIME would mean loading and parsing every message on screen
    /// to lay out a list.
    /// </remarks>
    public string FeedLink { get; init; } = string.Empty;

    public string FeedImage { get; init; } = string.Empty;

    /// <summary>True for a message a feed delivered.</summary>
    public bool IsFeedItem => FeedLink.Length > 0 || FeedImage.Length > 0;

    /// <summary>Focused Inbox (§12): true for Focused, false for Other. Meaningful in the Inbox only.</summary>
    public bool IsFocused { get; init; } = true;

    /// <summary>The To addresses, lower-cased, for the searches that ask who a message went to.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>The Cc addresses, lower-cased.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>When the message says it expires (its Expires header), or null — what AutoArchive's "delete expired items" reads.</summary>
    public DateTimeOffset? Expires { get; init; }

    /// <summary>
    /// True for a row the server described and whose message has not been fetched: Send/Receive's
    /// Download Headers writes these, and Process Marked Headers turns them into whole messages.
    /// </summary>
    public bool HeaderOnly { get; init; }

    /// <summary>True when the reader has asked for this header's message on the next send/receive.</summary>
    public bool MarkedForDownload { get; init; }
}

/// <summary>
/// What checking a message's own signatures came to, and when.
/// </summary>
/// <remarks>
/// The verdict is spelled as <c>Mailbox.Security</c>'s <c>AuthVerdict</c> spells it, in lower
/// case. The store does not reference that project — a store that knows about verdicts is a
/// store that has to change when one is added — so it keeps the word and lets the caller read
/// it back into the enum.
/// </remarks>
public sealed record MessageAuthentication(string Dkim, string? SigningDomain, DateTimeOffset Checked);

/// <summary>
/// A message in the Recover Deleted Items holding area (§11): what is left of its row, enough to
/// list it and put it back.
/// </summary>
public sealed record RecoverableMessage(
    long Id,
    long? OriginalFolderId,
    string OriginalFolderName,
    string FromName,
    string FromAddress,
    string Subject,
    DateTimeOffset Received,
    DateTimeOffset Deleted,
    long SizeBytes)
{
    public string DisplayFrom => FromName.Length > 0 ? FromName : FromAddress;
}

/// <summary>A saved search, listed under Search Folders in the folder pane.</summary>
public sealed record SearchFolder(long Id, string Name, int Ordinal, Mailbox.Core.Search.SearchFolderQuery Query);

/// <summary>A named colour a message can carry.</summary>
public sealed record Category(long Id, string Name, string ColourToken, string? Shortcut, int Ordinal);

/// <summary>
/// One entry in the Auto-Complete List: an address mail has gone to, the name it went under,
/// and how much it has been used.
/// </summary>
public sealed record Nickname(string Address, string DisplayName, int Weight, DateTimeOffset LastUsed)
{
    /// <summary>The entry as the To line writes it: <c>Name &lt;address&gt;</c>, or the address alone.</summary>
    public string Formatted => DisplayName.Length > 0 ? $"{DisplayName} <{Address}>" : Address;
}

/// <summary>State of one message in the send queue.</summary>
public enum OutboxState
{
    Queued,
    Sending,
    Sent,
    Failed,

    /// <summary>Held back by the user — Work Offline, or an explicit hold.</summary>
    Held,
}

public sealed record OutboxItem(
    long Id,
    long AccountId,
    long BlobId,
    OutboxState State,
    int Attempts,
    DateTimeOffset Queued,
    DateTimeOffset? NextTry,
    string? LastError);

/// <summary>What the mail server runs for the account: the Sieve script Mailbox last put there.</summary>
/// <param name="Script">The script as published.</param>
/// <param name="Include">The script that was active before Mailbox's, included first — or null.</param>
/// <param name="Published">When it was put there.</param>
/// <param name="Stale">True when the rules or folder names have changed since, or a publish failed.</param>
public sealed record SieveState(string Script, string? Include, DateTimeOffset Published, bool Stale);

/// <summary>A view a reader saved by name — the definition is Core's MailView document.</summary>
public sealed record SavedView(long Id, string Name, string Definition);
