using Mailbox.Protocols;

namespace Mailbox.Tests;

/// <summary>
/// What the new-mail notification says, from a send/receive result. Pure, so the wording is
/// tested without a desktop: nothing when nothing came, a count when it did, and which account
/// when more than one received.
/// </summary>
public class NewMailNoticeTests
{
    private static SendReceiveResult Run(params (string Address, int Received)[] accounts)
        => new([.. accounts.Select(a => new AccountRunResult(a.Address, a.Received, 0))]);

    [Fact]
    public void NothingArrivedIsNoNotice()
    {
        Assert.Null(NewMailNotice.For(Run(("you@example.com", 0))));
        Assert.Null(NewMailNotice.For(Run()));
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
}
