using Mailbox.Contacts;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The address book's half of the Auto-Complete List: who it offers for what has been typed, and
/// what goes on the recipient line when one is taken.
/// </summary>
public class ContactSuggestionTests
{
    private static (PimStore Store, ContactBook Book) Fresh()
    {
        var store = PimStore.Transient();
        var book = new ContactBook(new PimRepository(store));
        var address = book.Default();

        book.Save(
            new Contact
            {
                Uid = "a.person@example.com",
                DisplayName = "A. Person",
                FirstName = "A.",
                LastName = "Person",
                Company = "Example Ltd.",
                Emails = [new ContactEmail("a.person@example.com"), new ContactEmail("a.person@example.net")],
            },
            address.Id);

        book.Save(
            new Contact
            {
                Uid = "b.other@example.com",
                DisplayName = "B. Other",
                FirstName = "B.",
                LastName = "Other",
                Emails = [new ContactEmail("b.other@example.com")],
            },
            address.Id);

        book.Save(
            new Contact
            {
                Uid = "team@example.com",
                DisplayName = "Research team",
                IsGroup = true,
                Members =
                [
                    new GroupMember(Uid: "a.person@example.com"),
                    new GroupMember("c.reader@example.org", "C. Reader"),
                ],
            },
            address.Id);

        return (store, book);
    }

    [Fact]
    public void SomebodyIsOfferedOncePerAddressTheyHave()
    {
        var (store, book) = Fresh();
        using var _ = store;

        var offered = ContactSuggestions.For(book, "Pers");

        Assert.Equal(2, offered.Count);
        Assert.All(offered, s => Assert.Equal("A. Person", s.DisplayName));
        Assert.Equal(["a.person@example.com", "a.person@example.net"], offered.Select(s => s.Address));
        Assert.Equal("A. Person <a.person@example.com>", offered[0].Insert);
        Assert.Equal("Contact", offered[0].Detail);

        // The ✕ empties the remembered-addresses cache; it is not how somebody leaves the
        // address book.
        Assert.All(offered, s => Assert.False(s.CanForget));
    }

    [Fact]
    public void TypingAnAddressFindsTheContactThatHoldsIt()
    {
        var (store, book) = Fresh();
        using var _ = store;

        var offered = ContactSuggestions.For(book, "b.oth");

        Assert.Equal("B. Other <b.other@example.com>", Assert.Single(offered).Insert);
    }

    /// <summary>
    /// A distribution list is one entry that puts everybody on the line: there is no token to
    /// leave unresolved, and a name that silently means several people is one somebody sends to
    /// by mistake.
    /// </summary>
    [Fact]
    public void AGroupIsOfferedOnceAndInsertsEverybodyInIt()
    {
        var (store, book) = Fresh();
        using var _ = store;

        var group = Assert.Single(ContactSuggestions.For(book, "Research"));

        Assert.Equal("Research team", group.DisplayName);
        Assert.Equal("2 members", group.Detail);
        Assert.Equal(string.Empty, group.Address);

        // The member kept by UID is resolved through the address book, so a changed address
        // reaches the line rather than a copy of the old one.
        Assert.Contains("A. Person <a.person@example.com>", group.Insert, StringComparison.Ordinal);
        Assert.Contains("C. Reader <c.reader@example.org>", group.Insert, StringComparison.Ordinal);
        Assert.Contains("; ", group.Insert, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupWithNobodyReachableIsNotOffered()
    {
        var (store, book) = Fresh();
        using var _ = store;

        book.Save(
            new Contact { Uid = "empty@example.com", DisplayName = "Nobody at all", IsGroup = true },
            book.Default().Id);

        Assert.Empty(ContactSuggestions.For(book, "Nobody"));
    }

    [Fact]
    public void NothingIsOfferedForNothingTyped()
    {
        var (store, book) = Fresh();
        using var _ = store;

        Assert.Empty(ContactSuggestions.For(book, string.Empty));
        Assert.Empty(ContactSuggestions.For(book, "   "));
        Assert.Empty(ContactSuggestions.For(book, "zzz"));
    }
}
