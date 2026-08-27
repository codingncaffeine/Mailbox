using Mailbox.Core.Feeds;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Boards: named collections an article is saved into, and the one thing a folder could not do —
/// any address at all going onto one.
/// </summary>
/// <remarks>
/// What is worth proving here is not that a row can be inserted. It is the four things a reader
/// would notice if they were wrong: that saving does not move the article out of its feed, that
/// an article can be on more than one board at once, that removing a board keeps what was on it,
/// and that a page which will not load is still saved rather than silently dropped.
/// </remarks>
public class BoardTests
{
    private static (MailRepository Mail, MailStore Store, long Account) Store()
    {
        var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);

        return (mail, store, account.Id);
    }

    private static long Article(MailRepository mail, long folderId, string subject, DateTimeOffset? when = null)
    {
        var stamp = when ?? DateTimeOffset.UtcNow;
        var summary = new MessageSummary(
            0, folderId, Guid.NewGuid().ToString("n"), null, "A. Person", "person@example.com",
            subject, subject, stamp, stamp, subject.Length, false, false, false);

        return mail.AddMessage(folderId, summary)!.Value;
    }

    [Fact]
    public void SavingKeepsTheArticleWhereItIs()
    {
        // The whole point of a board being a join rather than a folder: an article saved onto one
        // is still in the feed it arrived in, still counted there, and still found by a search of
        // that folder.
        var (mail, store, account) = Store();
        using var _ = store;

        var feed = mail.AddFolder(account, "The Verge").Id;
        var article = Article(mail, feed, "A flood of new phones");
        var board = mail.AddBoard("Phones", DateTimeOffset.UtcNow);

        mail.SaveToBoard([article], board.Id, DateTimeOffset.UtcNow);

        Assert.Single(mail.Messages(feed));
        Assert.Equal(feed, mail.GetMessage(article)!.FolderId);
        Assert.Single(mail.BoardMessages(board.Id));
    }

    [Fact]
    public void AnArticleCanBeOnSeveralBoardsAtOnce()
    {
        var (mail, store, account) = Store();
        using var _ = store;

        var feed = mail.AddFolder(account, "LWN").Id;
        var article = Article(mail, feed, "Using steal time");

        var kernel = mail.AddBoard("Kernel", DateTimeOffset.UtcNow);
        var reading = mail.AddBoard("To write about", DateTimeOffset.UtcNow);

        mail.SaveToBoard([article], kernel.Id, DateTimeOffset.UtcNow);
        mail.SaveToBoard([article], reading.Id, DateTimeOffset.UtcNow);

        var on = mail.BoardsFor([article])[article];
        Assert.Equal(2, on.Count);
        Assert.Contains(on, b => b.Name == "Kernel");
        Assert.Contains(on, b => b.Name == "To write about");
    }

    [Fact]
    public void SavingTheSameArticleTwiceIsNotTwoRows()
    {
        var (mail, store, account) = Store();
        using var _ = store;

        var feed = mail.AddFolder(account, "LWN").Id;
        var article = Article(mail, feed, "Using steal time");
        var board = mail.AddBoard("Kernel", DateTimeOffset.UtcNow);

        Assert.Equal(1, mail.SaveToBoard([article], board.Id, DateTimeOffset.UtcNow));
        Assert.Equal(0, mail.SaveToBoard([article], board.Id, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Single(mail.BoardMessages(board.Id));
        Assert.Equal(1, mail.Boards().Single().Count);
    }

    [Fact]
    public void ABoardIsReadNewestSavedFirst()
    {
        // Not newest published. A reader who saves a piece from last year expects to find it at
        // the top of the board they have just put it on.
        var (mail, store, account) = Store();
        using var _ = store;

        var feed = mail.AddFolder(account, "Archive").Id;
        var now = DateTimeOffset.UtcNow;

        var old = Article(mail, feed, "Written in 2019", now.AddYears(-6));
        var fresh = Article(mail, feed, "Written this morning", now);

        var board = mail.AddBoard("Keep", now);
        mail.SaveToBoard([fresh], board.Id, now.AddMinutes(-10));
        mail.SaveToBoard([old], board.Id, now);

        Assert.Equal(["Written in 2019", "Written this morning"],
            mail.BoardMessages(board.Id).Select(m => m.Subject));
    }

    [Fact]
    public void RemovingABoardKeepsWhatWasOnIt()
    {
        // A board is a collection. Tidying one away is not a licence to delete somebody's
        // reading, and the cascade must reach the join and stop there.
        var (mail, store, account) = Store();
        using var _ = store;

        var feed = mail.AddFolder(account, "The Verge").Id;
        var article = Article(mail, feed, "A flood of new phones");
        var board = mail.AddBoard("Phones", DateTimeOffset.UtcNow);
        mail.SaveToBoard([article], board.Id, DateTimeOffset.UtcNow);

        mail.DeleteBoard(board.Id);

        Assert.Empty(mail.Boards());
        Assert.NotNull(mail.GetMessage(article));
        Assert.Single(mail.Messages(feed));
    }

    [Fact]
    public void DeletingAnArticleTakesItsMembershipWithIt()
    {
        // The other direction of the cascade: a board must never list a row that is gone, which
        // would draw a headline that cannot be opened.
        var (mail, store, account) = Store();
        using var _ = store;

        var feed = mail.AddFolder(account, "The Verge").Id;
        var article = Article(mail, feed, "A flood of new phones");
        var board = mail.AddBoard("Phones", DateTimeOffset.UtcNow);
        mail.SaveToBoard([article], board.Id, DateTimeOffset.UtcNow);

        mail.DeleteMessage(article);

        Assert.Empty(mail.BoardMessages(board.Id));
        Assert.Equal(0, mail.Boards().Single().Count);
    }

    [Fact]
    public void TakingAnArticleOffABoardLeavesTheArticle()
    {
        var (mail, store, account) = Store();
        using var _ = store;

        var feed = mail.AddFolder(account, "The Verge").Id;
        var article = Article(mail, feed, "A flood of new phones");
        var board = mail.AddBoard("Phones", DateTimeOffset.UtcNow);
        mail.SaveToBoard([article], board.Id, DateTimeOffset.UtcNow);

        Assert.Equal(1, mail.RemoveFromBoard([article], board.Id));

        Assert.Empty(mail.BoardMessages(board.Id));
        Assert.NotNull(mail.GetMessage(article));
        Assert.False(mail.IsOnAnyBoard(article));
    }

    [Fact]
    public void TwoBoardsCannotShareAName()
    {
        var (mail, store, _) = Store();
        using var _s = store;

        var first = mail.AddBoard("Rust", DateTimeOffset.UtcNow);

        // Asking again for a board that exists hands back the one that exists rather than
        // throwing on the index: every caller wants "the board called this".
        Assert.Equal(first.Id, mail.AddBoard("rust", DateTimeOffset.UtcNow).Id);
        Assert.Single(mail.Boards());

        var second = mail.AddBoard("Kernel", DateTimeOffset.UtcNow);
        Assert.False(mail.RenameBoard(second.Id, "RUST"));
        Assert.True(mail.RenameBoard(second.Id, "Kernels"));
        Assert.Equal("Kernels", mail.Boards().Single(b => b.Id == second.Id).Name);
    }

    // ---- Saving an address that came from nowhere ------------------------------------------------

    [Fact]
    public void APageIsReadForWhatItSaysAboutItself()
    {
        var card = PageCards.Read(
            """
            <html><head>
              <title>Something else entirely</title>
              <meta property="og:title" content="A flood of new phones">
              <meta property="og:description" content="Every one of them is a rectangle.">
              <meta property="og:image" content="/img/card.png">
              <meta property="og:site_name" content="The Verge">
            </head><body>…</body></html>
            """,
            "https://example.com/phones");

        Assert.Equal("A flood of new phones", card.Title);
        Assert.Equal("Every one of them is a rectangle.", card.Summary);
        Assert.Equal("The Verge", card.SiteName);

        // Resolved against the page, because a great many sites write a bare path.
        Assert.Equal("https://example.com/img/card.png", card.ImageUrl);
    }

    [Fact]
    public void APageWithNoOpenGraphStillHasATitle()
    {
        var card = PageCards.Read(
            "<html><head><title>  A plain\n  old page  </title>"
            + "<meta name=\"description\" content=\"Nothing fancy.\"></head></html>",
            "https://www.example.com/page");

        Assert.Equal("A plain old page", card.Title);
        Assert.Equal("Nothing fancy.", card.Summary);

        // No og:site_name, so the host stands in for the publisher — without its www.
        Assert.Equal("example.com", card.Publisher);
    }

    [Fact]
    public void APicturePointingSomewhereOddIsNotKept()
    {
        // This column is handed to a fetcher and to an image control. A scheme somebody chose is
        // not something either should ever be given.
        var card = PageCards.Read(
            "<html><head><meta property=\"og:image\" content=\"javascript:alert(1)\"></head></html>",
            "https://example.com/page");

        Assert.Equal(string.Empty, card.ImageUrl);
    }

    [Fact]
    public void APageThatSaysNothingIsStillAHeadline()
    {
        var card = PageCards.Read("<html><body>no head at all</body></html>", "https://example.com/a/page");

        Assert.Equal(string.Empty, card.Title);
        Assert.Equal("example.com/a/page", card.Headline);
    }

    [Fact]
    public async Task AnAddressIsSavedEvenWhenThePageCannotBeRead()
    {
        // The network is not allowed to be the difference between saved and not saved. A bookmark
        // that fails because a site is down is how somebody stops trusting a keep pile.
        using var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var record = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(record.Id);
        var account = new OpenAccount(record, store, mail);

        var saved = await SavedLinks.SaveAsync(
            account, "example.com/an-article", fetch: null, DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.True(saved.Ok);
        Assert.Equal("https://example.com/an-article", saved.Card.Url);

        var folders = mail.Folders(record.Id);
        var root = folders.Single(f => f.ParentId is null && f.Name == FeedReceiver.RootFolder);
        var kept = folders.Single(f => f.ParentId == root.Id && f.Name == SavedLinks.SavedFolder);

        var article = Assert.Single(mail.Messages(kept.Id));
        Assert.Equal("example.com/an-article", article.Subject);

        // The two columns the article list draws a saved link from, so it sits in the list beside
        // the things a feed delivered rather than as a row with holes in it.
        Assert.Equal("https://example.com/an-article", article.FeedLink);
    }

    [Fact]
    public async Task SavingTheSameAddressTwiceIsOneArticleOnTwoBoards()
    {
        using var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var record = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(record.Id);
        var account = new OpenAccount(record, store, mail);
        var now = DateTimeOffset.UtcNow;

        var first = await SavedLinks.SaveAsync(account, "https://example.com/page", null, now,
            TestContext.Current.CancellationToken);
        var again = await SavedLinks.SaveAsync(account, "https://example.com/page", null, now,
            TestContext.Current.CancellationToken);

        Assert.True(again.AlreadyHere);
        Assert.Equal(first.MessageId, again.MessageId);

        var one = mail.AddBoard("Reading", now);
        var two = mail.AddBoard("Writing", now);
        mail.SaveToBoard([first.MessageId], one.Id, now);
        mail.SaveToBoard([again.MessageId], two.Id, now);

        Assert.Single(mail.BoardMessages(one.Id));
        Assert.Single(mail.BoardMessages(two.Id));
        Assert.Equal(2, mail.BoardsFor([first.MessageId])[first.MessageId].Count);
    }

    [Theory]
    [InlineData("example.com/page", "https://example.com/page")]
    [InlineData("  https://example.com/page  ", "https://example.com/page")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("example.com:8080/page", "https://example.com:8080/page")]
    public void AnAddressIsTakenAsPeopleActuallyPasteIt(string typed, string expected)
        => Assert.Equal(expected, SavedLinks.Normalize(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:someone@example.com")]
    public void AnythingThatIsNotAWebAddressIsRefused(string typed)
        => Assert.Null(SavedLinks.Normalize(typed));
}
