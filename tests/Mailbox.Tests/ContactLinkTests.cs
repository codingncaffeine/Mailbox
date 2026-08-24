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
}
