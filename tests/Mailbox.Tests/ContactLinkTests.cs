using Mailbox.Contacts;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// Linked contacts and the duplicate check — the model half Phase 12 deferred. A link is written
/// into both cards and survives the vCard round trip; the duplicate finder answers with a reason
/// a prompt can print, and knows the difference between "the same address" and "the same name".
/// </summary>
public class ContactLinkTests
{
    private static (PimStore Store, ContactBook Book, Collection Address) Fresh()
    {
        var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var book = new ContactBook(repository);
        return (store, book, book.Default());
    }

    private static Contact Person(string uid, string name, string email, string company = "", string phone = "")
        => new()
        {
            Uid = uid,
            DisplayName = name,
            Company = company,
            Emails = email.Length > 0 ? [new ContactEmail(email)] : [],
            Phones = phone.Length > 0 ? [new ContactPhone(phone)] : [],
        };

    // ---- Links ---------------------------------------------------------------------------------

    [Fact]
    public void ALinkIsWrittenIntoBothCardsAndTakenOutOfBoth()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var one = book.Save(Person("card-1", "A. Person", "a@example.com"), address.Id);
        var two = book.Save(Person("card-2", "A. Person", "ap@example.net"), address.Id);

        Assert.True(book.Link(one.Id, two.Id));

        // Both ends, read back off the store — a link only one card knows about is one the
        // reader can only see from one side.
        Assert.Contains("card-2", book.Full(one.Id)!.Links);
        Assert.Contains("card-1", book.Full(two.Id)!.Links);

        var linked = Assert.Single(book.Linked(one.Id));
        Assert.Equal(two.Id, linked.Id);

        // Linking again changes nothing and says so.
        Assert.False(book.Link(one.Id, two.Id));

        Assert.True(book.Unlink(one.Id, two.Id));
        Assert.Empty(book.Full(one.Id)!.Links);
        Assert.Empty(book.Full(two.Id)!.Links);
        Assert.Empty(book.Linked(one.Id));
    }

    [Fact]
    public void ALinkSurvivesTheCardItself()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var one = book.Save(Person("card-1", "A. Person", "a@example.com"), address.Id);
        var two = book.Save(Person("card-2", "A. Person", ""), address.Id);
        book.Link(one.Id, two.Id);

        // The truth is the vCard text, so the link must be in it — a server that hands the card
        // back verbatim hands the link back with it.
        var text = book.Repository.Item(one.Id)!.RawPayload;
        Assert.Contains("X-MAILBOX-LINK", text);
        Assert.Contains("card-2", text);

        var parsed = VCardCodec.ParseOne(text);
        Assert.Contains("card-2", parsed.Links);
    }

    [Fact]
    public void ACardCannotLinkToItself()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var one = book.Save(Person("card-1", "A. Person", "a@example.com"), address.Id);
        Assert.False(book.Link(one.Id, one.Id));
        Assert.Empty(book.Full(one.Id)!.Links);
    }

    [Fact]
    public void ThePeopleListShowsALinkedPairAsOnePerson()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var one = book.Save(Person("card-1", "A. Person", "a@example.com"), address.Id);
        var two = book.Save(Person("card-2", "Anne Person", "anne@example.net", phone: "020 7946 0958"), address.Id);
        book.Save(Person("card-3", "B. Other", "b@example.org"), address.Id);
        book.Link(one.Id, two.Id);

        // Every card for the pickers; one person for the People list.
        Assert.Equal(3, book.Rows().Count);
        var people = book.People();
        Assert.Equal(2, people.Count);

        // The collapsed row leads with the member whose File As sorts first, carries the other's
        // ways of reaching them, and answers to the other's name.
        var person = Assert.Single(people, r => r.AlsoNamed.Count > 0);
        Assert.Equal(2, person.Contact.Emails.Count);
        Assert.Single(person.Contact.Phones);
        Assert.NotEqual(person.Named(), Assert.Single(person.AlsoNamed));
    }

    [Fact]
    public void ThreeCardsLinkedPairwiseAreOnePerson()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var one = book.Save(Person("card-1", "A. Person", "a@example.com"), address.Id);
        var two = book.Save(Person("card-2", "A. Person", "b@example.com"), address.Id);
        var three = book.Save(Person("card-3", "A. Person", "c@example.com"), address.Id);
        book.Link(one.Id, two.Id);
        book.Link(two.Id, three.Id);

        // card-1 and card-3 never linked directly; the union is what makes them one person.
        var person = Assert.Single(book.People());
        Assert.Equal(3, person.Contact.Emails.Count);
        Assert.Equal(2, person.AlsoNamed.Count);
    }

    // ---- Duplicates ----------------------------------------------------------------------------

    [Fact]
    public void ASharedAddressIsCertainWhateverItsCase()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        book.Save(Person("card-1", "A. Person", "A.Person@Example.com"), address.Id);

        var match = Assert.Single(book.Duplicates(Person("card-2", "Somebody Else", "a.person@example.com")));
        Assert.Equal(DuplicateStrength.Certain, match.Strength);
        Assert.Contains("share the address", match.Reason);
    }

    [Fact]
    public void TheSameNameRanksByWhatElseAgrees()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        book.Save(Person("card-1", "B. Other", "", company: "Example Ltd."), address.Id);
        book.Save(Person("card-2", "B. Other", "", phone: "+44 20 7946 0958"), address.Id);
        book.Save(Person("card-3", "B. Other", ""), address.Id);

        // The same number written two ways is one telephone; the country code need not agree.
        var candidate = Person("card-4", "b.  other", "", company: "EXAMPLE ltd", phone: "020 7946 0958");
        var matches = book.Duplicates(candidate);

        Assert.Equal(3, matches.Count);
        Assert.All(matches.Take(2), m => Assert.Equal(DuplicateStrength.Likely, m.Strength));
        Assert.Equal(DuplicateStrength.Possible, matches[2].Strength);
    }

    [Fact]
    public void ALinkedCardIsNotADuplicateAndNorIsTheRowBeingEdited()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        var one = book.Save(Person("card-1", "A. Person", "a@example.com"), address.Id);
        var two = book.Save(Person("card-2", "A. Person", "a@example.com"), address.Id);
        book.Link(one.Id, two.Id);

        // The reader already said these are the same person and wanted both kept.
        var edited = book.Full(one.Id)!;
        Assert.Empty(book.Duplicates(edited, ignoreId: one.Id));
    }

    [Fact]
    public void AGroupAndAPersonAreNeverEachOther()
    {
        var (store, book, address) = Fresh();
        using var _ = store;

        book.Save(Person("card-1", "Project Alpha", "list@example.com"), address.Id);

        var group = Person("group-1", "Project Alpha", "list@example.com") with { IsGroup = true };
        Assert.Empty(book.Duplicates(group));
    }

    // ---- Updating one card with another's information ------------------------------------------

    /// <summary>
    /// The duplicate prompt's second answer: the stored card takes what the typed one says and
    /// keeps everything the typed one is silent about.
    /// </summary>
    /// <remarks>
    /// This was a replacement, so a card typed to correct a company destroyed the address, the
    /// birthday, the photograph, the note and every number the stored card had. What the reference
    /// does is compare the fields that hold something in both and copy the newer card's over, which
    /// is what these assertions are.
    /// </remarks>
    [Fact]
    public void UpdatingFromADuplicateKeepsWhatTheNewCardDoesNotSay()
    {
        var stored = new Contact
        {
            Uid = "card-1",
            DisplayName = "A. Person",
            FirstName = "A.",
            LastName = "Person",
            FileAs = "Person, A.",
            Company = "Example Ltd.",
            JobTitle = "Principal Engineer",
            Emails = [new ContactEmail("a.person@example.com"), new ContactEmail("a.person@example.net")],
            Phones = [new ContactPhone("+44 20 7946 0000"), new ContactPhone("+44 7700 900000", PhoneKind.Mobile)],
            Addresses = [new ContactAddress { Street = "1 Example Street", City = "London" }],
            Urls = ["https://example.com/a.person"],
            Categories = ["Colleagues"],
            Notes = "Prefers e-mail.",
            Birthday = new DateOnly(1980, 4, 1),
            Photo = new ContactPhoto([0x89, 0x50, 0x4E, 0x47], "image/png"),
            IsPrivate = true,
            Links = ["card-2"],
            FollowUpDue = DateTimeOffset.UnixEpoch.AddDays(1000),
        };

        var typed = new Contact
        {
            Uid = "card-3",
            DisplayName = "A. Person",
            FirstName = "A.",
            LastName = "Person",
            Company = "New Company Ltd.",
            Emails = [new ContactEmail("a.person@example.com")],
            Phones = [new ContactPhone("+44 161 496 0009", PhoneKind.Home)],
        };

        var merged = ContactMerge.Update(stored, typed);

        // Said by both: the newer card wins.
        Assert.Equal("New Company Ltd.", merged.Company);

        // Said only by the stored card: kept.
        Assert.Equal("Principal Engineer", merged.JobTitle);
        Assert.Equal("Person, A.", merged.FileAs);
        Assert.Equal("Prefers e-mail.", merged.Notes);
        Assert.Equal(new DateOnly(1980, 4, 1), merged.Birthday);
        Assert.Equal(stored.Photo, merged.Photo);
        Assert.Single(merged.Addresses);
        Assert.Equal(["https://example.com/a.person"], merged.Urls);
        Assert.Equal(["Colleagues"], merged.Categories);
        Assert.True(merged.IsPrivate);
        Assert.Equal(["card-2"], merged.Links);
        Assert.Equal(stored.FollowUpDue, merged.FollowUpDue);

        // Lists: the new entries first, then what the stored card was already reachable at, with
        // the address they share said once.
        Assert.Equal(
            ["a.person@example.com", "a.person@example.net"],
            merged.Emails.Select(e => e.Address));
        Assert.Equal(
            ["+44 161 496 0009", "+44 20 7946 0000", "+44 7700 900000"],
            merged.Phones.Select(p => p.Number));
    }

    /// <summary>One telephone written two ways is one telephone, here as everywhere else.</summary>
    [Fact]
    public void UpdatingDoesNotSayTheSameNumberTwice()
    {
        var stored = Person("card-1", "A. Person", "a.person@example.com", phone: "+44 20 7946 0000");
        var typed = Person("card-2", "A. Person", "A.Person@Example.com", phone: "020 7946 0000");

        var merged = ContactMerge.Update(stored, typed);

        Assert.Single(merged.Emails);
        Assert.Single(merged.Phones);
        Assert.Equal("020 7946 0000", merged.Phones[0].Number);
    }
}
