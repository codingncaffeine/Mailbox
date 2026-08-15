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
}

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
