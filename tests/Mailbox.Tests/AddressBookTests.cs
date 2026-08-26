using Mailbox.Contacts;

namespace Mailbox.Tests;

/// <summary>
/// The Address Book's own logic: what an advanced find matches.
/// </summary>
/// <remarks>
/// The window itself is checked by pressing it — <c>MAILBOX_ADDRESSBOOK</c> opens it and presses
/// its menus — because a menu cannot be clicked by a test and a modal over a modal cannot be
/// photographed. What is testable here is the part with rules in it: which contacts a find with
/// several fields filled in should let through.
/// </remarks>
public class AddressBookTests
{
    private static ContactRow Row(string name, string company = "", string address = "", string title = "")
        => new(1, 1, "Contacts", new Contact
        {
            Uid = "u",
            FirstName = name.Split(' ')[0],
            LastName = name.Contains(' ') ? name.Split(' ')[^1] : string.Empty,
            Company = company,
            JobTitle = title,
            Emails = address.Length == 0 ? [] : [new ContactEmail(address, string.Empty)],
        }, false);

    [Fact]
    public void AnEmptyFindAsksForNothing()
    {
        var find = new AdvancedFind(string.Empty, string.Empty, string.Empty, string.Empty);

        Assert.True(find.IsEmpty);
        Assert.True(find.Matches(Row("A. Person")));
    }

    [Fact]
    public void EveryFilledFieldHasToAnswer()
    {
        var find = new AdvancedFind("Person", "Three Hills", string.Empty, string.Empty);

        Assert.False(find.IsEmpty);
        Assert.True(find.Matches(Row("A. Person", company: "Three Hills Catering")));

        // The name matches and the company does not: a find is every field, not any of them.
        Assert.False(find.Matches(Row("A. Person", company: "Somewhere Else")));
        Assert.False(find.Matches(Row("B. Other", company: "Three Hills Catering")));
    }

    [Fact]
    public void AFindIgnoresCaseAndMatchesPartOfAField()
    {
        var find = new AdvancedFind(string.Empty, string.Empty, "EXAMPLE.NET", "manager");

        Assert.True(find.Matches(Row("A. Person", address: "a.person@example.net", title: "Sales Manager")));
        Assert.False(find.Matches(Row("A. Person", address: "a.person@example.com", title: "Sales Manager")));
    }
}
