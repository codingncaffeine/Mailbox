using System.Globalization;
using Mailbox.Contacts;
using Mailbox.Dav;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The DAV engine against a server that is really there.
/// </summary>
/// <remarks>
/// <see cref="FakeDavServer"/> answers what we expect a server to answer, which is exactly what
/// makes it useless for finding out what a server actually answers. These run only when told
/// where one is:
/// <code>
/// MAILBOX_CALDAV_URL=http://127.0.0.1:5232/ MAILBOX_CALDAV_USER=you MAILBOX_CALDAV_PASSWORD=secret \
///   dotnet test --filter RealDav
/// </code>
/// Each makes a calendar of its own on the server and removes it again, so a run leaves nothing
/// behind and two runs do not collide. Skipped, not passed, when no server is named — a green
/// test that did nothing is worse than no test.
/// </remarks>
public class RealDavTests
{
    private static string? Server => Environment.GetEnvironmentVariable("MAILBOX_CALDAV_URL");

    private static DavCredentials Credentials => new(
        Environment.GetEnvironmentVariable("MAILBOX_CALDAV_USER"),
        Environment.GetEnvironmentVariable("MAILBOX_CALDAV_PASSWORD"));

    /// <summary>A calendar of this run's own, and the store that keeps it here.</summary>
    private sealed class Fixture : IDisposable
    {
        public required DavClient Client { get; init; }
        public required PimStore Store { get; init; }
        public required PimRepository Repository { get; init; }
        public required Collection Calendar { get; init; }
        public required Uri Url { get; init; }

        public DavSync Sync => DavSync.For(Client, Repository, Calendar);

        public Collection Fresh => Repository.Collection(Calendar.Id)!;

        public void Dispose()
        {
            try
            {
                Client.DeleteAsync(Url).GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
                // The server is gone; there is nothing left to tidy.
            }

            Client.Dispose();
            Store.Dispose();
        }
    }

    private static async Task<Fixture> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        Assert.SkipUnless(Server is { Length: > 0 }, "Set MAILBOX_CALDAV_URL to run against a real server.");

        var client = new DavClient(Credentials);
        var home = await DavDiscovery.FindHomeAsync(client, new Uri(Server!), cancellationToken);
        Assert.SkipWhen(home is null, $"No calendar home set found at {Server}.");

        // A calendar of this run's own. The name carries the test, so a run that fails and leaves
        // one behind says which one it was.
        var url = new Uri(home!, $"mailbox-{name}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}/");
        var made = await client.MakeCalendarAsync(url, $"Mailbox {name}", cancellationToken);
        Assert.True(made.Ok, $"MKCALENDAR answered {(int)made.Status}.");

        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(
            CollectionKind.Events, $"Mailbox {name}", "#0078D4", Credentials.UserName ?? "dav", url.ToString());

        return new Fixture { Client = client, Store = store, Repository = repository, Calendar = calendar, Url = url };
    }

    private static CalendarEvent Appointment(string uid, string summary, DateTime start, string? rrule = null) => new()
    {
        Uid = uid,
        Summary = summary,
        Location = "Meeting room 2",
        Start = EventTime.At(start, "UTC"),
        End = EventTime.At(start.AddMinutes(30), "UTC"),
        Rrule = rrule,
    };

    /// <summary>An address book of this run's own, for the CardDAV half.</summary>
    private static async Task<Fixture> ConnectBookAsync(string name, CancellationToken cancellationToken)
    {
        Assert.SkipUnless(Server is { Length: > 0 }, "Set MAILBOX_CALDAV_URL to run against a real server.");

        var client = new DavClient(Credentials);
        var homes = await DavDiscovery.FindHomesAsync(client, new Uri(Server!), cancellationToken);
        Assert.SkipWhen(homes.Count == 0, $"No home set found at {Server}.");

        var url = new Uri(homes[^1], $"mailbox-{name}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}/");
        var made = await client.MakeAddressBookAsync(url, $"Mailbox {name}", cancellationToken);
        Assert.True(made.Ok, $"MKCOL answered {(int)made.Status}.");

        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var book = repository.AddCollection(
            CollectionKind.Contacts, $"Mailbox {name}", string.Empty, Credentials.UserName ?? "dav", url.ToString());

        return new Fixture { Client = client, Store = store, Repository = repository, Calendar = book, Url = url };
    }

    /// <summary>
    /// The walk RFC 6764 lays down, against a server that really has to answer each step: the
    /// well-known path, the principal, the home set, and what is in it.
    /// </summary>
    [Fact]
    public async Task DiscoveryFindsTheCalendarItWasJustGiven()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = await ConnectAsync("discovery", token);

        var found = await DavDiscovery.FindAsync(fixture.Client, new Uri(Server!), token);

        Assert.Contains(found, c => c.Url.AbsolutePath == fixture.Url.AbsolutePath);
        var mine = found.First(c => c.Url.AbsolutePath == fixture.Url.AbsolutePath);
        Assert.Equal(CollectionKind.Events, mine.Kind);
        Assert.False(mine.IsReadOnly);
    }

    /// <summary>
    /// A whole life: written here, pushed, changed, pushed again, deleted, and gone from both
    /// sides — with the server's own ETags carried through every step.
    /// </summary>
    [Fact]
    public async Task AnAppointmentGoesUpChangesAndComesOffAgain()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = await ConnectAsync("roundtrip", token);
        var repository = fixture.Repository;
        var sync = fixture.Sync;

        var item = repository.AddItem(PimEventCodec.ToItem(
            Appointment("roundtrip@mailbox.test", "Review", new DateTime(2026, 8, 18, 9, 0, 0)),
            fixture.Calendar.Id));
        repository.Queue(fixture.Calendar.Id, item.Id, "put");

        var first = await sync.SyncAsync(fixture.Fresh, token);
        Assert.Equal(1, first.Pushed);
        Assert.Empty(first.Conflicts);
        Assert.Empty(repository.Queued(fixture.Calendar.Id));

        var stored = repository.Item(item.Id)!;
        Assert.NotNull(stored.Etag);
        Assert.NotNull(stored.DavHref);

        // The server's own copy, read back through GET rather than through our own store.
        var theirs = await fixture.Client.GetAsync(new Uri(fixture.Url, stored.DavHref!), token);
        Assert.True(theirs.Ok);
        Assert.Contains("Review", theirs.Body, StringComparison.Ordinal);

        // Changed here, pushed again: the update carries If-Match and must be accepted.
        var edited = PimEventCodec.FromItem(stored) with { Summary = "Review with the team" };
        repository.UpdateItem(PimEventCodec.ToItem(edited, fixture.Calendar.Id, stored));
        repository.Queue(fixture.Calendar.Id, item.Id, "put");

        var second = await sync.SyncAsync(fixture.Fresh, token);
        Assert.Equal(1, second.Pushed);
        Assert.Empty(second.Conflicts);
        Assert.NotEqual(stored.Etag, repository.Item(item.Id)!.Etag);

        // And off again.
        repository.SetSyncState(item.Id, PimSyncState.Deleted);
        repository.Queue(fixture.Calendar.Id, item.Id, "delete");
        var third = await sync.SyncAsync(fixture.Fresh, token);

        Assert.Equal(1, third.Pushed);
        Assert.Empty(repository.Items(fixture.Calendar.Id));
        var gone = await fixture.Client.GetAsync(new Uri(fixture.Url, stored.DavHref!), token);
        Assert.False(gone.Ok);
    }

    /// <summary>
    /// A second client's edit, seen from here: the pull brings it in, and this store's own copy
    /// of it is the server's.
    /// </summary>
    [Fact]
    public async Task WhatAnotherClientWroteComesDownOnTheNextSync()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = await ConnectAsync("pull", token);

        // Straight onto the server, as another client would have.
        var payload = ICalendarCodec.SerializeCalendar([Appointment("pull@mailbox.test", "Standup", new DateTime(2026, 8, 19, 9, 0, 0))]);
        var written = await fixture.Client.PutAsync(new Uri(fixture.Url, "pull-mailbox-test.ics"), payload, cancellationToken: token);
        Assert.True(written.Ok, $"PUT answered {(int)written.Status}.");

        var result = await fixture.Sync.SyncAsync(fixture.Fresh, token);

        Assert.Equal(1, result.Pulled);
        var item = Assert.Single(fixture.Repository.Items(fixture.Calendar.Id));
        Assert.Equal("Standup", item.Summary);
        Assert.NotNull(item.Etag);
        Assert.Equal(PimSyncState.Synced, item.SyncState);

        // A second poll asks for what changed and finds nothing — whether by sync-collection or
        // by the CTag, which is the point of trying both against a real server.
        var again = await fixture.Sync.SyncAsync(fixture.Fresh, token);
        Assert.Equal(0, again.Pulled);
        Assert.Equal(0, again.Removed);
        Assert.True(await fixture.Sync.IsUnchangedAsync(fixture.Fresh, token));
    }

    /// <summary>
    /// The precondition, against a server that enforces it: an edit made here while the server's
    /// copy moved is refused, reported with both copies, and settled by re-sending with the tag
    /// the server actually holds.
    /// </summary>
    [Fact]
    public async Task TheServerRefusesAStaleWriteAndKeepingOursThenGoesThrough()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = await ConnectAsync("conflict", token);
        var repository = fixture.Repository;
        var sync = fixture.Sync;

        var href = new Uri(fixture.Url, "conflict-mailbox-test.ics");
        await fixture.Client.PutAsync(
            href,
            ICalendarCodec.SerializeCalendar([Appointment("conflict@mailbox.test", "Review", new DateTime(2026, 8, 20, 9, 0, 0))]),
            cancellationToken: token);
        await sync.SyncAsync(fixture.Fresh, token);

        var item = Assert.Single(repository.Items(fixture.Calendar.Id));
        var mine = PimEventCodec.FromItem(item) with { Summary = "Review moved here" };
        repository.UpdateItem(PimEventCodec.ToItem(mine, fixture.Calendar.Id, item));
        repository.Queue(fixture.Calendar.Id, item.Id, "put");

        // Somebody else gets there first, which moves the tag.
        await fixture.Client.PutAsync(
            href,
            ICalendarCodec.SerializeCalendar([Appointment("conflict@mailbox.test", "Review moved there", new DateTime(2026, 8, 20, 11, 0, 0))]),
            cancellationToken: token);

        var refused = await sync.SyncAsync(fixture.Fresh, token);

        var conflict = Assert.Single(refused.Conflicts);
        Assert.Equal(item.Id, conflict.ItemId);
        Assert.NotNull(conflict.ServerEtag);
        Assert.Contains("Review moved there", conflict.ServerPayload!, StringComparison.Ordinal);

        // Neither side has been overwritten: this is the whole reason the precondition is sent.
        Assert.Equal("Review moved here", repository.Item(item.Id)!.Summary);

        Assert.True(DavSync.KeepLocal(repository, conflict));
        var settled = await sync.SyncAsync(fixture.Fresh, token);

        Assert.Equal(1, settled.Pushed);
        Assert.Empty(settled.Conflicts);
        var theirs = await fixture.Client.GetAsync(href, token);
        Assert.Contains("Review moved here", theirs.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A server keeps one resource per UID, so a series' master and its override go up together —
    /// the case where sending the master alone silently deletes the override.
    /// </summary>
    [Fact]
    public async Task ASeriesAndItsOverrideTravelAsOneResource()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = await ConnectAsync("series", token);
        var repository = fixture.Repository;

        var master = Appointment("series@mailbox.test", "Weekly sync", new DateTime(2026, 8, 3, 9, 0, 0), "FREQ=WEEKLY;BYDAY=MO");
        var moved = master with
        {
            Rrule = null,
            RecurrenceId = EventTime.At(new DateTime(2026, 8, 17, 9, 0, 0), "UTC"),
            Start = EventTime.At(new DateTime(2026, 8, 17, 14, 0, 0), "UTC"),
            End = EventTime.At(new DateTime(2026, 8, 17, 15, 0, 0), "UTC"),
            Summary = "Weekly sync, moved",
        };

        var masterRow = repository.AddItem(PimEventCodec.ToItem(master, fixture.Calendar.Id));
        repository.AddItem(PimEventCodec.ToItem(moved, fixture.Calendar.Id));
        repository.Queue(fixture.Calendar.Id, masterRow.Id, "put");

        Assert.Equal(1, (await fixture.Sync.SyncAsync(fixture.Fresh, token)).Pushed);

        // Read back into a store that has never seen either, which is what another client does.
        using var second = PimStore.Transient();
        var other = new PimRepository(second);
        var mirror = other.AddCollection(
            CollectionKind.Events, "Mirror", "#0078D4", Credentials.UserName ?? "dav", fixture.Url.ToString());
        var pulled = await new DavSync(fixture.Client, other).SyncAsync(mirror, token);

        Assert.Equal(2, pulled.Pulled);
        var rows = other.ItemsByUid(mirror.Id, "series@mailbox.test");
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => !r.IsOverride && r.Rrule is { Length: > 0 });
        Assert.Contains(rows, r => r.IsOverride && r.Summary == "Weekly sync, moved");

        // Both rows are one resource on the server, as RFC 4791 requires.
        Assert.Single(rows.Select(r => r.DavHref).Distinct(StringComparer.Ordinal));
    }
    // ---- CardDAV, against the same server -------------------------------------------------------

    /// <summary>
    /// A contact's whole life over CardDAV: made here, pushed as a card, changed, and taken off
    /// again — with the addresses that make it findable written by the same pull.
    /// </summary>
    [Fact]
    public async Task AContactGoesUpAsACardAndComesBackWithItsAddresses()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = await ConnectBookAsync("contacts", token);
        var repository = fixture.Repository;
        var contacts = new ContactBook(repository);

        var written = contacts.Save(
            new Contact
            {
                Uid = "person@mailbox.test",
                DisplayName = "A. Person",
                FirstName = "A.",
                LastName = "Person",
                Company = "Example Ltd.",
                Emails = [new ContactEmail("a.person@example.com")],
                Phones = [new ContactPhone("+44 7700 900000", PhoneKind.Mobile)],
            },
            fixture.Calendar.Id);
        repository.Queue(fixture.Calendar.Id, written.Id, "put");

        var pushed = await fixture.Sync.SyncAsync(fixture.Fresh, token);
        Assert.Equal(1, pushed.Pushed);
        Assert.Empty(pushed.Conflicts);

        var stored = repository.Item(written.Id)!;
        Assert.NotNull(stored.Etag);

        var theirs = await fixture.Client.GetAsync(new Uri(fixture.Url, stored.DavHref!), token);
        Assert.True(theirs.Ok, $"GET answered {(int)theirs.Status}.");
        Assert.Contains("BEGIN:VCARD", theirs.Body, StringComparison.Ordinal);
        Assert.Contains("A. Person", theirs.Body, StringComparison.Ordinal);

        // Read back into a store that has never seen it, which is what another client does.
        using var second = PimStore.Transient();
        var other = new PimRepository(second);
        var mirror = other.AddCollection(
            CollectionKind.Contacts, "Mirror", string.Empty, Credentials.UserName ?? "dav", fixture.Url.ToString());
        Assert.Equal(1, (await DavSync.For(fixture.Client, other, mirror).SyncAsync(mirror, token)).Pulled);

        var mirrored = new ContactBook(other);
        var row = Assert.Single(mirrored.Rows());
        Assert.Equal("A. Person", row.Contact.DisplayName);
        Assert.Equal("Example Ltd.", row.Contact.Company);
        Assert.Equal("a.person@example.com", row.Contact.PrimaryEmail);
        Assert.Single(mirrored.WithAddress("a.person@example.com"));

        // And off again.
        repository.SetSyncState(written.Id, PimSyncState.Deleted);
        repository.Queue(fixture.Calendar.Id, written.Id, "delete");
        Assert.Equal(1, (await fixture.Sync.SyncAsync(fixture.Fresh, token)).Pushed);
        Assert.Empty(repository.Items(fixture.Calendar.Id));
    }

    /// <summary>
    /// Discovery finds an address book as well as a calendar: they are two home sets on one
    /// principal, and a client that asks for only one finds only one.
    /// </summary>
    [Fact]
    public async Task DiscoveryFindsTheAddressBookToo()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = await ConnectBookAsync("discovery-book", token);

        var found = await DavDiscovery.FindAsync(fixture.Client, new Uri(Server!), token);

        var mine = found.FirstOrDefault(c => c.Url.AbsolutePath == fixture.Url.AbsolutePath);
        Assert.NotNull(mine);
        Assert.Equal(CollectionKind.Contacts, mine!.Kind);
    }

    /// <summary>A distribution list is a card on the wire, and comes back with everyone in it.</summary>
    [Fact]
    public async Task AGroupSurvivesTheServer()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = await ConnectBookAsync("group", token);
        var contacts = new ContactBook(fixture.Repository);

        var written = contacts.Save(
            new Contact
            {
                Uid = "team@mailbox.test",
                DisplayName = "Research team",
                IsGroup = true,
                Members = [new GroupMember(Uid: "person@mailbox.test"), new GroupMember("b.person@example.com", "B. Person")],
            },
            fixture.Calendar.Id);
        fixture.Repository.Queue(fixture.Calendar.Id, written.Id, "put");
        Assert.Equal(1, (await fixture.Sync.SyncAsync(fixture.Fresh, token)).Pushed);

        using var second = PimStore.Transient();
        var other = new PimRepository(second);
        var mirror = other.AddCollection(
            CollectionKind.Contacts, "Mirror", string.Empty, Credentials.UserName ?? "dav", fixture.Url.ToString());
        Assert.Equal(1, (await DavSync.For(fixture.Client, other, mirror).SyncAsync(mirror, token)).Pulled);

        var group = new ContactBook(other).Full(other.Items(mirror.Id).Single().Id)!;
        Assert.True(group.IsGroup);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, m => m.Name == "B. Person");
    }
}
