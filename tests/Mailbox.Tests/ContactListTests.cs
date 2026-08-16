using Mailbox.Contacts;
using Mailbox.Controls.People;

namespace Mailbox.Tests;

/// <summary>
/// The parts of the People list that are arithmetic rather than drawing: the letter somebody
/// files under, and the initials drawn where there is no photograph.
/// </summary>
/// <remarks>
/// The rest of the list — the index's hit boxes, the rows, the empty state — is checked by
/// posing the module through the harness and photographing it, as every other drawn view is.
/// </remarks>
public class ContactListTests
{
    private static Contact Person(string first, string last, string company = "") => new()
    {
        Uid = $"{first}.{last}@example.com",
        DisplayName = $"{first} {last}",
        FirstName = first,
        LastName = last,
        Company = company,
    };

    [Fact]
    public void InitialsComeFromTheNameWhereThereIsOne()
    {
        Assert.Equal("AP", ContactListView.InitialsOf(Person("A.", "Person")));
        Assert.Equal("AP", ContactListView.InitialsOf(new Contact { Uid = "1", DisplayName = "A. Person" }));
        // Two words give two letters, whoever they belong to.
        Assert.Equal("3C", ContactListView.InitialsOf(new Contact { Uid = "2", DisplayName = "3 Hills Catering" }));
    }

    [Fact]
    public void TheIndexLetterFollowsHowTheContactFiles()
    {
        var person = Person("A.", "Person", "Example Ltd.");

        Assert.Equal('P', person.IndexLetter());
        Assert.Equal('A', person.IndexLetter(FileAsOrder.FirstLast));
        Assert.Equal('E', person.IndexLetter(FileAsOrder.Company));
    }

    [Fact]
    public void AGroupFilesUnderItsOwnName()
    {
        var group = new Contact { Uid = "team", DisplayName = "Research team", IsGroup = true };

        Assert.Equal("Research team", group.FiledAs());
        Assert.Equal('R', group.IndexLetter());
    }
}
