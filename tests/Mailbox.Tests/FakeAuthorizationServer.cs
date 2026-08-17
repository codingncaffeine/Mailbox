using System.Net;
using System.Text;
using System.Text.Json;

namespace Mailbox.Tests;

/// <summary>
/// An authorization server that lives in an <see cref="HttpMessageHandler"/>.
/// </summary>
/// <remarks>
/// Only the token endpoint is here, because only the token endpoint is spoken to over HTTP: the
/// authorization endpoint is a page in a browser, and what this application does with it is
/// build a URL and wait on a socket. Those are exercised by driving the loopback listener
/// directly, which is what a browser would do.
/// <para>
/// It checks the proof key rather than accepting anything, so a test that stopped sending one
/// would fail here rather than pass quietly.
/// </para>
/// </remarks>
public sealed class FakeAuthorizationServer : HttpMessageHandler
{
    private readonly Dictionary<string, string> _codes = new(StringComparer.Ordinal);

    /// <summary>Every token request's form, in order, for asserting what was sent.</summary>
    public List<IReadOnlyDictionary<string, string>> Requests { get; } = [];

    /// <summary>How long the access tokens it issues last.</summary>
    public int ExpiresIn { get; set; } = 3600;

    /// <summary>Issue a refresh token with the next grant. Google without offline access does not.</summary>
    public bool IssuesRefreshToken { get; set; } = true;

    /// <summary>Hand back a different refresh token on every renewal, as some providers do.</summary>
    public bool RotatesRefreshTokens { get; set; }

    /// <summary>Refuse the next request with this error instead of answering it.</summary>
    public string? NextError { get; set; }

    /// <summary>Answer the next request with 200 and an error in the body, which is also legal.</summary>
    public bool ErrorArrivesWithTwoHundred { get; set; }

    /// <summary>Refresh tokens this server has issued and still honours.</summary>
    public HashSet<string> LiveRefreshTokens { get; } = new(StringComparer.Ordinal);

    private int _issued;

    /// <summary>Registers a code as though the browser half had happened, with its challenge.</summary>
    public void Authorize(string code, string codeChallenge) => _codes[code] = codeChallenge;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var form = Parse(body);
        Requests.Add(form);

        if (NextError is { } refusal)
        {
            NextError = null;
            return Error(refusal);
        }

        return form.GetValueOrDefault("grant_type") switch
        {
            "authorization_code" => Code(form),
            "refresh_token" => Refresh(form),
            _ => Error("unsupported_grant_type"),
        };
    }

    private HttpResponseMessage Code(IReadOnlyDictionary<string, string> form)
    {
        var code = form.GetValueOrDefault("code", string.Empty);
        if (!_codes.TryGetValue(code, out var challenge)) return Error("invalid_grant");

        // The whole point of PKCE: the verifier presented here must hash to the challenge that
        // came with the authorization request.
        var verifier = form.GetValueOrDefault("code_verifier", string.Empty);
        if (verifier.Length == 0) return Error("invalid_request");
        if (Mailbox.Protocols.OAuth.PkceChallenge.Hash(verifier) != challenge) return Error("invalid_grant");

        _codes.Remove(code);
        return Tokens();
    }

    private HttpResponseMessage Refresh(IReadOnlyDictionary<string, string> form)
    {
        var token = form.GetValueOrDefault("refresh_token", string.Empty);
        return LiveRefreshTokens.Contains(token) ? Tokens(replacing: token) : Error("invalid_grant");
    }

    private HttpResponseMessage Tokens(string? replacing = null)
    {
        _issued++;
        var payload = new Dictionary<string, object>
        {
            ["access_token"] = $"access-{_issued}",
            ["token_type"] = "Bearer",
            ["expires_in"] = ExpiresIn,
            ["scope"] = "https://outlook.office.com/IMAP.AccessAsUser.All",
        };

        if (IssuesRefreshToken && (replacing is null || RotatesRefreshTokens))
        {
            var refresh = $"refresh-{_issued}";
            LiveRefreshTokens.Add(refresh);
            if (replacing is not null) LiveRefreshTokens.Remove(replacing);
            payload["refresh_token"] = refresh;
        }

        return Json(HttpStatusCode.OK, payload);
    }

    private HttpResponseMessage Error(string error)
        => Json(
            ErrorArrivesWithTwoHundred ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
            new Dictionary<string, object>
            {
                ["error"] = error,
                ["error_description"] = $"The request was refused: {error}.",
            });

    private static HttpResponseMessage Json(HttpStatusCode status, Dictionary<string, object> payload)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    private static Dictionary<string, string> Parse(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            form[Uri.UnescapeDataString(pair[..equals])] =
                Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }

        return form;
    }
}
