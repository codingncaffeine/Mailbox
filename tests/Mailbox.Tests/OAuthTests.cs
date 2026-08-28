using Mailbox.Protocols;
using Mailbox.Protocols.OAuth;

namespace Mailbox.Tests;

/// <summary>
/// The sign-in flow: the proof key, the loopback redirect, the token exchange and the renewals.
/// </summary>
/// <remarks>
/// Weighted towards the refusals, which is where the security of this lives and what no real
/// provider will produce on request. The browser half is driven by fetching the redirect URL the
/// way a browser would, so the listener is exercised as a web server rather than mocked out.
/// </remarks>
public class OAuthTests
{
    private static readonly OAuthProvider TestProvider = new(
        "test",
        "Test",
        new Uri("https://auth.example.net/authorize"),
        new Uri("https://auth.example.net/token"),
        "mail.read offline_access")
    {
        ClientId = "client-1234",
        ExtraParameters = new Dictionary<string, string> { ["access_type"] = "offline" },
    };

    /// <summary>The test's own cancellation, which anything taking one is handed.</summary>
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> QueryOf(Uri url)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0) found[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return found;
    }

    // ---- The proof key ----

    [Fact]
    public void AChallengeIsTheHashOfItsVerifier()
    {
        var pkce = PkceChallenge.Create();

        Assert.Equal(pkce.Challenge, PkceChallenge.Hash(pkce.Verifier));
        Assert.NotEqual(pkce.Verifier, pkce.Challenge);
    }

    [Fact]
    public void EverySignInGetsItsOwnVerifier()
    {
        var first = PkceChallenge.Create();
        var second = PkceChallenge.Create();

        Assert.NotEqual(first.Verifier, second.Verifier);
    }

    [Fact]
    public void AChallengeIsUrlSafeAndUnpadded()
    {
        var pkce = PkceChallenge.Create();

        Assert.DoesNotContain('+', pkce.Challenge);
        Assert.DoesNotContain('/', pkce.Challenge);
        Assert.DoesNotContain('=', pkce.Challenge);
        Assert.DoesNotContain('=', pkce.Verifier);
    }

    /// <summary>
    /// The specification's own worked example (RFC 7636 appendix B), so the encoding is checked
    /// against somebody else's bytes rather than only against itself.
    /// </summary>
    [Fact]
    public void TheHashMatchesTheSpecificationsOwnExample()
    {
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            PkceChallenge.Hash("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    // ---- The authorization request ----

    [Fact]
    public void TheAuthorizationUrlCarriesTheChallengeAndTheState()
    {
        var pkce = PkceChallenge.Create();
        var url = OAuthFlow.AuthorizationUrl(
            TestProvider, "client-1234", new Uri("http://127.0.0.1:41234/mailbox-oauth/"),
            pkce, "state-abc", "you@example.com");

        var query = QueryOf(url);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("client-1234", query["client_id"]);
        Assert.Equal(pkce.Challenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("state-abc", query["state"]);
        Assert.Equal("http://127.0.0.1:41234/mailbox-oauth/", query["redirect_uri"]);
        Assert.Equal("mail.read offline_access", query["scope"]);
        Assert.Equal("you@example.com", query["login_hint"]);
        Assert.Equal("offline", query["access_type"]);
    }

    /// <summary>
    /// A native application has no secret, and the request that would carry one is this one. The
    /// verifier is not in it either: the whole value of a proof key is that it is sent later, to
    /// a different endpoint, over a channel the browser cannot see.
    /// </summary>
    [Fact]
    public void TheAuthorizationUrlCarriesNoSecretAndNoVerifier()
    {
        var pkce = PkceChallenge.Create();
        var url = OAuthFlow.AuthorizationUrl(
            TestProvider, "client-1234", new Uri("http://127.0.0.1:41234/mailbox-oauth/"), pkce, "state-abc");

        Assert.DoesNotContain("client_secret", url.Query);
        Assert.DoesNotContain(pkce.Verifier, url.Query);
    }

    /// <summary>
    /// What is handed to the browser is the escaped form. Scopes are separated by spaces, and
    /// <see cref="Uri.ToString"/> hands back the unescaped display form — which would put raw
    /// spaces in a query string on the command line of whatever opens it.
    /// </summary>
    [Fact]
    public void TheUrlTheBrowserGetsIsEscaped()
    {
        var url = OAuthFlow.AuthorizationUrl(
            TestProvider, "client-1234", new Uri("http://127.0.0.1:41234/mailbox-oauth/"),
            PkceChallenge.Create(), "state-abc");

        Assert.DoesNotContain(' ', url.AbsoluteUri);
        Assert.Contains("scope=mail.read%20offline_access", url.AbsoluteUri);
    }

    // ---- The loopback listener ----

    [Fact]
    public async Task TheListenerHandsBackWhatTheBrowserWasSentTo()
    {
        using var redirect = LoopbackRedirect.Open();
        var waiting = redirect.WaitAsync(TimeSpan.FromSeconds(10), Stop);

        using var browser = new HttpClient();
        using var page = await browser.GetAsync($"{redirect.RedirectUri}?code=the-code&state=the-state", Stop);

        var answer = await waiting;

        Assert.Equal("the-code", answer["code"]);
        Assert.Equal("the-state", answer["state"]);
        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
    }

    /// <summary>
    /// The page the browser lands on is a constant. Echoing the query back is how a local page
    /// that has an authorization code in its own URL would also have script in it.
    /// </summary>
    [Fact]
    public async Task TheListenerNeverEchoesTheQueryIntoThePage()
    {
        using var redirect = LoopbackRedirect.Open();
        var waiting = redirect.WaitAsync(TimeSpan.FromSeconds(10), Stop);

        using var browser = new HttpClient();
        using var page = await browser.GetAsync(
            $"{redirect.RedirectUri}?code=<script>alert(1)</script>&state=s", Stop);

        var body = await page.Content.ReadAsStringAsync(Stop);
        await waiting;

        Assert.DoesNotContain("script>alert", body);
        Assert.DoesNotContain("code=", body);
    }

    /// <summary>
    /// A browser asks for an icon on its way past. Treating that as the redirect would end the
    /// wait before the redirect arrived, and the sign-in would hang for its full timeout.
    /// </summary>
    [Fact]
    public async Task ARequestWithNoQueryIsNotTheRedirect()
    {
        using var redirect = LoopbackRedirect.Open();
        var waiting = redirect.WaitAsync(TimeSpan.FromSeconds(10), Stop);

        using var browser = new HttpClient();
        using var ignored = await browser.GetAsync(redirect.RedirectUri, Stop);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ignored.StatusCode);
        Assert.False(waiting.IsCompleted);

        using var real = await browser.GetAsync($"{redirect.RedirectUri}?code=c&state=s", Stop);
        Assert.Equal("c", (await waiting)["code"]);
    }

    /// <summary>One request, then the door closes: a code cannot be presented at it twice.</summary>
    [Fact]
    public async Task TheListenerAnswersOneRequestAndStops()
    {
        using var redirect = LoopbackRedirect.Open();
        var waiting = redirect.WaitAsync(TimeSpan.FromSeconds(10), Stop);

        using var browser = new HttpClient();
        using var first = await browser.GetAsync($"{redirect.RedirectUri}?code=c&state=s", Stop);
        await waiting;

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => browser.GetAsync($"{redirect.RedirectUri}?code=c&state=s", Stop));
    }

    /// <summary>
    /// A duplicated parameter is how a request smuggles a second value past whichever end of a
    /// pipeline reads the other one. The first is the answer and the rest are ignored.
    /// </summary>
    [Fact]
    public async Task ADuplicatedParameterDoesNotOverrideTheFirst()
    {
        using var redirect = LoopbackRedirect.Open();
        var waiting = redirect.WaitAsync(TimeSpan.FromSeconds(10), Stop);

        using var browser = new HttpClient();
        using var page = await browser.GetAsync($"{redirect.RedirectUri}?state=mine&state=theirs&code=c", Stop);

        Assert.Equal("mine", (await waiting)["state"]);
    }

    [Fact]
    public async Task AnAbandonedSignInStopsWaiting()
    {
        using var redirect = LoopbackRedirect.Open();

        await Assert.ThrowsAsync<TimeoutException>(
            () => redirect.WaitAsync(TimeSpan.FromMilliseconds(200), Stop));
    }

    // ---- The whole round trip ----

    /// <summary>A browser: reads the URL it was handed and calls the redirect back, as one would.</summary>
    private static Action<Uri> Browser(FakeAuthorizationServer server, string code = "code-1",
        Func<Dictionary<string, string>, string>? state = null)
        => url =>
        {
            var query = QueryOf(url);
            server.Authorize(code, query["code_challenge"]);

            var back = $"{query["redirect_uri"]}?code={Uri.EscapeDataString(code)}"
                       + $"&state={Uri.EscapeDataString(state?.Invoke(query) ?? query["state"])}";

            // Fired and not awaited, which is what a browser does: the sign-in is waiting on the
            // socket, not on this.
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                try { using var page = await browser.GetAsync(back, CancellationToken.None); }
                catch (HttpRequestException) { /* the listener closed first, which some tests want */ }
            });
        };

    [Fact]
    public async Task ASignInComesBackWithTokens()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server, Browser(server));

        var tokens = await flow.SignInAsync(TestProvider, "client-1234", "you@example.com", Stop);

        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));

        // The verifier reached the token endpoint, and the code did not travel with a secret.
        var exchange = Assert.Single(server.Requests);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.False(exchange.ContainsKey("client_secret"));
        Assert.True(exchange["code_verifier"].Length >= 43);
    }

    /// <summary>
    /// The reply has to belong to the request that started it. Anything on the machine can call
    /// that port; the state is what says which of them the authorization server sent.
    /// </summary>
    [Fact]
    public async Task AReplyWithTheWrongStateIsRefused()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server, Browser(server, state: _ => "somebody-elses-state"));

        var refused = await Assert.ThrowsAsync<OAuthException>(
            () => flow.SignInAsync(TestProvider, "client-1234", null, Stop));

        Assert.Equal("invalid_request", refused.Error);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AReplyWithNoStateIsRefused()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server, url =>
        {
            var query = QueryOf(url);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                try { using var page = await browser.GetAsync($"{query["redirect_uri"]}?code=c", CancellationToken.None); }
                catch (HttpRequestException) { }
            });
        });

        var refused = await Assert.ThrowsAsync<OAuthException>(
            () => flow.SignInAsync(TestProvider, "client-1234", null, Stop));

        Assert.Equal("invalid_request", refused.Error);
    }

    [Fact]
    public async Task ARefusedSignInSaysSo()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server, url =>
        {
            var query = QueryOf(url);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                try
                {
                    using var page = await browser.GetAsync(
                        $"{query["redirect_uri"]}?error=access_denied&error_description=User+said+no"
                        + $"&state={Uri.EscapeDataString(query["state"])}", CancellationToken.None);
                }
                catch (HttpRequestException) { }
            });
        });

        var refused = await Assert.ThrowsAsync<OAuthException>(
            () => flow.SignInAsync(TestProvider, "client-1234", null, Stop));

        Assert.Equal("access_denied", refused.Error);
        Assert.Contains("refused", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AnEmptyClientIdIsRefusedBeforeABrowserOpens()
    {
        using var server = new FakeAuthorizationServer();
        var opened = false;
        using var flow = new OAuthFlow(server, _ => opened = true);

        var refused = await Assert.ThrowsAsync<OAuthException>(
            () => flow.SignInAsync(TestProvider, string.Empty, null, Stop));

        Assert.Equal("invalid_client", refused.Error);
        Assert.False(opened);
    }

    /// <summary>
    /// The endpoints are settings, and settings can be edited or badly guessed. Plain HTTP would
    /// put the code and both tokens on the wire in the clear.
    /// </summary>
    [Fact]
    public async Task APlainHttpEndpointIsRefused()
    {
        using var server = new FakeAuthorizationServer();
        var opened = false;
        using var flow = new OAuthFlow(server, _ => opened = true);

        var insecure = TestProvider with { Token = new Uri("http://auth.example.net/token") };

        var refused = await Assert.ThrowsAsync<OAuthException>(
            () => flow.SignInAsync(insecure, "client-1234", null, Stop));

        Assert.Equal("invalid_request", refused.Error);
        Assert.False(opened);
    }

    [Fact]
    public async Task AWrongVerifierIsRefusedByTheServer()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server);

        server.Authorize("code-1", PkceChallenge.Create().Challenge);

        var refused = await Assert.ThrowsAsync<OAuthException>(
            () => flow.ExchangeAsync(TestProvider, "client-1234", "code-1",
                PkceChallenge.Create().Verifier, new Uri("http://127.0.0.1:1/x/"), Stop));

        Assert.Equal("invalid_grant", refused.Error);
        Assert.True(refused.NeedsSignIn);
    }

    /// <summary>Some providers answer 200 with the error in the body, which is also legal.</summary>
    [Fact]
    public async Task AnErrorInATwoHundredIsStillAnError()
    {
        using var server = new FakeAuthorizationServer { ErrorArrivesWithTwoHundred = true, NextError = "invalid_scope" };
        using var flow = new OAuthFlow(server);

        var refused = await Assert.ThrowsAsync<OAuthException>(
            () => flow.RefreshAsync(TestProvider, "client-1234", "refresh-1", Stop));

        Assert.Equal("invalid_scope", refused.Error);
    }

    [Fact]
    public void AResponseWithNoAccessTokenIsRefused()
    {
        var refused = Assert.Throws<OAuthException>(
            () => OAuthFlow.Read("""{"token_type":"Bearer","expires_in":3600}"""));

        Assert.Equal("invalid_request", refused.Error);
    }

    /// <summary>A server that says nothing about the lifetime gets the hour every provider means.</summary>
    [Fact]
    public void AResponseWithNoLifetimeIsGivenAnHour()
    {
        var tokens = OAuthFlow.Read("""{"access_token":"a","token_type":"Bearer"}""");

        Assert.InRange(tokens.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(58), DateTimeOffset.UtcNow.AddMinutes(62));
    }

    // ---- Holding on to them ----

    [Fact]
    public async Task TheSignInIsKeptInTheKeyringAndTheAccessTokenIsNot()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server, Browser(server));
        var secrets = new InMemoryCredentialStore();

        var tokens = await flow.SignInAsync(TestProvider, "client-1234", "you@example.com", Stop);

        using var source = new OAuthTokenSource(TestProvider, "client-1234", "you@example.com", secrets, flow);
        await source.AdoptAsync(tokens, Stop);

        Assert.Equal("refresh-1", await secrets.LoadAsync("you@example.com", Credentials.OAuthRefresh, Stop));
        Assert.Null(await secrets.LoadAsync("you@example.com", Credentials.Incoming, Stop));
        Assert.Equal("access-1", await source.AccessTokenAsync(Stop));
    }

    [Fact]
    public async Task ATokenStillGoodIsNotBoughtAgain()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server, Browser(server));
        var secrets = new InMemoryCredentialStore();

        using var source = new OAuthTokenSource(TestProvider, "client-1234", "you@example.com", secrets, flow);
        await source.AdoptAsync(await flow.SignInAsync(TestProvider, "client-1234", null, Stop), Stop);

        var exchanges = server.Requests.Count;
        Assert.Equal("access-1", await source.AccessTokenAsync(Stop));
        Assert.Equal("access-1", await source.AccessTokenAsync(Stop));
        Assert.Equal(exchanges, server.Requests.Count);
    }

    /// <summary>
    /// A token inside the margin is treated as spent. One that expires during the round trip
    /// fails as an authentication error, which reads to a user as a wrong password.
    /// </summary>
    [Fact]
    public async Task ATokenAboutToExpireIsRenewedBeforeItIsHandedOut()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server, Browser(server));
        var secrets = new InMemoryCredentialStore();

        using var source = new OAuthTokenSource(TestProvider, "client-1234", "you@example.com", secrets, flow);
        var signedIn = await flow.SignInAsync(TestProvider, "client-1234", null, Stop);
        await source.AdoptAsync(signedIn with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) }, Stop);

        Assert.Equal("access-2", await source.AccessTokenAsync(Stop));
        Assert.Equal("refresh_token", server.Requests[^1]["grant_type"]);
    }

    /// <summary>
    /// A rotated refresh token replaces the one that bought it, and is written before the access
    /// token is used — the old one stops working the moment it is redeemed, so a crash between
    /// the two would leave an account that cannot renew and nobody would know why.
    /// </summary>
    [Fact]
    public async Task ARotatedRefreshTokenReplacesTheOneThatBoughtIt()
    {
        using var server = new FakeAuthorizationServer { RotatesRefreshTokens = true };
        using var flow = new OAuthFlow(server, Browser(server));
        var secrets = new InMemoryCredentialStore();

        using var source = new OAuthTokenSource(TestProvider, "client-1234", "you@example.com", secrets, flow);
        var signedIn = await flow.SignInAsync(TestProvider, "client-1234", null, Stop);
        await source.AdoptAsync(signedIn with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, Stop);

        await source.AccessTokenAsync(Stop);

        Assert.Equal("refresh-2", await secrets.LoadAsync("you@example.com", Credentials.OAuthRefresh, Stop));
        Assert.DoesNotContain("refresh-1", server.LiveRefreshTokens);
    }

    [Fact]
    public async Task AnAccountWithNoSavedSignInAsksForOne()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server);
        var secrets = new InMemoryCredentialStore();

        using var source = new OAuthTokenSource(TestProvider, "client-1234", "you@example.com", secrets, flow);

        var refused = await Assert.ThrowsAsync<OAuthException>(() => source.AccessTokenAsync(Stop));

        Assert.True(refused.NeedsSignIn);
        Assert.Empty(server.Requests);
    }

    /// <summary>
    /// A revoked sign-in is a sign-in to ask for again, not a server to retry — the difference
    /// between telling the user to press the button and retrying every ten minutes forever.
    /// </summary>
    [Fact]
    public async Task ARevokedSignInIsToldApartFromAServerBeingDown()
    {
        using var server = new FakeAuthorizationServer();
        using var flow = new OAuthFlow(server);
        var secrets = new InMemoryCredentialStore();
        await secrets.SaveAsync("you@example.com", Credentials.OAuthRefresh, "revoked-token", Stop);

        using var source = new OAuthTokenSource(TestProvider, "client-1234", "you@example.com", secrets, flow);

        var refused = await Assert.ThrowsAsync<OAuthException>(() => source.AccessTokenAsync(Stop));

        Assert.Equal("invalid_grant", refused.Error);
        Assert.True(refused.NeedsSignIn);
    }

    /// <summary>
    /// A send/receive opens the incoming and outgoing servers at once and both ask at the same
    /// moment. Two renewals would be two round trips to the same place, and with a rotating
    /// provider the second would redeem what the first had just replaced.
    /// </summary>
    [Fact]
    public async Task SimultaneousAsksRenewOnce()
    {
        using var server = new FakeAuthorizationServer { RotatesRefreshTokens = true };
        using var flow = new OAuthFlow(server);
        var secrets = new InMemoryCredentialStore();
        await secrets.SaveAsync("you@example.com", Credentials.OAuthRefresh, "refresh-0", Stop);
        server.LiveRefreshTokens.Add("refresh-0");

        using var source = new OAuthTokenSource(TestProvider, "client-1234", "you@example.com", secrets, flow);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => source.AccessTokenAsync(Stop)));

        Assert.Single(server.Requests);
        Assert.All(answers, token => Assert.Equal(answers[0], token));
    }

    [Fact]
    public async Task ForgettingAnAccountClearsTheKeyring()
    {
        var secrets = new InMemoryCredentialStore();
        await secrets.SaveAsync("you@example.com", Credentials.OAuthRefresh, "refresh-0", Stop);

        using var accounts = new OAuthAccounts(secrets);
        await accounts.ForgetAsync("you@example.com", Stop);

        Assert.Null(await secrets.LoadAsync("you@example.com", Credentials.OAuthRefresh, Stop));
    }

    /// <summary>
    /// One source per account, so the access token in it survives from one send/receive to the
    /// next — and a new client ID replaces it, the refresh token of the old registration being
    /// no good to the new one.
    /// </summary>
    [Fact]
    public void OneSourcePerAccountUntilTheRegistrationChanges()
    {
        var secrets = new InMemoryCredentialStore();
        using var accounts = new OAuthAccounts(secrets);

        var first = accounts.For("you@example.com", TestProvider, "client-1234");

        Assert.Same(first, accounts.For("you@example.com", TestProvider, "client-1234"));
        Assert.NotSame(first, accounts.For("you@example.com", TestProvider, "client-5678"));
    }

    // ---- What an account's stored authentication comes to ----

    [Fact]
    public void APasswordAccountHasNoTokenSource()
    {
        var secrets = new InMemoryCredentialStore();
        using var accounts = new OAuthAccounts(secrets);

        Assert.Null(AccountAuth.Password.Source("you@example.com", accounts));
        Assert.False(AccountAuth.Password.SignsIn);
    }

    /// <summary>
    /// Someone who registered their own client did it because the shipped one does not suit them,
    /// or because there is not one. Preferring ours would send them back to the consent screen
    /// they had already worked around.
    /// </summary>
    [Fact]
    public void APastedRegistrationBeatsTheShippedOne()
    {
        var mine = new AccountAuth(AuthKind.OAuth2, "test", "my-own-client");

        Assert.Equal("my-own-client", mine.ClientIdInUse(TestProvider));
        Assert.Equal(TestProvider.ClientId, new AccountAuth(AuthKind.OAuth2, "test").ClientIdInUse(TestProvider));
    }

    /// <summary>
    /// A half-configured account falls back to the password path rather than throwing: the
    /// failure then arrives from the server as a credential problem, instead of out of the
    /// settings loader with the whole send/receive behind it.
    /// </summary>
    [Fact]
    public void AnAccountNamingAProviderNobodyKnowsFallsBack()
    {
        var secrets = new InMemoryCredentialStore();
        using var accounts = new OAuthAccounts(secrets);

        var unknown = new AccountAuth(AuthKind.OAuth2, "some-provider-from-the-future", "client");

        Assert.Null(unknown.Provider);
        Assert.Null(unknown.Source("you@example.com", accounts));
    }

    [Fact]
    public void AnAccountWithNoRegistrationAtAllFallsBack()
    {
        var secrets = new InMemoryCredentialStore();
        using var accounts = new OAuthAccounts(secrets);

        // Google ships none by design, so an account that pasted none has nothing to sign in with.
        var google = new AccountAuth(AuthKind.OAuth2, "google");

        Assert.Equal(OAuthProviders.Google, google.Provider);
        Assert.Null(google.Source("you@example.com", accounts));
    }

    [Fact]
    public void AnAccountThatSignsInGetsItsSource()
    {
        var secrets = new InMemoryCredentialStore();
        using var accounts = new OAuthAccounts(secrets);

        var signedIn = new AccountAuth(AuthKind.OAuth2, "microsoft", "client-1234");
        var source = signedIn.Source("you@outlook.com", accounts);

        Assert.NotNull(source);
        Assert.Equal("you@outlook.com", source.UserName);
    }

    // ---- What it says about itself ----

    /// <summary>
    /// A record prints every property, so an interpolated one would put both credentials in the
    /// log. This one prints neither.
    /// </summary>
    [Fact]
    public void TokensDoNotPrintThemselves()
    {
        var tokens = new OAuthTokens("secret-access-token", DateTimeOffset.UtcNow.AddHours(1))
        {
            RefreshToken = "secret-refresh-token",
        };

        var printed = $"{tokens}";

        Assert.DoesNotContain("secret-access-token", printed);
        Assert.DoesNotContain("secret-refresh-token", printed);
        Assert.Contains("held", printed);
    }

    /// <summary>
    /// Google's terms forbid an open-source application shipping a sign-in credential, and its
    /// mail scope needs a paid annual assessment (§5). Both are settled by there being no client
    /// ID here to ship.
    /// </summary>
    [Fact]
    public void GoogleShipsNoClientIdAndAsksForNoMailScope()
    {
        Assert.Empty(OAuthProviders.Google.ClientId);
        Assert.False(OAuthProviders.Google.WorksOutOfTheBox);
        Assert.NotNull(OAuthProviders.Google.OwnClientGuidance);
        Assert.DoesNotContain("mail.google.com", OAuthProviders.Google.Scopes);
    }

    [Fact]
    public void MicrosoftAsksForOfflineAccessAndTheThreeServices()
    {
        var scopes = OAuthProviders.Microsoft.Scopes;

        Assert.Contains("offline_access", scopes);
        Assert.Contains("IMAP.AccessAsUser.All", scopes);
        Assert.Contains("POP.AccessAsUser.All", scopes);
        Assert.Contains("SMTP.Send", scopes);
    }

    [Fact]
    public void TheProvidersEndpointsAreAllHttps()
    {
        foreach (var provider in OAuthProviders.All)
        {
            Assert.Equal(Uri.UriSchemeHttps, provider.Authorization.Scheme);
            Assert.Equal(Uri.UriSchemeHttps, provider.Token.Scheme);
        }
    }

    [Fact]
    public void AConsumerAddressSignsInToMicrosoft()
    {
        Assert.Equal(OAuthProviders.Microsoft, OAuthProviders.ForMail("someone@outlook.com"));
        Assert.Equal(OAuthProviders.Microsoft, OAuthProviders.ForMail("someone@hotmail.com"));
        Assert.Null(OAuthProviders.ForMail("someone@fastmail.com"));
    }

    /// <summary>
    /// Gmail's default path is an app password over IMAP and SMTP, which needs no sign-in at all
    /// — the OAuth path exists for Tasks, which has no other door (§5).
    /// </summary>
    [Fact]
    public void GmailStillTakesAnAppPassword()
    {
        var found = Autoconfig.ForAddress("someone@gmail.com");

        Assert.Equal(AuthKind.AppPassword, found.Auth);
        Assert.Null(OAuthProviders.ForMail("someone@gmail.com"));
    }

    [Fact]
    public void ServerSettingsSayWhichCredentialTheyCarry()
    {
        var password = new ServerSettings("imap.example.net", 993);
        Assert.False(password.UsesOAuth);

        var secrets = new InMemoryCredentialStore();
        using var accounts = new OAuthAccounts(secrets);
        var signedIn = password with { Tokens = accounts.For("you@example.com", TestProvider, "c") };

        Assert.True(signedIn.UsesOAuth);
    }
}
