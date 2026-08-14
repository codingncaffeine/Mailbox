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

    private static string Key(long accountId, string field) => $"account.{accountId}.{field}";

    public static AccountSettings? Load(SettingsStore settings, long accountId)
    {
        var host = settings.GetString(Key(accountId, "incoming.host"));
        if (host.Length == 0) return null;

        return new AccountSettings(
            host,
            (int)settings.GetNumber(Key(accountId, "incoming.port"), 995),
            (SecureSocketOptions)(int)settings.GetNumber(
                Key(accountId, "incoming.security"), (int)SecureSocketOptions.SslOnConnect),
            settings.GetString(Key(accountId, "incoming.user")),
            settings.GetString(Key(accountId, "outgoing.host")),
            (int)settings.GetNumber(Key(accountId, "outgoing.port"), 587),
            (SecureSocketOptions)(int)settings.GetNumber(
                Key(accountId, "outgoing.security"), (int)SecureSocketOptions.StartTls),
            settings.GetString(Key(accountId, "outgoing.user")))
        {
            LeaveOnServer = settings.GetBool(Key(accountId, "leaveonserver"), true),
            DeleteAfterDays = settings.Has(Key(accountId, "deleteafterdays"))
                ? (int)settings.GetNumber(Key(accountId, "deleteafterdays"))
                : null,
        };
    }

    public void Save(SettingsStore settings, long accountId)
    {
        settings.Set(Key(accountId, "incoming.host"), IncomingHost);
        settings.Set(Key(accountId, "incoming.port"), IncomingPort);
        settings.Set(Key(accountId, "incoming.security"), (int)IncomingSecurity);
        settings.Set(Key(accountId, "incoming.user"), IncomingUser);
        settings.Set(Key(accountId, "outgoing.host"), OutgoingHost);
        settings.Set(Key(accountId, "outgoing.port"), OutgoingPort);
        settings.Set(Key(accountId, "outgoing.security"), (int)OutgoingSecurity);
        settings.Set(Key(accountId, "outgoing.user"), OutgoingUser);
        settings.Set(Key(accountId, "leaveonserver"), LeaveOnServer);
        if (DeleteAfterDays is { } days) settings.Set(Key(accountId, "deleteafterdays"), days);
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
