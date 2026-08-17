using MailKit.Security;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Protocols.OAuth;
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
    /// How this account proves who it is. A password unless the provider stopped accepting one.
    /// </summary>
    /// <remarks>
    /// Persisted rather than worked out from the address each time. Autoconfig's answer is a
    /// guess about a domain, and an account signed in with a client the user registered is not a
    /// guess — nor is a domain that moved to OAuth after the account was added, which has to keep
    /// working with the password it already has until someone signs in.
    /// </remarks>
    public AuthKind Auth { get; init; } = AuthKind.Password;

    /// <summary>Which authorization server this account signs in to, when it does.</summary>
    public string OAuthProviderId { get; init; } = string.Empty;

    /// <summary>
    /// The client registration used to sign in — empty for the provider's own.
    /// </summary>
    /// <remarks>
    /// In the settings file, not the keyring. A native application's client ID is an identifier
    /// that travels in a URL the browser shows; treating it as a secret would only make it harder
    /// for the person who pasted it to check what they pasted.
    /// </remarks>
    public string OAuthClientId { get; init; } = string.Empty;

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
            Auth = (AuthKind)(int)settings.GetNumber(Key(accountKey, "auth"), (int)AuthKind.Password),
            OAuthProviderId = settings.GetString(Key(accountKey, "oauth.provider")),
            OAuthClientId = settings.GetString(Key(accountKey, "oauth.client")),
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
        settings.Set(Key(accountKey, "auth"), (int)Auth);
        settings.Set(Key(accountKey, "oauth.provider"), OAuthProviderId);
        settings.Set(Key(accountKey, "oauth.client"), OAuthClientId);
    }

    /// <summary>
    /// How to reach the account's ManageSieve server: the incoming server's host unless the
    /// account names another, port 4190, STARTTLS required, and the incoming credentials. Null
    /// for a POP3 account, which has no server-side rules.
    /// </summary>
    public ServerSettings? ToSieveServer(Account account, ICredentialStore secrets,
        OAuthAccounts? oauth = null)
    {
        if (account.Protocol != MailProtocol.Imap) return null;

        var host = SieveHost.Length > 0 ? SieveHost : IncomingHost;
        if (host.Length == 0) return null;

        var password = secrets.LoadAsync(account.Address, Credentials.Incoming).GetAwaiter().GetResult() ?? string.Empty;
        return new ServerSettings(host, SievePort > 0 ? SievePort : 4190, SecureSocketOptions.StartTls,
            IncomingUser.Length > 0 ? IncomingUser : account.Address, password)
        {
            Tokens = TokensFor(account.Address, oauth),
        };
    }

    /// <summary>
    /// Builds what the transfer service needs, fetching passwords from the keyring at the last
    /// moment. They are held for the length of one send/receive and no longer.
    /// </summary>
    public AccountConnection ToConnection(Account account, ICredentialStore secrets,
        OAuthAccounts? oauth = null)
    {
        var tokens = TokensFor(account.Address, oauth);

        // Not read at all for an account that signs in. Loading them anyway would put a prompt
        // in front of a locked keyring for a password that is not there and would not be used.
        var incomingPassword = tokens is not null
            ? string.Empty
            : secrets.LoadAsync(account.Address, Credentials.Incoming).GetAwaiter().GetResult() ?? string.Empty;

        var outgoingPassword = tokens is not null
            ? string.Empty
            : secrets.LoadAsync(account.Address, Credentials.Outgoing).GetAwaiter().GetResult()
              ?? incomingPassword;

        return new AccountConnection(
            account.Id,
            account.Address,
            new ServerSettings(IncomingHost, IncomingPort, IncomingSecurity,
                IncomingUser.Length > 0 ? IncomingUser : account.Address, incomingPassword)
            {
                Tokens = tokens,
            },
            new ServerSettings(OutgoingHost, OutgoingPort, OutgoingSecurity,
                OutgoingUser, outgoingPassword)
            {
                // The same sign-in covers both directions: one consent, one refresh token, and
                // both scopes were asked for together.
                Tokens = tokens,
            })
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

    /// <summary>How this account authenticates, with the rule about what wins (see the type).</summary>
    public AccountAuth Authentication => new(Auth, OAuthProviderId, OAuthClientId);

    private IAccessTokenSource? TokensFor(string address, OAuthAccounts? oauth)
        => Authentication.Source(address, oauth);

    /// <summary>Turns an autoconfig guess into something storable.</summary>
    public static AccountSettings From(AutoconfigResult found) => new(
        found.Incoming.Host, found.Incoming.Port, found.Incoming.Security,
        found.Incoming.UserName,
        found.Outgoing.Host, found.Outgoing.Port, found.Outgoing.Security,
        found.Outgoing.UserName)
    {
        Auth = found.Auth,
        OAuthProviderId = found.Auth == AuthKind.OAuth2
            ? OAuthProviders.ForMail(found.Incoming.UserName)?.Id ?? string.Empty
            : string.Empty,
    };
}
