using Mailbox.Core.Search;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Searching a scope rather than a folder: what a reader with fifty subscriptions needs, and
/// what the free tier of the reader this is measured against does not have at all.
/// </summary>
public class FeedSearchTests
{
    private static (MailRepository Mail, MailStore Store, long Account) Store()
    {
        var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);

        return (mail, store, account.Id);
    }

    private static long Article(MailRepository mail, long folderId, string from, string subject, string body,
        DateTimeOffset? when = null)
    {
        var summary = new MessageSummary(
            0, folderId, Guid.NewGuid().ToString("n"), null, from, from.ToLowerInvariant() + "@example.com",
            subject, body, when ?? DateTimeOffset.UtcNow, when ?? DateTimeOffset.UtcNow,
            body.Length, false, false, false)
        {
            BodyText = body,
        };

        return mail.AddMessage(folderId, summary)!.Value;
    }

    [Fact]
    public void ASearchCanCoverManyFoldersAtOnce()
    {
        // A reader with fifty subscriptions typing into a search box would otherwise fire fifty
        // queries per keystroke.
        var (mail, store, account) = Store();
        using var _ = store;

        var verge = mail.AddFolder(account, "The Verge").Id;
        var ars = mail.AddFolder(account, "Ars Technica").Id;
        var lwn = mail.AddFolder(account, "LWN").Id;

        Article(mail, verge, "Verge", "A flood of new phones", "Phones everywhere.");
        Article(mail, ars, "Ars", "Flood defences in Rome", "Ancient engineering.");
        Article(mail, lwn, "LWN", "Kernel scheduling", "Nothing about water.");

        var query = SearchQuery.Parse("flood");

        Assert.Equal(2, mail.Search(query, new[] { verge, ars, lwn }, limit: 50).Count);
        Assert.Single(mail.Search(query, new[] { verge }, limit: 50));
    }

    [Fact]
    public void AScopeWithNothingInItFindsNothingRatherThanEverything()
    {
        // Not the same as "no scope", which searches the whole store. A heading a reader has just
        // emptied must not quietly widen their search to every message they own.
        var (mail, store, account) = Store();
        using var _ = store;

        var folder = mail.AddFolder(account, "The Verge").Id;
        Article(mail, folder, "Verge", "A flood of new phones", "Phones everywhere.");

        Assert.Empty(mail.Search(SearchQuery.Parse("flood"), Array.Empty<long>(), limit: 50));
        Assert.Single(mail.Search(SearchQuery.Parse("flood"), folderIds: null, limit: 50));
    }

    [Fact]
    public void TheKeywordsWorkOverAScopeAsTheyDoOverAFolder()
    {
        var (mail, store, account) = Store();
        using var _ = store;

        var lwn = mail.AddFolder(account, "LWN").Id;
        var verge = mail.AddFolder(account, "The Verge").Id;

        Article(mail, lwn, "corbet", "Using steal time", "Virtualisation.");
        Article(mail, verge, "Someone", "Steal this idea", "Nothing to do with corbet.");

        var scope = new[] { lwn, verge };

        Assert.Single(mail.Search(SearchQuery.Parse("from:corbet"), scope, limit: 50));
        Assert.Equal(2, mail.Search(SearchQuery.Parse("steal"), scope, limit: 50).Count);
    }

    [Fact]
    public void HeadlineOnlyIsTheSubjectColumnOfTheSameIndex()
    {
        // The control is a shorthand onto the grammar rather than a second search, so a reader
        // who knows the keywords and one who presses the button end up in the same place.
        var (mail, store, account) = Store();
        using var _ = store;

        var folder = mail.AddFolder(account, "The Verge").Id;
        Article(mail, folder, "Verge", "A flood of new phones", "Phones everywhere.");
        Article(mail, folder, "Verge", "Nothing to see", "There was a flood of complaints.");

        var scope = new[] { folder };

        Assert.Equal(2, mail.Search(SearchQuery.Parse("flood"), scope, limit: 50).Count);
        Assert.Single(mail.Search(SearchQuery.Parse("subject:flood"), scope, limit: 50));
    }

    [Fact]
    public void ADateBoundNarrowsToWhatIsRecent()
    {
        var (mail, store, account) = Store();
        using var _ = store;

        var folder = mail.AddFolder(account, "The Verge").Id;
        var now = DateTimeOffset.UtcNow;

        Article(mail, folder, "Verge", "Flood today", "Recent.", now);
        Article(mail, folder, "Verge", "Flood last year", "Old.", now.AddDays(-400));

        var recent = SearchQuery.Parse("flood") with { Received = (now.AddDays(-7), null) };

        Assert.Single(mail.Search(recent, new[] { folder }, limit: 50));
        Assert.Equal(2, mail.Search(SearchQuery.Parse("flood"), new[] { folder }, limit: 50).Count);
    }
}
