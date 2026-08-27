using System.Net;
using Mailbox.Core.Feeds;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// What a poll costs and what it does the second time — the half of a feed reader that cannot be
/// exercised against a real publisher, because it is about being asked twice.
/// </summary>
public class FeedPollTests
{
    private const string Url = "https://example.com/feed.xml";

    private static string Feed(params string[] items) => $"""
        <rss version="2.0"><channel><title>Example Weekly</title><link>https://example.com/</link>
        {string.Join('\n', items)}
        </channel></rss>
        """;

    private static string Item(string id, string title, string body = "Something happened.") => $"""
        <item><guid isPermaLink="false">{id}</guid><title>{title}</title>
          <link>https://example.com/{id}</link>
          <description>{body}</description></item>
        """;

    private static (OpenAccount Account, MailStore Store, MailRepository Mail) Account()
    {
        var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        return (new OpenAccount(account, store, mail), store, mail);
    }

    /// <summary>A publisher's page shaped like the ones a teaser links to.</summary>
    private static string ArticlePage(string headline) => $"""
        <!doctype html><html><head><title>{headline}</title>
          <meta property="og:image" content="https://example.com/img/{headline.GetHashCode(StringComparison.Ordinal)}.jpg">
        </head><body>
          <nav><a href="/">Home</a><a href="/news">News</a></nav>
          <div class="content">
            <p>The article opens with a paragraph long enough that nobody could mistake it for a
            caption, and goes on to say something worth having come here for.</p>
            <p>It continues, because an article is more than one paragraph, and this one has
            several so that the densest run on the page is unmistakably this one.</p>
            <p>And it finishes, having said its piece at a length that makes it plainly the
            thing a reader came to the page in order to read.</p>
          </div>
          <footer><p>Copyright somebody.</p></footer>
        </body></html>
        """;

    [Fact]
    public async Task AFeedThatSendsATeaserGetsTheArticleReadFromItsOwnPage()
    {
        // The single commonest complaint about reading by RSS, and the reason a reader with one
        // such subscription sees a list of headlines they cannot read.
        var server = new FakeFeedServer()
            .Serve(Url, Feed(Item("1", "First", "A sentence and a link.")))
            .Serve("https://example.com/1", ArticlePage("First"));

        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, mail) = Account();
        using var _ = store;
        using var receiver = new FeedReceiver(feeds, server);

        Assert.Equal(1, (await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Delivered);

        var folder = mail.Folders(account.Account.Id).Single(f => f.Name == "Example");
        var article = Assert.Single(mail.Messages(folder.Id));

        // The body is the article rather than the sentence, and the picture the feed never sent
        // came off the same page — one request, both answers.
        var raw = mail.LoadRaw(article.Id)!;
        using var buffer = new MemoryStream(raw);
        var message = MimeKit.MimeMessage.Load(buffer, TestContext.Current.CancellationToken);

        Assert.Contains("nobody could mistake it for a caption", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Copyright somebody", message.TextBody, StringComparison.Ordinal);
        Assert.StartsWith("https://example.com/img/", article.FeedImage, StringComparison.Ordinal);

        // The snippet stays the publisher's own sentence, and carries no address: it is what the
        // article list draws under the headline.
        Assert.Equal("A sentence and a link.", article.Preview);
    }

    [Fact]
    public async Task AFeedThatSendsTheWholeArticleIsNotFetchedTwice()
    {
        // The politeness half. A feed that publishes in full has nothing to add, and asking its
        // publisher for every article anyway is how a reader gets themselves blocked.
        var whole = string.Concat(Enumerable.Repeat(
            "<p>A paragraph of an article that the feed itself carried in full. </p>", 30));

        var server = new FakeFeedServer()
            .Serve(Url, Feed(Item("1", "First", System.Net.WebUtility.HtmlEncode(whole))))
            .Serve("https://example.com/1", ArticlePage("First"));

        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, _) = Account();
        using var _s = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(1, server.RequestsFor(Url));
        Assert.Equal(0, server.RequestsFor("https://example.com/1"));
    }

    [Fact]
    public async Task AReaderWhoTurnsItOffIsNotAskingThePublisherForAnything()
    {
        var server = new FakeFeedServer()
            .Serve(Url, Feed(Item("1", "First", "A sentence and a link.")))
            .Serve("https://example.com/1", ArticlePage("First"));

        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");
        feeds.Update(Url, f => f with { ReadFullArticle = false });

        var (account, store, _) = Account();
        using var _s = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(0, server.RequestsFor("https://example.com/1"));
    }

    [Fact]
    public async Task AnArticleAlreadyFiledAsATeaserIsFilledInWhenItIsOpened()
    {
        // The retroactive half: everything filed before this existed, or before the reader turned
        // it on, is still a teaser, and a reader opening one means "show me more of this".
        var server = new FakeFeedServer()
            .Serve(Url, Feed(Item("1", "First", "A sentence and a link.")))
            .Serve("https://example.com/1", ArticlePage("First"));

        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");
        feeds.Update(Url, f => f with { ReadFullArticle = false });

        var (account, store, mail) = Account();
        using var _s = store;

        using (var receiver = new FeedReceiver(feeds, server))
        {
            await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        }

        var folder = mail.Folders(account.Account.Id).Single(f => f.Name == "Example");
        var teaser = Assert.Single(mail.Messages(folder.Id));
        Assert.True(ArticleFill.LooksLikeTeaser(teaser));

        using var fetch = new FeedFetch(server);
        var written = await ArticleFill.FillAsync(account, teaser.Id, fetch, TestContext.Current.CancellationToken);
        Assert.True(written > 0, "the page behind the teaser was not read");

        // Rewritten in place: the same row, so a flag, a category or a board put on it survives.
        var filled = Assert.Single(mail.Messages(folder.Id));
        Assert.Equal(teaser.Id, filled.Id);
        Assert.True(filled.SizeBytes > teaser.SizeBytes);
        Assert.StartsWith("https://example.com/img/", filled.FeedImage, StringComparison.Ordinal);

        // And only once: a second opening is not a second request.
        var asked = server.RequestsFor("https://example.com/1");
        Assert.Equal(0, await ArticleFill.FillAsync(account, teaser.Id, fetch, TestContext.Current.CancellationToken));
        Assert.Equal(asked, server.RequestsFor("https://example.com/1"));
    }

    [Fact]
    public async Task AnUnchangedFeedCostsOneConditionalRequestAndNoBody()
    {
        // The difference between a subscription costing a megabyte an hour and costing nothing.
        var server = new FakeFeedServer().Serve(Url, Feed(Item("1", "First")), etag: "v1");
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, mail) = Account();
        using var _ = store;
        using var receiver = new FeedReceiver(feeds, server);

        var first = await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Delivered);
        Assert.Equal("v1", feeds.All[0].Etag.Trim('"'));

        var second = await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, force: true);
        Assert.Equal(0, second.Delivered);
        Assert.Equal(1, second.Unchanged);

        // The second request carried the tag the first was given, which is what earned the 304.
        Assert.Equal(2, server.RequestsFor(Url));
        Assert.NotEmpty(server.RequestLog(Url)[1].Headers.IfNoneMatch);
    }

    [Fact]
    public async Task APublisherThatRevisesAnArticleUpdatesItRatherThanDuplicatingIt()
    {
        var server = new FakeFeedServer().Serve(Url, Feed(Item("1", "First", "The original wording.")));
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, mail) = Account();
        using var _ = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var folder = mail.Folders(account.Account.Id).Single(f => f.Name == "Example");
        var before = Assert.Single(mail.Messages(folder.Id));

        // The reader flags it and reads it. Both must survive the publisher's correction.
        mail.SetFlagged(before.Id, true);
        mail.SetRead(before.Id, true);

        server.Serve(Url, Feed(Item("1", "First, corrected", "The corrected wording.")));
        var second = await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, force: true);

        Assert.Equal(0, second.Delivered);
        Assert.Equal(1, second.Revised);

        var after = Assert.Single(mail.Messages(folder.Id));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("First, corrected", after.Subject);
        Assert.True(after.IsFlagged);
        Assert.True(after.IsRead);
        Assert.Contains("corrected wording", System.Text.Encoding.UTF8.GetString(mail.LoadRaw(after.Id)!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArticleThatHasNotChangedIsNotRewritten()
    {
        // The counterpart of the test above, and the one that matters more: a fingerprint that
        // did not match would rewrite every article in every folder on every poll.
        var server = new FakeFeedServer().Serve(Url, Feed(Item("1", "First"), Item("2", "Second")));
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, mail) = Account();
        using var _ = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var third = await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, force: true);

        Assert.Equal(0, third.Delivered);
        Assert.Equal(0, third.Revised);
    }

    [Fact]
    public async Task AFeedMovedForGoodIsFollowedInTheSubscription()
    {
        const string moved = "https://example.com/new/feed.xml";
        var server = new FakeFeedServer()
            .Redirect(Url, moved)
            .Serve(moved, Feed(Item("1", "First")));

        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, _) = Account();
        using var __ = store;
        using var receiver = new FeedReceiver(feeds, server);

        Assert.Equal(1, (await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Delivered);

        // Not following it in the subscription means re-following it on every poll for the rest
        // of the feed's life, which is what publishers move a feed to stop.
        Assert.Equal(moved, feeds.All[0].Url);
    }

    [Fact]
    public async Task ATemporaryRedirectIsFollowedButNotRecorded()
    {
        const string elsewhere = "https://cdn.example.com/feed.xml";
        var server = new FakeFeedServer()
            .Redirect(Url, elsewhere, HttpStatusCode.Found)
            .Serve(elsewhere, Feed(Item("1", "First")));

        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, _) = Account();
        using var __ = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // A 302 is a detour for one request. Rewriting on one would move a feed every time a
        // publisher failed over to a spare.
        Assert.Equal(Url, feeds.All[0].Url);
    }

    [Fact]
    public async Task AFeedThatKeepsFailingIsAskedForLessAndLessOften()
    {
        var server = new FakeFeedServer().Refuse(Url, HttpStatusCode.InternalServerError);
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, _) = Account();
        using var __ = store;
        using var receiver = new FeedReceiver(feeds, server);

        var now = DateTimeOffset.UtcNow;
        var first = await receiver.PollAsync(account, now, TestContext.Current.CancellationToken);

        Assert.Single(first.Failed);
        Assert.Contains("500", feeds.All[0].LastError, StringComparison.Ordinal);
        Assert.Equal(1, feeds.All[0].Failures);

        var firstDue = feeds.All[0].NextDueUtc;
        Assert.NotNull(firstDue);
        Assert.True(firstDue > now);

        // While it is not due, it is not asked for at all.
        await receiver.PollAsync(account, now.AddMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal(1, server.RequestsFor(Url));

        // And each failure pushes it further out.
        await receiver.PollAsync(account, firstDue!.Value, TestContext.Current.CancellationToken);
        Assert.Equal(2, feeds.All[0].Failures);
        Assert.True(feeds.All[0].NextDueUtc - firstDue > firstDue - now);
    }

    [Fact]
    public async Task AFeedThatRecoversIsForgivenAtOnce()
    {
        var server = new FakeFeedServer().Refuse(Url, HttpStatusCode.ServiceUnavailable);
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, _) = Account();
        using var __ = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.True(feeds.All[0].IsFailing);

        server.Serve(Url, Feed(Item("1", "First")));
        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, force: true);

        Assert.False(feeds.All[0].IsFailing);
        Assert.Equal(0, feeds.All[0].Failures);
        Assert.Null(feeds.All[0].NextDueUtc);
    }

    [Fact]
    public async Task APublisherAskingToBeLeftAloneIsLeftAlone()
    {
        var server = new FakeFeedServer().Refuse(Url, HttpStatusCode.TooManyRequests, TimeSpan.FromHours(3));
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, _) = Account();
        using var __ = store;
        using var receiver = new FeedReceiver(feeds, server);

        var now = DateTimeOffset.UtcNow;
        await receiver.PollAsync(account, now, TestContext.Current.CancellationToken);

        // Three hours, not the fifteen minutes our own arithmetic would have chosen: the server
        // knows why it is refusing and we are guessing.
        Assert.True(feeds.All[0].NextDueUtc >= now.AddHours(2.9));
    }

    [Fact]
    public async Task ThePublishersOwnUpdateLimitIsHonouredAndCanBeTurnedOff()
    {
        var withTtl = Feed(Item("1", "First")).Replace("<link>https://example.com/</link>",
            "<link>https://example.com/</link><ttl>90</ttl>", StringComparison.Ordinal);

        var server = new FakeFeedServer().Serve(Url, withTtl);
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, _) = Account();
        using var __ = store;
        using var receiver = new FeedReceiver(feeds, server);

        var now = DateTimeOffset.UtcNow;
        await receiver.PollAsync(account, now, TestContext.Current.CancellationToken);

        Assert.Equal(90, feeds.All[0].ProviderLimitMinutes);
        Assert.Equal(now.AddMinutes(90), feeds.All[0].NextDueUtc);

        // The reference gives the reader a tick to ignore it, and so does this.
        feeds.Update(Url, f => f with { UseProviderLimit = false, NextDueUtc = null });
        await receiver.PollAsync(account, now, TestContext.Current.CancellationToken);
        Assert.Null(feeds.All[0].NextDueUtc);
    }

    [Fact]
    public async Task AFeedFiledUnderAHeadingDeliversIntoAFolderInsideIt()
    {
        // The unread count against "Technology" is the thing a reader with fifty subscriptions
        // actually wants, and the folder pane already totals a subtree.
        var server = new FakeFeedServer().Serve(Url, Feed(Item("1", "First")));
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example Weekly", "Technology");

        var (account, store, mail) = Account();
        using var _ = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var folders = mail.Folders(account.Account.Id);
        var root = Assert.Single(folders, f => f.Name == FeedReceiver.RootFolder);
        var heading = Assert.Single(folders, f => f.ParentId == root.Id && f.Name == "Technology");
        var own = Assert.Single(folders, f => f.ParentId == heading.Id && f.Name == "Example Weekly");

        Assert.Single(mail.Messages(own.Id));
    }

    [Fact]
    public async Task AnArticlesPictureAndAddressAreColumnsTheListCanDraw()
    {
        var withImage = Feed($"""
            <item><guid>1</guid><title>First</title><link>https://example.com/1</link>
            <description><![CDATA[<p>Words</p><img src="https://example.com/hero.jpg">]]></description></item>
            """);

        var server = new FakeFeedServer().Serve(Url, withImage);
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, mail) = Account();
        using var _ = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var folder = mail.Folders(account.Account.Id).Single(f => f.Name == "Example");
        var message = Assert.Single(mail.Messages(folder.Id));

        // Off the row, not out of the MIME: the list draws a thumbnail per visible row and
        // cannot parse a message to lay itself out.
        Assert.Equal("https://example.com/hero.jpg", message.FeedImage);
        Assert.Equal("https://example.com/1", message.FeedLink);
        Assert.True(message.IsFeedItem);
    }

    [Fact]
    public async Task TheFilesAnEntryCarriesAreFetchedOnlyWhenAskedFor()
    {
        const string audio = "https://example.com/ep.mp3";
        var podcast = Feed($"""
            <item><guid>1</guid><title>Episode</title><link>https://example.com/1</link>
            <enclosure url="{audio}" type="audio/mpeg" length="9"/></item>
            """);

        var server = new FakeFeedServer().Serve(Url, podcast).Serve(audio, "MP3BYTES", "audio/mpeg");
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, mail) = Account();
        using var _ = store;
        using var receiver = new FeedReceiver(feeds, server);

        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(0, server.RequestsFor(audio));

        // With the reference's own option on, it arrives as an attachment.
        feeds.Update(Url, f => f with { DownloadEnclosures = true });
        server.Serve(Url, podcast.Replace("Episode", "Episode, corrected", StringComparison.Ordinal));
        await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, force: true);

        Assert.Equal(1, server.RequestsFor(audio));

        var folder = mail.Folders(account.Account.Id).Single(f => f.Name == "Example");
        var message = Assert.Single(mail.Messages(folder.Id));
        Assert.True(message.HasAttachment);

        using var raw = new MemoryStream(mail.LoadRaw(message.Id)!);
        var mime = MimeKit.MimeMessage.Load(raw, TestContext.Current.CancellationToken);
        Assert.Contains(mime.Attachments, a => a.ContentDisposition?.FileName == "ep.mp3");
    }

    [Fact]
    public async Task ADeadHostIsRecordedAgainstTheFeedRatherThanLostToTheLog()
    {
        // A reader whose feed stopped working needs to be told which one and why; the RSS Feeds
        // tab shows exactly this text.
        var server = new FakeFeedServer();
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Url, "Example");

        var (account, store, _) = Account();
        using var __ = store;
        using var receiver = new FeedReceiver(feeds, server);

        var report = await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Single(report.Failed);
        Assert.True(feeds.All[0].IsFailing);
        Assert.Contains("404", feeds.All[0].LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyFeedsArePolledTogetherRatherThanOneAfterAnother()
    {
        var server = new FakeFeedServer();
        var feeds = new FeedSubscriptions(SettingsStore.Transient());

        for (var n = 0; n < 20; n++)
        {
            var url = $"https://example.com/{n}/feed.xml";
            server.Serve(url, Feed(Item($"{n}", $"Item {n}")));
            feeds.Add(url, $"Feed {n}");
        }

        var (account, store, mail) = Account();
        using var _ = store;
        using var receiver = new FeedReceiver(feeds, server);

        var report = await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(20, report.Delivered);
        Assert.Equal(20, report.Polled);

        // Every one of them filed into its own folder, from work that ran on six threads at once.
        var root = mail.Folders(account.Account.Id).Single(f => f.Name == FeedReceiver.RootFolder);
        Assert.Equal(20, mail.Folders(account.Account.Id).Count(f => f.ParentId == root.Id));
    }
}
