using System.Text;
using Mailbox.Core.Feeds;
using Mailbox.Protocols;

namespace Mailbox.Tests;

/// <summary>
/// What the wild actually serves: feeds that are not well-formed XML, feeds in encodings nobody
/// uses any more, dates in the forms the specifications allow and .NET does not take.
/// </summary>
public class FeedRobustnessTests
{
    [Fact]
    public void AnUndeclaredEntityDoesNotCostTheWholeFeed()
    {
        // &nbsp; is an HTML entity. XML has never heard of it, and one of them in a title used to
        // take the entire feed to a FormatException — which is the single most common way a real
        // feed fails to parse.
        var text = """
            <rss version="2.0"><channel><title>Tom &amp; Jerry&nbsp;Weekly</title>
            <item><title>Fish &amp; Chips &mdash; a study</title><guid>1</guid></item>
            </channel></rss>
            """;

        var channel = FeedParser.Parse(text);

        Assert.Equal("Tom & Jerry Weekly", channel.Title);
        Assert.Equal("Fish & Chips — a study", Assert.Single(channel.Items).Title);
    }

    [Fact]
    public void ABareAmpersandIsEscapedRatherThanFatal()
    {
        var text = """
            <rss version="2.0"><channel><title>T</title>
            <item><title>A</title><link>https://example.com/a?x=1&y=2&amp;z=3</link><guid>1</guid></item>
            </channel></rss>
            """;

        Assert.Equal("https://example.com/a?x=1&y=2&z=3", Assert.Single(FeedParser.Parse(text).Items).Link);
    }

    [Fact]
    public void ADoctypeAndAByteOrderMarkAreBothSurvivable()
    {
        var text = "﻿<?xml version=\"1.0\"?>\n"
            + "<!DOCTYPE rss PUBLIC \"-//W3C//DTD\" \"http://www.w3.org/TR/xhtml1.dtd\">\n"
            + "<rss version=\"2.0\"><channel><title>T</title><item><title>A</title><guid>1</guid></item></channel></rss>";

        Assert.Single(FeedParser.Parse(text).Items);
    }

    [Fact]
    public void ACharacterXmlForbidsIsDroppedRatherThanRefused()
    {
        // A control character leaked out of a database column. One of them refuses the document.
        var text = "<rss version=\"2.0\"><channel><title>T\u0008itle</title>"
            + "<item><title>A</title><guid>1</guid></item></channel></rss>";

        Assert.Equal("Title", FeedParser.Parse(text).Title);
    }

    [Fact]
    public void TheRepairLeavesCdataExactlyAsThePublisherWroteIt()
    {
        // The repair must not run inside CDATA: an RSS description is nearly always CDATA holding
        // HTML, where an ampersand is already literal text. Rewriting there would put a visible
        // "&#160;" in front of the reader instead of a space.
        var text = """
            <rss version="2.0"><channel><title>T &nbsp; needs repair</title>
            <item><title>A</title><guid>1</guid>
            <description><![CDATA[<p>Kept&nbsp;whole &amp; entire. 5 &lt; 6</p>]]></description>
            </item></channel></rss>
            """;

        var item = Assert.Single(FeedParser.Parse(text).Items);
        Assert.Equal("<p>Kept&nbsp;whole &amp; entire. 5 &lt; 6</p>", item.Html);
    }

    [Fact]
    public void TextThatIsNotAFeedIsStillRefused()
    {
        // The repair is narrow on purpose. Being generous about XML must not turn "this is not a
        // feed" into "this is an empty feed".
        Assert.Throws<FormatException>(() => FeedParser.Parse("not xml at all"));
        Assert.Throws<FormatException>(() => FeedParser.Parse("<html><body>a page &nbsp; here</body></html>"));
        Assert.Throws<FormatException>(() => FeedParser.Parse(string.Empty));
    }

    [Fact]
    public void ARelativeLinkIsResolvedAgainstWhereTheFeedCameFrom()
    {
        var text = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>T</title>
            <entry><id>1</id><title>A</title><link href="/2026/08/post"/></entry></feed>
            """;

        // Uri.TryCreate reads a leading slash as an absolute *file* path on Unix, so getting this
        // wrong files every article in the feed with a link that goes nowhere — on Linux only.
        Assert.Equal("https://example.org/2026/08/post",
            Assert.Single(FeedParser.Parse(text, "https://example.org/blog/feed.xml").Items).Link);
    }

    [Fact]
    public void XmlBaseIsHonouredWhereAPublisherSetsOne()
    {
        var text = """
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://cdn.example.net/2026/">
            <title>T</title><entry><id>1</id><title>A</title><link href="post.html"/></entry></feed>
            """;

        Assert.Equal("https://cdn.example.net/2026/post.html",
            Assert.Single(FeedParser.Parse(text, "https://example.org/feed.xml").Items).Link);
    }

    [Fact]
    public void AtomsThreeKindsOfContentAreEachReadAsWhatTheySay()
    {
        // xhtml is markup written as markup: taking its text would hand the reading pane the
        // article with every tag stripped out.
        var xhtml = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>T</title><entry><id>1</id><title>A</title>
            <content type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Real <b>markup</b></p></div></content>
            </entry></feed>
            """;
        Assert.Contains("<b>markup</b>", Assert.Single(FeedParser.Parse(xhtml).Items).Html, StringComparison.Ordinal);

        // text is plain text: unescaped, everything after a less-than sign is eaten as a tag.
        var plain = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>T</title><entry><id>1</id><title>A</title>
            <content type="text">a &lt; b, and that matters</content></entry></feed>
            """;
        Assert.Equal("a &lt; b, and that matters", Assert.Single(FeedParser.Parse(plain).Items).Html);
    }

    [Fact]
    public void ATitleThePublisherEncodedTwiceIsShownOnce()
    {
        // Seen on The Verge: the feed carries &amp;#8217; where an apostrophe was meant, so what
        // survives the XML parse is the literal text "&#8217;" in the subject line.
        var text = """
            <rss version="2.0"><channel><title>T</title>
            <item><guid>1</guid><title>OpenAI&amp;#8217;s week</title></item></channel></rss>
            """;

        Assert.Equal("OpenAI’s week", Assert.Single(FeedParser.Parse(text).Items).Title);

        // And a title with an ordinary ampersand keeps it, rather than being decoded twice.
        var ampersand = """
            <rss version="2.0"><channel><title>T</title>
            <item><guid>1</guid><title>Marks &amp; Spencer</title></item></channel></rss>
            """;
        Assert.Equal("Marks & Spencer", Assert.Single(FeedParser.Parse(ampersand).Items).Title);
    }

    [Fact]
    public void AnEntryWithNoIdentityOfItsOwnStillGetsOne()
    {
        // A feed generated from a database query commonly has no guid, no id and no link. Such an
        // entry used to be dropped on the floor, so the feed appeared to deliver nothing at all.
        var text = """
            <rss version="2.0"><channel><title>T</title>
            <item><title>First</title><description>One</description></item>
            <item><title>Second</title><description>Two</description></item>
            </channel></rss>
            """;

        var items = FeedParser.Parse(text).Items;
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.NotEmpty(i.Id));
        Assert.Equal(2, items.Select(i => i.Id).Distinct().Count());

        // And the identity is of the content, so the same feed read twice gives the same answer.
        Assert.Equal(items[0].Id, FeedParser.Parse(text).Items[0].Id);
    }

    [Fact]
    public void EnclosuresMediaPicturesAndCategoriesAreAllRead()
    {
        var text = """
            <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/"><channel><title>T</title>
            <ttl>45</ttl>
            <item><guid>1</guid><title>A</title>
              <enclosure url="https://example.com/ep.mp3" type="audio/mpeg" length="12345"/>
              <media:thumbnail url="https://example.com/thumb.jpg"/>
              <category>Tech</category><category>Linux</category>
            </item></channel></rss>
            """;

        var channel = FeedParser.Parse(text);
        var item = Assert.Single(channel.Items);

        var enclosure = Assert.Single(item.Enclosures);
        Assert.Equal("https://example.com/ep.mp3", enclosure.Url);
        Assert.Equal(12345, enclosure.Length);
        Assert.Equal("https://example.com/thumb.jpg", item.ImageUrl);
        Assert.Equal(["Tech", "Linux"], item.Categories);
        Assert.Equal(TimeSpan.FromMinutes(45), channel.UpdateLimit);
    }

    [Fact]
    public void APictureIsTakenFromTheBodyWhenTheFeedPublishesNoMetadata()
    {
        // The many feeds that publish no media metadata at all and simply open the body with the
        // picture. Without this the article list has no thumbnail for most of the web.
        var text = """
            <rss version="2.0"><channel><title>T</title><item><guid>1</guid><title>A</title>
            <description><![CDATA[<p>Words</p><img src="/img/hero.jpg" alt="x">]]></description>
            </item></channel></rss>
            """;

        Assert.Equal("https://example.com/img/hero.jpg",
            Assert.Single(FeedParser.Parse(text, "https://example.com/feed").Items).ImageUrl);
    }

    [Fact]
    public void TheSyndicationModulesUpdatePeriodIsAnUpdateLimitToo()
    {
        var text = """
            <rss version="2.0" xmlns:sy="http://purl.org/rss/1.0/modules/syndication/">
            <channel><title>T</title><sy:updatePeriod>hourly</sy:updatePeriod><sy:updateFrequency>4</sy:updateFrequency>
            <item><guid>1</guid><title>A</title></item></channel></rss>
            """;

        Assert.Equal(TimeSpan.FromMinutes(15), FeedParser.Parse(text).UpdateLimit);
    }

    [Fact]
    public void JsonFeedReadsAsTheSameThing()
    {
        var text = """
            {
              "version": "https://jsonfeed.org/version/1.1",
              "title": "Example JSON",
              "home_page_url": "https://example.com/",
              "icon": "https://example.com/icon.png",
              "items": [
                {
                  "id": "1",
                  "url": "https://example.com/a",
                  "title": "An entry",
                  "content_html": "<p>Words.</p>",
                  "summary": "A summary",
                  "date_published": "2026-08-16T09:00:00Z",
                  "authors": [{ "name": "A. Person" }],
                  "tags": ["Tech"],
                  "attachments": [{ "url": "https://example.com/ep.mp3", "mime_type": "audio/mpeg", "size_in_bytes": 99 }]
                }
              ]
            }
            """;

        var channel = FeedParser.Parse(text);
        var item = Assert.Single(channel.Items);

        Assert.Equal("Example JSON", channel.Title);
        Assert.Equal("https://example.com/icon.png", channel.IconUrl);
        Assert.Equal("An entry", item.Title);
        Assert.Equal("A. Person", item.Author);
        Assert.Equal("A summary", item.Summary);
        Assert.Equal(["Tech"], item.Categories);
        Assert.Equal(99, Assert.Single(item.Enclosures).Length);
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero), item.Published);
    }

    [Theory]
    [InlineData("Sun, 16 Aug 2026 09:00:00 GMT", 9)]
    [InlineData("Sun, 16 Aug 2026 09:00:00 +0000", 9)]
    [InlineData("Sun, 16 Aug 2026 04:00:00 EST", 9)]
    [InlineData("2026-08-16T09:00:00Z", 9)]
    [InlineData("2026-08-16T09:00:00+00:00", 9)]
    public void TheDateFormatsFeedsActuallyUseAreAllRead(string written, int expectedHourUtc)
    {
        // RSS says RFC 822, which allows an alphabetic zone that .NET will not parse. Dropping
        // the date sorts the article to the bottom of the list for ever.
        var parsed = FeedDates.Parse(written);

        Assert.NotNull(parsed);
        Assert.Equal(expectedHourUtc, parsed!.Value.UtcDateTime.Hour);
    }

    [Fact]
    public void ADateThatMeansNothingIsNullRatherThanNow()
    {
        // "Now" would sort the entry to the top of the list on every single poll.
        Assert.Null(FeedDates.Parse("whenever"));
        Assert.Null(FeedDates.Parse(string.Empty));
    }

    [Fact]
    public void AFeedInWindows1252ArrivesWithItsPunctuationIntact()
    {
        // A great many publishers still serve this, and .NET does not carry the encoding by
        // default — so getting it wrong replaces every apostrophe and dash with a diamond.
        // The test has to register the provider for its own use; the production path registers
        // it for itself, which is the thing being proved.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var latin = Encoding.GetEncoding("windows-1252");
        var bytes = latin.GetBytes("<rss version=\"2.0\"><channel><title>Café — naïve</title>"
            + "<item><guid>1</guid><title>A</title></item></channel></rss>");

        var declared = new System.Net.Http.Headers.MediaTypeHeaderValue("application/rss+xml") { CharSet = "windows-1252" };
        Assert.Equal("Café — naïve", FeedParser.Parse(FeedFetch.Decode(bytes, declared)).Title);

        // And with no charset on the response, the declaration inside the document is believed.
        var withProlog = latin.GetBytes("<?xml version=\"1.0\" encoding=\"windows-1252\"?>"
            + "<rss version=\"2.0\"><channel><title>Café</title><item><guid>1</guid><title>A</title></item></channel></rss>");
        Assert.Equal("Café", FeedParser.Parse(FeedFetch.Decode(withProlog, null)).Title);
    }
}
