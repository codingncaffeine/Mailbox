using Mailbox.Core.Diagnostics;

namespace Mailbox.Protocols.OAuth;

/// <summary>Somewhere to get a bearer token for one account, renewed as needed.</summary>
/// <remarks>
/// A seam rather than the class, because the sessions that authenticate with one belong to the
/// protocol layer and should not have to know whether a token came from a keyring, a sign-in or a
/// test.
/// </remarks>
public interface IAccessTokenSource
{
    /// <summary>The account the token belongs to, which SASL sends alongside it.</summary>
    string UserName { get; }

    /// <summary>
    /// A token good right now, renewing it first if it is not.
    /// </summary>
    /// <exception cref="OAuthException">
    /// When renewal failed. <see cref="OAuthException.NeedsSignIn"/> tells a caller whether to
    /// ask the user to sign in again or to treat it as a server being unreachable.
    /// </exception>
    Task<string> AccessTokenAsync(CancellationToken cancellation = default);
}

/// <summary>
/// Keeps one account signed in: the refresh token in the keyring, the access token in memory.
/// </summary>
/// <remarks>
/// The split is the whole point. A refresh token is a long-lived credential and belongs where
/// passwords go — the desktop keyring, never a file. An access token lasts about an hour,
/// is renewable from the other, and is therefore never written down at all: a copy on disk would
/// be a credential the user cannot revoke and did not know they had.
/// <para>
/// Renewals are single-flight. A send/receive opens IMAP and SMTP at once and both ask at the
/// same moment; letting each renew would spend two round trips to reach the same place, and with
/// a provider that rotates refresh tokens the second would be redeeming one the first had just
/// replaced.
/// </para>
/// </remarks>
public sealed class OAuthTokenSource : IAccessTokenSource, IDisposable
{
    /// <summary>
    /// How much life an access token must have left to be handed out. A token that dies during
    /// the round trip fails as an authentication error, which reads to the user as a bad password.
    /// </summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OAuthProvider _provider;
    private readonly string _clientId;
    private readonly string _account;
    private readonly ICredentialStore _secrets;
    private readonly OAuthFlow _flow;
    private readonly bool _ownsFlow;

    private OAuthTokens? _current;

    public OAuthTokenSource(
        OAuthProvider provider,
        string clientId,
        string account,
        ICredentialStore secrets,
        OAuthFlow? flow = null)
    {
        _provider = provider;
        _clientId = clientId;
        _account = account;
        _secrets = secrets;
        _flow = flow ?? new OAuthFlow();
        _ownsFlow = flow is null;
        UserName = account;
    }

    public string UserName { get; init; }

    /// <summary>The moment the token in hand expires, for a status line. Null when there is none.</summary>
    public DateTimeOffset? ExpiresAt => _current?.ExpiresAt;

    public async Task<string> AccessTokenAsync(CancellationToken cancellation = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_current is { } held && held.IsUsable(now, Margin)) return held.AccessToken;

        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Asked again inside the gate: whoever was ahead in the queue has probably just
            // renewed it, and this is what stops the rest of the queue doing it again.
            now = DateTimeOffset.UtcNow;
            if (_current is { } fresh && fresh.IsUsable(now, Margin)) return fresh.AccessToken;

            var refresh = _current?.RefreshToken is { Length: > 0 } known
                ? known
                : await _secrets.LoadAsync(_account, Credentials.OAuthRefresh, cancellation)
                    .ConfigureAwait(false) ?? string.Empty;

            if (refresh.Length == 0)
            {
                throw new OAuthException("invalid_grant",
                    $"There is no saved {_provider.Name} sign-in for {_account}.");
            }

            var renewed = await _flow.RefreshAsync(_provider, _clientId, refresh, cancellation)
                .ConfigureAwait(false);

            // A rotated refresh token replaces the one that bought it, and the old one usually
            // stops working the moment it is used — so this is written before the access token is
            // handed out, not after the poll it is about to serve.
            if (!string.Equals(renewed.RefreshToken, refresh, StringComparison.Ordinal))
            {
                await SaveRefreshTokenAsync(renewed, cancellation).ConfigureAwait(false);
            }

            _current = renewed;
            Log.Info($"Renewed the {_provider.Name} sign-in for {_account}.");
            return renewed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes what a sign-in produced: the refresh token to the keyring, the rest in hand.</summary>
    public async Task AdoptAsync(OAuthTokens tokens, CancellationToken cancellation = default)
    {
        _current = tokens;
        if (tokens.RefreshToken.Length > 0) await SaveRefreshTokenAsync(tokens, cancellation).ConfigureAwait(false);
    }

    /// <summary>Forgets the account's sign-in, here and in the keyring.</summary>
    public async Task ForgetAsync(CancellationToken cancellation = default)
    {
        _current = null;
        await _secrets.DeleteAsync(_account, Credentials.OAuthRefresh, cancellation).ConfigureAwait(false);
    }

    private async Task SaveRefreshTokenAsync(OAuthTokens tokens, CancellationToken cancellation)
    {
        var saved = await _secrets
            .SaveAsync(_account, Credentials.OAuthRefresh, tokens.RefreshToken, cancellation)
            .ConfigureAwait(false);

        if (!saved)
        {
            Log.Warn(
                $"The {_provider.Name} sign-in for {_account} could only be kept for this session: "
                + $"secrets are going to {_secrets.Description}.");
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsFlow) _flow.Dispose();
    }
}
