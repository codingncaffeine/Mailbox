using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Mailbox.Dav;

/// <summary>How this account proves who it is.</summary>
/// <remarks>
/// Basic and Digest over TLS, an app password (which is a password as far as the wire is
/// concerned), and OAuth2 bearer where a provider requires it. Digest is not built here: .NET's
/// own handler answers a Digest challenge when it is given credentials, so asking for it is a
/// matter of handing them over rather than of writing the exchange.
/// </remarks>
public sealed record DavCredentials(string? UserName = null, string? Password = null, string? BearerToken = null)
{
    public bool IsEmpty => string.IsNullOrEmpty(UserName) && string.IsNullOrEmpty(BearerToken);
}

/// <summary>What came back from a request that writes.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Etag">The new ETag, when the server returned one.</param>
/// <param name="Conflict">True for 412: the server's copy moved under us and nothing was written.</param>
public sealed record DavWriteResult(HttpStatusCode Status, string? Etag, bool Conflict)
{
    public bool Ok => (int)Status is >= 200 and < 300;
}

/// <summary>What a body request came back with.</summary>
public sealed record DavResponse(HttpStatusCode Status, string Body, IReadOnlyList<string> Allow, string? Etag)
{
    public bool Ok => (int)Status is >= 200 and < 300;

    /// <summary>The 207 read into its responses.</summary>
    public DavXml.MultiStatus MultiStatus => DavXml.ReadMultiStatus(Body);
}

/// <summary>
/// The WebDAV verbs: PROPFIND, REPORT, PUT and DELETE with ETag preconditions, MKCOL and
/// OPTIONS.
/// </summary>
/// <remarks>
/// Takes an <see cref="HttpMessageHandler"/> so a fake server is a handler and every one of these
/// is testable without a network — which is the only way the conflict paths get exercised at all,
/// since a 412 is exactly the case a live server will not produce on demand.
/// </remarks>
public sealed class DavClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public DavClient(DavCredentials? credentials = null, HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            var socket = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                PreAuthenticate = true,
            };

            if (credentials is { UserName: { Length: > 0 } user })
            {
                socket.Credentials = new NetworkCredential(user, credentials.Password ?? string.Empty);
            }

            handler = socket;
            _ownsClient = true;
        }

        _http = new HttpClient(handler, disposeHandler: _ownsClient)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/1.0");

        // Basic up front as well as on challenge: several servers answer an unauthenticated
        // PROPFIND with a 401 that carries no challenge at all, and pre-authenticating is what
        // turns that into a working first request rather than a login loop.
        if (credentials is { BearerToken: { Length: > 0 } token })
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (credentials is { UserName: { Length: > 0 } name })
        {
            var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{name}:{credentials.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", pair);
        }
    }

    public void Dispose() => _http.Dispose();

    public Task<DavResponse> PropFindAsync(Uri url, string body, int depth, CancellationToken cancellationToken = default)
        => SendAsync(new HttpMethod("PROPFIND"), url, body, depth, cancellationToken);

    public Task<DavResponse> ReportAsync(Uri url, string body, int depth = 1, CancellationToken cancellationToken = default)
        => SendAsync(new HttpMethod("REPORT"), url, body, depth, cancellationToken);

    /// <summary>What a server says it can do, which is how a CalDAV endpoint is recognised.</summary>
    public async Task<DavResponse> OptionsAsync(Uri url, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, url);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var allow = response.Headers.TryGetValues("DAV", out var values)
            ? values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
            : [];
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new DavResponse(response.StatusCode, body, allow, null);
    }

    /// <summary>
    /// Writes an item. <paramref name="ifMatch"/> guards an update against the server's copy
    /// having moved; <paramref name="ifNoneMatch"/> guards a create against there already being
    /// one — RFC 4791's own two preconditions, and the reason a sync never silently clobbers.
    /// </summary>
    public async Task<DavWriteResult> PutAsync(
        Uri url,
        string payload,
        string? ifMatch = null,
        bool ifNoneMatch = false,
        string contentType = "text/calendar; charset=utf-8",
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(payload, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        if (ifMatch is { Length: > 0 }) request.Headers.TryAddWithoutValidation("If-Match", Quote(ifMatch));
        else if (ifNoneMatch) request.Headers.TryAddWithoutValidation("If-None-Match", "*");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return new DavWriteResult(
            response.StatusCode,
            response.Headers.ETag?.Tag.Trim('"'),
            response.StatusCode == HttpStatusCode.PreconditionFailed);
    }

    public async Task<DavWriteResult> DeleteAsync(Uri url, string? ifMatch = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (ifMatch is { Length: > 0 }) request.Headers.TryAddWithoutValidation("If-Match", Quote(ifMatch));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return new DavWriteResult(response.StatusCode, null, response.StatusCode == HttpStatusCode.PreconditionFailed);
    }

    /// <summary>Makes a calendar collection — MKCOL with a resourcetype, as RFC 4791 has it.</summary>
    public async Task<DavWriteResult> MakeCalendarAsync(Uri url, string displayName, CancellationToken cancellationToken = default)
    {
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:set><D:prop><D:displayname>{System.Security.SecurityElement.Escape(displayName)}</D:displayname></D:prop></D:set>
            </C:mkcalendar>
            """;

        using var request = new HttpRequestMessage(new HttpMethod("MKCALENDAR"), url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return new DavWriteResult(response.StatusCode, null, false);
    }

    /// <summary>
    /// Makes an address book — extended MKCOL (RFC 5689) with a resourcetype, there being no
    /// MKADDRESSBOOK the way there is a MKCALENDAR.
    /// </summary>
    public async Task<DavWriteResult> MakeAddressBookAsync(Uri url, string displayName, CancellationToken cancellationToken = default)
    {
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:mkcol xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              <D:set><D:prop>
                <D:resourcetype><D:collection/><C:addressbook/></D:resourcetype>
                <D:displayname>{System.Security.SecurityElement.Escape(displayName)}</D:displayname>
              </D:prop></D:set>
            </D:mkcol>
            """;

        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return new DavWriteResult(response.StatusCode, null, false);
    }

    /// <summary>Reads one item's payload, for a server whose multiget cannot be trusted.</summary>
    public async Task<DavResponse> GetAsync(Uri url, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new DavResponse(response.StatusCode, body, [], response.Headers.ETag?.Tag.Trim('"'));
    }

    private async Task<DavResponse> SendAsync(HttpMethod method, Uri url, string body, int depth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new DavResponse(response.StatusCode, text, [], response.Headers.ETag?.Tag.Trim('"'));
    }

    private static string Quote(string etag)
        => etag.StartsWith('"') || etag.StartsWith("W/", StringComparison.Ordinal) ? etag : "\"" + etag + "\"";
}
