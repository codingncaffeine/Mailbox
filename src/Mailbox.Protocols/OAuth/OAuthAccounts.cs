using System.Collections.Concurrent;

namespace Mailbox.Protocols.OAuth;

/// <summary>
/// One token source per account, for the life of the application.
/// </summary>
/// <remarks>
/// The access token is the thing being kept: it is good for about an hour, and a source built
/// fresh for each send/receive would throw it away and buy another every few minutes — a round
/// trip to the provider before every poll, and with a provider that rotates refresh tokens, a new
/// long-lived credential written to the keyring each time.
/// <para>
/// Keyed by address for the same reason account settings are (§4): a row id belongs to one store
/// file, and the point of a file per account is that it can be restored somewhere the id differs.
/// </para>
/// </remarks>
public sealed class OAuthAccounts(ICredentialStore secrets) : IDisposable
{
    private readonly ConcurrentDictionary<string, OAuthTokenSource> _sources =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The source for an account, made on first use.
    /// </summary>
    /// <remarks>
    /// A client ID that has changed — the user pasted a different registration — replaces the
    /// source rather than being ignored, because the refresh token belonging to the old client
    /// will not be redeemed by the new one and the failure is otherwise silent until the next
    /// renewal.
    /// </remarks>
    public OAuthTokenSource For(string address, OAuthProvider provider, string clientId)
    {
        var wanted = new Signature(provider.Id, clientId);

        return _sources.AddOrUpdate(
            address,
            _ => Make(address, provider, clientId),
            (_, existing) =>
            {
                if (Signatures.TryGetValue(address, out var held) && held == wanted) return existing;

                existing.Dispose();
                return Make(address, provider, clientId);
            });
    }

    /// <summary>Whether an account has a source in hand, without making one.</summary>
    public bool Has(string address) => _sources.ContainsKey(address);

    /// <summary>Forgets an account's sign-in — the source and the saved refresh token both.</summary>
    public async Task ForgetAsync(string address, CancellationToken cancellation = default)
    {
        if (_sources.TryRemove(address, out var source))
        {
            await source.ForgetAsync(cancellation).ConfigureAwait(false);
            source.Dispose();
            return;
        }

        // No source made this session does not mean no sign-in saved: an account added on a
        // previous run has a refresh token in the keyring and nothing here.
        await secrets.DeleteAsync(address, Credentials.OAuthRefresh, cancellation).ConfigureAwait(false);
    }

    private readonly record struct Signature(string Provider, string ClientId);

    private ConcurrentDictionary<string, Signature> Signatures { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    private OAuthTokenSource Make(string address, OAuthProvider provider, string clientId)
    {
        Signatures[address] = new Signature(provider.Id, clientId);
        return new OAuthTokenSource(provider, clientId, address, secrets);
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values) source.Dispose();
        _sources.Clear();
    }
}
