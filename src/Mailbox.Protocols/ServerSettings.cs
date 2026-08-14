using MailKit.Security;

namespace Mailbox.Protocols;

/// <summary>How to reach one server.</summary>
public sealed record ServerSettings(
    string Host,
    int Port,
    SecureSocketOptions Security = SecureSocketOptions.Auto,
    string UserName = "",
    string Password = "")
{
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

    /// <summary>Stop a single poll after this many messages, so a first run is interruptible.</summary>
    public int MaxPerPoll { get; init; } = 500;
}

/// <summary>Everything needed to poll and send for one account.</summary>
public sealed record AccountConnection(
    long AccountId,
    string Address,
    ServerSettings Incoming,
    ServerSettings Outgoing)
{
    public Pop3Policy Policy { get; init; } = new();
}
