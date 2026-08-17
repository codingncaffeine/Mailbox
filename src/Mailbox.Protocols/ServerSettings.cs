using MailKit.Security;
using Mailbox.Protocols.OAuth;
using Mailbox.Security.Tls;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>How to reach one server.</summary>
public sealed record ServerSettings(
    string Host,
    int Port,
    SecureSocketOptions Security = SecureSocketOptions.Auto,
    string UserName = "",
    string Password = "")
{
    /// <summary>
    /// Where to get a bearer token, for an account that signs in rather than holding a password.
    /// Null for the ordinary case, which is most of them.
    /// </summary>
    /// <remarks>
    /// A source rather than a token: an access token is good for about an hour, and a poll that
    /// took one at the moment the account was loaded would present a stale one on any run that
    /// had been up longer than that. Asking at authentication time is what makes the renewal
    /// happen where it can be reported.
    /// </remarks>
    public IAccessTokenSource? Tokens { get; init; }

    /// <summary>True when this server is reached with a token rather than a password.</summary>
    public bool UsesOAuth => Tokens is not null;

    /// <summary>
    /// Which server certificates the reader has agreed to, or null to accept only what the
    /// machine's own trust store vouches for.
    /// </summary>
    /// <remarks>
    /// Null is the strict answer and the right default: a connection with no trust store behind it
    /// refuses anything the platform will not vouch for, and says nothing about why. Handing one in
    /// is what lets the refusal be **explained and then answered** — the certificate is recorded
    /// rather than thrown away, so the caller can show it and ask.
    /// </remarks>
    public CertificateTrust? Trust { get; init; }

    /// <summary>True once there is enough here to attempt a connection.</summary>
    public bool IsComplete => Host.Length > 0 && Port > 0;

    public override string ToString() => $"{Host}:{Port} ({Security})";
}

/// <summary>
/// What to do with a message once it has been downloaded.
/// </summary>
/// <remarks>
/// POP3's oldest and sharpest edge. The default has to be to leave mail on the server: a client
/// that deletes by default will, the first time someone tries it beside an existing setup,
/// silently empty a mailbox they were still using elsewhere.
/// </remarks>
public sealed record Pop3Policy
{
    /// <summary>Leave downloaded mail where it is. The safe default, and the default.</summary>
    public bool LeaveOnServer { get; init; } = true;

    /// <summary>
    /// Remove mail from the server this many days after it was downloaded, or null to keep it
    /// indefinitely. Only consulted when <see cref="LeaveOnServer"/> is set.
    /// </summary>
    public int? DeleteAfterDays { get; init; }

    /// <summary>Remove from the server when it is deleted here.</summary>
    public bool DeleteWhenRemovedLocally { get; init; }

    /// <summary>
    /// Where a poll files what it downloads: a folder of the account, or null for its Inbox.
    /// The reference's Change Folder on the account list. A folder that no longer exists
    /// falls back to the Inbox rather than losing mail.
    /// </summary>
    public long? DeliveryFolderId { get; init; }

    /// <summary>Stop a single poll after this many messages, so a first run is interruptible.</summary>
    public int MaxPerPoll { get; init; } = 500;
}

/// <summary>
/// How much of an IMAP mailbox is kept here.
/// </summary>
/// <remarks>
/// The reference's "Mail to keep offline" slider. A mailbox is often years deep, and the first
/// sync of all of it is the difference between a client that is usable in a minute and one that
/// is downloading for an afternoon. Older mail stays on the server, and the folder says so.
/// </remarks>
public sealed record ImapPolicy
{
    /// <summary>Months of mail to keep here, counted from now; 0 keeps everything.</summary>
    public int OfflineMonths { get; init; } = 12;

    /// <summary>The oldest arrival worth downloading, or null for no limit.</summary>
    public DateTimeOffset? Cutoff(DateTimeOffset now) =>
        OfflineMonths <= 0 ? null : now.AddMonths(-OfflineMonths);
}

/// <summary>Everything needed to poll and send for one account.</summary>
public sealed record AccountConnection(
    long AccountId,
    string Address,
    ServerSettings Incoming,
    ServerSettings Outgoing)
{
    /// <summary>Which protocol collects this account's mail. POP3 unless said otherwise.</summary>
    public MailProtocol Protocol { get; init; } = MailProtocol.Pop3;

    public Pop3Policy Policy { get; init; } = new();

    public ImapPolicy Sync { get; init; } = new();
}
