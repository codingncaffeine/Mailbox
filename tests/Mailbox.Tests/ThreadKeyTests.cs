using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// A conversation is a reply relationship: the headers decide the thread key, and the subject
/// only carries mail that has no usable identity.
/// </summary>
public sealed class ThreadKeyTests : IDisposable
{
    public void Dispose() => GC.SuppressFinalize(this);

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static MessageSummary Mail(string uid, string subject, string? messageId, string? inReplyTo = null)
        => new(0, 0, uid, messageId, "Alice", "alice@example.com", subject, "Preview",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1024, false, false, false)
        { InReplyTo = inReplyTo };

    private string KeyOf(MailRepository repo, long folder, string uid)
        => repo.Messages(folder, int.MaxValue).First(m => m.ServerUid == uid).ThreadKey;

    [Fact]
    public void AReplyJoinsItsParentsConversationWhateverItsSubjectSays()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        repo.AddMessage(inbox.Id, Mail("1", "Budget review", "<root@a>"));
        repo.AddMessage(inbox.Id, Mail("2", "Budget review — revised figures", "<r1@b>", inReplyTo: "<root@a>"));

        Assert.Equal(KeyOf(repo, inbox.Id, "1"), KeyOf(repo, inbox.Id, "2"));
    }

    [Fact]
    public void TwoStrangersWhoBothWroteLunchAreTwoConversations()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        repo.AddMessage(inbox.Id, Mail("1", "Lunch", "<a@a>"));
        repo.AddMessage(inbox.Id, Mail("2", "Lunch", "<b@b>"));

        Assert.NotEqual(KeyOf(repo, inbox.Id, "1"), KeyOf(repo, inbox.Id, "2"));
    }

    [Fact]
    public void AParentArrivingAfterItsRepliesAdoptsTheirConversation()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        // Sync fetches newest first: the reply lands, then its reply, then the root.
        repo.AddMessage(inbox.Id, Mail("2", "RE: Plans", "<r1@b>", inReplyTo: "<root@a>"));
        repo.AddMessage(inbox.Id, Mail("3", "RE: Plans", "<r2@c>", inReplyTo: "<r1@b>"));
        repo.AddMessage(inbox.Id, Mail("1", "Plans", "<root@a>"));

        Assert.Equal(KeyOf(repo, inbox.Id, "2"), KeyOf(repo, inbox.Id, "3"));
        Assert.Equal(KeyOf(repo, inbox.Id, "2"), KeyOf(repo, inbox.Id, "1"));
    }

    [Fact]
    public void MailWithNoIdentityStillThreadsBySubject()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        repo.AddMessage(inbox.Id, Mail("1", "Digest", messageId: null));
        repo.AddMessage(inbox.Id, Mail("2", "RE: Digest", messageId: null));

        Assert.Equal(KeyOf(repo, inbox.Id, "1"), KeyOf(repo, inbox.Id, "2"));
    }

    [Fact]
    public void AReplyWhoseParentNeverComesKeepsTheSubjectKey()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        // Not its own identity: a late parent could still adopt it, and an edited-subject
        // orphan standing alone is the queued References work, not this rule's.
        repo.AddMessage(inbox.Id, Mail("1", "RE: Plans", "<r1@b>", inReplyTo: "<gone@x>"));

        Assert.Equal(MailRepository.ThreadKeyOf("Plans"), KeyOf(repo, inbox.Id, "1"));
    }
}
