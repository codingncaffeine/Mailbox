using Mailbox.Core.People;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// The People page's "Default Full Name order", which decides what a typed name means — the
/// string itself cannot say whether the family name leads.
/// </summary>
public class FullNameTests
{
    [Fact]
    public void FirstMiddleLastIsTheDefaultReading()
    {
        var parts = FullNames.Parse("Anne Marie Vries", FullNameOrder.FirstMiddleLast);
        Assert.Equal(("Anne", "Marie", "Vries"), (parts.First, parts.Middle, parts.Last));

        var two = FullNames.Parse("Anne Vries", FullNameOrder.FirstMiddleLast);
        Assert.Equal(("Anne", "", "Vries"), (two.First, two.Middle, two.Last));
    }

    [Fact]
    public void LastFirstLeadsWithTheFamilyName()
    {
        var parts = FullNames.Parse("Vries Anne Marie", FullNameOrder.LastFirst);
        Assert.Equal(("Anne", "Marie", "Vries"), (parts.First, parts.Middle, parts.Last));
    }

    [Fact]
    public void FirstLastLastKeepsATwoPartFamilyNameWhole()
    {
        var parts = FullNames.Parse("Maria Garcia Lopez", FullNameOrder.FirstLastLast);
        Assert.Equal(("Maria", "", "Garcia Lopez"), (parts.First, parts.Middle, parts.Last));
    }

    [Fact]
    public void ACommaOverrulesTheOption()
    {
        // The writer just said which is the family name; the default has nothing to add.
        foreach (var order in new[] { FullNameOrder.FirstMiddleLast, FullNameOrder.LastFirst, FullNameOrder.FirstLastLast })
        {
            var parts = FullNames.Parse("Vries, Anne Marie", order);
            Assert.Equal(("Anne", "Marie", "Vries"), (parts.First, parts.Middle, parts.Last));
        }
    }

    [Fact]
    public void OneWordIsAFirstNameAlone()
    {
        // Filing a person under the only word they have would make "Cher" a surname.
        var parts = FullNames.Parse("Cher", FullNameOrder.LastFirst);
        Assert.Equal(("Cher", "", ""), (parts.First, parts.Middle, parts.Last));
    }

    [Fact]
    public void ATitleAndASuffixComeOffTheEnds()
    {
        var parts = FullNames.Parse("Dr. Anne Marie Smith Jr.", FullNameOrder.FirstMiddleLast);
        Assert.Equal(("Dr.", "Anne", "Marie", "Smith", "Jr."),
            (parts.Prefix, parts.First, parts.Middle, parts.Last, parts.Suffix));

        // And back to one line in speaking order.
        Assert.Equal("Dr. Anne Marie Smith Jr.", parts.Joined());
    }

    [Fact]
    public void ATitleAloneStaysAName()
    {
        // "Miss" with nothing after it is somebody called Miss, not an empty card with a title.
        var parts = FullNames.Parse("Miss", FullNameOrder.FirstMiddleLast);
        Assert.Equal(("", "Miss", "", ""), (parts.Prefix, parts.First, parts.Last, parts.Suffix));
    }

    [Fact]
    public void ASuffixAfterACommaStillReads()
    {
        var parts = FullNames.Parse("Mr. John Smith, Sr.", FullNameOrder.FirstMiddleLast);
        Assert.Equal(("Mr.", "John", "Smith", "Sr."), (parts.Prefix, parts.First, parts.Last, parts.Suffix));
    }
}
