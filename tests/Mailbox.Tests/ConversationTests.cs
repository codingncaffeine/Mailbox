using Mailbox.Store.Lists;

namespace Mailbox.Tests;

/// <summary>
/// Threading has one failure mode that matters: a conversation that wrongly swallows an
/// unrelated message hides it, because the collapsed row shows only the newest. So the tests
/// lean on what must stay separate as much as on what must come together.
/// </summary>
public class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed record Row(
        string Subject,
        int MinutesAgo,
        string ThreadKey,
        long FolderId = 1,
        bool IsUnread = false) : IThreadable
    {
        public string DisplayFrom => "Alice";
        public DateTimeOffset Received => Now.AddMinutes(-MinutesAgo);
        public long SizeBytes => 1024;
        public bool IsFlagged => false;
        public bool HasAttachment => false;
    }

    [Fact]
    public void RepliesFoldUnderTheMessageTheyReplyTo()
    {
        var threads = Conversations.Build([
            new Row("Re: Budget", 5, "budget"),
            new Row("Budget", 60, "budget"),
            new Row("Lunch", 10, "lunch"),
        ]);

        Assert.Equal(2, threads.Count);
        Assert.True(threads[0].IsThread);
        Assert.Equal(2, threads[0].Count);
        Assert.False(threads[1].IsThread);
    }

    /// <summary>The collapsed row shows the newest, so that is what represents the thread.</summary>
    [Fact]
    public void TheNewestRepresentsTheThread()
    {
        var threads = Conversations.Build([
            new Row("Budget", 60, "budget"),
            new Row("Re: Budget", 5, "budget"),
            new Row("Re: Re: Budget", 30, "budget"),
        ]);

        Assert.Equal("Re: Budget", threads[0].Newest.Subject);
        Assert.Equal(["Re: Budget", "Re: Re: Budget", "Budget"],
            threads[0].Messages.Select(m => m.Subject));
    }

    /// <summary>
    /// A single message is not a conversation. Drawing it with an expander that reveals only
    /// itself would be nonsense.
    /// </summary>
    [Fact]
    public void OneMessageIsNotAThread()
    {
        var threads = Conversations.Build([new Row("Alone", 5, "alone")]);

        Assert.Single(threads);
        Assert.False(threads[0].IsThread);
    }

    /// <summary>
    /// Messages with no thread key stand alone rather than being heaped together under an
    /// empty key — which would fold every one of them into a single false conversation.
    /// </summary>
    [Fact]
    public void MessagesWithoutAThreadKeyDoNotFoldTogether()
    {
        var threads = Conversations.Build([
            new Row("One", 5, string.Empty),
            new Row("Two", 10, string.Empty),
            new Row("Three", 15, string.Empty),
        ]);

        Assert.Equal(3, threads.Count);
        Assert.All(threads, t => Assert.False(t.IsThread));
    }

    [Fact]
    public void AThreadIsUnreadWhenAnyMessageInItIs()
    {
        var threads = Conversations.Build([
            new Row("Budget", 60, "budget"),
            new Row("Re: Budget", 5, "budget") { IsUnread = true },
        ]);

        Assert.True(threads[0].HasUnread);
    }

    /// <summary>
    /// A conversation spanning Inbox and Sent is showing replies that are not in either folder
    /// on their own, which is why the reference marks it.
    /// </summary>
    [Fact]
    public void AThreadAcrossFoldersIsMarkedAsSplit()
    {
        var sameFolder = Conversations.Build([
            new Row("Budget", 60, "budget"),
            new Row("Re: Budget", 5, "budget"),
        ]);

        var acrossFolders = Conversations.Build([
            new Row("Budget", 60, "budget", FolderId: 1),
            new Row("Re: Budget", 5, "budget", FolderId: 2),
        ]);

        Assert.False(sameFolder[0].IsSplit);
        Assert.True(acrossFolders[0].IsSplit);
    }

    [Fact]
    public void ThreadKeysAreMatchedIgnoringCase()
    {
        var threads = Conversations.Build([
            new Row("Budget", 60, "Budget"),
            new Row("Re: budget", 5, "budget"),
        ]);

        Assert.Single(threads);
        Assert.Equal(2, threads[0].Count);
    }

    /// <summary>Threads appear where their first message did, not reshuffled to the front.</summary>
    [Fact]
    public void ThreadsHoldTheirPlaceInTheSequence()
    {
        var threads = Conversations.Build([
            new Row("Lunch", 1, "lunch"),
            new Row("Budget", 60, "budget"),
            new Row("Re: Budget", 2, "budget"),
        ]);

        Assert.Equal(["lunch", "budget"], threads.Select(t => t.Newest.ThreadKey));
    }

    [Fact]
    public void NothingInProducesNothingOut()
        => Assert.Empty(Conversations.Build<Row>([]));
}
