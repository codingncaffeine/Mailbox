using Mailbox.Protocols;

namespace Mailbox.Tests;

/// <summary>
/// What the new-mail notification says, from a send/receive result. Pure, so the wording is
/// tested without a desktop: nothing when nothing came, a count when it did, and which account
/// when more than one received. And what a toast is <em>about</em>: one message each while there
/// are few, so Reply and Delete on the toast have a message to act on; a count past that.
/// </summary>
public class NewMailNoticeTests
{
    /// <summary>A run in which each account's Inbox got the given number of messages, ids 1..n.</summary>
    private static SendReceiveResult Run(params (string Address, int Received)[] accounts)
        => new([.. accounts.Select(a => new AccountRunResult(a.Address, a.Received, 0)
        {
            Arrived = [.. Enumerable.Range(1, a.Received).Select(i => (long)i)],
        })]);

    private static ArrivedMessage? Describe(string address, long id)
        => new($"Sender {id}", $"Subject {id}", $"First line of {id}.\nSecond line.");

    [Fact]
    public void NothingArrivedIsNoNotice()
    {
        Assert.Null(NewMailNotice.For(Run(("you@example.com", 0))));
        Assert.Null(NewMailNotice.For(Run()));
        Assert.Empty(NewMailNotice.Toasts(Run(), Describe));
    }

    [Fact]
    public void OneMessageIsSingular()
    {
        var notice = NewMailNotice.For(Run(("you@example.com", 1)))!.Value;
        Assert.Equal("1 new message", notice.Summary);
        Assert.Equal("you@example.com", notice.Body);
    }

    [Fact]
    public void ManyMessagesFromOneAccountNameTheAccount()
    {
        var notice = NewMailNotice.For(Run(("you@example.com", 5)))!.Value;
        Assert.Equal("5 new messages", notice.Summary);
        Assert.Equal("you@example.com", notice.Body);
    }

    [Fact]
    public void SeveralAccountsAreListedWithTheirCounts()
    {
        var notice = NewMailNotice.For(Run(
            ("you@example.com", 2),
            ("work@example.net", 3),
            ("quiet@example.org", 0)))!.Value;

        // The total across the accounts that received, and a line each for those that did.
        Assert.Equal("5 new messages", notice.Summary);
        Assert.Contains("you@example.com: 2", notice.Body);
        Assert.Contains("work@example.net: 3", notice.Body);
        Assert.DoesNotContain("quiet@example.org", notice.Body);
    }

    [Fact]
    public void MailFiledAsJunkIsDownloadedButNotNews()
    {
        // Received counts the download; Arrived is what reached the Inbox. A run whose only
        // message went to Junk says nothing, or the toast would undo the filter.
        var junked = new SendReceiveResult([new AccountRunResult("you@example.com", 1, 0)]);

        Assert.Null(NewMailNotice.For(junked));
        Assert.Empty(NewMailNotice.Toasts(junked, Describe));
    }

    [Fact]
    public void AFewMessagesGetAToastEachNamingSenderAndSubject()
    {
        var toasts = NewMailNotice.Toasts(Run(("you@example.com", 2)), Describe);

        Assert.Equal(2, toasts.Count);
        Assert.All(toasts, t => Assert.True(t.IsSingle));

        Assert.Equal("Sender 1", toasts[0].Summary);
        Assert.Equal("Subject 1\nFirst line of 1.", toasts[0].Body);
        Assert.Equal("you@example.com", toasts[0].Address);
        Assert.Equal(1, toasts[0].MessageId);
        Assert.Equal(2, toasts[1].MessageId);
    }

    [Fact]
    public void ManyMessagesCollapseToOneCountToast()
    {
        var toasts = NewMailNotice.Toasts(Run(("you@example.com", NewMailNotice.PerMessageLimit + 1)), Describe);

        var only = Assert.Single(toasts);
        Assert.False(only.IsSingle);
        Assert.Equal($"{NewMailNotice.PerMessageLimit + 1} new messages", only.Summary);
        Assert.Equal("you@example.com", only.Body);
    }

    [Fact]
    public void AMessageThatCannotBeReadFallsBackToTheCount()
    {
        // The store could not describe it — gone already, or unreadable. Better one honest count
        // than a toast that names nothing.
        var toasts = NewMailNotice.Toasts(Run(("you@example.com", 2)), (_, _) => null);

        var only = Assert.Single(toasts);
        Assert.False(only.IsSingle);
        Assert.Equal("2 new messages", only.Summary);
    }

    [Fact]
    public void ASenderlessMessageIsHeadedByTheAccount()
    {
        var toasts = NewMailNotice.Toasts(
            Run(("you@example.com", 1)),
            (_, _) => new ArrivedMessage(string.Empty, string.Empty, string.Empty));

        var only = Assert.Single(toasts);
        Assert.Equal("you@example.com", only.Summary);
        Assert.Equal("(no subject)", only.Body);
    }

    [Fact]
    public void ALongFirstLineIsCutShort()
    {
        var toasts = NewMailNotice.Toasts(
            Run(("you@example.com", 1)),
            (_, _) => new ArrivedMessage("A. Person", "Hello", new string('x', 200)));

        var body = Assert.Single(toasts).Body;
        Assert.StartsWith("Hello\n", body);
        Assert.EndsWith("…", body);
        Assert.True(body.Length < 100);
    }
}
