using System.Net;
using System.Net.Http;
using Mailbox.Dav;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The store half of the account wizard's calendar-and-contacts page: what discovery found,
/// written as collections the sync engine will pick up — and written once, however many times
/// the wizard is run.
/// </summary>
public class DavAccountSetupTests
{
    [Fact]
    public async Task WhatDiscoveryFindsIsWrittenWithItsAccountAndAddress()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);

        var found = await DavDiscovery.FindAsync(client, server.Origin, TestContext.Current.CancellationToken);
        var outcome = DavAccountSetup.Add(repository, "you@example.net", found);

        var added = Assert.Single(outcome.Added);
        Assert.Empty(outcome.AlreadyHere);
        Assert.Equal("Work", added.DisplayName);
        Assert.Equal(CollectionKind.Events, added.Kind);
        Assert.Equal("you@example.net", added.Account);
        Assert.Equal("#107C10", added.Color);
        Assert.False(added.IsReadOnly);

        // The row the sync loop will group and fetch by: the address, stored absolute.
        var stored = Assert.Single(repository.Collections(), c => c.DavUrl is { Length: > 0 });
        Assert.Equal(server.CalendarUrl.AbsoluteUri, stored.DavUrl);
    }

    [Fact]
    public async Task RunningTheWizardTwiceAddsNothingTwice()
    {
        using var server = new FakeDavServer();
        using var client = new DavClient(handler: server);
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);

        var found = await DavDiscovery.FindAsync(client, server.Origin, TestContext.Current.CancellationToken);
        DavAccountSetup.Add(repository, "you@example.net", found);
        var again = DavAccountSetup.Add(repository, "you@example.net", found);

        Assert.Empty(again.Added);
        Assert.Single(again.AlreadyHere);
        Assert.Single(repository.Collections(), c => c.DavUrl is { Length: > 0 });
        Assert.Equal("Those are already here — nothing was added twice.", again.Said());
    }

    [Fact]
    public async Task AnAddressBookIsFiledAsAnAddressBook()
    {
        using var server = new FakeDavServer(addressBook: true);
        using var client = new DavClient(handler: server);
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);

        var found = await DavDiscovery.FindAsync(client, server.Origin, TestContext.Current.CancellationToken);
        var outcome = DavAccountSetup.Add(repository, "you@example.net", found);

        var added = Assert.Single(outcome.Added);
        Assert.Equal(CollectionKind.Contacts, added.Kind);
        Assert.Equal("1 address book added. They fill on the next send/receive.", outcome.Said());
    }

    [Fact]
    public void AReadOnlyCollectionStaysReadOnly()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);

        var outcome = DavAccountSetup.Add(
            repository,
            "you@example.net",
            [
                new DavCollection(
                    new Uri("https://dav.example.net/calendars/you/holidays/"),
                    CollectionKind.Events, "Holidays", string.Empty, null, null, IsReadOnly: true),
            ]);

        Assert.True(Assert.Single(outcome.Added).IsReadOnly);
    }

    [Fact]
    public void TheSentenceCountsByKind()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);

        var outcome = DavAccountSetup.Add(
            repository,
            "you@example.net",
            [
                Collection("https://dav.example.net/a/", CollectionKind.Events, "One"),
                Collection("https://dav.example.net/b/", CollectionKind.Events, "Two"),
                Collection("https://dav.example.net/c/", CollectionKind.Contacts, "People"),
            ]);

        Assert.Equal("2 calendars and 1 address book added. They fill on the next send/receive.", outcome.Said());

        static DavCollection Collection(string url, CollectionKind kind, string name)
            => new(new Uri(url), kind, name, string.Empty, null, null, IsReadOnly: false);
    }

    /// <summary>
    /// Discovery is allowed to fail every step into the next, so a wrong password would come out
    /// of it as "nothing found" — the preflight is what tells the two apart, and the wizard asks
    /// it first.
    /// </summary>
    [Fact]
    public async Task ARefusedSignInIsToldApartFromAnEmptyServer()
    {
        using var refusing = new DavClient(handler: new Refuses());
        Assert.True(await DavDiscovery.RefusesSignInAsync(
            refusing, new Uri("https://dav.example.net/"), TestContext.Current.CancellationToken));

        using var server = new FakeDavServer();
        using var open = new DavClient(handler: server);
        Assert.False(await DavDiscovery.RefusesSignInAsync(
            open, server.Origin, TestContext.Current.CancellationToken));
    }

    private sealed class Refuses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
