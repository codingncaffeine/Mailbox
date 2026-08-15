using Mailbox.Core.Rules;
using Mailbox.Core.Search;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Search folders: each template finds what its name says over an account's mail, a custom
/// folder runs the rules' own conditions, and the queries round-trip through the store.
/// </summary>
public class SearchFolderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Own = ["you@example.com"];

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static long Deliver(MailRepository repo, long folderId, string from, string subject,
        string body = "Hello", string to = "you@example.com", string? cc = null, bool read = false,
        bool attachment = false, MessageImportance importance = MessageImportance.Normal, DateTimeOffset? received = null,
        int size = 1000)
    {
        var message = new MimeMessage { Subject = subject, Importance = importance };
        message.From.Add(new MailboxAddress("Sender", from));
        foreach (var t in to.Split(';')) message.To.Add(new MailboxAddress(string.Empty, t));
        if (cc is not null) message.Cc.Add(new MailboxAddress(string.Empty, cc));
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
        var summary = MessageMapper.ToSummary(message, Guid.NewGuid().ToString("n"), size, received ?? Now, read);
        return repo.AddMessage(folderId, summary, raw)!.Value;
    }

    private static IReadOnlyList<string> Subjects(MailRepository repo, SearchFolderQuery query)
        => [.. repo.SearchFolderResults(query, Own, Now).Select(m => m.Subject).Order()];

    [Fact]
    public void TheReadingMailTemplatesFindWhatTheySay()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        Deliver(repo, inbox.Id, "a@example.org", "Unread one");
        Deliver(repo, inbox.Id, "a@example.org", "Read one", read: true);
        var flagged = Deliver(repo, inbox.Id, "a@example.org", "Flagged read", read: true);
        repo.SetFlagged([flagged], true);
        Deliver(repo, inbox.Id, "a@example.org", "Important", read: true, importance: MessageImportance.High);

        Assert.Equal(["Unread one"], Subjects(repo, new(SearchFolderKind.Unread)));
        Assert.Equal(["Flagged read"], Subjects(repo, new(SearchFolderKind.Flagged)));
        Assert.Equal(["Flagged read", "Unread one"], Subjects(repo, new(SearchFolderKind.UnreadOrFlagged)));
        Assert.Equal(["Important"], Subjects(repo, new(SearchFolderKind.Important)));
        Assert.Equal(1, repo.SearchFolderUnread(new(SearchFolderKind.UnreadOrFlagged), Own, Now));
    }

    [Fact]
    public void ThePeopleTemplatesReadSenderAndRecipients()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var sent = repo.FolderWithRole(inbox.AccountId, FolderRole.Sent)!;

        Deliver(repo, inbox.Id, "alice@example.org", "From Alice");
        Deliver(repo, sent.Id, "you@example.com", "To Alice", to: "alice@example.org");
        Deliver(repo, inbox.Id, "list@example.net", "Via list", to: "list@example.net");
        Deliver(repo, inbox.Id, "bob@example.org", "Cc me", to: "carol@example.org", cc: "you@example.com");

        Assert.Equal(["From Alice"], Subjects(repo, new(SearchFolderKind.From) { Values = ["alice@example.org"] }));
        Assert.Equal(["From Alice", "To Alice"], Subjects(repo, new(SearchFolderKind.FromOrTo) { Values = ["alice@example.org"] }));
        Assert.Equal(["Cc me", "From Alice"], Subjects(repo, new(SearchFolderKind.From) { Values = ["@example.org"] }));

        // Directly to me: my address in To. Cc does not count, and a list did not address me.
        Assert.Equal(["From Alice"], Subjects(repo, new(SearchFolderKind.SentDirectlyToMe)));

        // Public groups: nothing of mine in To or Cc.
        Assert.Equal(["To Alice", "Via list"], Subjects(repo, new(SearchFolderKind.SentToLists)));
    }

    [Fact]
    public void TheOrganizingTemplatesReadTheColumns()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var tagged = Deliver(repo, inbox.Id, "a@example.org", "Tagged");
        var red = repo.Categories().First(c => c.Name == "Red Category");
        repo.Assign([tagged], red.Id);
        Deliver(repo, inbox.Id, "a@example.org", "Big one", size: 5 * 1024 * 1024);
        Deliver(repo, inbox.Id, "a@example.org", "Ancient", received: Now.AddDays(-400));
        Deliver(repo, inbox.Id, "a@example.org", "With file", attachment: true);
        Deliver(repo, inbox.Id, "a@example.org", "Budget talk", body: "The quarterly budget is attached.");

        Assert.Equal(["Tagged"], Subjects(repo, new(SearchFolderKind.Categorized)));
        Assert.Equal(["Tagged"], Subjects(repo, new(SearchFolderKind.Categorized) { Values = ["Red Category"] }));
        Assert.Empty(Subjects(repo, new(SearchFolderKind.Categorized) { Values = ["Blue Category"] }));
        Assert.Equal(["Big one"], Subjects(repo, new(SearchFolderKind.Large) { Threshold = 1024 }));
        Assert.Equal(["Ancient"], Subjects(repo, new(SearchFolderKind.Old) { Threshold = 365 }));
        Assert.Equal(["With file"], Subjects(repo, new(SearchFolderKind.WithAttachments)));
        Assert.Equal(["Budget talk"], Subjects(repo, new(SearchFolderKind.WithWords) { Values = ["budget"] }));
    }

    [Fact]
    public void DeletedAndJunkAreLeftOutUnlessAsked()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var deleted = repo.FolderWithRole(inbox.AccountId, FolderRole.Deleted)!;
        var junk = repo.FolderWithRole(inbox.AccountId, FolderRole.Junk)!;

        Deliver(repo, inbox.Id, "a@example.org", "Kept");
        Deliver(repo, deleted.Id, "a@example.org", "Binned");
        Deliver(repo, junk.Id, "a@example.org", "Junked");

        Assert.Equal(["Kept"], Subjects(repo, new(SearchFolderKind.Unread)));
        Assert.Equal(["Binned", "Junked", "Kept"], Subjects(repo, new(SearchFolderKind.Unread) { IncludeDeleted = true }));
    }

    [Fact]
    public void ACustomFolderRunsTheRulesOwnConditions()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        Deliver(repo, inbox.Id, "alice@example.org", "Plan", body: "Let us meet on Thursday.");
        Deliver(repo, inbox.Id, "alice@example.org", "Other", body: "Nothing to see.");
        Deliver(repo, inbox.Id, "bob@example.org", "Plan", body: "Thursday works.");

        var query = new SearchFolderQuery(SearchFolderKind.Custom)
        {
            Conditions =
            [
                new RuleCondition(RuleConditionKind.From) { Values = ["alice@example.org"] },
                new RuleCondition(RuleConditionKind.BodyContains) { Values = ["thursday"] },
            ],
        };

        var found = repo.SearchFolderResults(query, Own, Now);
        Assert.Equal("Plan", Assert.Single(found).Subject);
        Assert.Equal("alice@example.org", found[0].FromAddress);
    }

    [Fact]
    public void SearchFoldersAreStoredAndRoundTrip()
    {
        var (store, repo, _) = Fresh();
        using var __ = store;

        var query = new SearchFolderQuery(SearchFolderKind.Large) { Threshold = 512, IncludeDeleted = true };
        var folder = repo.AddSearchFolder(query.DefaultName(), query, Now);

        var back = Assert.Single(repo.SearchFolders());
        Assert.Equal("Large Mail (over 512 KB)", back.Name);
        Assert.Equal(SearchFolderKind.Large, back.Query.Kind);
        Assert.Equal(512, back.Query.Threshold);
        Assert.True(back.Query.IncludeDeleted);

        repo.UpdateSearchFolder(folder.Id, "Big", query with { Threshold = 2048 });
        Assert.Equal(2048, Assert.Single(repo.SearchFolders()).Query.Threshold);

        repo.DeleteSearchFolder(folder.Id);
        Assert.Empty(repo.SearchFolders());
    }

    [Fact]
    public void TheMapperReadsImportanceAndRecipients()
    {
        var message = new MimeMessage { Subject = "x", Importance = MessageImportance.High };
        message.From.Add(new MailboxAddress("A", "a@example.org"));
        message.To.Add(new MailboxAddress("You", "You@Example.com"));
        message.Cc.Add(new MailboxAddress("B", "b@example.org"));
        message.Body = new TextPart("plain") { Text = "hi" };

        var summary = MessageMapper.ToSummary(message, "1", 10, Now);
        Assert.Equal(2, summary.Importance);
        Assert.Equal(["you@example.com"], summary.To);
        Assert.Equal(["b@example.org"], summary.Cc);
    }
}
