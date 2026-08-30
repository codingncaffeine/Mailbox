using System.Net;
using System.Net.Http;
using System.Text;

namespace Mailbox.App.Views;

/// <summary>
/// A tiny CalDAV/CardDAV server in an <see cref="HttpMessageHandler"/>, for a posed run: the
/// account wizard's discovery has to be pressed through the real client, and a capture run has
/// no business reaching the network.
/// </summary>
/// <remarks>
/// Gated on <c>MAILBOX_DAV_FAKE</c>. It answers exactly the conversation discovery holds — the
/// principal, the two home sets, and one listing per home — and nothing else, so a wizard that
/// started asking for more would show up as a failed pose rather than pass by accident. The
/// collections are shaped to exercise what the wizard draws: a writable coloured calendar, a
/// read-only one, and an address book. A password other than <see cref="Password"/> answers
/// 401, so the refused sign-in can be posed too.
/// </remarks>
internal sealed class PosedDavServer : HttpMessageHandler
{
    /// <summary>The one password the posed server accepts.</summary>
    internal const string Password = "correct horse";

    /// <summary>The handler for this run, or null when the run is not posing DAV.</summary>
    internal static PosedDavServer? FromEnvironment()
        => Environment.GetEnvironmentVariable("MAILBOX_DAV_FAKE") is "1" or "true" ? new PosedDavServer() : null;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Authorised(request))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        if (request.Method.Method != "PROPFIND")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        }

        var path = request.RequestUri!.AbsolutePath;
        return ReadBodyAsync(request, cancellationToken).ContinueWith(
            body => Answer(path, body.Result),
            cancellationToken,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private static async Task<string> ReadBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    private static bool Authorised(HttpRequestMessage request)
    {
        if (request.Headers.Authorization is not { Scheme: "Basic", Parameter: { Length: > 0 } basic }) return false;

        try
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic));
            return pair.IndexOf(':') is var colon and > 0 && pair[(colon + 1)..] == Password;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static HttpResponseMessage Answer(string path, string body)
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
                  <d:prop><c:calendar-home-set><d:href>/calendars/you/</d:href></c:calendar-home-set></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                """);
        }

        if (body.Contains("addressbook-home-set", StringComparison.Ordinal))
        {
            return MultiStatus($"""
                <d:response><d:href>{path}</d:href><d:propstat>
                  <d:prop><card:addressbook-home-set><d:href>/addressbooks/you/</d:href></card:addressbook-home-set></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                """);
        }

        // The two homes' listings, at depth 1: what the wizard's list draws.
        if (path.StartsWith("/calendars/", StringComparison.Ordinal))
        {
            return MultiStatus(
                Collection("/calendars/you/work/", "Work", calendar: true, colour: "#0078D4FF", writable: true)
                + Collection("/calendars/you/holidays/", "Public Holidays", calendar: true, colour: "#107C10FF", writable: false));
        }

        if (path.StartsWith("/addressbooks/", StringComparison.Ordinal))
        {
            return MultiStatus(
                Collection("/addressbooks/you/people/", "People", calendar: false, colour: null, writable: true));
        }

        return MultiStatus(string.Empty);
    }

    private static string Collection(string href, string name, bool calendar, string? colour, bool writable)
        => $"""
            <d:response><d:href>{href}</d:href><d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/>{(calendar ? "<c:calendar/>" : "<card:addressbook/>")}</d:resourcetype>
                <d:displayname>{name}</d:displayname>
                {(colour is null ? string.Empty : $"<x1:calendar-color>{colour}</x1:calendar-color>")}
                {(calendar ? "<c:supported-calendar-component-set><c:comp name=\"VEVENT\"/></c:supported-calendar-component-set>" : string.Empty)}
                <d:current-user-privilege-set><d:privilege><d:read/></d:privilege>{(writable ? "<d:privilege><d:write/></d:privilege>" : string.Empty)}</d:current-user-privilege-set>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            """;

    private static HttpResponseMessage MultiStatus(string responses)
        => new((HttpStatusCode)207)
        {
            Content = new StringContent(
                $"""
                 <?xml version="1.0" encoding="utf-8"?>
                 <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"
                                xmlns:card="urn:ietf:params:xml:ns:carddav"
                                xmlns:cs="http://calendarserver.org/ns/" xmlns:x1="http://apple.com/ns/ical/">
                 {responses}
                 </d:multistatus>
                 """,
                Encoding.UTF8,
                "application/xml"),
        };
}
