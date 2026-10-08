using MailKit;
using MailKit.Security;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Protocols.OAuth;

/// <summary>
/// Signing in to a mail server, with either kind of credential.
/// </summary>
/// <remarks>
/// One place, so IMAP, POP3 and SMTP cannot come to differ about it. The token is fetched here
/// rather than earlier because fetching it is what renews it: an access token taken when the
/// account was loaded would be an hour stale on a client that has been running all day.
/// </remarks>
public static class SaslAuthentication
{
    /// <summary>
    /// Points a client at the reader's own certificate decisions before it connects.
    /// </summary>
    /// <remarks>
    /// Set on the client rather than passed to Connect, because the callback has to be in place
    /// before the handshake and there is no other moment. Where no trust store was handed in the
    /// client keeps the platform's own judgement, which refuses anything it cannot vouch for —
    /// the strict answer, and the right default.
    /// </remarks>
    public static void UseTrust(IMailService client, ServerSettings server)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (server.Trust is not { } trust) return;

        client.ServerCertificateValidationCallback = trust.For(server.Host, server.Port);
    }

    /// <summary>
    /// Authenticates a connected client: XOAUTH2 where the account signs in, a password
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// <see cref="SaslMechanismOAuth2"/> rather than passing the token as a password. The
    /// mechanism is a different SASL exchange, not a different string in the same one — a token
    /// handed to <c>AUTHENTICATE PLAIN</c> is rejected by every provider that issues one.
    /// </remarks>
    public static async Task AuthenticateAsync(
        IMailService client, ServerSettings server, CancellationToken cancellation)
    {
        if (server.Tokens is not { } tokens)
        {
            // No password for an account that signs in with one is a keyring that did not answer
            // or never held it — not something the server can be asked about. Sending an empty
            // password got "the server rejected the username or password", which sent the reader
            // to check a password that was right all along.
            if (server.UserName.Length > 0 && string.IsNullOrEmpty(server.Password))
                throw new PasswordUnavailableException();

            await client.AuthenticateAsync(server.UserName, server.Password, cancellation)
                .ConfigureAwait(false);
            return;
        }

        var token = await tokens.AccessTokenAsync(cancellation).ConfigureAwait(false);
        var user = server.UserName.Length > 0 ? server.UserName : tokens.UserName;

        // Said before the attempt rather than after: an XOAUTH2 failure against a server that
        // never advertised it looks identical to a bad password, and this is the line that says
        // which of the two was even being tried.
        if (!client.AuthenticationMechanisms.Contains("XOAUTH2")
            && !client.AuthenticationMechanisms.Contains("OAUTHBEARER"))
        {
            Log.Warn(
                $"{server.Host} did not offer XOAUTH2; the sign-in will be attempted anyway. "
                + $"It advertised: {string.Join(", ", client.AuthenticationMechanisms)}.");
        }

        await client.AuthenticateAsync(new SaslMechanismOAuth2(user, token), cancellation)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// An account that signs in with a password has none to sign in with: the desktop keyring did not
/// give it to Mailbox. Its message is the sentence the send/receive window shows.
/// </summary>
public sealed class PasswordUnavailableException()
    : Exception("Mailbox could not get this account's password from the desktop keyring. "
                + "Check that the keyring is unlocked, or enter the password again in Account Settings.");
