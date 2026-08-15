using Mailbox.Core.Diagnostics;
using Mailbox.Core.Rules;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App;

/// <summary>
/// The application's side of server-side rules: which accounts can have them, what their
/// servers can do (remembered between runs), and putting the rules there — from the Rules and
/// Alerts dialog as rules change, and again after a send/receive for any account whose server
/// is behind.
/// </summary>
/// <remarks>
/// Every publish goes through <see cref="SievePublisher"/> off the UI thread. The store says
/// whether the server is current; this class only decides when to try. A harness run
/// (<c>MAILBOX_CAPTURE</c>) never touches a server: it may pose accounts that do not exist.
/// </remarks>
public static class SieveSync
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>True for an account that can have rules on the server: IMAP, with an incoming host.</summary>
    public static bool Supports(OpenAccount account)
        => account.Account.Protocol == MailProtocol.Imap
           && AccountSettings.Load(App.Settings, account.Account.Address) is { IncomingHost.Length: > 0 };

    /// <summary>The ManageSieve server for an account, or null when there is none to speak of.</summary>
    public static ServerSettings? ServerFor(OpenAccount account)
        => AccountSettings.Load(App.Settings, account.Account.Address)?.ToSieveServer(account.Account, App.Secrets);

    // ---- What the server can do, remembered ------------------------------------------------------

    private static string Key(string address, string field) => $"account.{address}.sieve.{field}";

    /// <summary>The extensions the server advertised last time it was asked, or null when it never was.</summary>
    public static IReadOnlySet<string>? KnownExtensions(OpenAccount account)
    {
        var key = Key(account.Account.Address, "extensions");
        if (!App.Settings.Has(key)) return null;
        return App.Settings.GetString(key).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Asks the server what it can do, remembers the answer, and returns it. Throws when it cannot be reached or refuses the sign-in.</summary>
    public static async Task<IReadOnlySet<string>> ProbeAsync(OpenAccount account, CancellationToken cancellation = default)
    {
        // Off the UI thread from the start: the password comes from the keyring, which can be slow to answer.
        var capabilities = await Task.Run(() =>
        {
            if (ServerFor(account) is not { } server) throw new InvalidOperationException("The account has no server for rules.");
            return SievePublisher.ProbeAsync(server, cancellation);
        }, cancellation);
        App.Settings.Set(Key(account.Account.Address, "extensions"), string.Join(' ', capabilities.Extensions.Order(StringComparer.Ordinal)));
        App.Settings.Set(Key(account.Account.Address, "implementation"), capabilities.Implementation);
        Log.Info($"Sieve: {account.Account.Address} — {capabilities.Implementation} offers {capabilities.Extensions.Count} extensions.");
        return capabilities.Extensions;
    }

    /// <summary>The compiler's view of an account, from what is remembered about its server.</summary>
    public static SieveContext ContextFor(OpenAccount account, IReadOnlySet<string> extensions) => new()
    {
        OwnAddresses = [account.Account.Address],
        FolderPath = id => account.Mail.GetFolder(id)?.ImapPath,
        DeletedItemsPath = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Deleted)?.ImapPath,
        Extensions = extensions,
    };

    // ---- Publishing ------------------------------------------------------------------------------

    /// <summary>Puts the account's server-side rules on its server, or takes them down when none are left. Never throws.</summary>
    public static async Task<SievePublishOutcome> PublishAsync(OpenAccount account, CancellationToken cancellation = default)
    {
        if (Theming.WindowCapture.IsRequested)
        {
            return new SievePublishOutcome(true, "Server rules are not published from a capture run.");
        }

        await Gate.WaitAsync(cancellation);
        try
        {
            return await Task.Run(() =>
            {
                if (ServerFor(account) is not { } server)
                {
                    return Task.FromResult(new SievePublishOutcome(false, "The account has no server for rules."));
                }

                return SievePublisher.PublishAsync(
                    server, account.Mail, account.Account.Id, [account.Account.Address], null, cancellation);
            }, cancellation);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// After a send/receive: any account whose server is behind gets another try. Quiet — the
    /// outcome is logged, and the Rules and Alerts dialog shows the state next time it opens.
    /// </summary>
    public static async Task RepublishStaleAsync()
    {
        if (Theming.WindowCapture.IsRequested) return;

        foreach (var account in App.Accounts.All)
        {
            if (!Supports(account)) continue;
            var behind = account.Mail.SieveState() is { } state
                ? state.Stale
                : account.Mail.Rules().Any(r => r.Enabled && r.ServerSide);
            if (!behind) continue;

            var outcome = await PublishAsync(account);
            Log.Info($"Sieve: retry for {account.Account.Address} — {outcome.Message}");
        }
    }
}
