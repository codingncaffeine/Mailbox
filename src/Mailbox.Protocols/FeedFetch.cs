using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mailbox.Core.Feeds;

namespace Mailbox.Protocols;

/// <summary>What one request for a feed came back with.</summary>
/// <param name="Status">The HTTP status, or 0 when the request never got that far.</param>
/// <param name="Text">The body as text, empty for a 304 or a failure.</param>
/// <param name="Error">What went wrong, or empty.</param>
public sealed record FeedFetchResult(
    HttpStatusCode Status,
    string Text,
    string Error = "")
{
    /// <summary>The server has what we already have: nothing to read, and nothing wrong.</summary>
    public bool NotModified => Status == HttpStatusCode.NotModified;

    public bool Ok => (int)Status is >= 200 and < 300;

    /// <summary>The ETag to send back next time.</summary>
    public string Etag { get; init; } = string.Empty;

    /// <summary>The Last-Modified to send back next time.</summary>
    public string LastModified { get; init; } = string.Empty;

    /// <summary>Where the feed ended up, after any redirects.</summary>
    public string FinalUrl { get; init; } = string.Empty;

    /// <summary>Set when the server said the feed has moved for good, and to where.</summary>
    public string MovedTo { get; init; } = string.Empty;

    /// <summary>How long the server asked to be left alone, from Retry-After.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// Asking a publisher for a feed, politely.
/// </summary>
/// <remarks>
/// A feed is polled forever, so what a poll costs matters more than what it does. Every part of
/// this is about the cost of asking again for something that has not changed:
/// <list type="bullet">
/// <item>the ETag and Last-Modified from last time go back out, so an unchanged feed is a 304
/// with no body at all — which is the difference between a subscription costing a megabyte an
/// hour and costing nothing;</item>
/// <item>compression is asked for, because feeds are XML and XML compresses to a fifth;</item>
/// <item>a permanent redirect is reported so the subscription can be rewritten, rather than
/// being followed again on every poll for the rest of its life;</item>
/// <item>the body is read with a ceiling on it, so a publisher who serves a gigabyte by accident
/// takes down one poll rather than the application;</item>
/// <item>and Retry-After is honoured, because the alternative to honouring it is being blocked.</item>
/// </list>
/// <para>
/// The handler is injectable, as the DAV client's is, so all of this is testable against a fake
/// server rather than against somebody's real feed.
/// </para>
/// </remarks>
public sealed class FeedFetch : IDisposable
{
    /// <summary>
    /// The most of a feed that will be read. Generous — the largest feeds in ordinary use run to
    /// a couple of megabytes — and finite, which is the point.
    /// </summary>
    public const int MaximumBytes = 16 * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public FeedFetch(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        if (handler is null)
        {
            handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,

                // Followed by hand rather than automatically, because a permanent redirect is
                // information the subscription wants and the handler would swallow it.
                AllowAutoRedirect = false,
            };
            _ownsClient = true;
        }

        _client = new HttpClient(handler, disposeHandler: _ownsClient)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaximumBytes,
        };

        // Some publishers refuse a request with no user agent, and one that lies about being a
        // browser would be a worse citizen than one that says what it is.
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/1.0 (+feeds)");
        _client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/atom+xml, application/rss+xml, application/feed+json, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.8");
    }

    /// <summary>Asks for a document, sending back what was learnt last time.</summary>
    public async Task<FeedFetchResult> GetAsync(
        string url,
        string? etag = null,
        string? lastModified = null,
        CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            return new FeedFetchResult(0, string.Empty, "That is not a web address.");
        }

        var moved = string.Empty;

        try
        {
            // Five is what browsers allow. A loop is what a misconfigured site serves.
            for (var hop = 0; hop < 5; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address);

                if (etag is { Length: > 0 } tag && EntityTagHeaderValue.TryParse(tag, out var parsed))
                {
                    request.Headers.IfNoneMatch.Add(parsed);
                }

                if (lastModified is { Length: > 0 } since && DateTimeOffset.TryParse(since, out var when))
                {
                    request.Headers.IfModifiedSince = when;
                }

                using var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                    .ConfigureAwait(false);

                if (Redirect(response) is { } next)
                {
                    if (!Uri.TryCreate(address, next, out var target)) break;

                    // Only a 301 or a 308 says the old address is wrong. A 302 or a 307 is a
                    // detour for this request, and rewriting the subscription on one of those
                    // would move a feed every time a publisher failed over.
                    if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect)
                    {
                        moved = target.AbsoluteUri;
                    }

                    // The caching headers belong to the address they were learnt from.
                    if (moved.Length > 0)
                    {
                        etag = null;
                        lastModified = null;
                    }

                    address = target;
                    continue;
                }

                return await ReadAsync(response, address, moved, cancellation).ConfigureAwait(false);
            }

            return new FeedFetchResult(0, string.Empty, "That address redirects in a loop.") { MovedTo = moved };
        }
        catch (HttpRequestException ex)
        {
            return new FeedFetchResult(0, string.Empty, Plain(ex));
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new FeedFetchResult(0, string.Empty, "The server did not answer in time.");
        }
    }

    private static string? Redirect(HttpResponseMessage response)
        => response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
               or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
           && response.Headers.Location is { } location
            ? location.OriginalString
            : null;

    private static async Task<FeedFetchResult> ReadAsync(
        HttpResponseMessage response, Uri address, string moved, CancellationToken cancellation)
    {
        var etag = response.Headers.ETag?.ToString() ?? string.Empty;
        var modified = response.Content.Headers.LastModified?.ToString("r") ?? string.Empty;
        var retry = RetryAfter(response);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new FeedFetchResult(response.StatusCode, string.Empty)
            {
                // A 304 carries no body and often no headers; keeping what we sent is what makes
                // the next poll conditional too.
                Etag = etag,
                LastModified = modified,
                FinalUrl = address.AbsoluteUri,
                MovedTo = moved,
                RetryAfter = retry,
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            return new FeedFetchResult(response.StatusCode, string.Empty, Explain(response.StatusCode))
            {
                FinalUrl = address.AbsoluteUri,
                RetryAfter = retry,
            };
        }

        var bytes = await ReadCappedAsync(response, cancellation).ConfigureAwait(false);
        if (bytes is null)
        {
            return new FeedFetchResult(response.StatusCode, string.Empty,
                $"The feed is larger than {MaximumBytes / (1024 * 1024)} MB and was not read.")
            {
                FinalUrl = address.AbsoluteUri,
            };
        }

        return new FeedFetchResult(response.StatusCode, Decode(bytes, response.Content.Headers.ContentType))
        {
            Etag = etag,
            LastModified = modified,
            FinalUrl = address.AbsoluteUri,
            MovedTo = moved,
            RetryAfter = retry,
        };
    }

    /// <summary>The body, or null when it ran past the ceiling.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > MaximumBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[64 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The bytes as text, in whatever the publisher wrote them in.
    /// </summary>
    /// <remarks>
    /// In order of how much the source can be trusted: a byte-order mark, which is unambiguous;
    /// the charset on the response, which the server knows and the document may not; the XML
    /// declaration, which the document knows and the server may not; and UTF-8, which is what
    /// everything written this decade is.
    /// <para>
    /// This is what stops a feed from a publisher still writing Windows-1252 — and there are a
    /// great many of them — arriving with a black diamond in place of every apostrophe.
    /// </para>
    /// </remarks>
    internal static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        if (bytes.Length == 0) return string.Empty;

        if (ByteOrderMark(bytes) is { } marked) return marked.GetString(bytes).TrimStart('\uFEFF');

        if (contentType?.CharSet is { Length: > 0 } charset && Find(charset) is { } declared)
        {
            return declared.GetString(bytes);
        }

        if (DeclaredInProlog(bytes) is { } prolog && Find(prolog) is { } inline)
        {
            return inline.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static Encoding? ByteOrderMark(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;

        return null;
    }

    /// <summary>
    /// The encoding named in the XML declaration, read as ASCII off the front of the document.
    /// </summary>
    /// <remarks>
    /// Safe to read as ASCII whatever the document turns out to be in: every encoding a feed is
    /// served in agrees with ASCII over the characters a declaration is written with, which is
    /// precisely why XML puts the declaration there.
    /// </remarks>
    private static string? DeclaredInProlog(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200));

        var at = head.IndexOf("encoding", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var quote = head.IndexOfAny(['"', '\''], at);
        if (quote < 0) return null;

        var end = head.IndexOf(head[quote], quote + 1);
        return end > quote ? head[(quote + 1)..end].Trim() : null;
    }

    /// <summary>
    /// The legacy single-byte encodings, which .NET does not carry by default.
    /// </summary>
    /// <remarks>
    /// Windows-1252 and the ISO-8859 family are not in the default set on .NET, and a great many
    /// feeds are still written in them. Without this a publisher's apostrophes and dashes arrive
    /// as replacement characters.
    /// </remarks>
    private static readonly Lazy<bool> CodePages = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    private static Encoding? Find(string name)
    {
        _ = CodePages.Value;

        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header) return null;

        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date) return date - DateTimeOffset.UtcNow;

        return null;
    }

    /// <summary>What a status means, in a sentence a reader can act on.</summary>
    private static string Explain(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => "The feed is not there any more (404).",
        HttpStatusCode.Gone => "The publisher has withdrawn this feed (410).",
        HttpStatusCode.Unauthorized => "The feed needs a sign-in (401).",
        HttpStatusCode.Forbidden => "The publisher refused the request (403).",
        HttpStatusCode.TooManyRequests => "The publisher asked to be polled less often (429).",
        HttpStatusCode.InternalServerError => "The publisher's server has a fault (500).",
        HttpStatusCode.BadGateway => "The publisher's server is unreachable (502).",
        HttpStatusCode.ServiceUnavailable => "The publisher's server is unavailable (503).",
        HttpStatusCode.GatewayTimeout => "The publisher's server did not answer (504).",
        _ => $"The publisher's server answered {(int)status}.",
    };

    /// <summary>
    /// A network failure in one sentence rather than the chain of four the exception carries.
    /// </summary>
    private static string Plain(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "That host could not be found.",
        HttpRequestError.ConnectionError => "That host refused the connection.",
        HttpRequestError.SecureConnectionError => "The secure connection could not be made.",
        _ => ex.Message,
    };

    public void Dispose() => _client.Dispose();
}
