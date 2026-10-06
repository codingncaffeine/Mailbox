using Mailbox.Core.Search;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Gmail lists one message in a folder for every label it wears, so the store holds a copy per
/// label. Across folders such a message is shown once; on any other server, copies in two
/// folders are two messages and every one is shown.
/// </summary>
public class GmailLabelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(MailStore Store, MailRepository Repo, long AccountId, Folder Inbox, Folder Work, Folder Receipts)
        : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    /// <summary>One message wearing Inbox, Work and Receipts, and a second that wears Work alone.</summary>
    private static Fixture Labelled(bool labels)
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@gmail.com", "You", MailProtocol.Imap);
        repo.CreateStandardFolders(account.Id);
        repo.SetUsesLabels(account.Id, labels);

        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;
        var work = repo.AddFolder(account.Id, "Work", FolderRole.None, null, "Work");
        var receipts = repo.AddFolder(account.Id, "Receipts", FolderRole.None, null, "Receipts");

        // Receipts first, so the copy kept is chosen by where it is and not by which came first.
        foreach (var folder in new[] { receipts, work, inbox })
        {
            repo.AddMessage(folder.Id, Message(folder.Id, "<budget@example.com>", "Budget review"));
        }

        repo.AddMessage(work.Id, Message(work.Id, "<other@example.com>", "Budget for the offsite"));
        return new Fixture(store, repo, account.Id, inbox, work, receipts);
    }

    private static MessageSummary Message(long folderId, string messageId, string subject)
        => new(0, folderId, null, messageId, "Sender", "sender@example.com", subject, "Body", Now, Now, 100, false, false, false);

    [Fact]
    public void AMessageWearingSeveralLabelsIsFoundOnceAndAsTheInboxsCopy()
    {
        using var f = Labelled(labels: true);

        var found = f.Repo.Search("budget");

        Assert.Equal(2, found.Count);
        Assert.Equal(f.Inbox.Id, Assert.Single(found, m => m.MessageId == "<budget@example.com>").FolderId);
    }

    /// <summary>
    /// The copy kept is one inside the search's own scope: searching two labels must not hide a
    /// message behind its Inbox copy, which that search cannot show.
    /// </summary>
    [Fact]
    public void ACopyOutsideTheScopeDoesNotHideOneInsideIt()
    {
        using var f = Labelled(labels: true);

        var found = f.Repo.Search(SearchQuery.Parse("review"), [f.Work.Id, f.Receipts.Id]);

        Assert.Contains(Assert.Single(found).FolderId, new[] { f.Work.Id, f.Receipts.Id });
    }

    /// <summary>The positive control: the same rows on an ordinary server are three messages.</summary>
    [Fact]
    public void OnAnOrdinaryServerEveryCopyIsShown()
    {
        using var f = Labelled(labels: false);

        Assert.Equal(3, f.Repo.Search("review").Count);
        Assert.Equal(4, f.Repo.SearchFolderResults(new SearchFolderQuery(SearchFolderKind.Unread), [], Now).Count);
        Assert.Equal(4, f.Repo.SearchFolderUnread(new SearchFolderQuery(SearchFolderKind.Unread), [], Now));
    }

    /// <summary>A search folder lists the message once, and its count says the same as its list.</summary>
    [Fact]
    public void ASearchFolderListsAndCountsALabelledMessageOnce()
    {
        using var f = Labelled(labels: true);
        var unread = new SearchFolderQuery(SearchFolderKind.Unread);

        var listed = f.Repo.SearchFolderResults(unread, [], Now);

        Assert.Equal(2, listed.Count);
        Assert.Equal(listed.Count(m => !m.IsRead), f.Repo.SearchFolderUnread(unread, [], Now));
    }

    /// <summary>Within one folder nothing changes: a folder cannot list a message twice.</summary>
    [Fact]
    public void ASearchOfOneFolderIsUntouched()
    {
        using var f = Labelled(labels: true);

        Assert.Equal(2, f.Repo.Search(SearchQuery.Parse("budget"), f.Work.Id).Count);
    }
}
