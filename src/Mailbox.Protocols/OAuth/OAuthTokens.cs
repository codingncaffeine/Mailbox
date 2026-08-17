namespace Mailbox.Protocols.OAuth;

/// <summary>What an authorization server hands back, and how long it is good for.</summary>
/// <remarks>
/// The refresh token is the credential worth protecting: an access token lasts an hour and a
/// refresh token lasts until it is revoked. That is why one goes to the keyring and the other
/// stays in memory, and why neither is ever written to the log — see <see cref="ToString"/>.
/// </remarks>
public sealed record OAuthTokens(string AccessToken, DateTimeOffset ExpiresAt)
{
    /// <summary>The token that buys the next access token, or empty when none came back.</summary>
    public string RefreshToken { get; init; } = string.Empty;

    /// <summary>What the server actually granted, which need not be what was asked for.</summary>
    public string Scope { get; init; } = string.Empty;

    /// <summary>True when this access token is worth using at the given moment.</summary>
    /// <param name="margin">
    /// How long a token must have left to count as usable. A token that expires during the
    /// round trip is the same as an expired one, and the failure it causes — an authentication
    /// error mid-poll — looks like a wrong password rather than a stale token.
    /// </param>
    public bool IsUsable(DateTimeOffset now, TimeSpan margin)
        => AccessToken.Length > 0 && ExpiresAt - margin > now;

    /// <summary>
    /// Deliberately says nothing. A record's generated <c>ToString</c> prints every property,
    /// which would put both tokens in any log line, exception message or debugger view that ever
    /// interpolated one.
    /// </summary>
    public override string ToString()
        => $"OAuthTokens {{ expires {ExpiresAt:u}, refresh token {(RefreshToken.Length > 0 ? "held" : "none")} }}";
}

/// <summary>An authorization server said no, and this is what it said.</summary>
public sealed class OAuthException(string error, string? description = null)
    : Exception(Describe(error, description))
{
    /// <summary>The machine-readable code — <c>invalid_grant</c>, <c>access_denied</c>.</summary>
    public string Error { get; } = error;

    /// <summary>
    /// True when the refresh token is no longer any good, which is a sign-in to be asked for
    /// again rather than an error to retry. Revoking access, changing a password and not using
    /// an account for months all end here.
    /// </summary>
    public bool NeedsSignIn => Error is "invalid_grant" or "expired_token" or "unauthorized_client";

    private static string Describe(string error, string? description)
    {
        var readable = error switch
        {
            "access_denied" => "The sign-in was refused.",
            "invalid_grant" => "The saved sign-in is no longer valid. Sign in again.",
            "invalid_client" => "That client ID was not accepted.",
            "invalid_scope" => "The account was not granted what Mailbox asked for.",
            _ => $"The sign-in failed ({error}).",
        };

        // The description comes from the other end. It is shown because it is usually the only
        // thing that says which of a provider's several reasons this was, but it is stripped of
        // anything that could forge a second line in the log.
        var detail = Tidy(description);
        return detail.Length > 0 ? $"{readable} {detail}" : readable;
    }

    private static string Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length > 300 ? clean[..300] + "…" : clean;
    }
}
