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
}
