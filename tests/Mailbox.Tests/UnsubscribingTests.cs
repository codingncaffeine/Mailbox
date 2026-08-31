using Mailbox.Core;

namespace Mailbox.Tests;

/// <summary>
/// Reading what a mailing list offers as its way out — the two headers, RFC 2369 and 8058.
/// </summary>
public class UnsubscribingTests
{
    [Fact]
    public void AMessageWithoutTheHeaderOffersNothing()
    {
        Assert.Null(UnsubscribeOffer.Parse(null, null));
        Assert.Null(UnsubscribeOffer.Parse("  ", null));
        Assert.Null(UnsubscribeOffer.Parse("no brackets at all", null));
    }

    [Fact]
    public void MailtoAndWebEntriesAreSortedIntoTheirLanes()
    {
        var offer = UnsubscribeOffer.Parse(
            "<mailto:leave@list.example?subject=unsubscribe>, <https://list.example/leave?u=1>", null);

        Assert.NotNull(offer);
        Assert.Equal("mailto:leave@list.example?subject=unsubscribe", offer!.Mailto.Single().AbsoluteUri);
        Assert.Equal("https://list.example/leave?u=1", offer.Web.Single().AbsoluteUri);
        Assert.Null(offer.OneClick);
    }

    [Fact]
    public void ThePostHeaderMakesTheHttpsEntryOneClick()
    {
        var offer = UnsubscribeOffer.Parse(
            "<https://list.example/leave?u=1>",
            "List-Unsubscribe=One-Click");

        Assert.Equal("https://list.example/leave?u=1", offer!.OneClick!.AbsoluteUri);
    }

    [Fact]
    public void OneClickNeverRidesPlainHttp()
    {
        // RFC 8058 requires HTTPS; a plain-http entry can still be a page for the browser.
        var offer = UnsubscribeOffer.Parse(
            "<http://list.example/leave>",
            "List-Unsubscribe=One-Click");

        Assert.NotNull(offer);
        Assert.Null(offer!.OneClick);
        Assert.Single(offer.Web);
    }

    [Fact]
    public void ATypoInOneEntryDoesNotCostTheOther()
    {
        var offer = UnsubscribeOffer.Parse(
            "<not a uri>, <mailto:leave@list.example>", null);

        Assert.Equal("leave@list.example", offer!.Mailto.Single().AbsoluteUri["mailto:".Length..]);
        Assert.Empty(offer.Web);
    }

    [Fact]
    public void CommentsOutsideBracketsAreIgnored()
    {
        var offer = UnsubscribeOffer.Parse(
            "(Use this to opt out) <https://list.example/leave>", null);

        Assert.Single(offer!.Web);
    }
}
