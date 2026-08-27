using Mailbox.Core.Feeds;

namespace Mailbox.Tests;

/// <summary>
/// The addresses whose feed is a rule rather than a search: worked out exactly, with no request
/// at all.
/// </summary>
public class FeedPlatformTests
{
    [Theory]
    [InlineData("https://www.youtube.com/channel/UCXuqSBlHAE6Xw-yeJA0Tunw",
        "https://www.youtube.com/feeds/videos.xml?channel_id=UCXuqSBlHAE6Xw-yeJA0Tunw")]
    [InlineData("https://www.reddit.com/r/linux",
        "https://www.reddit.com/r/linux/.rss")]
    [InlineData("https://www.reddit.com/r/linux/",
        "https://www.reddit.com/r/linux/.rss")]
    [InlineData("https://old.reddit.com/user/someone",
        "https://www.reddit.com/user/someone/.rss")]
    [InlineData("https://github.com/dotnet/runtime",
        "https://github.com/dotnet/runtime/releases.atom")]
    [InlineData("https://github.com/dotnet",
        "https://github.com/dotnet.atom")]
    [InlineData("https://medium.com/@someone",
        "https://medium.com/feed/@someone")]
    [InlineData("https://example.substack.com",
        "https://example.substack.com/feed")]
    [InlineData("https://example.blogspot.com/",
        "https://example.blogspot.com/feeds/posts/default")]
    [InlineData("https://mastodon.social/@someone",
        "https://mastodon.social/@someone.rss")]
    public void AnAddressWhoseFeedIsARuleNeedsNoLookingAtAll(string typed, string expected)
        => Assert.Equal(expected, FeedPlatforms.For(typed)[0].Url);

    [Fact]
    public void ARepositoryOffersItsReleasesFirstAndItsCommitsLast()
    {
        // A reader following a project almost always wants to hear about releases, and almost
        // never about every commit.
        var found = FeedPlatforms.For("https://github.com/dotnet/runtime");

        Assert.Equal(3, found.Count);
        Assert.EndsWith("releases.atom", found[0].Url, StringComparison.Ordinal);
        Assert.EndsWith("commits.atom", found[^1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinarySiteImpliesNothingAndIsLeftToTheSearch()
    {
        Assert.Empty(FeedPlatforms.For("https://arstechnica.com"));
        Assert.Empty(FeedPlatforms.For("not an address"));
    }

    [Theory]
    [InlineData("cheese sandwich", true)]
    [InlineData("cybersecurity", true)]
    [InlineData("theverge.com", false)]
    [InlineData("https://theverge.com", false)]
    [InlineData("news.ycombinator.com/rss", false)]
    public void ASubjectIsToldFromAPlace(string typed, bool isTopic)
        => Assert.Equal(isTopic, FeedPlatforms.LooksLikeTopic(typed));

    [Fact]
    public void ASubjectBecomesAStandingSearch()
    {
        // The nearest honest thing to a hosted reader's topic search, which needs an index of
        // the open web that a local application cannot have. This is a real feed address.
        var found = Assert.Single(FeedPlatforms.ForTopic("cheese sandwich"));

        Assert.Contains("news.google.com/rss/search", found.Url, StringComparison.Ordinal);
        Assert.Contains("cheese%20sandwich", found.Url, StringComparison.Ordinal);
        Assert.Contains("cheese sandwich", found.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void APageThatLinksToItsFeedInTheFooterIsStillRead()
    {
        // The sites that advertise nothing in their head and put "RSS" in the footer, which the
        // reader can see and the application could not.
        const string html = """
            <html><head><title>A site</title></head>
            <body><p>Words</p>
            <footer><a href="/about">About</a> · <a href="/feed.xml">RSS</a></footer>
            </body></html>
            """;

        var found = Assert.Single(FeedLinks.LinkedFrom(html, "https://example.com/"));
        Assert.Equal("https://example.com/feed.xml", found.Url);
    }

    [Fact]
    public void AnOrdinaryLinkIsNotMistakenForAFeed()
    {
        const string html = """
            <html><body><a href="/about">About us</a><a href="/contact">Contact</a></body></html>
            """;

        Assert.Empty(FeedLinks.LinkedFrom(html, "https://example.com/"));
    }
}
