using Mailbox.Contacts;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// Contacts in the PIM store: the columns a list draws from, the addresses it is found by, and
/// the vCard underneath both, which stays the truth.
/// </summary>
public class ContactStoreTests
{
    private static (PimStore Store, ContactBook Book, Collection Address) Fresh()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var book = new ContactBook(repository);
        return (store, book, book.Default());
    }

    private static Contact Person(string uid = "person-1", string first = "A.", string last = "Person") => new()
    {
        Uid = uid,
        DisplayName = $"{first} {last}",
        FirstName = first,
        LastName = last,
        Company = "Example Ltd.",
        JobTitle = "Principal Engineer",
        Emails = [new ContactEmail("a.person@example.com"), new ContactEmail("a.person@example.net")],
        Phones = [new ContactPhone("+44 20 7946 0000"), new ContactPhone("+44 7700 900000", PhoneKind.Mobile)],
        Addresses = [new ContactAddress { Street = "1 Example Street", City = "London", PostalCode = "EC1A 1AA" }],
        Notes = "Prefers e-mail.",
        Birthday = new DateOnly(1980, 4, 1),
    };

    [Fact]
    public void TheDefaultAddressBookIsMadeWhenThereIsNone()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        Assert.Equal("Contacts", address.DisplayName);
        Assert.Equal(CollectionKind.Contacts, address.Kind);
        Assert.True(address.IsDefault);
        Assert.Equal(address.Id, book.Default().Id);
    }

    [Fact]
    public void AContactIsWrittenToItsColumnsAndReadBackWithoutParsingItsCard()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        book.Save(Person(), address.Id);
        var row = Assert.Single(book.Rows());

        Assert.Equal("A. Person", row.Contact.DisplayName);
        Assert.Equal("Person, A.", row.Contact.FiledAs());
        Assert.Equal("Example Ltd.", row.Contact.Company);
        Assert.Equal("Principal Engineer", row.Contact.JobTitle);
        Assert.Equal("Contacts", row.CollectionName);

        // The addresses and numbers come from the side table, so a list row has them without
        // reading a card.
        Assert.Equal(["a.person@example.com", "a.person@example.net"], row.Contact.Emails.Select(e => e.Address));
        Assert.Contains(row.Contact.Phones, p => p.Kind == PhoneKind.Mobile);
    }

    [Fact]
    public void TheWholeCardIsThereWhenTheContactIsOpened()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var written = book.Save(Person(), address.Id);
        var full = book.Full(written.Id);

        Assert.NotNull(full);
        Assert.Equal("Prefers e-mail.", full!.Notes);
        Assert.Equal(new DateOnly(1980, 4, 1), full.Birthday);
        Assert.Equal("1 Example Street", Assert.Single(full.Addresses).Street);
    }

    [Fact]
    public void ContactsComeBackFiledInOrder()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        book.Save(Person("c", "C.", "Zeta"), address.Id);
        book.Save(Person("a", "A.", "Alpha"), address.Id);
        book.Save(Person("b", "B.", "Middle"), address.Id);

        Assert.Equal(["Alpha, A.", "Middle, B.", "Zeta, C."], book.Rows().Select(r => r.Contact.FiledAs()));
    }

    [Fact]
    public void AnAddressFindsWhoeverHoldsIt()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        book.Save(Person(), address.Id);
        book.Save(Person("b", "B.", "Other") with { Emails = [new ContactEmail("b.other@example.com")] }, address.Id);

        var found = Assert.Single(book.WithAddress("A.Person@Example.com"));
        Assert.Equal("A. Person", found.Contact.DisplayName);
        Assert.Empty(book.WithAddress("nobody@example.com"));
    }

    [Fact]
    public void TypingAPrefixOffersTheContactsItCouldMean()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        book.Save(Person(), address.Id);
        book.Save(Person("b", "B.", "Other") with { Company = "Another Ltd.", Emails = [new ContactEmail("b.other@example.com")] }, address.Id);

        Assert.Equal("A. Person", Assert.Single(book.Matching("Pers")).Contact.DisplayName);
        Assert.Equal("B. Other", Assert.Single(book.Matching("b.oth")).Contact.DisplayName);
        Assert.Equal("B. Other", Assert.Single(book.Matching("Another")).Contact.DisplayName);
        Assert.Empty(book.Matching(""));
        Assert.Empty(book.Matching("zzz"));
    }

    [Fact]
    public void AnEditKeepsTheRowAndItsServerBookkeeping()
    {
        var (store, book, address) = Fresh();
        using var _ = store;
        var repository = new PimRepository(store);

        var written = book.Save(Person(), address.Id);
        repository.SetSyncState(written.Id, PimSyncState.Synced, "etag-1", "person.vcf");

        var stored = repository.Item(written.Id)!;
        var edited = book.Full(written.Id)! with { JobTitle = "Head of Engineering" };
        var again = book.Save(edited, address.Id, stored);

        Assert.Equal(written.Id, again.Id);
        Assert.Equal("etag-1", again.Etag);
        Assert.Equal("person.vcf", again.DavHref);
        Assert.Equal(PimSyncState.Modified, repository.Item(written.Id)!.SyncState);
        Assert.Equal("Head of Engineering", book.Rows()[0].Contact.JobTitle);
    }

    [Fact]
    public void APhotographIsKeptBesideTheRowAndInTheCard()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var pixels = new byte[] { 1, 2, 3, 4, 5 };
        var written = book.Save(Person() with { Photo = new ContactPhoto(pixels, "image/png") }, address.Id);

        Assert.Equal(pixels, new PimRepository(store).ContactPhoto(written.Id)?.Bytes);
        Assert.Equal(pixels, book.Full(written.Id)?.Photo?.Bytes);
    }

    [Fact]
    public void ADistributionListIsStoredAsOneAndKeepsItsMembers()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var group = new Contact
        {
            Uid = "team",
            DisplayName = "Research team",
            IsGroup = true,
            Members = [new GroupMember(Uid: "person-1"), new GroupMember("b.person@example.com", "B. Person")],
        };

        var written = book.Save(group, address.Id);

        Assert.True(new PimRepository(store).Item(written.Id)!.IsGroup);
        var full = book.Full(written.Id)!;
        Assert.True(full.IsGroup);
        Assert.Equal(2, full.Members.Count);
        Assert.Contains(full.Members, m => m.Name == "B. Person");
    }

    /// <summary>
    /// Search spans the whole store, so a contact has to be findable by the things a person
    /// remembers: their name, their company, and the address they write from.
    /// </summary>
    [Fact]
    public void AContactIsFoundByNameCompanyOrAddress()
    {
        var (store, book, address) = Fresh();
        using var _ = store;
        var repository = new PimRepository(store);

        book.Save(Person(), address.Id);

        Assert.Contains(repository.Search("Person"), i => i.Uid == "person-1");
        Assert.Contains(repository.Search("Example"), i => i.Uid == "person-1");
        Assert.Contains(repository.Search("a.person@example.com"), i => i.Uid == "person-1");
    }

    /// <summary>
    /// A card that will not parse still draws a row and can still be repaired — the same
    /// promise the calendar makes about a damaged appointment.
    /// </summary>
    [Fact]
    public void ARowWhoseCardWillNotParseFallsBackToItsColumns()
    {
        var (store, book, address) = Fresh();
        using var _ = store;
        var repository = new PimRepository(store);

        var written = book.Save(Person(), address.Id);
        repository.UpdateItem(repository.Item(written.Id)! with { RawPayload = "not a vCard at all" });

        var row = Assert.Single(book.Rows());
        Assert.Equal("A. Person", row.Contact.DisplayName);
        Assert.Equal("Example Ltd.", row.Contact.Company);
        Assert.Equal("A. Person", book.Full(written.Id)!.DisplayName);
    }
}
