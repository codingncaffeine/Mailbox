using Mailbox.Core.Feeds;
using Mailbox.Protocols;

namespace Mailbox.Tests;

/// <summary>
/// Finding a feed from an address somebody actually has, and moving a subscription list in and
/// out of the application.
/// </summary>
public class FeedDiscoveryTests
{
    private const string Page = """
        <!DOCTYPE html>
        <html><head>
          <title>Example</title>
          <link rel="stylesheet" href="/style.css">
          <link rel="alternate" type="application/rss+xml" title="Example &amp; Co" href="/feed.xml">
          <link rel="alternate" type="application/atom+xml" title="Comments" href="https://example.com/comments/atom">
          <link rel="alternate" type="application/json" href="/manifest.json">
        </head><body><p>A page</p></body></html>
        """;

    private static string Feed(string title) => $"""
        <rss version="2.0"><channel><title>{title}</title>
        <item><guid>1</guid><title>First</title></item></channel></rss>
        """;

    [Fact]
    public void APageAdvertisingFeedsHandsThemOverWithTheirNames()
    {
        var found = FeedLinks.In(Page, "https://example.com/blog/");

        Assert.Equal(2, found.Count);
        Assert.Equal("https://example.com/feed.xml", found[0].Url);
        Assert.Equal("Example & Co", found[0].Title);
        Assert.Equal("https://example.com/comments/atom", found[1].Url);

        // The manifest is application/json and not a feed. Subscribing to it finds nothing.
        Assert.DoesNotContain(found, f => f.Url.EndsWith("manifest.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("theverge.com", "https://theverge.com/")]
    [InlineData("  https://example.com/feed  ", "https://example.com/feed")]
    [InlineData("feed://example.com/rss", "https://example.com/rss")]
    public void TheAddressSomebodyTypedIsTheAddressTheyMeant(string typed, string expected)
    {
        // Nobody types a scheme, people paste feed:// out of old links, and copied addresses
        // carry whitespace.
        Assert.Equal(expected, FeedFinder.Normalize(typed));
    }

    [Fact]
    public async Task PastingASiteAddressFindsTheFeedBehindIt()
    {
        var server = new FakeFeedServer()
            .Serve("https://example.com/", Page, "text/html")
            .Serve("https://example.com/feed.xml", Feed("Example Weekly"));

        using var fetch = new FeedFetch(server);
        var search = await new FeedFinder(fetch).FindAsync("example.com", TestContext.Current.CancellationToken);

        Assert.True(search.Found);
        Assert.Equal("https://example.com/feed.xml", search.Feeds[0].Url);
    }

    [Fact]
    public async Task PastingTheFeedItselfIsRecognisedWithoutAnotherRequest()
    {
        var server = new FakeFeedServer().Serve("https://example.com/feed.xml", Feed("Example Weekly"));

        using var fetch = new FeedFetch(server);
        var search = await new FeedFinder(fetch).FindAsync("https://example.com/feed.xml", TestContext.Current.CancellationToken);

        Assert.True(search.Found);
        Assert.Equal("Example Weekly", search.Feeds[0].Title);
        Assert.Equal(1, server.RequestsFor("https://example.com/feed.xml"));
    }

    [Fact]
    public async Task ASiteThatAdvertisesNothingIsStillLookedIn()
    {
        // Most publishing software puts the feed at one of a handful of paths whether the page
        // says so or not.
        var server = new FakeFeedServer()
            .Serve("https://example.com/", "<html><head><title>Bare</title></head><body>x</body></html>", "text/html")
            .Serve("https://example.com/feed", Feed("Found By Looking"));

        using var fetch = new FeedFetch(server);
        var search = await new FeedFinder(fetch).FindAsync("example.com", TestContext.Current.CancellationToken);

        Assert.True(search.Found);
        Assert.Equal("Found By Looking", search.Feeds[0].Title);
    }

    [Fact]
    public async Task AnAddressWithNoFeedAnywhereSaysSoRatherThanFailingSilently()
    {
        var server = new FakeFeedServer()
            .Serve("https://example.com/", "<html><head><title>Bare</title></head><body>x</body></html>", "text/html");

        using var fetch = new FeedFetch(server);
        var search = await new FeedFinder(fetch).FindAsync("example.com", TestContext.Current.CancellationToken);

        Assert.False(search.Found);
        Assert.NotEmpty(search.Error);
    }

    // ---- OPML --------------------------------------------------------------------------------

    [Fact]
    public void AnOutlineFromAnotherReaderIsReadHeadingsAndAll()
    {
        // What an export from Feedly, Inoreader or NetNewsWire looks like: feeds nested under
        // headings, with the spellings of xmlUrl that readers actually write.
        var opml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="1.0"><head><title>subscriptions</title></head><body>
              <outline text="Technology" title="Technology">
                <outline type="rss" text="The Verge" xmlUrl="https://theverge.com/rss/index.xml" htmlUrl="https://theverge.com"/>
                <outline type="rss" text="Ars Technica" xmlurl="https://arstechnica.com/feed"/>
              </outline>
              <outline text="Loose" type="rss" url="https://example.com/loose.xml"/>
            </body></opml>
            """;

        var entries = Opml.Read(opml);

        Assert.Equal(3, entries.Count);
        Assert.Equal("Technology", entries[0].Category);
        Assert.Equal("The Verge", entries[0].Title);
        Assert.Equal("https://theverge.com", entries[0].SiteUrl);
        Assert.Equal("Technology", entries[1].Category);
        Assert.Empty(entries[2].Category);
        Assert.Equal("https://example.com/loose.xml", entries[2].Url);
    }

    [Fact]
    public void AnOutlineWrittenHereIsOneAnotherReaderCanRead()
    {
        var written = Opml.Write("Mailbox subscriptions",
        [
            new OpmlEntry("The Verge", "https://theverge.com/rss/index.xml", "Technology", "https://theverge.com"),
            new OpmlEntry("Ars Technica", "https://arstechnica.com/feed", "Technology"),
            new OpmlEntry("Loose", "https://example.com/loose.xml"),
        ], new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero));

        // The round trip is the test that matters: a subscription list that cannot be moved out
        // again is one the reader has to be trusted with.
        var read = Opml.Read(written);

        Assert.Equal(3, read.Count);
        Assert.Equal("Technology", read.Single(e => e.Title == "The Verge").Category);
        Assert.Empty(read.Single(e => e.Title == "Loose").Category);
        Assert.Contains("Mailbox subscriptions", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedHeadingIsWrittenAsNestedOutlines()
    {
        var written = Opml.Write("Mailbox subscriptions",
        [
            new OpmlEntry("LWN", "https://lwn.net/headlines/rss", "Technology/Linux"),
            new OpmlEntry("The Verge", "https://theverge.com/rss/index.xml", "Technology"),
        ], new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero));

        // Depth goes out as depth: a reader that never learnt the slash convention still gets
        // a Linux outline inside a Technology one, not a heading with a slash in its name.
        Assert.DoesNotContain("Technology/Linux", written, StringComparison.Ordinal);
        Assert.Contains("text=\"Linux\"", written, StringComparison.Ordinal);

        // And our own reader joins it back, so the trip stays lossless.
        var read = Opml.Read(written);
        Assert.Equal("Technology/Linux", read.Single(e => e.Title == "LWN").Category);
        Assert.Equal("Technology", read.Single(e => e.Title == "The Verge").Category);
    }

    [Fact]
    public void ADeeplyNestedOutlineKeepsItsFullHeading()
    {
        var opml = """
            <opml version="2.0"><body>
              <outline text="News"><outline text="UK">
                <outline type="rss" text="Example" xmlUrl="https://example.com/uk.xml"/>
              </outline></outline>
            </body></opml>
            """;

        // Joined rather than flattened to the innermost, so a three-deep outline survives a trip
        // through a two-level reader with its structure legible.
        Assert.Equal("News/UK", Assert.Single(Opml.Read(opml)).Category);
    }

    [Fact]
    public void TextThatIsNotAnOutlineIsRefused()
        => Assert.Throws<FormatException>(() => Opml.Read("<rss version=\"2.0\"><channel/></rss>"));

    [Fact]
    public void ASubscriptionListRoundTripsThroughAnOutline()
    {
        var feeds = new FeedSubscriptions(Mailbox.Core.Settings.SettingsStore.Transient());
        feeds.Add("https://a.example/feed", "A", "Tech");
        feeds.Add("https://b.example/feed", "B", "Tech");
        feeds.Add("https://c.example/feed", "C");

        var written = Opml.Write("Mailbox", feeds.All.Select(f =>
            new OpmlEntry(f.Name, f.Url, f.Category, f.SiteUrl)), DateTimeOffset.UnixEpoch);

        var back = new FeedSubscriptions(Mailbox.Core.Settings.SettingsStore.Transient());
        foreach (var entry in Opml.Read(written)) back.Add(entry.Url, entry.Title, entry.Category);

        Assert.Equal(3, back.All.Count);
        Assert.Equal(["Tech"], back.Categories);
        Assert.Equal("B", back.All.Single(f => f.Url == "https://b.example/feed").Name);
    }

    [Fact]
    public void TwoFeedsWithTheSameNameUnderOneHeadingGetFoldersOfTheirOwn()
    {
        // Otherwise both deliver into one folder and read as a single feed whose articles come
        // from two places.
        var feeds = new FeedSubscriptions(Mailbox.Core.Settings.SettingsStore.Transient());
        feeds.Add("https://a.example/feed", "Blog", "Tech");
        feeds.Add("https://b.example/feed", "Blog", "Tech");
        feeds.Add("https://c.example/feed", "Blog", "News");

        Assert.Equal("Blog", feeds.All[0].Name);
        Assert.Equal("Blog (2)", feeds.All[1].Name);
        Assert.Equal("Blog", feeds.All[2].Name);
    }

    [Fact]
    public void ASubscriptionKeepsEverythingItLearnedAcrossARestart()
    {
        var settings = Mailbox.Core.Settings.SettingsStore.Transient();
        var feeds = new FeedSubscriptions(settings);

        feeds.Add("https://example.com/feed", "Example", "Tech");
        feeds.Update("https://example.com/feed", f => f with
        {
            Etag = "\"v1\"",
            LastError = "That host could not be found.",
            Failures = 3,
            ProviderLimitMinutes = 45,
            DownloadFullArticle = true,
            UseProviderLimit = false,
            IconUrl = "https://example.com/icon.png",
        });

        var again = new FeedSubscriptions(settings).All[0];

        Assert.Equal("Tech", again.Category);
        Assert.Equal("\"v1\"", again.Etag);
        Assert.Equal(3, again.Failures);
        Assert.Equal(45, again.ProviderLimitMinutes);
        Assert.True(again.DownloadFullArticle);
        Assert.False(again.UseProviderLimit);
        Assert.True(again.IsFailing);
        Assert.Equal("Tech/Example", again.FolderPath);
    }

    [Fact]
    public void ASubscriptionFileWrittenByTheOlderBuildStillReads()
    {
        // The three keys this used to hold. Everything added since defaults, rather than the
        // reader losing their subscriptions to a format change.
        var settings = Mailbox.Core.Settings.SettingsStore.Transient();
        settings.Set(FeedSubscriptions.Key,
            """[{"url":"https://example.com/feed","name":"Example","checked":1755334800}]""");

        var feed = Assert.Single(new FeedSubscriptions(settings).All);

        Assert.Equal("Example", feed.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1755334800), feed.LastChecked);
        Assert.Empty(feed.Category);
        Assert.True(feed.UseProviderLimit);
    }
}
