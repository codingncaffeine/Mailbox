using Mailbox.Contacts;
using Mailbox.Dav;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// Address books over CardDAV: the same engine as the calendar's, with the other noun. What is
/// tested here is what differs — the home set, the REPORT, the content type, and a card becoming
/// a row with its addresses and its photograph beside it.
/// </summary>
public class CardDavTests
{
    private static string Vcard(string uid, string name, string email = "a.person@example.com") => $"""
        BEGIN:VCARD
        VERSION:3.0
        UID:{uid}
        FN:{name}
        N:{name.Split(' ')[^1]};{name.Split(' ')[0]};;;
        EMAIL;TYPE=INTERNET:{email}
        TEL;TYPE=CELL:+44 7700 900000
        REV:2026-08-16T09:12:00Z
        END:VCARD
        """.ReplaceLineEndings("\r\n");

    private static (PimStore Store, PimRepository Repository, Collection Book) Fresh(FakeDavServer server)
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var book = repository.AddCollection(
            CollectionKind.Contacts, "Contacts", string.Empty, "you@example.net", server.CalendarUrl.ToString());
        return (store, repository, book);
    }

    private static DavSync Sync(DavClient client, PimRepository repository)
        => new(client, repository, DavPayloads.AddressBook);

    /// <summary>
    /// A server with address books and no calendars answers one home set and not the other, which
    /// is why both are asked for.
    /// </summary>
    [Fact]
    public async Task DiscoveryFindsAnAddressBookThroughItsOwnHomeSet()
    {
        using var server = new FakeDavServer(addressBook: true);
        using var client = new DavClient(handler: server);

        var found = await DavDiscovery.FindAsync(client, server.Origin, TestContext.Current.CancellationToken);

        var book = Assert.Single(found);
        Assert.Equal(CollectionKind.Contacts, book.Kind);
        Assert.Equal("Contacts", book.DisplayName);
        Assert.Equal(server.CalendarUrl.AbsolutePath, book.Url.AbsolutePath);
    }

    [Fact]
    public async Task APulledCardBecomesAContactWithItsAddressesBesideIt()
    {
        using var server = new FakeDavServer(addressBook: true);
        server.Publish("person-1.vcf", Vcard("person-1", "A. Person"));

        using var client = new DavClient(handler: server);
        var (store, repository, book) = Fresh(server);
        using var _ = store;

        var result = await Sync(client, repository).SyncAsync(book, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Pulled);
        var item = Assert.Single(repository.Items(book.Id));
        Assert.Equal("A. Person", item.Summary);
        Assert.Equal("Person, A.", item.FileAs);
        Assert.NotNull(item.Etag);

        // The addresses are what a contact is found by, and they are written by the same pull.
        var contacts = new ContactBook(repository);
        Assert.Equal("A. Person", Assert.Single(contacts.WithAddress("a.person@example.com")).Contact.DisplayName);
        Assert.Contains(repository.Search("a.person@example.com"), i => i.Uid == "person-1");
    }

    [Fact]
    public async Task AContactMadeHereGoesUpAsAWholeCard()
    {
        using var server = new FakeDavServer(addressBook: true);
        using var client = new DavClient(handler: server);
        var (store, repository, book) = Fresh(server);
        using var _ = store;

        var contacts = new ContactBook(repository);
        var written = contacts.Save(
            new Contact
            {
                Uid = "local-1",
                DisplayName = "B. Person",
                FirstName = "B.",
                LastName = "Person",
                Emails = [new ContactEmail("b.person@example.com")],
            },
            book.Id);
        repository.Queue(book.Id, written.Id, "put");

        var result = await Sync(client, repository).SyncAsync(book, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Pushed);
        Assert.Empty(result.Conflicts);
        var payload = server.PayloadOf(repository.Item(written.Id)!.DavHref!);
        Assert.StartsWith("BEGIN:VCARD", payload, StringComparison.Ordinal);
        Assert.Contains("B. Person", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ETag path, on the other noun: a card changed here and there is refused, both copies
    /// come back, and keeping ours sends it again with the tag the server holds.
    /// </summary>
    [Fact]
    public async Task ARefusedCardIsReportedWithBothCopiesAndSettledTheSameWay()
    {
        using var server = new FakeDavServer(addressBook: true);
        var href = server.Publish("clash.vcf", Vcard("clash", "A. Person"));

        using var client = new DavClient(handler: server);
        var (store, repository, book) = Fresh(server);
        using var _ = store;
        var sync = Sync(client, repository);
        await sync.SyncAsync(book, TestContext.Current.CancellationToken);

        var contacts = new ContactBook(repository);
        var item = repository.Items(book.Id).Single();
        contacts.Save(contacts.Full(item.Id)! with { JobTitle = "Head of Engineering" }, book.Id, item);
        repository.Queue(book.Id, item.Id, "put");

        server.Publish("clash.vcf", Vcard("clash", "A. Person", "moved@example.com"));

        var refused = await sync.SyncAsync(repository.Collection(book.Id)!, TestContext.Current.CancellationToken);

        var conflict = Assert.Single(refused.Conflicts);
        Assert.Contains("moved@example.com", conflict.ServerPayload!, StringComparison.Ordinal);
        Assert.Equal("Head of Engineering", contacts.Full(item.Id)!.JobTitle);

        Assert.True(DavSync.KeepLocal(repository, conflict));
        var settled = await sync.SyncAsync(repository.Collection(book.Id)!, TestContext.Current.CancellationToken);

        Assert.Equal(1, settled.Pushed);
        Assert.Contains("Head of Engineering", server.PayloadOf(href), StringComparison.Ordinal);
    }

    /// <summary>Keeping the server's copy of a card writes it here, addresses and all.</summary>
    [Fact]
    public async Task KeepingTheServersCardRewritesTheContactHere()
    {
        using var server = new FakeDavServer(addressBook: true);
        server.Publish("clash.vcf", Vcard("clash", "A. Person"));

        using var client = new DavClient(handler: server);
        var (store, repository, book) = Fresh(server);
        using var _ = store;
        var sync = Sync(client, repository);
        await sync.SyncAsync(book, TestContext.Current.CancellationToken);

        var contacts = new ContactBook(repository);
        var item = repository.Items(book.Id).Single();
        contacts.Save(contacts.Full(item.Id)! with { Emails = [new ContactEmail("mine@example.com")] }, book.Id, item);
        repository.Queue(book.Id, item.Id, "put");
        server.Publish("clash.vcf", Vcard("clash", "A. Person", "theirs@example.com"));

        var conflict = Assert.Single((await sync.SyncAsync(repository.Collection(book.Id)!, TestContext.Current.CancellationToken)).Conflicts);
        Assert.True(DavSync.KeepServer(repository, conflict));

        Assert.Equal("theirs@example.com", contacts.Full(item.Id)!.PrimaryEmail);
        Assert.Single(contacts.WithAddress("theirs@example.com"));
        Assert.Empty(contacts.WithAddress("mine@example.com"));
    }

    [Fact]
    public async Task ACardTheServerDroppedIsRemovedHere()
    {
        using var server = new FakeDavServer(addressBook: true);
        var href = server.Publish("gone.vcf", Vcard("gone", "A. Person"));
        server.Publish("stays.vcf", Vcard("stays", "B. Person", "b@example.com"));

        using var client = new DavClient(handler: server);
        var (store, repository, book) = Fresh(server);
        using var _ = store;
        var sync = Sync(client, repository);
        await sync.SyncAsync(book, TestContext.Current.CancellationToken);

        server.Withdraw(href);
        var second = await sync.SyncAsync(repository.Collection(book.Id)!, TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Removed);
        Assert.Equal("B. Person", Assert.Single(repository.Items(book.Id)).Summary);
    }

    /// <summary>A photograph in a pulled card is kept beside the row, where a list can draw it.</summary>
    [Fact]
    public async Task APhotographInAPulledCardIsKeptBesideTheRow()
    {
        using var server = new FakeDavServer(addressBook: true);
        var pixels = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var card = VCardCodec.Serialize(
            new Contact
            {
                Uid = "photo-1",
                DisplayName = "A. Person",
                LastName = "Person",
                Photo = new ContactPhoto(pixels, "image/png"),
            },
            VCardVersion.V3);
        server.Publish("photo-1.vcf", card);

        using var client = new DavClient(handler: server);
        var (store, repository, book) = Fresh(server);
        using var _ = store;

        await Sync(client, repository).SyncAsync(book, TestContext.Current.CancellationToken);

        var item = Assert.Single(repository.Items(book.Id));
        Assert.Equal(pixels, repository.ContactPhoto(item.Id)?.Bytes);
    }

    /// <summary>
    /// A distribution list is a card like any other on the wire, and has to come back as a group
    /// with everyone still in it.
    /// </summary>
    [Fact]
    public async Task AGroupSurvivesTheRoundTripThroughTheServer()
    {
        using var server = new FakeDavServer(addressBook: true);
        using var client = new DavClient(handler: server);
        var (store, repository, book) = Fresh(server);
        using var _ = store;

        var contacts = new ContactBook(repository);
        var written = contacts.Save(
            new Contact
            {
                Uid = "team",
                DisplayName = "Research team",
                IsGroup = true,
                Members = [new GroupMember(Uid: "person-1"), new GroupMember("b.person@example.com", "B. Person")],
            },
            book.Id);
        repository.Queue(book.Id, written.Id, "put");
        Assert.Equal(1, (await Sync(client, repository).SyncAsync(book, TestContext.Current.CancellationToken)).Pushed);

        // Read back into a store that has never seen it, which is what another client does.
        using var second = PimStore.Transient();
        var other = new PimRepository(second);
        var mirror = other.AddCollection(CollectionKind.Contacts, "Mirror", string.Empty, "you@example.net", server.CalendarUrl.ToString());
        Assert.Equal(1, (await Sync(client, other).SyncAsync(mirror, TestContext.Current.CancellationToken)).Pulled);

        var group = new ContactBook(other).Full(other.Items(mirror.Id).Single().Id)!;
        Assert.True(group.IsGroup);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, m => m.Name == "B. Person");
    }
}
