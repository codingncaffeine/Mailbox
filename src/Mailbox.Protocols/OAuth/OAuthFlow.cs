using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Protocols.OAuth;

/// <summary>
/// The authorization code flow, as a native application is allowed to perform it.
/// </summary>
/// <remarks>
/// RFC 6749's authorization code grant with RFC 7636's proof key and RFC 8252's loopback
/// redirect. No client secret is used, sent, or asked for: a program on the user's machine cannot
/// hold one, and a flow that pretends otherwise is either shipping a secret in the open or
/// requiring a server the project would have to run.
/// <para>
/// Takes an <see cref="HttpMessageHandler"/> and a way to open a browser, so a fake authorization
/// server is a handler and a test never launches anything. That matters more here than elsewhere:
/// the paths worth testing are the refusals, and no real provider will produce a state mismatch
/// on request.
/// </para>
/// </remarks>
public sealed class OAuthFlow : IDisposable
{
    /// <summary>How long a sign-in may sit in the browser before the listener gives up.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly Action<Uri> _openBrowser;

    public OAuthFlow(HttpMessageHandler? handler = null, Action<Uri>? openBrowser = null)
    {
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _openBrowser = openBrowser ?? OpenInDefaultBrowser;
    }

    /// <summary>
    /// Signs in: opens a browser, waits for the redirect, and exchanges the code for tokens.
    /// </summary>
    /// <param name="clientId">
    /// The registration to sign in with — the provider's own where it has one, the user's where
    /// it has not. Empty is refused here rather than at the authorization server, which answers
    /// an unregistered client with a page rather than a redirect and so would hang the wait.
    /// </param>
    /// <param name="loginHint">
    /// The address being added, so the browser opens on the right account instead of whichever
    /// one the user is already signed in to. A hint only: the account that comes back is whatever
    /// the user chose, and the caller checks it.
    /// </param>
    public async Task<OAuthTokens> SignInAsync(
        OAuthProvider provider,
        string clientId,
        string? loginHint = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new OAuthException("invalid_client", $"No client ID for {provider.Name}.");
        }

        RequireHttps(provider.Authorization, "authorization");
        RequireHttps(provider.Token, "token");

        // Microsoft matches a registered redirect URI by name, and the one a public client is
        // allowed to register is "http://localhost" with any port; everything else takes the
        // literal address the specification asks for. Both listen on the loopback interface.
        var host = provider.Id == "microsoft" ? "localhost" : "127.0.0.1";

        using var redirect = LoopbackRedirect.Open(host);
        var pkce = PkceChallenge.Create();
        var state = NewState();

        var url = AuthorizationUrl(provider, clientId, redirect.RedirectUri, pkce, state, loginHint);
        Log.Info($"Signing in to {provider.Name}; the browser will open. Listening on port {redirect.Port}.");

        _openBrowser(url);

        var answer = await redirect.WaitAsync(SignInTimeout, cancellation).ConfigureAwait(false);

        // The state is checked before anything else on that URL is read. Everything else there is
        // a claim by whoever made the request, and only a matching state says the request came
        // from the sign-in this process started rather than from something else on the machine.
        if (!answer.TryGetValue("state", out var returned) || !FixedTimeEquals(returned, state))
        {
            throw new OAuthException("invalid_request",
                "The reply to the sign-in did not match the request that started it.");
        }

        if (answer.TryGetValue("error", out var error))
        {
            throw new OAuthException(error, answer.GetValueOrDefault("error_description"));
        }

        if (!answer.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw new OAuthException("invalid_request", "The sign-in came back without a code.");
        }

        var tokens = await ExchangeAsync(provider, clientId, code, pkce.Verifier, redirect.RedirectUri, cancellation)
            .ConfigureAwait(false);

        if (tokens.RefreshToken.Length == 0)
        {
            // Worth saying plainly. Without one the account works until the access token expires
            // and then asks to sign in again, which reads as a bug rather than as a provider that
            // was never asked for offline access.
            Log.Warn($"{provider.Name} returned no refresh token; this account will have to sign in again later.");
        }

        return tokens;
    }

    /// <summary>Where the browser is sent. Public so a test can read the request without making one.</summary>
    public static Uri AuthorizationUrl(
        OAuthProvider provider,
        string clientId,
        Uri redirectUri,
        PkceChallenge pkce,
        string state,
        string? loginHint = null)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("scope", provider.Scopes),
            new("state", state),
            new("code_challenge", pkce.Challenge),
            new("code_challenge_method", PkceChallenge.Method),
        };

        if (!string.IsNullOrWhiteSpace(loginHint)) parameters.Add(new("login_hint", loginHint));
        foreach (var extra in provider.ExtraParameters) parameters.Add(new(extra.Key, extra.Value));

        var query = string.Join('&', parameters.Select(
            p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        var builder = new UriBuilder(provider.Authorization);
        builder.Query = builder.Query.Length > 1 ? builder.Query.TrimStart('?') + "&" + query : query;
        return builder.Uri;
    }

    /// <summary>Turns an authorization code into tokens.</summary>
    public Task<OAuthTokens> ExchangeAsync(
        OAuthProvider provider,
        string clientId,
        string code,
        string verifier,
        Uri redirectUri,
        CancellationToken cancellation = default)
        => PostAsync(provider,
            [
                new("client_id", clientId),
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri.AbsoluteUri),
                new("code_verifier", verifier),
            ],
            cancellation);

    /// <summary>
    /// Buys a new access token with a refresh token.
    /// </summary>
    /// <remarks>
    /// A server may hand back a new refresh token as well, and where it does the old one usually
    /// stops working — so the answer's refresh token replaces the one that bought it, and where
    /// none came back the caller keeps what it had.
    /// </remarks>
    public async Task<OAuthTokens> RefreshAsync(
        OAuthProvider provider,
        string clientId,
        string refreshToken,
        CancellationToken cancellation = default)
    {
        var tokens = await PostAsync(provider,
            [
                new("client_id", clientId),
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("scope", provider.Scopes),
            ],
            cancellation).ConfigureAwait(false);

        return tokens.RefreshToken.Length > 0 ? tokens : tokens with { RefreshToken = refreshToken };
    }

    private async Task<OAuthTokens> PostAsync(
        OAuthProvider provider,
        List<KeyValuePair<string, string>> form,
        CancellationToken cancellation)
    {
        RequireHttps(provider.Token, "token");

        using var request = new HttpRequestMessage(HttpMethod.Post, provider.Token)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ReadError(body);
            throw new OAuthException(error ?? $"http_{(int)response.StatusCode}", description);
        }

        return Read(body);
    }

    /// <summary>Reads a token response. Internal so the parsing has tests of its own.</summary>
    internal static OAuthTokens Read(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // An authorization server is allowed to answer 200 with an error in the body, and a
        // couple do.
        if (root.TryGetProperty("error", out var failed) && failed.ValueKind == JsonValueKind.String)
        {
            throw new OAuthException(failed.GetString() ?? "invalid_request",
                root.TryGetProperty("error_description", out var why) ? why.GetString() : null);
        }

        var access = root.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        if (string.IsNullOrEmpty(access))
        {
            throw new OAuthException("invalid_request", "The token response carried no access token.");
        }

        // expires_in is seconds, and a server that omits it means "you will find out"; an hour is
        // every provider's answer and the margin covers being wrong about it.
        var seconds = root.TryGetProperty("expires_in", out var life) && life.TryGetInt32(out var value)
            ? value
            : 3600;

        return new OAuthTokens(access, DateTimeOffset.UtcNow.AddSeconds(seconds))
        {
            RefreshToken = root.TryGetProperty("refresh_token", out var refresh)
                ? refresh.GetString() ?? string.Empty
                : string.Empty,
            Scope = root.TryGetProperty("scope", out var scope) ? scope.GetString() ?? string.Empty : string.Empty,
        };
    }

    private static (string? Error, string? Description) ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return (
                root.TryGetProperty("error", out var error) ? error.GetString() : null,
                root.TryGetProperty("error_description", out var description) ? description.GetString() : null);
        }
        catch (JsonException)
        {
            // A gateway in front of the provider answering HTML is not a protocol error worth a
            // stack trace, but it is worth not pretending to have parsed.
            return (null, null);
        }
    }

    /// <summary>
    /// Refuses a plain-HTTP endpoint.
    /// </summary>
    /// <remarks>
    /// The only http endpoint in this flow is the loopback redirect, which never leaves the
    /// machine. A provider's own endpoints reached over http would put the authorization code and
    /// the tokens on the wire in the clear — and a provider record is settings, which a bad
    /// autoconfiguration or an edited file can reach.
    /// </remarks>
    private static void RequireHttps(Uri endpoint, string which)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new OAuthException("invalid_request",
                $"The {which} endpoint is not https, so it will not be used.");
        }
    }

    private static string NewState() => PkceChallenge.Base64Url(RandomNumberGenerator.GetBytes(32));

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// Hands the URL to the desktop.
    /// </summary>
    /// <remarks>
    /// <c>xdg-open</c> rather than <see cref="ProcessStartInfo.UseShellExecute"/>, which on Linux
    /// goes through the same tool anyway and swallows the failure when it is missing.
    /// </remarks>
    private static void OpenInDefaultBrowser(Uri url)
        // AbsoluteUri, not ToString(): the display form unescapes, and the scopes are separated by
        // spaces — which would reach the browser as spaces in a query string.
        //
        // This is the fallback used when nothing injected an opener; the account wizard supplies
        // its own. A failure here is not fatal, and the shared helper logs it: the listener is up
        // either way, and the URL can be pasted into a browser by hand.
        => Mailbox.Core.Platform.DesktopOpen.Open(url.AbsoluteUri);

    public void Dispose() => _http.Dispose();
}
