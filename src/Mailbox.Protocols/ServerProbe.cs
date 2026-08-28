using Mailbox.Core.Diagnostics;

namespace Mailbox.Protocols;

/// <summary>What a server said when we asked, before an account was saved.</summary>
public sealed record ProbeResult(bool Reached, bool CanAuthenticate, string Explanation)
{
    /// <summary>True when nothing needs saying: the server is there and will take a login.</summary>
    public bool IsClear => Reached && CanAuthenticate;
}

/// <summary>
/// Asks a sending server whether it will accept a login at all, before the account is saved.
/// </summary>
/// <remarks>
/// There is one failure this exists for. Microsoft consumer mailboxes created from 2025 onward
/// frequently have SMTP AUTH switched off at the tenant, and a client that finds out at the
/// first send reports it as an authentication failure — which sends the user to check a
/// password that was never the problem. The server says so on connecting, before any
/// credential is offered, so the wizard can say it in words instead.
/// <para>
/// It connects and disconnects. Nothing is sent, and no password is offered: what is being
/// read is the greeting, which is public.
/// </para>
/// </remarks>
public sealed class ServerProbe(Func<ISmtpSession>? session = null)
{
    private readonly Func<ISmtpSession> _session = session ?? (() => new MailKitSmtpSession());

    /// <summary>
    /// Reaches the incoming server, so whatever it is going to object to is objected to now.
    /// </summary>
    /// <remarks>
    /// Checking only the outgoing server was half a check. An account whose incoming host
    /// presents a certificate for another name — which is what shared hosting looks like, and
    /// which the outgoing host may well have already been trusted for under a different name —
    /// was added cleanly and then failed on every send/receive afterwards, with the question that
    /// would have settled it never asked. Reaching both means both are settled once, at setup,
    /// where the reader is already answering questions about this account.
    /// <para>
    /// Connect only, and no credentials: this is about whether the server is there and whether
    /// its certificate is acceptable, and a password is checked by the first real poll. Trying to
    /// authenticate here would turn a mistyped password into a refusal to add the account at all.
    /// </para>
    /// </remarks>
    public async Task<ProbeResult> CheckReceivingAsync(
        ServerSettings server, MailProtocolKind protocol, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        using var client = protocol == MailProtocolKind.Imap
            ? (IDisposable)new MailKitImapSession()
            : new MailKitPop3Session();

        try
        {
            switch (client)
            {
                case MailKitImapSession imap: await imap.ConnectAsync(server, cancellation); break;
                case MailKitPop3Session pop: await pop.ConnectAsync(server, cancellation); break;
            }

            return new ProbeResult(Reached: true, CanAuthenticate: true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeResult(
                Reached: false,
                CanAuthenticate: false,
                $"{server.Host} could not be reached: {ex.Message}");
        }
    }

    public async Task<ProbeResult> CheckSendingAsync(
        ServerSettings server, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var client = _session();

        try
        {
            await client.ConnectAsync(server, cancellation);

            if (client.AuthenticationMechanisms.Count > 0)
            {
                return new ProbeResult(Reached: true, CanAuthenticate: true, string.Empty);
            }

            return new ProbeResult(
                Reached: true,
                CanAuthenticate: false,
                $"{server.Host} accepted the connection but offers no way to sign in. "
                + "Sending is switched off for this mailbox rather than misconfigured here — "
                + "on a Microsoft mailbox it is the SMTP AUTH setting, which an administrator "
                + "turns on per mailbox. Receiving will work; sending will be refused until it is.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not reaching the server is worth saying too, and it is a different sentence: the
            // address may be wrong, or the machine may simply be offline.
            Log.Info($"Could not reach {server.Host}:{server.Port} while checking the account.");

            return new ProbeResult(
                Reached: false,
                CanAuthenticate: false,
                $"Could not reach {server.Host} on port {server.Port}. {SmtpSender.Classify(ex).Error}");
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(CancellationToken.None);
            client.Dispose();
        }
    }
}
