namespace Mailbox.Protocols.OAuth;

/// <summary>
/// How one account authenticates, as it is stored: a kind, and the sign-in details behind it.
/// </summary>
/// <remarks>
/// Here rather than beside the rest of an account's settings because the interesting part is the
/// rule, not the storage — which registration wins, and what a half-configured account falls back
/// to. That rule decides whether mail is collected at all, so it is somewhere it can be tested.
/// </remarks>
public sealed record AccountAuth(AuthKind Kind, string ProviderId = "", string ClientId = "")
{
    /// <summary>An ordinary account, which is most of them.</summary>
    public static readonly AccountAuth Password = new(AuthKind.Password);

    /// <summary>True when this account is meant to sign in through a browser.</summary>
    public bool SignsIn => Kind == AuthKind.OAuth2;

    /// <summary>
    /// The authorization server this account signs in to, or null when it does not sign in or
    /// names one this build has never heard of.
    /// </summary>
    public OAuthProvider? Provider => SignsIn ? OAuthProviders.ById(ProviderId) : null;

    /// <summary>
    /// Which registration this account signs in with: the one the user pasted, or the provider's
    /// own where they pasted none.
    /// </summary>
    /// <remarks>
    /// The user's wins on purpose. Someone who registered their own client did it because the
    /// shipped one does not suit them — or because there is not one — and silently preferring
    /// ours would send them back to a consent screen they had already worked around.
    /// </remarks>
    public string ClientIdInUse(OAuthProvider provider)
        => ClientId.Length > 0 ? ClientId : provider.ClientId;

    /// <summary>
    /// Where this account's bearer tokens come from, or null when it uses a password.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception for an account that is half-configured — a provider this
    /// build does not know, or a registration nobody ever pasted. The password path then fails at
    /// the server, with a message about credentials, instead of throwing out of the settings
    /// loader and taking the whole send/receive with it.
    /// </remarks>
    public IAccessTokenSource? Source(string address, OAuthAccounts? accounts)
    {
        if (!SignsIn || accounts is null) return null;
        if (Provider is not { } provider) return null;

        var clientId = ClientIdInUse(provider);
        return clientId.Length > 0 ? accounts.For(address, provider, clientId) : null;
    }
}
