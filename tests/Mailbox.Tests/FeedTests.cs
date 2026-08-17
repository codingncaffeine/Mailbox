using Mailbox.Core.Feeds;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Feeds: what the three shapes of them parse to, and what a subscription list keeps.
/// </summary>
public class FeedTests
{
    private const string Rss = """
        <?xml version="1.0"?>
        <rss version="2.0">
          <channel>
            <title>Example Weekly</title>
            <link>https://example.com/</link>
            <item>
              <title>The first thing</title>
              <link>https://example.com/1</link>
              <guid isPermaLink="false">tag:example.com,2026:1</guid>
              <pubDate>Sun, 16 Aug 2026 09:00:00 +0000</pubDate>
              <description>&lt;p&gt;Something happened.&lt;/p&gt;</description>
            </item>
            <item>
              <title>The second thing</title>
              <link>https://example.com/2</link>
              <guid isPermaLink="false">tag:example.com,2026:2</guid>
            </item>
          </channel>
        </rss>
        """;

    private const string Atom = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Example Atom</title>
          <link href="https://example.org/" rel="alternate"/>
          <entry>
            <title>An entry</title>
            <id>urn:uuid:1</id>
            <link href="https://example.org/a" rel="alternate"/>
            <published>2026-08-16T09:00:00Z</published>
            <author><name>A. Person</name></author>
            <content type="html">&lt;p&gt;Words.&lt;/p&gt;</content>
          </entry>
        </feed>
        """;

    [Fact]
    public void AnRssFeedReadsAsItsChannelAndItsItems()
    {
        var channel = FeedParser.Parse(Rss);

        Assert.Equal("Example Weekly", channel.Title);
        Assert.Equal("https://example.com/", channel.Link);
        Assert.Equal(2, channel.Items.Count);

        var first = channel.Items[0];
        Assert.Equal("tag:example.com,2026:1", first.Id);
        Assert.Equal("The first thing", first.Title);
        Assert.Equal("https://example.com/1", first.Link);
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero), first.Published);
        Assert.Contains("Something happened.", first.Html, StringComparison.Ordinal);

        // A date is optional, and an item without one is still an item.
        Assert.Null(channel.Items[1].Published);
    }

    [Fact]
    public void AnAtomFeedReadsTheSameWay()
    {
        var channel = FeedParser.Parse(Atom);
        var entry = Assert.Single(channel.Items);

        Assert.Equal("Example Atom", channel.Title);
        Assert.Equal("https://example.org/", channel.Link);
        Assert.Equal("urn:uuid:1", entry.Id);

        // Atom hangs its address off an attribute and its author off a name element, which is the
        // whole of what makes it a different shape rather than a different idea.
        Assert.Equal("https://example.org/a", entry.Link);
        Assert.Equal("A. Person", entry.Author);
        Assert.Contains("Words.", entry.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void AFeedInANamespaceNobodyMentionedStillReads()
    {
        // Feeds in the wild put their elements in namespaces the specification does not name; a
        // reader that insists on the right one reads nothing at all.
        var text = Rss.Replace("<rss version=\"2.0\">", "<rss version=\"2.0\" xmlns=\"http://backend.userland.com/rss2\">", StringComparison.Ordinal);

        Assert.Equal(2, FeedParser.Parse(text).Items.Count);
    }

    [Fact]
    public void TextThatIsNotAFeedIsRefusedRatherThanGuessedAt()
    {
        Assert.Throws<FormatException>(() => FeedParser.Parse("not xml at all"));
        Assert.Throws<FormatException>(() => FeedParser.Parse("<html><body>a page</body></html>"));
    }

    [Fact]
    public void ASubscriptionIsKeptOncePerAddressAndSurvivesARestart()
    {
        var settings = SettingsStore.Transient();
        var feeds = new FeedSubscriptions(settings);

        feeds.Add("https://example.com/feed.xml", "Example");
        feeds.Add("https://example.com/feed.xml", "Example again");

        Assert.Single(feeds.All);
        Assert.Equal("Example", feeds.All[0].Name);

        feeds.Checked("https://example.com/feed.xml", new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero));
        feeds.Rename("https://example.com/feed.xml", "Weekly");

        var again = new FeedSubscriptions(settings);
        Assert.Equal("Weekly", again.All[0].Name);
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero), again.All[0].LastChecked);

        Assert.True(again.Remove("https://example.com/feed.xml"));
        Assert.Empty(new FeedSubscriptions(settings).All);
    }

    [Fact]
    public void AnItemBecomesAMessageInItsOwnFolder_Once()
    {
        using var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var open = new OpenAccount(account, store, mail);

        var feed = new FeedSubscription("https://example.com/feed.xml", "Example Weekly");
        var channel = FeedParser.Parse(Rss);

        var delivered = FeedReceiver.Deliver(open, feed, channel, DateTimeOffset.UtcNow);
        Assert.Equal(2, delivered);

        // Under the reference's own heading, in a folder named for the feed.
        var folders = mail.Folders(account.Id);
        var root = Assert.Single(folders, f => f.Name == FeedReceiver.RootFolder);
        var folder = Assert.Single(folders, f => f.ParentId == root.Id && f.Name == "Example Weekly");

        var messages = mail.Messages(folder.Id);
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Subject == "The first thing");
        Assert.All(messages, m => Assert.False(m.IsRead));

        // The item's own id is filed as the server id, which is what stops a second download
        // delivering it twice — the same job a POP3 UIDL does.
        Assert.Equal(0, FeedReceiver.Deliver(open, feed, channel, DateTimeOffset.UtcNow));
        Assert.Equal(2, mail.Messages(folder.Id).Count);
    }

    [Fact]
    public void AnItemCarriesItsLinkSoTheOriginalIsAlwaysReachable()
    {
        using var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var open = new OpenAccount(account, store, mail);

        FeedReceiver.Deliver(open, new FeedSubscription("https://example.org/atom", "Example Atom"), FeedParser.Parse(Atom), DateTimeOffset.UtcNow);

        var folder = mail.Folders(account.Id).Single(f => f.Name == "Example Atom");
        var message = Assert.Single(mail.Messages(folder.Id));
        var raw = System.Text.Encoding.UTF8.GetString(mail.LoadRaw(message.Id)!);

        Assert.Contains("https://example.org/a", raw, StringComparison.Ordinal);
        Assert.Contains("A. Person", raw, StringComparison.Ordinal);
    }
}
