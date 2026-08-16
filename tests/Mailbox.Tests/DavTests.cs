using Mailbox.Dav;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The DAV engine against <see cref="FakeDavServer"/>: discovery, both sync paths, the ETag
/// preconditions, and the queue that makes an offline change a longer queue rather than a lost
/// one.
/// </summary>
public class DavTests
{
    private static string Vevent(string uid, string summary, string start = "20260816T090000Z") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:{uid}
        DTSTAMP:20260801T000000Z
        DTSTART:{start}
        DTEND:20260816T100000Z
        SUMMARY:{summary}
        END:VEVENT
        END:VCALENDAR
        """.ReplaceLineEndings("\r\n");

    private static (PimStore Store, PimRepository Repository, Collection Calendar) Fresh(FakeDavServer server)
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(
            CollectionKind.Events, "Work", "#107C10", "you@example.net", server.CalendarUrl.ToString());
        return (store, repository, calendar);
    }

    [Fact]
    public async Task DiscoveryWalksPrincipalThenHomeSetThenTheCollections()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);

        var found = await DavDiscovery.FindAsync(client, server.Origin, TestContext.Current.CancellationToken);

        var calendar = Assert.Single(found);
        Assert.Equal(CollectionKind.Events, calendar.Kind);
        Assert.Equal("Work", calendar.DisplayName);
        Assert.Equal("#107C10", calendar.Colour);
        Assert.False(calendar.IsReadOnly);
        Assert.Equal(server.CalendarUrl.AbsolutePath, calendar.Url.AbsolutePath);
    }

    [Fact]
    public async Task ASyncCollectionPullWritesWhatTheServerHas()
    {
        using var server = new FakeDavServer();
        server.Publish("one.ics", Vevent("one@test", "Standup"));
        server.Publish("two.ics", Vevent("two@test", "Retro"));

        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;

        var result = await new DavSync(client, repository).SyncAsync(calendar, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Pulled);
        Assert.Equal(0, result.Removed);
        var items = repository.Items(calendar.Id);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Summary == "Standup");
        Assert.All(items, i => Assert.NotNull(i.Etag));
        Assert.NotNull(repository.Collection(calendar.Id)!.SyncToken);
    }

    [Fact]
    public async Task ASecondSyncAsksOnlyForWhatChanged()
    {
        using var server = new FakeDavServer();
        server.Publish("one.ics", Vevent("one@test", "Standup"));

        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;
        var sync = new DavSync(client, repository);

        await sync.SyncAsync(calendar, TestContext.Current.CancellationToken);
        server.Publish("two.ics", Vevent("two@test", "Retro"));

        var second = await sync.SyncAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Pulled);
        Assert.Equal(2, repository.Items(calendar.Id).Count);
    }

    [Fact]
    public async Task AnItemTheServerDroppedIsRemovedHere()
    {
        using var server = new FakeDavServer();
        var href = server.Publish("one.ics", Vevent("one@test", "Standup"));
        server.Publish("two.ics", Vevent("two@test", "Retro"));

        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;
        var sync = new DavSync(client, repository);

        await sync.SyncAsync(calendar, TestContext.Current.CancellationToken);
        server.Withdraw(href);
        var second = await sync.SyncAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Removed);
        Assert.Single(repository.Items(calendar.Id));
    }

    /// <summary>
    /// Several servers still do not implement RFC 6578, and refetching everything each poll is
    /// not acceptable at real sizes — so the fallback has to be an ETag diff, not a refetch.
    /// </summary>
    [Fact]
    public async Task AServerWithoutSyncCollectionFallsBackToAnEtagDiff()
    {
        using var server = new FakeDavServer { SupportsSyncCollection = false };
        server.Publish("one.ics", Vevent("one@test", "Standup"));
        server.Publish("two.ics", Vevent("two@test", "Retro"));

        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;
        var sync = new DavSync(client, repository);

        var first = await sync.SyncAsync(calendar, TestContext.Current.CancellationToken);
        Assert.Equal(2, first.Pulled);
        Assert.NotNull(repository.Collection(calendar.Id)!.Ctag);

        var second = await sync.SyncAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken);
        Assert.Equal(0, second.Pulled);
        Assert.Equal(0, second.Removed);
        Assert.Equal(2, repository.Items(calendar.Id).Count);
    }

    [Fact]
    public async Task AnUnchangedCollectionIsRecognisedFromItsCtagAlone()
    {
        using var server = new FakeDavServer { SupportsSyncCollection = false };
        server.Publish("one.ics", Vevent("one@test", "Standup"));

        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;
        var sync = new DavSync(client, repository);
        await sync.SyncAsync(calendar, TestContext.Current.CancellationToken);

        server.Requests.Clear();
        Assert.True(await sync.IsUnchangedAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AQueuedChangeIsPutWithItsPreconditionAndThenDequeued()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;

        var appointment = new CalendarEvent
        {
            Uid = "local@test",
            Summary = "Dentist",
            Start = EventTime.At(new DateTime(2026, 8, 20, 9, 0, 0), "UTC"),
            End = EventTime.At(new DateTime(2026, 8, 20, 10, 0, 0), "UTC"),
        };
        var item = repository.AddItem(PimEventCodec.ToItem(appointment, calendar.Id));
        repository.Queue(calendar.Id, item.Id, "put");

        var result = await new DavSync(client, repository).SyncAsync(calendar, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Pushed);
        Assert.Empty(repository.Queued(calendar.Id));
        Assert.Equal(1, server.Count);
        Assert.Contains("Dentist", server.PayloadOf(repository.Item(item.Id)!.DavHref!), StringComparison.Ordinal);
        Assert.NotNull(repository.Item(item.Id)!.Etag);
    }

    /// <summary>
    /// The case the ETag precondition exists for: the server's copy moved between the read and
    /// the write. Nothing may be overwritten, and the change stays queued.
    /// </summary>
    [Fact]
    public async Task AWriteTheServerRefusesIsReportedAndStaysQueued()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;

        var appointment = new CalendarEvent
        {
            Uid = "clash@test",
            Summary = "Review",
            Start = EventTime.At(new DateTime(2026, 8, 20, 9, 0, 0), "UTC"),
            End = EventTime.At(new DateTime(2026, 8, 20, 10, 0, 0), "UTC"),
        };
        var item = repository.AddItem(PimEventCodec.ToItem(appointment, calendar.Id));
        repository.Queue(calendar.Id, item.Id, "put");
        server.NextWriteConflicts = true;

        var result = await new DavSync(client, repository).SyncAsync(calendar, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Pushed);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("Review", conflict.Summary);
        var queued = Assert.Single(repository.Queued(calendar.Id));
        Assert.Equal(1, queued.Attempts);
        Assert.NotNull(queued.LastError);
    }

    [Fact]
    public async Task AQueuedDeleteRemovesTheServersCopyAndThenTheRow()
    {
        using var server = new FakeDavServer();
        var href = server.Publish("one.ics", Vevent("one@test", "Standup"));

        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;
        var sync = new DavSync(client, repository);
        await sync.SyncAsync(calendar, TestContext.Current.CancellationToken);

        var item = Assert.Single(repository.Items(calendar.Id));
        repository.SetSyncState(item.Id, PimSyncState.Deleted);
        repository.Queue(calendar.Id, item.Id, "delete");

        var result = await sync.SyncAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Pushed);
        Assert.False(server.Has(href));
        Assert.Empty(repository.Items(calendar.Id));
        Assert.Empty(repository.Queued(calendar.Id));
    }

    /// <summary>
    /// A server keeps one resource per UID, so a series and its overrides go up together — a PUT
    /// of the master alone is a delete of every override.
    /// </summary>
    [Fact]
    public async Task ASeriesAndItsOverridesArePushedAsOneResource()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;

        var master = new CalendarEvent
        {
            Uid = "series@test",
            Summary = "Weekly sync",
            Start = EventTime.At(new DateTime(2026, 8, 3, 10, 0, 0), "UTC"),
            End = EventTime.At(new DateTime(2026, 8, 3, 10, 30, 0), "UTC"),
            Rrule = "FREQ=WEEKLY;BYDAY=MO",
        };
        var stored = repository.AddItem(PimEventCodec.ToItem(master, calendar.Id));
        repository.AddItem(PimEventCodec.ToItem(
            master with
            {
                Rrule = null,
                RecurrenceId = EventTime.At(new DateTime(2026, 8, 17, 10, 0, 0), "UTC"),
                Start = EventTime.At(new DateTime(2026, 8, 17, 14, 0, 0), "UTC"),
                End = EventTime.At(new DateTime(2026, 8, 17, 15, 0, 0), "UTC"),
            },
            calendar.Id));

        repository.Queue(calendar.Id, stored.Id, "put");
        await new DavSync(client, repository).SyncAsync(calendar, TestContext.Current.CancellationToken);

        var payload = server.PayloadOf(repository.Item(stored.Id)!.DavHref!);
        Assert.Contains("RRULE", payload, StringComparison.Ordinal);
        Assert.Contains("RECURRENCE-ID", payload, StringComparison.Ordinal);
        Assert.Equal(2, payload.Split("BEGIN:VEVENT").Length - 1);
    }

    [Fact]
    public async Task AReadOnlyCalendarNeverWrites()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var store = PimStore.Transient();
        using var _ = store;
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(
            CollectionKind.Events, "Holidays", string.Empty, "you@example.net", server.CalendarUrl.ToString(), readOnly: true);

        var item = repository.AddItem(PimEventCodec.ToItem(
            new CalendarEvent
            {
                Uid = "nope@test",
                Summary = "Nope",
                Start = EventTime.At(new DateTime(2026, 8, 20, 9, 0, 0), "UTC"),
                End = EventTime.At(new DateTime(2026, 8, 20, 10, 0, 0), "UTC"),
            },
            calendar.Id));
        repository.Queue(calendar.Id, item.Id, "put");

        var result = await new DavSync(client, repository).SyncAsync(calendar, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, server.Count);
        Assert.Single(repository.Queued(calendar.Id));
    }

    [Fact]
    public void AMultiStatusWithA404ForOneHrefReportsItRemoved()
    {
        const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/c/a.ics</d:href><d:propstat><d:prop><d:getetag>"1"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
              <d:response><d:href>/c/b.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
              <d:sync-token>sync-9</d:sync-token>
            </d:multistatus>
            """;

        var multi = DavXml.ReadMultiStatus(Xml);

        Assert.Equal("sync-9", multi.SyncToken);
        Assert.Equal("/c/a.ics", Assert.Single(multi.Found).Href);
        Assert.Equal("/c/b.ics", Assert.Single(multi.Removed));
    }

    [Fact]
    public void ABodyThatIsNotAMultiStatusComesBackEmptyRatherThanThrowing()
    {
        var multi = DavXml.ReadMultiStatus("<html><body>Not found</body></html>");
        Assert.Empty(multi.Resources);
        Assert.Null(multi.SyncToken);
    }

    // ---- A conflict, from the refusal to the answer ---------------------------------------------

    /// <summary>
    /// Sets up the case the whole conflict path exists for: an appointment edited here while
    /// somebody else edited it there, so the queued write is refused on its ETag.
    /// </summary>
    private static async Task<(DavSync Sync, DavConflict Conflict, PimItem Item, string Href)> Clash(
        FakeDavServer server, DavClient client, PimRepository repository, Collection calendar, CancellationToken cancellationToken)
    {
        var href = server.Publish("clash.ics", Vevent("clash@test", "Review"));
        var sync = new DavSync(client, repository);
        await sync.SyncAsync(calendar, cancellationToken);

        var item = repository.Items(calendar.Id).Single();
        var mine = PimEventCodec.FromItem(item) with { Summary = "Review moved here" };
        repository.UpdateItem(PimEventCodec.ToItem(mine, calendar.Id, item));
        repository.Queue(calendar.Id, item.Id, "put");

        // And there, behind this machine's back, which is what moves the tag.
        server.Publish("clash.ics", Vevent("clash@test", "Review moved there"));

        var refused = await sync.SyncAsync(repository.Collection(calendar.Id)!, cancellationToken);
        return (sync, Assert.Single(refused.Conflicts), item, href);
    }

    /// <summary>
    /// The refusal itself: both copies come back with the report, and neither side has moved.
    /// </summary>
    [Fact]
    public async Task ARefusedWriteReportsBothCopiesAndOverwritesNeither()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;

        var (_, conflict, item, href) = await Clash(server, client, repository, calendar, TestContext.Current.CancellationToken);

        Assert.Equal(item.Id, conflict.ItemId);
        Assert.Equal(calendar.Id, conflict.CollectionId);
        Assert.Contains("Review moved there", conflict.ServerPayload!, StringComparison.Ordinal);
        Assert.NotNull(conflict.ServerEtag);

        // The server still has its own copy, and this machine still has its own — the pull that
        // follows the refused push must not settle the argument by overwriting the local edit.
        Assert.Contains("Review moved there", server.PayloadOf(href), StringComparison.Ordinal);
        Assert.Equal("Review moved here", repository.Item(item.Id)!.Summary);
        Assert.Equal(1, Assert.Single(repository.Queued(calendar.Id)).Attempts);
    }

    /// <summary>
    /// Keeping the local copy sends it again carrying the tag the server actually holds, which is
    /// the only thing that lets the second attempt through the precondition that stopped the first.
    /// </summary>
    [Fact]
    public async Task KeepingTheLocalCopySendsItAgainCarryingTheServersTag()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;

        var (sync, conflict, item, href) = await Clash(server, client, repository, calendar, TestContext.Current.CancellationToken);

        Assert.True(DavSync.KeepLocal(repository, conflict));
        Assert.Equal(conflict.ServerEtag, repository.Item(item.Id)!.Etag);
        var requeued = Assert.Single(repository.Queued(calendar.Id));
        Assert.Equal(0, requeued.Attempts);
        Assert.Null(requeued.LastError);

        var result = await sync.SyncAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Pushed);
        Assert.Empty(result.Conflicts);
        Assert.Contains("Review moved here", server.PayloadOf(href), StringComparison.Ordinal);
        Assert.Empty(repository.Queued(calendar.Id));
    }

    /// <summary>Keeping the server's copy writes it here and takes the refused change off the queue.</summary>
    [Fact]
    public async Task KeepingTheServersCopyWritesItHereAndClearsTheQueue()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;

        var (sync, conflict, item, href) = await Clash(server, client, repository, calendar, TestContext.Current.CancellationToken);

        Assert.True(DavSync.KeepServer(repository, conflict));

        var kept = repository.Item(item.Id)!;
        Assert.Equal("Review moved there", kept.Summary);
        Assert.Equal(PimSyncState.Synced, kept.SyncState);
        Assert.Equal(conflict.ServerEtag, kept.Etag);
        Assert.Empty(repository.Queued(calendar.Id));

        // Nothing more to say to the server, and its copy is untouched.
        var result = await sync.SyncAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.Pushed);
        Assert.Contains("Review moved there", server.PayloadOf(href), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unanswered conflict is asked about again rather than quietly dropped or retried into a
    /// loop of its own: it stays queued, and the next run reports it once more.
    /// </summary>
    [Fact]
    public async Task AConflictNobodyAnsweredIsStillWaitingAfterTheNextSync()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;

        var (sync, _, item, _) = await Clash(server, client, repository, calendar, TestContext.Current.CancellationToken);

        var again = await sync.SyncAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken);

        Assert.Single(again.Conflicts);
        Assert.Equal("Review moved here", repository.Item(item.Id)!.Summary);
        Assert.Equal(2, Assert.Single(repository.Queued(calendar.Id)).Attempts);
    }

    /// <summary>
    /// A deletion the server refuses is a conflict with a copy to show as well: the reader is
    /// choosing between "gone" and whatever the server now holds.
    /// </summary>
    [Fact]
    public async Task ARefusedDeleteCarriesTheServersCopyToo()
    {
        using var server = new FakeDavServer();
        var href = server.Publish("gone.ics", Vevent("gone@test", "Standup"));
        using var client = new DavClient(handler: server);
        var (store, repository, calendar) = Fresh(server);
        using var _ = store;
        var sync = new DavSync(client, repository);
        await sync.SyncAsync(calendar, TestContext.Current.CancellationToken);

        var item = repository.Items(calendar.Id).Single();
        repository.SetSyncState(item.Id, PimSyncState.Deleted);
        repository.Queue(calendar.Id, item.Id, "delete");

        // Somebody else edited it before the delete went up, so the tag the delete carries is no
        // longer the one the server holds.
        server.Publish("gone.ics", Vevent("gone@test", "Standup, at the new time"));

        var result = await sync.SyncAsync(repository.Collection(calendar.Id)!, TestContext.Current.CancellationToken);

        var conflict = Assert.Single(result.Conflicts);
        Assert.True(conflict.LocalDelete);
        Assert.Equal("Standup", conflict.Summary);
        Assert.Contains("Standup", conflict.ServerPayload!, StringComparison.Ordinal);
        Assert.True(server.Has(href));

        // Keeping the server's copy brings the row back rather than leaving it marked deleted.
        Assert.True(DavSync.KeepServer(repository, conflict));
        Assert.Equal(PimSyncState.Synced, repository.Item(item.Id)!.SyncState);
        Assert.Empty(repository.Queued(calendar.Id));
    }

    [Fact]
    public void ATimeRangeFilterIsWrittenAsRfc5545Stamps()
    {
        var body = DavXml.CalendarQuery(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains("20260801T000000Z", body, StringComparison.Ordinal);
        Assert.Contains("20260901T000000Z", body, StringComparison.Ordinal);
        Assert.Contains("VEVENT", body, StringComparison.Ordinal);
    }
}
