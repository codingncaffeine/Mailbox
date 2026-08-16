using MailKit.Security;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App;

/// <summary>
/// The server details for one account, kept beside the other preferences.
/// </summary>
/// <remarks>
/// In the settings file rather than the mail store, and passwords in neither: the store holds
/// mail, the settings file holds choices, and the keyring holds secrets. Keeping them apart
/// means a store handed to someone for debugging carries no credentials, and a settings file
/// copied between machines carries no mail.
/// </remarks>
public sealed record AccountSettings(
    string IncomingHost,
    int IncomingPort,
    SecureSocketOptions IncomingSecurity,
    string IncomingUser,
    string OutgoingHost,
    int OutgoingPort,
    SecureSocketOptions OutgoingSecurity,
    string OutgoingUser)
{
    public bool LeaveOnServer { get; init; } = true;

    public int? DeleteAfterDays { get; init; }

    /// <summary>
    /// Months of an IMAP mailbox to keep offline, from the reference's "Mail to keep offline"
    /// slider; 0 keeps everything. Ignored for POP3, which downloads what the server hands it.
    /// </summary>
    public int OfflineMonths { get; init; } = 12;

    /// <summary>
    /// The ManageSieve server for server-side rules — empty for the incoming server's host,
    /// which is where it nearly always is. IMAP only.
    /// </summary>
    public string SieveHost { get; init; } = string.Empty;

    /// <summary>ManageSieve's port; 4190 by convention.</summary>
    public int SievePort { get; init; } = 4190;

    /// <summary>
    /// The folder a POP3 poll delivers into, or null for the Inbox — the reference's Change
    /// Folder. A folder id within the account's own file, as the rules keep theirs: the file is
    /// the account, so the id survives a backup and restore with it.
    /// </summary>
    public long? DeliveryFolderId { get; init; }

    /// <summary>
    /// Settings key off the address, not the row id. A row id belongs to one store file, and
    /// the point of a file per account is that it can be restored or copied — after which the
    /// id may differ but the address does not.
    /// </summary>
    private static string Key(string address, string field) => $"account.{address}.{field}";

    public static AccountSettings? Load(SettingsStore settings, string address)
    {
        var accountKey = address;
        var host = settings.GetString(Key(accountKey, "incoming.host"));
        if (host.Length == 0) return null;

        return new AccountSettings(
            host,
            (int)settings.GetNumber(Key(accountKey, "incoming.port"), 995),
            (SecureSocketOptions)(int)settings.GetNumber(
                Key(accountKey, "incoming.security"), (int)SecureSocketOptions.SslOnConnect),
            settings.GetString(Key(accountKey, "incoming.user")),
            settings.GetString(Key(accountKey, "outgoing.host")),
            (int)settings.GetNumber(Key(accountKey, "outgoing.port"), 587),
            (SecureSocketOptions)(int)settings.GetNumber(
                Key(accountKey, "outgoing.security"), (int)SecureSocketOptions.StartTls),
            settings.GetString(Key(accountKey, "outgoing.user")))
        {
            LeaveOnServer = settings.GetBool(Key(accountKey, "leaveonserver"), true),
            DeleteAfterDays = settings.Has(Key(accountKey, "deleteafterdays"))
                ? (int)settings.GetNumber(Key(accountKey, "deleteafterdays"))
                : null,
            OfflineMonths = (int)settings.GetNumber(Key(accountKey, "offlinemonths"), 12),
            SieveHost = settings.GetString(Key(accountKey, "sieve.host")),
            SievePort = (int)settings.GetNumber(Key(accountKey, "sieve.port"), 4190),
            DeliveryFolderId = settings.Has(Key(accountKey, "delivery.folder"))
                ? (long)settings.GetNumber(Key(accountKey, "delivery.folder"))
                : null,
        };
    }

    public void Save(SettingsStore settings, string address)
    {
        var accountKey = address;
        settings.Set(Key(accountKey, "incoming.host"), IncomingHost);
        settings.Set(Key(accountKey, "incoming.port"), IncomingPort);
        settings.Set(Key(accountKey, "incoming.security"), (int)IncomingSecurity);
        settings.Set(Key(accountKey, "incoming.user"), IncomingUser);
        settings.Set(Key(accountKey, "outgoing.host"), OutgoingHost);
        settings.Set(Key(accountKey, "outgoing.port"), OutgoingPort);
        settings.Set(Key(accountKey, "outgoing.security"), (int)OutgoingSecurity);
        settings.Set(Key(accountKey, "outgoing.user"), OutgoingUser);
        settings.Set(Key(accountKey, "leaveonserver"), LeaveOnServer);
        if (DeleteAfterDays is { } days) settings.Set(Key(accountKey, "deleteafterdays"), days);
        settings.Set(Key(accountKey, "offlinemonths"), OfflineMonths);
        settings.Set(Key(accountKey, "sieve.host"), SieveHost);
        settings.Set(Key(accountKey, "sieve.port"), SievePort);
        if (DeliveryFolderId is { } folder) settings.Set(Key(accountKey, "delivery.folder"), folder);
        else settings.Remove(Key(accountKey, "delivery.folder"));
    }

    /// <summary>
    /// How to reach the account's ManageSieve server: the incoming server's host unless the
    /// account names another, port 4190, STARTTLS required, and the incoming credentials. Null
    /// for a POP3 account, which has no server-side rules.
    /// </summary>
    public ServerSettings? ToSieveServer(Account account, ICredentialStore secrets)
    {
        if (account.Protocol != MailProtocol.Imap) return null;

        var host = SieveHost.Length > 0 ? SieveHost : IncomingHost;
        if (host.Length == 0) return null;

        var password = secrets.LoadAsync(account.Address, Credentials.Incoming).GetAwaiter().GetResult() ?? string.Empty;
        return new ServerSettings(host, SievePort > 0 ? SievePort : 4190, SecureSocketOptions.StartTls,
            IncomingUser.Length > 0 ? IncomingUser : account.Address, password);
    }

    /// <summary>
    /// Builds what the transfer service needs, fetching passwords from the keyring at the last
    /// moment. They are held for the length of one send/receive and no longer.
    /// </summary>
    public AccountConnection ToConnection(Account account, ICredentialStore secrets)
    {
        var incomingPassword = secrets
            .LoadAsync(account.Address, Credentials.Incoming).GetAwaiter().GetResult() ?? string.Empty;

        var outgoingPassword = secrets
            .LoadAsync(account.Address, Credentials.Outgoing).GetAwaiter().GetResult()
            ?? incomingPassword;

        return new AccountConnection(
            account.Id,
            account.Address,
            new ServerSettings(IncomingHost, IncomingPort, IncomingSecurity,
                IncomingUser.Length > 0 ? IncomingUser : account.Address, incomingPassword),
            new ServerSettings(OutgoingHost, OutgoingPort, OutgoingSecurity,
                OutgoingUser, outgoingPassword))
        {
            // The account's own protocol decides which collector runs. Everything else is
            // shared: one send path, one outbox, one store.
            Protocol = account.Protocol,
            Policy = new Pop3Policy
            {
                LeaveOnServer = LeaveOnServer,
                DeleteAfterDays = DeleteAfterDays,
                DeliveryFolderId = DeliveryFolderId,
            },
            Sync = new ImapPolicy { OfflineMonths = OfflineMonths },
        };
    }

    /// <summary>Turns an autoconfig guess into something storable.</summary>
    public static AccountSettings From(AutoconfigResult found) => new(
        found.Incoming.Host, found.Incoming.Port, found.Incoming.Security,
        found.Incoming.UserName,
        found.Outgoing.Host, found.Outgoing.Port, found.Outgoing.Security,
        found.Outgoing.UserName);
}
