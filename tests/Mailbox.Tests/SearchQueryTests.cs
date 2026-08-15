using Mailbox.Core.Search;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// The search box's grammar — the reference's keywords — pure, and then run against a store.
/// </summary>
public class SearchQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero); // a Saturday

    [Fact]
    public void PlainWordsAreWordsAndKeywordsAreFilters()
    {
        var query = SearchQuery.Parse("budget from:alice subject:\"q3 numbers\" hasattachment:yes read:no flagged:yes importance:high", Now);

        Assert.Equal(["budget"], query.Words);
        Assert.Equal(["alice"], query.From);
        Assert.Equal(["q3 numbers"], query.Subject);
        Assert.True(query.HasAttachment);
        Assert.False(query.IsRead);
        Assert.True(query.IsFlagged);
        Assert.Equal(2, query.Importance);
        Assert.True(query.HasText);
    }

    [Fact]
    public void AnUnknownKeywordIsAWordAndAnEmptyBoxIsEmpty()
    {
        var query = SearchQuery.Parse("re: numbers unread:yes", Now);
        Assert.Equal(["re:", "numbers"], query.Words);
        Assert.False(query.IsRead);

        Assert.True(SearchQuery.Parse("   ", Now).IsEmpty);
        Assert.False(SearchQuery.Parse("hasattachment:yes", Now).IsEmpty);
        Assert.False(SearchQuery.Parse("hasattachment:yes", Now).HasText);
    }

    [Fact]
    public void SizesAndDatesParse()
    {
        Assert.Equal((Bound.After, 1024L * 1024), SearchQuery.ParseSize(">1mb"));
        Assert.Equal((Bound.Before, 10L * 1024), SearchQuery.ParseSize("<10kb"));
        Assert.Equal((Bound.After, 100L * 1024), SearchQuery.ParseSize("large"));
        Assert.Null(SearchQuery.ParseSize("many"));

        var today = SearchQuery.ParseSpan("today", Now)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), today.After);
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero), today.Before);

        var week = SearchQuery.ParseSpan("this week", Now)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero), week.After);

        var after = SearchQuery.ParseSpan(">2026-08-01", Now)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero), after.After);
        Assert.Null(after.Before);

        Assert.Null(SearchQuery.ParseSpan("someday", Now));
    }

    // ---- Against a store -----------------------------------------------------------------------

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static long Deliver(MailRepository repo, long folderId, string from, string subject, string body,
        bool read = false, bool attachment = false, string to = "you@example.com", MessageImportance importance = MessageImportance.Normal)
    {
        var message = new MimeMessage { Subject = subject, Importance = importance };
        message.From.Add(new MailboxAddress("Sender " + from, from));
        message.To.Add(new MailboxAddress(string.Empty, to));
        message.MessageId = $"<{Guid.NewGuid():n}@example.com>";
        message.Body = attachment
            ? new Multipart("mixed")
            {
                new TextPart("plain") { Text = body },
                new MimePart("application", "pdf") { FileName = "a.pdf", Content = new MimeContent(new MemoryStream(new byte[10])) },
            }
            : new TextPart("plain") { Text = body };

        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();
        return repo.AddMessage(folderId, MessageMapper.ToSummary(message, Guid.NewGuid().ToString("n"), raw.Length, Now, read), raw)!.Value;
    }

    [Fact]
    public void KeywordsNarrowTheFullTextSearch()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        Deliver(repo, inbox.Id, "alice@example.org", "Q3 numbers", "The budget is attached.", attachment: true);
        Deliver(repo, inbox.Id, "bob@example.org", "Lunch", "No budget for lunch.", read: true);
        Deliver(repo, inbox.Id, "alice@example.org", "Weekend", "Nothing about money.", importance: MessageImportance.High);

        IReadOnlyList<string> Find(string text) => [.. repo.Search(SearchQuery.Parse(text, Now)).Select(m => m.Subject).Order()];

        Assert.Equal(["Lunch", "Q3 numbers"], Find("budget"));
        Assert.Equal(["Q3 numbers"], Find("budget from:alice"));
        Assert.Equal(["Q3 numbers"], Find("budget hasattachment:yes"));
        Assert.Equal(["Lunch"], Find("budget read:yes"));
        Assert.Equal(["Q3 numbers", "Weekend"], Find("from:alice"));
        Assert.Equal(["Weekend"], Find("importance:high"));
        Assert.Equal(["Q3 numbers"], Find("subject:numbers"));
        Assert.Equal(["Lunch", "Q3 numbers"], Find("body:budget"));
        Assert.Empty(Find("body:numbers"));
        Assert.Equal(["Lunch", "Q3 numbers", "Weekend"], Find("to:you@example.com"));
        Assert.Equal(["Lunch", "Q3 numbers", "Weekend"], Find("received:today"));
        Assert.Empty(Find("received:yesterday"));
    }
}
