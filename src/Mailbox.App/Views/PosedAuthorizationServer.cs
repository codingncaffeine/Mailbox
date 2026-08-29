using System.Net;
using System.Text;
using System.Web;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// An authorization server for a posed run: the browser's half and the provider's half, both
/// answered inside the process.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> A sign-in is the one path in the account wizard that reaches outside
/// the machine, and it is also the path that decides where an account's credential is kept. A
/// posed run had neither half of it: there is no browser to open, so the loopback listener waited
/// on a redirect nobody was going to send, and even with one the token endpoint is a provider
/// nothing here should ever call. So the wizard logged the authorization request it would have
/// made and stopped, and everything after that — the state check, the exchange, the token parse,
/// the refresh token going to the keyring, the account being written with <c>auth OAuth2</c> —
/// had never run under a harness at all.
/// <para>
/// What is faked is exactly two things: the browser, which is answered by knocking on the
/// application's own loopback redirect with the state it was given, and the provider's token
/// endpoint, which is answered by a message handler. Everything between them is the real flow —
/// <c>LoopbackRedirect</c>'s listener, the fixed-time state comparison, PKCE, the JSON reader and
/// <c>OAuthTokenSource.AdoptAsync</c>. The claim a run can then make is about this application's
/// side of a sign-in, which is the side worth checking.
/// </para>
/// <para>
/// Only under <c>MAILBOX_CAPTURE</c> and only when <c>MAILBOX_OAUTH_FAKE</c> asks for it: a
/// reader's sign-in must always be a real one. The value chooses what the server answers —
/// <c>1</c> for a token pair, <c>norefresh</c> for an access token with no refresh token beside
/// it, <c>deny</c> for a browser that comes back refused, <c>error</c> for a token endpoint that
/// answers a failure.
/// </para>
/// </remarks>
internal static class PosedAuthorizationServer
{
    /// <summary>The refresh token this server hands out, so a read-back can name what it expects.</summary>
    internal const string RefreshToken = "posed-refresh-token";

    /// <summary>The access token this server hands out. Never written down by the application.</summary>
    internal const string AccessToken = "posed-access-token";

    private static string Mode
        => Environment.GetEnvironmentVariable("MAILBOX_OAUTH_FAKE")?.Trim().ToLowerInvariant() ?? string.Empty;

    internal static bool IsRequested => Theming.WindowCapture.IsRequested && Mode.Length > 0;

    /// <summary>The provider's token endpoint, for a posed run — or null, which means the real one.</summary>
    internal static HttpMessageHandler? HandlerOrNull() => IsRequested ? new Handler() : null;

    /// <summary>
    /// Knocks on the loopback redirect the way the browser would, carrying the state it was given.
    /// </summary>
    /// <remarks>
    /// The state is taken off the authorization request rather than invented, because that is the
    /// check being exercised: a reply carrying the wrong one is refused, and a fake that made its
    /// own up would prove the refusal rather than the sign-in.
    /// </remarks>
    internal static async Task AnswerAsync(Uri authorizationRequest)
    {
        try
        {
            var asked = HttpUtility.ParseQueryString(authorizationRequest.Query);
            var redirect = asked["redirect_uri"];
            var state = asked["state"];

            if (redirect is not { Length: > 0 } || state is not { Length: > 0 })
            {
                Log.Warn("Harness: posed sign-in — the authorization request carried no redirect or no state.");
                return;
            }

            var reply = Mode == "deny"
                ? $"{redirect}?error=access_denied&error_description=The+sign-in+was+refused.&state={Uri.EscapeDataString(state)}"
                : $"{redirect}?code=posed-authorization-code&state={Uri.EscapeDataString(state)}";

            Log.Info($"Harness: posed sign-in — answering the redirect on {new Uri(redirect).Port} "
                     + $"as “{Mode}”. The request asked for scope “{asked["scope"]}”, "
                     + $"challenge method “{asked["code_challenge_method"]}”, "
                     + $"client “{asked["client_id"]}”, hint “{asked["login_hint"]}”.");

            using var browser = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var answer = await browser.GetAsync(new Uri(reply));
            Log.Info($"Harness: posed sign-in — the redirect answered {(int)answer.StatusCode}.");
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the posed sign-in could not answer the redirect.", ex);
        }
    }

    /// <summary>The token endpoint, which never reaches the network.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var sent = HttpUtility.ParseQueryString(form);

            // What the exchange sent, which is the half of PKCE this side is responsible for: a
            // verifier has to travel with the code, and it has to be the one the challenge was
            // made from. Logged rather than checked here — the check belongs to a real server, and
            // a fake that enforced it would only be testing itself.
            Log.Info($"Harness: posed sign-in — the token endpoint was asked for "
                     + $"“{sent["grant_type"]}” with client “{sent["client_id"]}”, "
                     + $"verifier {(sent["code_verifier"] is { Length: > 0 } v ? $"{v.Length} characters" : "none")}, "
                     + $"redirect “{sent["redirect_uri"]}”.");

            if (Mode == "error")
            {
                return Json(HttpStatusCode.BadRequest,
                    """{"error":"invalid_grant","error_description":"The code has already been used."}""");
            }

            var refresh = Mode == "norefresh"
                ? string.Empty
                : $"""
                   "refresh_token":"{RefreshToken}",
                   """;

            return Json(HttpStatusCode.OK,
                $$"""
                  {"access_token":"{{AccessToken}}",{{refresh}}"expires_in":3600,"token_type":"Bearer","scope":"posed"}
                  """);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
