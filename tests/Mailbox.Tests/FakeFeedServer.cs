using System.Net;
using System.Text;

namespace Mailbox.Tests;

/// <summary>
/// A publisher, posed: it answers what it is told to answer and records what it was asked.
/// </summary>
/// <remarks>
/// The whole of the fetching layer is about what happens on the second poll of a feed — the
/// conditional request, the redirect, the backoff, the retry the server asked for — and none of
/// that can be exercised against a real publisher on demand. So the handler is the server, as it
/// is for the DAV client.
/// </remarks>
public sealed class FakeFeedServer : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request made, in order, so a test can assert what was and was not asked for.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    public int RequestsFor(string url)
        => Requests.Count(r => string.Equals(r.RequestUri?.AbsoluteUri, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The requests made for one address, in order.
    /// </summary>
    /// <remarks>
    /// Asked for by address rather than by position in the whole list, because a poll asks for
    /// more than the feed: reading a teaser's own page is another request, and a test that indexed
    /// the flat list was really asserting that nothing else was ever fetched.
    /// </remarks>
    public List<HttpRequestMessage> RequestLog(string url)
        => [.. Requests.Where(r => string.Equals(r.RequestUri?.AbsoluteUri, url, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Serves a body at an address.</summary>
    public FakeFeedServer Serve(
        string url,
        string body,
        string mediaType = "application/rss+xml",
        string? etag = null,
        Encoding? encoding = null)
    {
        _routes[url] = request =>
        {
            // The conditional half: a request carrying the tag we last handed out is told there
            // is nothing to send, which is the whole point of handing it out.
            if (etag is { Length: > 0 } tag
                && request.Headers.IfNoneMatch.Any(t => t.Tag == tag || t.Tag == $"\"{tag.Trim('"')}\""))
            {
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{tag.Trim('"')}\"");
                return notModified;
            }

            var bytes = (encoding ?? Encoding.UTF8).GetBytes(body);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType)
            {
                CharSet = encoding is null ? null : encoding.WebName,
            };

            if (etag is { Length: > 0 } issued)
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{issued.Trim('"')}\"");
            }

            return response;
        };

        return this;
    }

    /// <summary>Answers with a status and nothing else.</summary>
    public FakeFeedServer Refuse(string url, HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        _routes[url] = _ =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter is { } delta)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
            }

            return response;
        };

        return this;
    }

    /// <summary>Sends the caller somewhere else.</summary>
    public FakeFeedServer Redirect(string url, string to, HttpStatusCode status = HttpStatusCode.MovedPermanently)
    {
        _routes[url] = _ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(to);
            return response;
        };

        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
        return Task.FromResult(_routes.TryGetValue(url, out var route)
            ? route(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
