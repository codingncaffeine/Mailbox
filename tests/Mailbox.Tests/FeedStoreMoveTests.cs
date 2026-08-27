using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Moving somebody's feeds out of a mail account and into the feed reader's own store.
/// </summary>
/// <remarks>
/// This runs once, on a reader's only copy of things, so what is tested is not that it works but
/// that it cannot lose anything: the articles arrive with the state that was on them, the boards
/// arrive with what was on them and when, nothing is deleted until everything has landed, and a
/// second run is a no-op rather than a second copy.
/// </remarks>
public class FeedStoreMoveTests
{
    private const string Root = "RSS Feeds";

    private static (OpenAccount Account, MailStore Store) Mail(string address = "you@example.com")
    {
        var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var record = mail.AddAccount(address, "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(record.Id);

        return (new OpenAccount(record, store, mail), store);
    }

    private static (OpenAccount Account, MailStore Store) Feeds()
    {
        var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var record = mail.AddAccount(FeedStores.Address, FeedStores.DisplayName, MailProtocol.Pop3);

        return (new OpenAccount(record, store, mail), store);
    }

    /// <summary>An account with a feeds tree in it: two feeds under a heading, plus a loose one.</summary>
    private static (long Verge, long Lwn, long Loose) Seed(OpenAccount account)
    {
        var mail = account.Mail;
        var id = account.Account.Id;

        var root = mail.AddFolder(id, Root);
        var heading = mail.AddFolder(id, "Technology", parentId: root.Id);
        var verge = mail.AddFolder(id, "The Verge", parentId: heading.Id);
        var lwn = mail.AddFolder(id, "LWN.net", parentId: heading.Id);
        var loose = mail.AddFolder(id, "xkcd", parentId: root.Id);

        return (verge.Id, lwn.Id, loose.Id);
    }

    private static long Article(MailRepository mail, long folderId, string subject,
        bool read = false, bool flagged = false)
    {
        var when = DateTimeOffset.UtcNow.AddMinutes(-subject.Length);
        var summary = new MessageSummary(
            0, folderId, $"uid-{subject}", null, "A. Person", "person@example.com",
            subject, subject, when, when, subject.Length, read, flagged, false)
        {
            FeedLink = $"https://example.com/{subject}",
        };

        return mail.AddMessage(folderId, summary, System.Text.Encoding.UTF8.GetBytes($"Subject: {subject}\r\n\r\nBody."))!.Value;
    }

    [Fact]
    public void TheWholeTreeArrivesAndTheOldOneGoes()
    {
        var (from, fromStore) = Mail();
        var (feeds, feedStore) = Feeds();
        using var _a = fromStore;
        using var _b = feedStore;

        var (verge, lwn, loose) = Seed(from);
        Article(from.Mail, verge, "phones");
        Article(from.Mail, verge, "watches");
        Article(from.Mail, lwn, "kernel");
        Article(from.Mail, loose, "comic");

        var moved = FeedStoreMove.Move(feeds, from, Root);

        Assert.True(moved.Completed);
        Assert.Equal(4, moved.Articles);
        Assert.Equal(5, moved.Folders);

        // The tree, with its shape: a heading holding two feeds, and one feed at the top.
        var here = feeds.Mail.Folders(feeds.Account.Id);
        var newRoot = Assert.Single(here, f => f.ParentId is null && f.Name == Root);
        var heading = Assert.Single(here, f => f.ParentId == newRoot.Id && f.Name == "Technology");
        Assert.Equal(2, here.Count(f => f.ParentId == heading.Id));
        Assert.Single(here, f => f.ParentId == newRoot.Id && f.Name == "xkcd");

        // And the mail account no longer has any of it.
        Assert.DoesNotContain(from.Mail.Folders(from.Account.Id), f => f.Name == Root);
    }

    [Fact]
    public void AnArticleArrivesWithWhatTheReaderPutOnIt()
    {
        var (from, fromStore) = Mail();
        var (feeds, feedStore) = Feeds();
        using var _a = fromStore;
        using var _b = feedStore;

        var (verge, _, _) = Seed(from);
        Article(from.Mail, verge, "read-and-flagged", read: true, flagged: true);
        Article(from.Mail, verge, "untouched");

        FeedStoreMove.Move(feeds, from, Root);

        var landed = feeds.Mail.Folders(feeds.Account.Id).Single(f => f.Name == "The Verge");
        var all = feeds.Mail.Messages(landed.Id);

        var kept = Assert.Single(all, m => m.Subject == "read-and-flagged");
        Assert.True(kept.IsRead);
        Assert.True(kept.IsFlagged);

        // And the message itself, not just its row: the raw is what the reading pane renders.
        Assert.NotNull(feeds.Mail.LoadRaw(kept.Id));
        Assert.Equal("https://example.com/read-and-flagged", kept.FeedLink);

        var other = Assert.Single(all, m => m.Subject == "untouched");
        Assert.False(other.IsRead);
        Assert.False(other.IsFlagged);
    }

    [Fact]
    public void BoardsComeWithTheArticlesTheyHold()
    {
        // Boards live in the store their articles live in, so a move that left them behind would
        // empty every keep pile — silently, the board still listed and simply holding nothing.
        var (from, fromStore) = Mail();
        var (feeds, feedStore) = Feeds();
        using var _a = fromStore;
        using var _b = feedStore;

        var (verge, lwn, _) = Seed(from);
        var first = Article(from.Mail, verge, "phones");
        var second = Article(from.Mail, lwn, "kernel");

        var now = DateTimeOffset.UtcNow;
        var board = from.Mail.AddBoard("Reading", now, "Things to come back to");
        from.Mail.SaveToBoard([second], board.Id, now.AddHours(-2));
        from.Mail.SaveToBoard([first], board.Id, now);

        var moved = FeedStoreMove.Move(feeds, from, Root);
        Assert.Equal(1, moved.Boards);

        var landed = Assert.Single(feeds.Mail.Boards());
        Assert.Equal("Reading", landed.Name);
        Assert.Equal("Things to come back to", landed.Description);
        Assert.Equal(2, landed.Count);

        // In the order it was saved in, which is the order a board is read in — a move that
        // re-saved everything at the moment of the move would hand back a pile nobody made.
        Assert.Equal(["phones", "kernel"], feeds.Mail.BoardMessages(landed.Id).Select(m => m.Subject));
    }

    [Fact]
    public void ColourCategoriesTravelByName()
    {
        var (from, fromStore) = Mail();
        var (feeds, feedStore) = Feeds();
        using var _a = fromStore;
        using var _b = feedStore;

        var (verge, _, _) = Seed(from);
        var article = Article(from.Mail, verge, "phones");

        var red = from.Mail.Categories().First();
        from.Mail.Assign([article], red.Id);

        FeedStoreMove.Move(feeds, from, Root);

        var landing = feeds.Mail.Folders(feeds.Account.Id).Single(f => f.Name == "The Verge");
        var landed = Assert.Single(feeds.Mail.Messages(landing.Id));

        var carried = Assert.Single(feeds.Mail.CategoriesFor([landed.Id])[landed.Id]);
        Assert.Equal(red.Name, carried.Name);
    }

    [Fact]
    public void RunningItTwiceIsNotTwoCopies()
    {
        var (from, fromStore) = Mail();
        var (feeds, feedStore) = Feeds();
        using var _a = fromStore;
        using var _b = feedStore;

        var (verge, _, _) = Seed(from);
        Article(from.Mail, verge, "phones");

        Assert.Equal(1, FeedStoreMove.Move(feeds, from, Root).Articles);

        // The second run finds nothing: the tree it moved is gone from the mail account.
        Assert.Equal(FeedMove.Nothing, FeedStoreMove.Move(feeds, from, Root) with { Completed = false });

        var landed = feeds.Mail.Folders(feeds.Account.Id).Single(f => f.Name == "The Verge");
        Assert.Single(feeds.Mail.Messages(landed.Id));
    }

    [Fact]
    public void AMoveInterruptedHalfWayPicksUpWhereItStopped()
    {
        // A run that died after copying but before deleting leaves both copies. The next one
        // must recognise what it already carried rather than filing it a second time.
        var (from, fromStore) = Mail();
        var (feeds, feedStore) = Feeds();
        using var _a = fromStore;
        using var _b = feedStore;

        var (verge, _, _) = Seed(from);
        Article(from.Mail, verge, "phones");
        Article(from.Mail, verge, "watches");

        // Stand one of them in the feeds store already, as an interrupted run would have.
        var root = feeds.Mail.AddFolder(feeds.Account.Id, Root);
        var heading = feeds.Mail.AddFolder(feeds.Account.Id, "Technology", parentId: root.Id);
        var landing = feeds.Mail.AddFolder(feeds.Account.Id, "The Verge", parentId: heading.Id);
        Article(feeds.Mail, landing.Id, "phones");

        var moved = FeedStoreMove.Move(feeds, from, Root);

        Assert.True(moved.Completed);
        Assert.Equal(1, moved.Articles);
        Assert.Equal(2, feeds.Mail.Messages(landing.Id).Count);
    }

    [Fact]
    public void AnAccountWithNoFeedsIsLeftAlone()
    {
        var (from, fromStore) = Mail();
        var (feeds, feedStore) = Feeds();
        using var _a = fromStore;
        using var _b = feedStore;

        var inbox = from.Mail.FolderWithRole(from.Account.Id, FolderRole.Inbox)!;
        Article(from.Mail, inbox.Id, "an ordinary message");

        Assert.False(FeedStoreMove.Move(feeds, from, Root).DidAnything);

        Assert.Single(from.Mail.Messages(inbox.Id));
        Assert.Empty(feeds.Mail.Folders(feeds.Account.Id));
    }

    [Fact]
    public void MailSavedToABoardStaysWhereItIs()
    {
        // A reader may have put an ordinary message on a board. It is not a feed article and does
        // not travel, and the board it was on must not take a dangling reference with it.
        var (from, fromStore) = Mail();
        var (feeds, feedStore) = Feeds();
        using var _a = fromStore;
        using var _b = feedStore;

        var (verge, _, _) = Seed(from);
        var article = Article(from.Mail, verge, "phones");

        var inbox = from.Mail.FolderWithRole(from.Account.Id, FolderRole.Inbox)!;
        var letter = Article(from.Mail, inbox.Id, "a letter");

        var now = DateTimeOffset.UtcNow;
        var board = from.Mail.AddBoard("Mixed", now);
        from.Mail.SaveToBoard([article, letter], board.Id, now);

        FeedStoreMove.Move(feeds, from, Root);

        var landed = Assert.Single(feeds.Mail.Boards());
        Assert.Equal(["phones"], feeds.Mail.BoardMessages(landed.Id).Select(m => m.Subject));

        // And the letter is still in the inbox where it was.
        Assert.Single(from.Mail.Messages(inbox.Id));
    }

    [Fact]
    public void EveryAccountIsSweptAtOnce()
    {
        var (one, oneStore) = Mail("one@example.com");
        var (two, twoStore) = Mail("two@example.com");
        var (feeds, feedStore) = Feeds();
        using var _a = oneStore;
        using var _b = twoStore;
        using var _c = feedStore;

        var (verge, _, _) = Seed(one);
        Article(one.Mail, verge, "phones");

        var (otherVerge, _, _) = Seed(two);
        Article(two.Mail, otherVerge, "watches");

        var moved = FeedStoreMove.MoveAll(feeds, [one, two], Root);

        Assert.True(moved.Completed);
        Assert.Equal(2, moved.Articles);

        // One tree, not two: both accounts had the same feed, and it is one feed.
        var landing = feeds.Mail.Folders(feeds.Account.Id).Single(f => f.Name == "The Verge");
        Assert.Equal(2, feeds.Mail.Messages(landing.Id).Count);
    }
}
