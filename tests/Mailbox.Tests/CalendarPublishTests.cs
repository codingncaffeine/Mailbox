using System.Net;
using System.Text;
using Mailbox.Core.Calendars;
using Mailbox.Core.Settings;
using Mailbox.Dav;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// A web server that takes a PUT and then serves back what it was given.
/// </summary>
/// <remarks>
/// Both halves on one handler on purpose: what publishing writes is exactly what a subscription
/// reads, and a test that could not read its own publish back would not have proved that.
/// </remarks>
internal sealed class PublishingWebServer : HttpMessageHandler
{
    public string? Stored { get; private set; }

    public string? ContentType { get; private set; }

    /// <summary>Refuse the next write with this, for the paths a real server would take.</summary>
    public HttpStatusCode? Refuse { get; set; }

    public List<string> Methods { get; } = [];

    public Uri Url { get; } = new("https://example.com/calendars/team.ics");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Methods.Add(request.Method.Method);

        if (request.Method == HttpMethod.Put)
        {
            if (Refuse is { } status) return Task.FromResult(new HttpResponseMessage(status));

            Stored = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }

        if (request.Method == HttpMethod.Get && Stored is { } document)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(document, Encoding.UTF8, "text/calendar"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
    }
}

/// <summary>Publishing a calendar: the whole of it, to one address, as one document.</summary>
public class CalendarPublishTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    private static (PimStore Store, PimRepository Repository, Collection Calendar) Fresh()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        return (store, repository, repository.AddCollection(CollectionKind.Events, "Team", "#107C10"));
    }

    private static void Add(PimRepository repository, long calendar, string uid, string summary, int dayOffset = 0)
        => repository.AddItem(PimEventCodec.ToItem(
            new CalendarEvent
            {
                Uid = uid,
                Summary = summary,
                Start = EventTime.At(Start.AddDays(dayOffset).UtcDateTime, "UTC"),
                End = EventTime.At(Start.AddDays(dayOffset).AddHours(1).UtcDateTime, "UTC"),
            },
            calendar,
            null));

    [Fact]
    public async Task PublishingWritesTheWholeCalendarAsOneDocument()
    {
        using var server = new PublishingWebServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh();
        using var _ = store;

        Add(repository, calendar.Id, "a@example.com", "Stand-up");
        Add(repository, calendar.Id, "b@example.com", "Retro", 1);

        var result = await CalendarPublisher.PublishAsync(
            client, repository, calendar, server.Url, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Written);
        Assert.Equal(["PUT"], server.Methods);
        Assert.Equal("text/calendar", server.ContentType);

        // One VCALENDAR holding both, and METHOD:PUBLISH — this is a calendar to read, not an
        // invitation and not a request for anything back (RFC 5546).
        var document = Assert.IsType<string>(server.Stored);
        Assert.Contains("METHOD:PUBLISH", document, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(document, "BEGIN:VCALENDAR"));
        Assert.Equal(2, Occurrences(document, "BEGIN:VEVENT"));
    }

    /// <summary>
    /// The point of publishing to a document rather than to a collection: the other half of this
    /// application can read it straight back.
    /// </summary>
    [Fact]
    public async Task WhatIsPublishedIsWhatASubscriptionReads()
    {
        using var server = new PublishingWebServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh();
        using var _ = store;

        Add(repository, calendar.Id, "a@example.com", "Stand-up");
        Add(repository, calendar.Id, "b@example.com", "Retro", 1);
        await CalendarPublisher.PublishAsync(client, repository, calendar, server.Url, TestContext.Current.CancellationToken);

        // A second machine, subscribing to the address the first one published to.
        using var reader = PimStore.Transient();
        var theirs = new PimRepository(reader);
        var subscription = theirs.AddCollection(
            CollectionKind.Events, "Team", account: string.Empty, davUrl: server.Url.ToString(), readOnly: true);

        var sync = await DavSync.For(client, theirs, subscription)
            .SyncAsync(subscription, TestContext.Current.CancellationToken);

        Assert.Equal(2, sync.Pulled);
        Assert.Equal(
            ["Retro", "Stand-up"],
            theirs.Items(subscription.Id).Select(i => i.Summary).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AServerThatRefusesTheWriteIsReportedRatherThanRecordedAsPublished()
    {
        using var server = new PublishingWebServer { Refuse = HttpStatusCode.Unauthorized };
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh();
        using var _ = store;

        Add(repository, calendar.Id, "a@example.com", "Stand-up");

        var result = await CalendarPublisher.PublishAsync(
            client, repository, calendar, server.Url, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(0, result.Written);
        Assert.StartsWith("401", result.Refused, StringComparison.Ordinal);
        Assert.Null(server.Stored);
    }

    [Fact]
    public void WhereACalendarIsPublishedSurvivesARestartAndIsOnePlacePerCalendar()
    {
        var settings = SettingsStore.Transient();
        var published = new PublishedCalendars(settings);

        published.Set(7, "https://example.com/a.ics", "Team");

        // Publishing the same calendar again moves it rather than adding a second place: the
        // reference's Change… changes where a calendar goes.
        published.Set(7, "https://example.com/b.ics", "Team");
        Assert.Single(published.All);
        Assert.Equal("https://example.com/b.ics", published.All[0].Url);

        published.Set(9, "https://example.com/c.ics", "Personal");
        published.Published(7, new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero));
        published.Renamed(7, "The Team");

        var again = new PublishedCalendars(settings);
        Assert.Equal(2, again.All.Count);
        Assert.Equal("The Team", again.For(7)!.Name);
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero), again.For(7)!.LastPublished);

        Assert.True(again.Remove(7));
        Assert.Null(new PublishedCalendars(settings).For(7));
        Assert.NotNull(new PublishedCalendars(settings).For(9));
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
