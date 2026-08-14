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
            Policy = new Pop3Policy
            {
                LeaveOnServer = LeaveOnServer,
                DeleteAfterDays = DeleteAfterDays,
            },
        };
    }

    /// <summary>Turns an autoconfig guess into something storable.</summary>
    public static AccountSettings From(AutoconfigResult found) => new(
        found.Incoming.Host, found.Incoming.Port, found.Incoming.Security,
        found.Incoming.UserName,
        found.Outgoing.Host, found.Outgoing.Port, found.Outgoing.Security,
        found.Outgoing.UserName);
}
