using Mailbox.Core.Focus;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Focused Inbox (§12): the classifier's rules, pure; the arrival handler over a store; and the
/// "always" override that remembers a sender and moves what they already sent.
/// </summary>
public class FocusedInboxTests
{
    private static FocusFacts Person() => new()
    {
        FromAddress = "alice@example.org",
        FromName = "Alice Chen",
        HeaderNames = ["from", "to", "subject", "date", "message-id"],
        AddressedToMe = true,
    };

    [Fact]
    public void APersonWritingToMeIsFocused() => Assert.True(FocusedInbox.IsFocused(Person()));

    [Fact]
    public void TheReadersOwnWordWinsOverEverything()
    {
        Assert.False(FocusedInbox.IsFocused(Person() with { Override = false }));
        Assert.True(FocusedInbox.IsFocused(Person() with { HeaderNames = ["list-id"], Override = true }));
    }

    [Fact]
    public void SomeoneIHaveWrittenToIsFocusedWhateverTheirHeaders()
        => Assert.True(FocusedInbox.IsFocused(Person() with { HeaderNames = ["list-unsubscribe"], KnownCorrespondent = true, AddressedToMe = false }));

    [Theory]
    [InlineData("list-id")]
    [InlineData("list-unsubscribe")]
    [InlineData("x-mailchimp-id")]
    public void ListMailIsOther(string header)
        => Assert.False(FocusedInbox.IsFocused(Person() with { HeaderNames = ["from", header] }));

    [Theory]
    [InlineData("bulk")]
    [InlineData("List")]
    public void BulkPrecedenceIsOther(string precedence)
        => Assert.False(FocusedInbox.IsFocused(Person() with { Precedence = precedence }));

    [Fact]
    public void AutoSubmittedIsOtherUnlessItSaysNo()
    {
        Assert.False(FocusedInbox.IsFocused(Person() with { AutoSubmitted = "auto-generated" }));
        Assert.True(FocusedInbox.IsFocused(Person() with { AutoSubmitted = "no" }));
    }

    [Theory]
    [InlineData("noreply@shop.example")]
    [InlineData("no-reply@shop.example")]
    [InlineData("notifications@service.example")]
    [InlineData("newsletter@paper.example")]
    [InlineData("info@company.example")]
    public void MachineSendersAreOther(string address)
        => Assert.False(FocusedInbox.IsFocused(Person() with { FromAddress = address }));

    [Fact]
    public void AStrangerWhoDidNotAddressMeIsOther()
        => Assert.False(FocusedInbox.IsFocused(Person() with { AddressedToMe = false }));

    // ---- Over a store -----------------------------------------------------------------------

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static (long Id, MimeMessage Message) Deliver(MailRepository repo, Folder inbox, string from, string to = "you@example.com", params (string, string)[] headers)
    {
        var message = new MimeMessage { Subject = "Hello" };
        message.From.Add(new MailboxAddress("Sender", from));
        message.To.Add(new MailboxAddress(string.Empty, to));
        foreach (var (name, value) in headers) message.Headers.Add(name, value);
        message.Body = new TextPart("plain") { Text = "Body" };
        message.MessageId = $"<{Guid.NewGuid():n}@example.com>";

        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();
        var summary = MessageMapper.ToSummary(message, Guid.NewGuid().ToString("n"), raw.Length, DateTimeOffset.UtcNow);
        return (repo.AddMessage(inbox.Id, summary, raw)!.Value, message);
    }

    [Fact]
    public void TheHandlerMarksArrivalsAndTheInboxListsEachHalf()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var handler = new FocusedInboxHandler();

        var (person, personMessage) = Deliver(repo, inbox, "alice@example.org");
        var (list, listMessage) = Deliver(repo, inbox, "digest@lists.example", "list@lists.example", ("List-Id", "<list.lists.example>"));
        handler.Handle(repo, inbox, person, personMessage);
        handler.Handle(repo, inbox, list, listMessage);

        Assert.True(repo.GetMessage(person)!.IsFocused);
        Assert.False(repo.GetMessage(list)!.IsFocused);
        Assert.Equal([person], repo.Messages(inbox.Id, focused: true).Select(m => m.Id));
        Assert.Equal([list], repo.Messages(inbox.Id, focused: false).Select(m => m.Id));
        Assert.Equal(2, repo.Messages(inbox.Id).Count);
    }

    [Fact]
    public void AlwaysMoveRemembersTheSenderAndMovesWhatTheyAlreadySent()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var handler = new FocusedInboxHandler();

        var (first, firstMessage) = Deliver(repo, inbox, "alice@example.org");
        handler.Handle(repo, inbox, first, firstMessage);
        Assert.True(repo.GetMessage(first)!.IsFocused);

        // "Always move to Other": the existing message moves, and the next from her follows.
        Assert.Equal(1, repo.SetFocusOverride("Alice@Example.org", focused: false, DateTimeOffset.UtcNow));
        Assert.False(repo.GetMessage(first)!.IsFocused);
        Assert.False(repo.FocusOverride("alice@example.org"));

        var (second, secondMessage) = Deliver(repo, inbox, "alice@example.org");
        handler.Handle(repo, inbox, second, secondMessage);
        Assert.False(repo.GetMessage(second)!.IsFocused);

        // Having written to her outranks the headers but not her override; clearing the
        // override to Focused brings both back.
        repo.RecordRecipients([("alice@example.org", "Alice")], DateTimeOffset.UtcNow);
        Assert.Equal(2, repo.SetFocusOverride("alice@example.org", focused: true, DateTimeOffset.UtcNow));
        Assert.True(repo.GetMessage(second)!.IsFocused);
    }
}
