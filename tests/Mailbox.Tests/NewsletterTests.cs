using System.Text;
using Mailbox.Core.Feeds;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Newsletters read as feeds: what counts as one, what is routed, and above all what is left
/// alone.
/// </summary>
public class NewsletterTests
{
    private static byte[] Issue(
        string from = "The Weekly <news@example.com>",
        string subject = "Issue 42",
        string? listId = null,
        string? unsubscribe = "<https://example.com/u/1>",
        string? precedence = null)
    {
        var text = new StringBuilder();
        text.Append("From: ").Append(from).Append("\r\n");
        text.Append("To: you@example.com\r\n");
        text.Append("Subject: ").Append(subject).Append("\r\n");
        text.Append("Date: Thu, 27 Aug 2026 09:00:00 +0000\r\n");
        text.Append("Message-Id: <").Append(Guid.NewGuid().ToString("n")).Append("@example.com>\r\n");

        if (listId is not null) text.Append("List-Id: ").Append(listId).Append("\r\n");
        if (unsubscribe is not null) text.Append("List-Unsubscribe: ").Append(unsubscribe).Append("\r\n");
        if (precedence is not null) text.Append("Precedence: ").Append(precedence).Append("\r\n");

        text.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
        text.Append("This week's issue.\r\n");

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static (OpenAccount Account, MailStore Store, MailRepository Mail, Folder Inbox) Mailbox()
    {
        var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var inbox = mail.FolderWithRole(account.Id, FolderRole.Inbox)!;

        return (new OpenAccount(account, store, mail), store, mail, inbox);
    }

    private static long File(MailRepository mail, Folder folder, byte[] raw)
    {
        using var stream = new MemoryStream(raw);
        var message = MimeMessage.Load(stream, TestContext.Current.CancellationToken);
        return mail.AddMessage(folder.Id, MessageMapper.ToSummary(message, null, raw.Length, DateTimeOffset.UtcNow), raw)!.Value;
    }

    [Fact]
    public void TheMarksOfBulkMailSomebodySignedUpFor()
    {
        // List-Unsubscribe is the one header that means "somebody subscribed to this", and it is
        // what every mailing list and marketing platform sets.
        var marks = Newsletters.Marks(Issue());

        Assert.True(marks.IsNewsletter);
        Assert.Equal("news@example.com", marks.Identity);
        Assert.Equal("The Weekly", marks.Name);
    }

    [Fact]
    public void AListIdIsPreferredToTheSendingAddress()
    {
        // The stable one: a publication that changes which service sends it keeps its list, and
        // routing on the address alone would lose the subscription the day they move.
        var marks = Newsletters.Marks(Issue(
            from: "The Weekly <bounce-482@mailer.example.net>",
            listId: "The Weekly <weekly.example.com>"));

        Assert.Equal("weekly.example.com", marks.Identity);
    }

    [Fact]
    public void OrdinaryMailIsNotANewsletter()
    {
        Assert.False(Newsletters.Marks(Issue(
            from: "A. Person <alice@example.com>",
            unsubscribe: null)).IsNewsletter);
    }

    [Fact]
    public void PrecedenceBulkCountsWhereThereIsNoUnsubscribeHeader()
    {
        Assert.True(Newsletters.Marks(Issue(unsubscribe: null, precedence: "bulk")).IsNewsletter);
    }

    [Fact]
    public void AFoldedHeaderIsStillRead()
    {
        // List-Unsubscribe routinely runs past the line limit and is folded across two lines.
        var raw = Encoding.UTF8.GetBytes(
            "From: The Weekly <news@example.com>\r\n"
            + "Subject: Issue 42\r\n"
            + "List-Unsubscribe: <https://example.com/a/very/long/unsubscribe/address>,\r\n"
            + "\t<mailto:unsubscribe@example.com>\r\n"
            + "\r\nBody\r\n");

        Assert.True(Newsletters.Marks(raw).IsNewsletter);
    }

    [Fact]
    public void ANewsletterIsAFeedWhoseTransportIsTheInbox()
    {
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        var feed = feeds.Add(Newsletters.AddressFor("news@example.com"), "The Weekly", "Reading");

        Assert.True(feed.IsNewsletter());
        Assert.Equal("Reading/The Weekly", feed.FolderPath);
    }

    [Fact]
    public async Task ANewsletterThePollWouldChokeOnIsSkipped()
    {
        // Its issues arrive as mail, so there is nothing to ask for — and the poll must not try,
        // nor count it as a feed that failed.
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Newsletters.AddressFor("news@example.com"), "The Weekly");

        var (account, store, _, _) = Mailbox();
        using var _s = store;

        var server = new FakeFeedServer();
        using var receiver = new FeedReceiver(feeds, server);

        var report = await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Polled);
        Assert.Empty(report.Failed);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void AnIssueOfARoutedNewsletterIsFiledUnderTheFeedsTree()
    {
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Newsletters.AddressFor("news@example.com"), "The Weekly", "Reading");

        var (account, store, mail, inbox) = Mailbox();
        using var _s = store;

        var raw = Issue();
        var id = File(mail, inbox, raw);

        using var stream = new MemoryStream(raw);
        var landed = new NewsletterRouter(feeds).Handle(mail, inbox, id, MimeMessage.Load(stream, TestContext.Current.CancellationToken));

        var folders = mail.Folders(account.Account.Id);
        var root = Assert.Single(folders, f => f.Name == FeedReceiver.RootFolder);
        var heading = Assert.Single(folders, f => f.ParentId == root.Id && f.Name == "Reading");
        var own = Assert.Single(folders, f => f.ParentId == heading.Id && f.Name == "The Weekly");

        Assert.Equal(own.Id, landed);
        Assert.Single(mail.Messages(own.Id));
        Assert.Empty(mail.Messages(inbox.Id));
    }

    [Fact]
    public void ANewsletterNobodyAskedForStaysInTheInbox()
    {
        // The whole safety of this: what looks like bulk mail includes receipts, password resets
        // and calendar invitations. Detection is a suggestion; the reader's tick is the decision.
        var feeds = new FeedSubscriptions(SettingsStore.Transient());

        var (account, store, mail, inbox) = Mailbox();
        using var _s = store;

        var raw = Issue();
        var id = File(mail, inbox, raw);

        using var stream = new MemoryStream(raw);
        var landed = new NewsletterRouter(feeds).Handle(mail, inbox, id, MimeMessage.Load(stream, TestContext.Current.CancellationToken));

        Assert.Equal(inbox.Id, landed);
        Assert.Single(mail.Messages(inbox.Id));
        Assert.DoesNotContain(mail.Folders(account.Account.Id), f => f.Name == FeedReceiver.RootFolder);
    }

    [Fact]
    public void OrdinaryMailFromAPersonIsNeverTouched()
    {
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(Newsletters.AddressFor("alice@example.com"), "Alice");

        var (_, store, mail, inbox) = Mailbox();
        using var _s = store;

        var raw = Issue(from: "A. Person <alice@example.com>", unsubscribe: null);
        var id = File(mail, inbox, raw);

        using var stream = new MemoryStream(raw);

        // Even with a subscription filed under her address: without the marks of bulk mail this
        // is a person writing, and a reader's correspondence must never be filed as reading.
        Assert.Equal(inbox.Id, new NewsletterRouter(feeds).Handle(mail, inbox, id, MimeMessage.Load(stream, TestContext.Current.CancellationToken)));
        Assert.Single(mail.Messages(inbox.Id));
    }

    [Fact]
    public void TheInboxIsReadForWhatIsAlreadyThere()
    {
        // The reader does not remember what they subscribed to over the years, so the mailbox is
        // read and what it holds is offered — the same idea as feed discovery.
        var (account, store, mail, inbox) = Mailbox();
        using var _s = store;

        File(mail, inbox, Issue(subject: "Issue 41"));
        File(mail, inbox, Issue(subject: "Issue 42"));
        File(mail, inbox, Issue(from: "Other <other@example.org>", subject: "Hello", listId: "Other <other.example.org>"));
        File(mail, inbox, Issue(from: "A. Person <alice@example.com>", subject: "Lunch?", unsubscribe: null));

        var found = NewsletterScan.In(mail, inbox.Id);

        Assert.Equal(2, found.Count);

        // Most issues first, so what a reader actually reads is at the top.
        Assert.Equal("news@example.com", found[0].Identity);
        Assert.Equal(2, found[0].Issues);
        Assert.Equal("other.example.org", found[1].Identity);

        Assert.DoesNotContain(found, f => f.From == "alice@example.com");
    }

    [Fact]
    public void TakingUpANewsletterBringsItsBackNumbersIntoTheFeedsStore()
    {
        // A subscription that starts empty and fills up over weeks looks broken — and the
        // issues must land in the store the Feeds module reads, which is not the mailbox's.
        var feeds = new FeedSubscriptions(SettingsStore.Transient());

        var (account, store, mail, inbox) = Mailbox();
        using var _s = store;

        var feedStore = MailStore.Transient();
        using var _f = feedStore;
        var feedMail = new MailRepository(feedStore);
        var feedAccount = new OpenAccount(
            feedMail.AddAccount("feeds@local", "Feeds", MailProtocol.Pop3), feedStore, feedMail);

        File(mail, inbox, Issue(subject: "Issue 41"));
        File(mail, inbox, Issue(subject: "Issue 42"));
        File(mail, inbox, Issue(from: "A. Person <alice@example.com>", subject: "Lunch?", unsubscribe: null));

        var feed = feeds.Add(Newsletters.AddressFor("news@example.com"), "The Weekly");
        var moved = NewsletterScan.Gather(feedAccount, account, inbox.Id, feed, "news@example.com");

        Assert.Equal(2, moved);

        // The issues stand in the feeds store's folder for the feed…
        var own = Assert.Single(
            feedMail.Folders(feedAccount.Account.Id), f => f.Name == "The Weekly");
        Assert.Equal(2, feedMail.Messages(own.Id).Count);

        // …the originals went to the mailbox's Deleted Items rather than vanishing…
        var deleted = mail.FolderWithRole(account.Account.Id, FolderRole.Deleted)!;
        Assert.Equal(2, mail.Messages(deleted.Id).Count);

        // …and the letter from a person is still where it was.
        Assert.Single(mail.Messages(inbox.Id));
    }
}
