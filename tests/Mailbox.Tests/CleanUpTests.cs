using Mailbox.Core.Conversations;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>Conversation Clean Up's containment rule, pure; and Ignore Conversation over a store.</summary>
public class CleanUpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private const string Original = "Could you send the Q3 numbers over before Thursday? The variance on line 14 is the one I want to look at first.";
    private const string Reply = "Sure, attached.\n\n> Could you send the Q3 numbers over before Thursday? The variance on line 14\n> is the one I want to look at first.";

    [Fact]
    public void AMessageWhollyQuotedInALaterReplyIsRedundant()
    {
        var thread = new List<CleanUpCandidate>
        {
            new(1, T0, Original),
            new(2, T0.AddHours(1), Reply),
        };

        Assert.Equal([1L], CleanUp.Redundant(thread));
    }

    [Fact]
    public void TheNewestMessageAndAModifiedOriginalAreKept()
    {
        var thread = new List<CleanUpCandidate>
        {
            new(1, T0, Original + " And one more thing that the reply does not quote."),
            new(2, T0.AddHours(1), Reply),
        };

        Assert.Empty(CleanUp.Redundant(thread));
    }

    [Fact]
    public void ThePolicySwitchesKeepWhatTheySay()
    {
        var thread = new List<CleanUpCandidate>
        {
            new(1, T0, Original) { IsUnread = true },
            new(2, T0.AddHours(1), Reply),
        };

        Assert.Empty(CleanUp.Redundant(thread, new CleanUpPolicy { KeepUnread = true }));
        Assert.Equal([1L], CleanUp.Redundant(thread, new CleanUpPolicy { KeepUnread = false }));

        var flagged = new List<CleanUpCandidate> { new(1, T0, Original) { IsFlagged = true }, new(2, T0.AddHours(1), Reply) };
        Assert.Empty(CleanUp.Redundant(flagged));
        Assert.Equal([1L], CleanUp.Redundant(flagged, new CleanUpPolicy { KeepFlagged = false }));
    }

    [Fact]
    public void ShortMessagesAreNeverRedundant()
    {
        var thread = new List<CleanUpCandidate> { new(1, T0, "ok"), new(2, T0.AddHours(1), "> ok\n\nGreat, thanks.") };
        Assert.Empty(CleanUp.Redundant(thread));
    }

    [Fact]
    public void FoldingStripsQuotesHeadersAndCase()
    {
        var folded = CleanUp.Fold("> From: A. Person\n> Sent: Monday\n>> Hello   THERE\n-- \nsig");
        Assert.Equal("hello there sig", folded);
    }

    // ---- Ignore Conversation over a store ----------------------------------------------------

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static (long Id, MimeMessage Message) Deliver(MailRepository repo, Folder inbox, string subject)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress("Sender", "a@example.org"));
        message.To.Add(new MailboxAddress(string.Empty, "you@example.com"));
        message.Body = new TextPart("plain") { Text = "Body" };
        message.MessageId = $"<{Guid.NewGuid():n}@example.com>";
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();
        return (repo.AddMessage(inbox.Id, MessageMapper.ToSummary(message, Guid.NewGuid().ToString("n"), raw.Length, T0), raw)!.Value, message);
    }

    [Fact]
    public void AnIgnoredConversationsArrivalsGoToDeletedItems()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var deleted = repo.FolderWithRole(inbox.AccountId, FolderRole.Deleted)!;
        var handler = new IgnoreHandler();

        var (first, firstMessage) = Deliver(repo, inbox, "Lunch plans");
        Assert.Equal(inbox.Id, handler.Handle(repo, inbox, first, firstMessage));

        var key = MailRepository.ThreadKeyOf("Re: Lunch plans");
        repo.Ignore(key, "Lunch plans", T0);
        Assert.True(repo.IsIgnored(key));

        var (reply, replyMessage) = Deliver(repo, inbox, "RE: Lunch plans");
        Assert.Equal(deleted.Id, handler.Handle(repo, inbox, reply, replyMessage));
        Assert.Equal(deleted.Id, repo.GetMessage(reply)!.FolderId);

        // The conversation across folders, Deleted Items aside and included.
        Assert.Equal([first], repo.MessagesInThread(key).Select(m => m.Id));
        Assert.Equal(2, repo.MessagesInThread(key, includeDeleted: true).Count);

        repo.Unignore(key);
        var (later, laterMessage) = Deliver(repo, inbox, "Re: Lunch plans");
        Assert.Equal(inbox.Id, handler.Handle(repo, inbox, later, laterMessage));
    }
}
