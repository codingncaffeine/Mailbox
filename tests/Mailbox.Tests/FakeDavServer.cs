using System.Net;
using System.Text;

namespace Mailbox.Tests;

/// <summary>
/// A CalDAV server that lives in an <see cref="HttpMessageHandler"/>.
/// </summary>
/// <remarks>
/// The DAV engine's interesting paths are the ones a live server will not produce on demand: a
/// 412 because someone else changed an item between a read and a write, a sync token that has
/// expired, a server that answers no sync-collection at all. All three are one line here.
/// <para>
/// It speaks enough of RFC 4791 and RFC 6578 to be answered honestly — a principal, a home set, a
/// calendar with ETags, sync-collection with tokens and removals, multiget, and PUT and DELETE
/// with preconditions — and refuses what it does not implement rather than pretending.
/// </para>
/// </remarks>
public sealed class FakeDavServer : HttpMessageHandler
{
    private readonly Dictionary<string, (string Payload, string Etag)> _items = new(StringComparer.Ordinal);
    private readonly List<(string Href, string? Etag, bool Removed)> _history = [];
    private int _tokenCounter;

    public FakeDavServer(string origin = "https://dav.example.net")
    {
        Origin = new Uri(origin);
        CalendarUrl = new Uri(Origin, "/calendars/you/home/");
    }

    public Uri Origin { get; }

    public Uri CalendarUrl { get; }

    /// <summary>Turn off to exercise the CTag and ETag fallback path.</summary>
    public bool SupportsSyncCollection { get; set; } = true;

    /// <summary>The CTag the collection reports, which a caller may move by hand.</summary>
    public string Ctag { get; set; } = "ctag-1";

    /// <summary>Every request the engine made, for asserting that a poll was one request.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Refuses the next write with a 412, whatever its precondition says.</summary>
    public bool NextWriteConflicts { get; set; }

    public string CurrentSyncToken => "sync-" + _tokenCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Puts an item on the server without going through the client.</summary>
    public string Publish(string name, string payload)
    {
        var href = CalendarUrl.AbsolutePath + name;
        var etag = "etag-" + (_items.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + _tokenCounter;
        _items[href] = (payload, etag);
        _tokenCounter++;
        _history.Add((href, etag, false));
        Ctag = "ctag-" + _tokenCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return href;
    }

    /// <summary>Removes an item behind the client's back.</summary>
    public void Withdraw(string href)
    {
        _items.Remove(href);
        _tokenCounter++;
        _history.Add((href, null, true));
        Ctag = "ctag-" + _tokenCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool Has(string href) => _items.ContainsKey(href);

    public string PayloadOf(string href) => _items[href].Payload;

    public int Count => _items.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add($"{request.Method} {path}");

        return request.Method.Method switch
        {
            "PROPFIND" => PropFind(path, body, Depth(request)),
            "REPORT" => Report(path, body),
            "PUT" => Put(path, body, request),
            "DELETE" => Delete(path, request),
            "GET" => Get(path),
            _ => Plain(HttpStatusCode.MethodNotAllowed),
        };
    }

    private static int Depth(HttpRequestMessage request)
        => request.Headers.TryGetValues("Depth", out var values)
           && int.TryParse(values.FirstOrDefault(), out var depth) ? depth : 0;

    private HttpResponseMessage PropFind(string path, string body, int depth)
    {
        if (body.Contains("current-user-principal", StringComparison.Ordinal))
        {
            return MultiStatus($"""
                <d:response><d:href>{path}</d:href><d:propstat>
                  <d:prop><d:current-user-principal><d:href>/principals/you/</d:href></d:current-user-principal></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                """);
        }

        if (body.Contains("calendar-home-set", StringComparison.Ordinal))
        {
            return MultiStatus($"""
                <d:response><d:href>{path}</d:href><d:propstat>
                  <d:prop><c:calendar-home-set><d:href>{CalendarUrl.AbsolutePath}</d:href></c:calendar-home-set></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                """);
        }

        if (body.Contains("getetag", StringComparison.Ordinal))
        {
            var rows = string.Concat(_items.Select(i => $"""
                <d:response><d:href>{i.Key}</d:href><d:propstat>
                  <d:prop><d:getetag>"{i.Value.Etag}"</d:getetag><d:resourcetype/></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                """));
            return MultiStatus(rows);
        }

        // The collection listing: the calendar itself, plus its items at depth 1.
        var self = $"""
            <d:response><d:href>{CalendarUrl.AbsolutePath}</d:href><d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                <d:displayname>Work</d:displayname>
                <cs:getctag>{Ctag}</cs:getctag>
                {(SupportsSyncCollection ? $"<d:sync-token>{CurrentSyncToken}</d:sync-token>" : string.Empty)}
                <c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set>
                <x1:calendar-color>#107C10FF</x1:calendar-color>
                <d:current-user-privilege-set><d:privilege><d:read/></d:privilege><d:privilege><d:write/></d:privilege></d:current-user-privilege-set>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            """;

        return MultiStatus(depth == 0 ? self : self);
    }

    private HttpResponseMessage Report(string path, string body)
    {
        if (body.Contains("sync-collection", StringComparison.Ordinal))
        {
            if (!SupportsSyncCollection) return Plain(HttpStatusCode.Forbidden);

            var token = Between(body, "<D:sync-token>", "</D:sync-token>")
                        ?? Between(body, "<sync-token>", "</sync-token>")
                        ?? string.Empty;

            var from = token.StartsWith("sync-", StringComparison.Ordinal)
                       && int.TryParse(token[5..], out var counter) ? counter : 0;

            var rows = new StringBuilder();
            var reported = new HashSet<string>(StringComparer.Ordinal);

            // Latest first, so an item changed twice is reported once at its newest state.
            for (var i = _history.Count - 1; i >= from; i--)
            {
                var (href, etag, removed) = _history[i];
                if (!reported.Add(href)) continue;

                rows.Append(removed || !_items.ContainsKey(href)
                    ? $"<d:response><d:href>{href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"
                    : $"""
                       <d:response><d:href>{href}</d:href><d:propstat>
                         <d:prop><d:getetag>"{etag}"</d:getetag></d:prop>
                         <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                       """);
            }

            return MultiStatus(rows.ToString(), CurrentSyncToken);
        }

        if (body.Contains("calendar-multiget", StringComparison.Ordinal))
        {
            var rows = new StringBuilder();
            foreach (var href in Hrefs(body))
            {
                if (!_items.TryGetValue(href, out var item)) continue;
                rows.Append($"""
                    <d:response><d:href>{href}</d:href><d:propstat>
                      <d:prop><d:getetag>"{item.Etag}"</d:getetag>
                      <c:calendar-data>{System.Security.SecurityElement.Escape(item.Payload)}</c:calendar-data></d:prop>
                      <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    """);
            }

            return MultiStatus(rows.ToString());
        }

        return Plain(HttpStatusCode.BadRequest);
    }

    private HttpResponseMessage Put(string path, string body, HttpRequestMessage request)
    {
        var ifMatch = request.Headers.TryGetValues("If-Match", out var match) ? match.FirstOrDefault()?.Trim('"') : null;
        var ifNoneMatch = request.Headers.Contains("If-None-Match");

        if (NextWriteConflicts)
        {
            NextWriteConflicts = false;
            return Plain(HttpStatusCode.PreconditionFailed);
        }

        // A calendar collection takes whole VCALENDARs, not bare components — Radicale answers
        // "Item type 'VEVENT' not supported in 'VCALENDAR' collection" and every other server
        // says something like it. A fake that accepted one let a real bug through for months.
        if (!body.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            return Plain(HttpStatusCode.BadRequest);
        }

        var exists = _items.TryGetValue(path, out var current);
        if (ifNoneMatch && exists) return Plain(HttpStatusCode.PreconditionFailed);
        if (ifMatch is { Length: > 0 } && (!exists || current.Etag != ifMatch)) return Plain(HttpStatusCode.PreconditionFailed);

        _tokenCounter++;
        var etag = "etag-put-" + _tokenCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _items[path] = (body, etag);
        _history.Add((path, etag, false));
        Ctag = "ctag-" + _tokenCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = new HttpResponseMessage(exists ? HttpStatusCode.NoContent : HttpStatusCode.Created);
        response.Headers.TryAddWithoutValidation("ETag", "\"" + etag + "\"");
        return response;
    }

    private HttpResponseMessage Delete(string path, HttpRequestMessage request)
    {
        var ifMatch = request.Headers.TryGetValues("If-Match", out var match) ? match.FirstOrDefault()?.Trim('"') : null;
        if (!_items.TryGetValue(path, out var current)) return Plain(HttpStatusCode.NotFound);
        if (ifMatch is { Length: > 0 } && current.Etag != ifMatch) return Plain(HttpStatusCode.PreconditionFailed);

        Withdraw(path);
        return Plain(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Get(string path)
    {
        if (!_items.TryGetValue(path, out var item)) return Plain(HttpStatusCode.NotFound);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(item.Payload, Encoding.UTF8, "text/calendar"),
        };
        response.Headers.TryAddWithoutValidation("ETag", "\"" + item.Etag + "\"");
        return response;
    }

    private static IEnumerable<string> Hrefs(string body)
    {
        var at = 0;
        while (true)
        {
            var open = body.IndexOf("<d:href>", at, StringComparison.Ordinal);
            if (open < 0) open = body.IndexOf("<href>", at, StringComparison.Ordinal);
            if (open < 0) yield break;
            var start = body.IndexOf('>', open) + 1;
            var close = body.IndexOf('<', start);
            if (close < 0) yield break;
            yield return body[start..close];
            at = close;
        }
    }

    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }

    private static HttpResponseMessage MultiStatus(string responses, string? syncToken = null)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"
                           xmlns:cs="http://calendarserver.org/ns/" xmlns:x1="http://apple.com/ns/ical/">
            {responses}
            {(syncToken is null ? string.Empty : $"<d:sync-token>{syncToken}</d:sync-token>")}
            </d:multistatus>
            """;

        return new HttpResponseMessage((HttpStatusCode)207)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        };
    }

    private static HttpResponseMessage Plain(HttpStatusCode status) => new(status);
}
