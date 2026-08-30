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
/// Keyed by address for the same reason account settings are: a row id belongs to one store
/// file, and the point of a file per account is that it can be restored somewhere the id differs.
/// </para>
/// </remarks>
public sealed class OAuthAccounts(ICredentialStore secrets) : IDisposable
{
    private readonly Dictionary<string, Held> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>What is being kept, and which registration it belongs to.</summary>
    private readonly record struct Held(string ProviderId, string ClientId, OAuthTokenSource Source);

    /// <summary>
    /// The source for an account, made on first use.
    /// </summary>
    /// <remarks>
    /// A client ID that has changed — the user pasted a different registration — replaces the
    /// source rather than being ignored, because the refresh token belonging to the old client
    /// will not be redeemed by the new one and the failure is otherwise silent until the next
    /// renewal.
    /// <para>
    /// A plain lock rather than a concurrent dictionary: replacing means disposing the one that
    /// was there, and a factory a concurrent dictionary is free to run twice is the wrong place
    /// for a side effect that cannot be taken back.
    /// </para>
    /// </remarks>
    public OAuthTokenSource For(string address, OAuthProvider provider, string clientId)
    {
        lock (_gate)
        {
            if (_sources.TryGetValue(address, out var held))
            {
                if (held.ProviderId == provider.Id && held.ClientId == clientId) return held.Source;
                held.Source.Dispose();
            }

            var source = new OAuthTokenSource(provider, clientId, address, secrets);
            _sources[address] = new Held(provider.Id, clientId, source);
            return source;
        }
    }

    /// <summary>Whether an account has a source in hand, without making one.</summary>
    public bool Has(string address)
    {
        lock (_gate) return _sources.ContainsKey(address);
    }

    /// <summary>Forgets an account's sign-in — the source and the saved refresh token both.</summary>
    public async Task ForgetAsync(string address, CancellationToken cancellation = default)
    {
        OAuthTokenSource? source;
        lock (_gate)
        {
            source = _sources.TryGetValue(address, out var held) ? held.Source : null;
            _sources.Remove(address);
        }

        if (source is not null)
        {
            await source.ForgetAsync(cancellation).ConfigureAwait(false);
            source.Dispose();
            return;
        }

        // No source made this session does not mean no sign-in saved: an account added on a
        // previous run has a refresh token in the keyring and nothing here.
        await secrets.DeleteAsync(address, Credentials.OAuthRefresh, cancellation).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var held in _sources.Values) held.Source.Dispose();
            _sources.Clear();
        }
    }
}
