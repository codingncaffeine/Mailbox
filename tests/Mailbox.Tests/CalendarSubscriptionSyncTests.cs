using System.Net;
using System.Text;
using Mailbox.Dav;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// A web server with one calendar file on it, and nothing else.
/// </summary>
/// <remarks>
/// This is what a <c>webcal:</c> address actually points at, and the point of the fake is what
/// it refuses: PROPFIND and REPORT get a 405, exactly as a static file server gives them. A
/// subscription that only worked against <see cref="FakeDavServer"/> would be a subscription
/// that only worked against a CalDAV server, which is the bug this covers.
/// </remarks>
internal sealed class PublishedCalendarServer : HttpMessageHandler
{
    public string Document { get; set; } = string.Empty;

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Every method the engine used, so a test can prove it did not go looking for DAV.</summary>
    public List<string> Methods { get; } = [];

    public Uri Url { get; } = new("https://example.com/calendars/holidays.ics");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Methods.Add(request.Method.Method);

        if (request.Method != HttpMethod.Get)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        }

        var response = new HttpResponseMessage(Status)
        {
            Content = new StringContent(Document, Encoding.UTF8, "text/calendar"),
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"doc-1\"");
        return Task.FromResult(response);
    }
}

/// <summary>
/// Internet calendar subscriptions: a document at an address, fetched whole, replacing what the
/// collection held.
/// </summary>
public class CalendarSubscriptionSyncTests
{
    private static string Calendar(params (string Uid, string Summary)[] events)
    {
        var body = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n");
        foreach (var (uid, summary) in events)
        {
            body.Append($"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260801T000000Z\r\n")
                .Append($"DTSTART:20260816T090000Z\r\nDTEND:20260816T100000Z\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\n");
        }

        return body.Append("END:VCALENDAR\r\n").ToString();
    }

    private static (PimStore Store, PimRepository Repository, Collection Subscription) Fresh(PublishedCalendarServer server)
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var subscription = repository.AddCollection(
            CollectionKind.Events, "Holidays", "#0078D4",
            account: string.Empty, davUrl: server.Url.ToString(), readOnly: true);
        return (store, repository, subscription);
    }

    /// <summary>
    /// The account is what tells a subscription from a shared calendar: both are read-only, and
    /// only one of them is a document.
    /// </summary>
    [Fact]
    public void ASubscriptionIsToldFromASharedCalendarByItsAccount()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);

        var subscription = repository.AddCollection(
            CollectionKind.Events, "Holidays", account: string.Empty,
            davUrl: "https://example.com/h.ics", readOnly: true);
        var shared = repository.AddCollection(
            CollectionKind.Events, "Team", account: "you@example.net",
            davUrl: "https://dav.example.net/calendars/team/", readOnly: true);
        var mine = repository.AddCollection(CollectionKind.Events, "Personal");

        Assert.True(DavSync.IsSubscription(subscription));
        Assert.False(DavSync.IsSubscription(shared));
        Assert.False(DavSync.IsSubscription(mine));
    }

    [Fact]
    public async Task ASubscriptionFillsFromTheDocumentAndAsksForNothingElse()
    {
        using var server = new PublishedCalendarServer
        {
            Document = Calendar(("a@example.com", "New Year"), ("b@example.com", "May Day")),
        };
        using var client = new DavClient(handler: server);
        var (store, repository, subscription) = Fresh(server);
        using var _ = store;

        var result = await DavSync.For(client, repository, subscription)
            .SyncAsync(subscription, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Pulled);
        Assert.Equal(
            ["May Day", "New Year"],
            repository.Items(subscription.Id).Select(i => i.Summary).OrderBy(s => s));

        // The whole point: no PROPFIND and no REPORT, which a static file server refuses anyway.
        Assert.Equal(["GET"], server.Methods.Distinct());
    }

    /// <summary>
    /// The document is the calendar, so what the publisher drops is dropped. Safe here and
    /// nowhere else: a subscription is read-only, so there is no local edit to lose.
    /// </summary>
    [Fact]
    public async Task WhatThePublisherRemovesGoesOnTheNextFetch()
    {
        using var server = new PublishedCalendarServer
        {
            Document = Calendar(("a@example.com", "New Year"), ("b@example.com", "May Day")),
        };
        using var client = new DavClient(handler: server);
        var (store, repository, subscription) = Fresh(server);
        using var _ = store;

        var sync = DavSync.For(client, repository, subscription);
        await sync.SyncAsync(subscription, TestContext.Current.CancellationToken);

        server.Document = Calendar(("a@example.com", "New Year's Day"));
        var second = await sync.SyncAsync(subscription, TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Pulled);
        Assert.Equal(1, second.Removed);
        var left = Assert.Single(repository.Items(subscription.Id));
        Assert.Equal("New Year's Day", left.Summary);
    }

    /// <summary>
    /// A publisher having a bad day empties nothing. A calendar that vanished whenever the far
    /// end served an error page would be worse than one that went stale.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "<html><body>Not here</body></html>")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task AnAnswerThatIsNotACalendarLeavesWhatWasThere(HttpStatusCode status, string body)
    {
        using var server = new PublishedCalendarServer
        {
            Document = Calendar(("a@example.com", "New Year")),
        };
        using var client = new DavClient(handler: server);
        var (store, repository, subscription) = Fresh(server);
        using var _ = store;

        var sync = DavSync.For(client, repository, subscription);
        await sync.SyncAsync(subscription, TestContext.Current.CancellationToken);

        server.Status = status;
        server.Document = body;
        var second = await sync.SyncAsync(subscription, TestContext.Current.CancellationToken);

        Assert.Equal((0, 0), (second.Pulled, second.Removed));
        Assert.Single(repository.Items(subscription.Id));
    }

    /// <summary>A subscription is read-only, so nothing it holds is ever pushed back.</summary>
    [Fact]
    public async Task NothingIsPushedToAPublisher()
    {
        using var server = new PublishedCalendarServer
        {
            Document = Calendar(("a@example.com", "New Year")),
        };
        using var client = new DavClient(handler: server);
        var (store, repository, subscription) = Fresh(server);
        using var _ = store;

        var result = await DavSync.For(client, repository, subscription)
            .SyncAsync(subscription, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Pushed);
        Assert.DoesNotContain("PUT", server.Methods);
        Assert.DoesNotContain("DELETE", server.Methods);
    }
}
