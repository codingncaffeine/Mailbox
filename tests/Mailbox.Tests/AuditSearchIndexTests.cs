using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The search index against the store it stands for: search must find what is there, and must
/// not go on finding what was moved away, deleted, or deleted with the folder around it.
/// </summary>
/// <remarks>
/// <c>messages_fts</c> is an external-content FTS5 table — it keeps no copy of the text and is
/// held in step by three triggers on <c>messages</c>. That is the arrangement that cannot go
/// stale in the ordinary way and can go wrong in an unusual one: a row written or removed by a
/// path the triggers do not see leaves an index that disagrees with the store, and there is
/// nothing on screen to say so. So these press the repository's own move and delete and then ask
/// the index, rather than asking the row.
/// </remarks>
public class AuditSearchIndexTests : IDisposable
{
    private readonly MailStore _store = MailStore.Transient();

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    private (MailRepository Mail, long Account, long Inbox, long Archive, long Deleted) Fresh()
    {
        var mail = new MailRepository(_store);
        var account = mail.AddAccount("you@example.com", "A. Person", MailProtocol.Imap).Id;
        var inbox = mail.AddFolder(account, "Inbox", FolderRole.Inbox).Id;
        var archive = mail.AddFolder(account, "Archive").Id;
        var deleted = mail.AddFolder(account, "Deleted Items", FolderRole.Deleted).Id;
        return (mail, account, inbox, archive, deleted);
    }

    private static long Add(MailRepository mail, long folder, string subject, string body, string uid)
        => mail.AddMessage(folder, new MessageSummary(
            Id: 0,
            FolderId: folder,
            ServerUid: uid,
            MessageId: $"<{uid}@example.com>",
            FromName: "A. Person",
            FromAddress: "a.person@example.com",
            Subject: subject,
            Preview: body,
            Sent: null,
            Received: DateTimeOffset.UnixEpoch.AddSeconds(uid.Length * 1000),
            SizeBytes: 2,
            IsRead: false,
            IsFlagged: false,
            HasAttachment: false) { BodyText = body },
            "From: a.person@example.com\r\n\r\n"u8.ToArray())
           ?? throw new InvalidOperationException("nothing filed");

    private static long[] Found(IEnumerable<MessageSummary> rows) => [.. rows.Select(r => r.Id).Order()];

    /// <summary>Every message row has exactly one index row, and its text is the row's.</summary>
    private void AssertIndexAgreesWithTheStore()
    {
        var messages = _store.ScalarLong("SELECT count(*) FROM messages");
        var indexed = _store.ScalarLong(
            "SELECT count(*) FROM messages m JOIN messages_fts f ON f.rowid = m.id " +
            "WHERE messages_fts MATCH '\"a.person\"'");

        Assert.Equal(messages, indexed);
    }

    [Fact]
    public void SearchFindsAMessageInTheFolderItIsIn()
    {
        var (mail, _, inbox, archive, _) = Fresh();
        var damson = Add(mail, inbox, "The damson harvest", "damsons everywhere", "one");
        Add(mail, archive, "Archived apricot", "apricot stones", "two");

        Assert.Equal([damson], Found(mail.Search("damson")));
        Assert.Equal([damson], Found(mail.Search("damson", inbox)));
        Assert.Empty(mail.Search("damson", archive));
        AssertIndexAgreesWithTheStore();
    }

    [Fact]
    public void AMovedMessageIsFoundInItsNewFolderAndNotItsOld()
    {
        var (mail, _, inbox, archive, _) = Fresh();
        var damson = Add(mail, inbox, "The damson harvest", "damsons everywhere", "one");

        mail.MoveMessages([damson], archive);

        Assert.Equal([damson], Found(mail.Search("damson")));
        Assert.Equal([damson], Found(mail.Search("damson", archive)));
        Assert.Empty(mail.Search("damson", inbox));
        AssertIndexAgreesWithTheStore();
    }

    [Fact]
    public void ADeletedMessageIsFoundInDeletedItemsAndNowhereElse()
    {
        var (mail, _, inbox, _, deleted) = Fresh();
        var damson = Add(mail, inbox, "The damson harvest", "damsons everywhere", "one");

        // Delete, as the Home tab means it: a move to Deleted Items.
        mail.MoveMessages([damson], deleted);

        Assert.Equal([damson], Found(mail.Search("damson", deleted)));
        Assert.Empty(mail.Search("damson", inbox));
        AssertIndexAgreesWithTheStore();
    }

    [Fact]
    public void AMessageDeletedForGoodIsNotFoundAtAll()
    {
        var (mail, _, inbox, _, _) = Fresh();
        var damson = Add(mail, inbox, "The damson harvest", "damsons everywhere", "one");
        var quince = Add(mail, inbox, "Quarterly quince", "quinces are late", "two");

        mail.DeleteMessages([damson]);

        Assert.Empty(mail.Search("damson"));
        Assert.Equal([quince], Found(mail.Search("quince")));
        Assert.Equal(0, _store.ScalarLong(
            "SELECT count(*) FROM messages_fts WHERE messages_fts MATCH 'damson'"));
        AssertIndexAgreesWithTheStore();
    }

    /// <summary>
    /// The one an external-content index is most likely to miss: the rows go by a foreign key's
    /// cascade rather than by a DELETE anyone wrote, and a trigger that did not fire would leave
    /// every one of them in the index.
    /// </summary>
    [Fact]
    public void DeletingAFolderTakesItsMessagesOutOfTheIndex()
    {
        var (mail, _, inbox, archive, _) = Fresh();
        Add(mail, archive, "Archived apricot", "apricot stones", "one");
        Add(mail, archive, "Archived medlar", "medlar jelly", "two");
        var quince = Add(mail, inbox, "Quarterly quince", "quinces are late", "three");

        mail.RemoveFolderTree(archive);

        Assert.Empty(mail.Search("apricot"));
        Assert.Empty(mail.Search("medlar"));
        Assert.Equal(0, _store.ScalarLong(
            "SELECT count(*) FROM messages_fts WHERE messages_fts MATCH 'apricot OR medlar'"));
        Assert.Equal([quince], Found(mail.Search("quince")));
        AssertIndexAgreesWithTheStore();
    }

    [Fact]
    public void RemovingAnAccountTakesItsMessagesOutOfTheIndex()
    {
        var (mail, account, inbox, _, _) = Fresh();
        Add(mail, inbox, "The damson harvest", "damsons everywhere", "one");

        mail.RemoveAccount(account);

        Assert.Empty(mail.Search("damson"));
        Assert.Equal(0, _store.ScalarLong(
            "SELECT count(*) FROM messages_fts WHERE messages_fts MATCH 'damson'"));
        Assert.Equal(0, _store.ScalarLong("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void ARecoveredMessageIsSearchableAgain()
    {
        var (mail, _, inbox, _, _) = Fresh();
        var damson = Add(mail, inbox, "The damson harvest", "damsons everywhere", "one");

        mail.DeleteMessages([damson]);
        Assert.Empty(mail.Search("damson"));

        var held = mail.Recoverable();
        Assert.Single(held);
        mail.Restore([held[0].Id], inbox);

        var back = mail.Search("damson");
        Assert.Single(back);
        Assert.Equal("The damson harvest", back[0].Subject);
        AssertIndexAgreesWithTheStore();
    }

    [Fact]
    public void EditingAMessageReindexesItUnderItsNewWordsAndNotItsOld()
    {
        var (mail, _, inbox, _, _) = Fresh();
        var damson = Add(mail, inbox, "The damson harvest", "damsons everywhere", "one");

        _store.Execute(
            "UPDATE messages SET subject = 'The medlar harvest', preview = 'medlars everywhere', " +
            "body_text = 'medlars everywhere' WHERE id = $id", ("$id", damson));

        Assert.Empty(mail.Search("damson"));
        Assert.Equal([damson], Found(mail.Search("medlar")));
        AssertIndexAgreesWithTheStore();
    }

    /// <summary>
    /// Search reaches the body, not only the headers — the thing schema 11 was added for and the
    /// thing a store migrated past it can silently stop doing.
    /// </summary>
    [Fact]
    public void SearchReachesTheBody()
    {
        var (mail, _, inbox, _, _) = Fresh();
        var damson = Add(mail, inbox, "Nothing in the subject", "the medlars are ready", "one");

        Assert.Equal([damson], Found(mail.Search("medlars")));
        Assert.Equal([damson], Found(mail.Search("body:medlars")));
        Assert.Empty(mail.Search("subject:medlars"));
    }
}
