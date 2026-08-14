using Mailbox.Protocols;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Mapping is tested against messages rather than servers, because the cases that matter are
/// the malformed ones: no From, a date from a machine with the wrong clock, a body that is only
/// HTML. Those arrive constantly and none of them should produce an empty-looking row.
/// </summary>
public class MessageMapperTests
{
    private static MimeMessage Parse(string raw)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw));
        return MimeMessage.Load(stream);
    }

    private static Store.MessageSummary Map(string raw)
        => MessageMapper.ToSummary(Parse(raw), "uid-1", raw.Length, DateTimeOffset.UnixEpoch);

    [Fact]
    public void TakesTheSenderAndSubject()
    {
        var summary = Map(
            "From: Alice Chen <alice@example.com>\r\nSubject: Q3 numbers\r\n\r\nBody here.");

        Assert.Equal("Alice Chen", summary.FromName);
        Assert.Equal("alice@example.com", summary.FromAddress);
        Assert.Equal("Q3 numbers", summary.Subject);
        Assert.Equal("Alice Chen", summary.DisplayFrom);
    }

    /// <summary>A sender with no display name shows their address, not a blank column.</summary>
    [Fact]
    public void FallsBackToTheAddressWhenThereIsNoName()
    {
        var summary = Map("From: alice@example.com\r\nSubject: Hi\r\n\r\nBody");

        Assert.Equal(string.Empty, summary.FromName);
        Assert.Equal("alice@example.com", summary.DisplayFrom);
    }

    [Fact]
    public void SaysSoWhenThereIsNoSenderAtAll()
    {
        var summary = Map("Subject: Anonymous\r\n\r\nBody");

        Assert.Equal("unknown sender", summary.DisplayFrom);
    }

    /// <summary>
    /// A date in the future pins the message to the top of a date-sorted list permanently. It
    /// comes from a sender's wrong clock, and is not trusted.
    /// </summary>
    [Fact]
    public void ADateFromTheFutureIsNotTrusted()
    {
        var future = DateTimeOffset.UtcNow.AddYears(3).ToString("r");
        var summary = Map($"From: a@example.com\r\nDate: {future}\r\nSubject: S\r\n\r\nBody");

        Assert.Null(summary.Sent);
    }

    [Fact]
    public void APlausibleDateIsKept()
    {
        var summary = Map(
            "From: a@example.com\r\nDate: Thu, 13 Aug 2026 09:41:00 +0000\r\nSubject: S\r\n\r\nBody");

        Assert.Equal(2026, summary.Sent!.Value.Year);
    }

    [Fact]
    public void PreviewComesFromThePlainBodyWhenThereIsOne()
    {
        var summary = Map(
            "From: a@example.com\r\nSubject: S\r\n\r\nThanks for   pulling\r\nthose together.");

        Assert.Equal("Thanks for pulling those together.", summary.Preview);
    }

    /// <summary>An HTML-only message still needs a readable preview, not a preview of markup.</summary>
    [Fact]
    public void PreviewStripsMarkupFromAnHtmlOnlyMessage()
    {
        var summary = Map(
            "From: a@example.com\r\nSubject: S\r\nContent-Type: text/html\r\n\r\n" +
            "<html><body><p>Hello <b>there</b></p></body></html>");

        Assert.Equal("Hello there", summary.Preview);
    }

    [Fact]
    public void PreviewDoesNotReadStylesheetsAsProse()
    {
        var summary = Map(
            "From: a@example.com\r\nSubject: S\r\nContent-Type: text/html\r\n\r\n" +
            "<html><head><style>.x{color:red}</style></head><body>Real text</body></html>");

        Assert.Equal("Real text", summary.Preview);
        Assert.DoesNotContain("color", summary.Preview);
    }

    [Fact]
    public void PreviewDecodesEntities()
    {
        var summary = Map(
            "From: a@example.com\r\nSubject: S\r\nContent-Type: text/html\r\n\r\n" +
            "<p>Tom &amp; Jerry &lt;3</p>");

        Assert.Equal("Tom & Jerry <3", summary.Preview);
    }

    [Theory]
    [InlineData("one   two", 200, "one two")]
    [InlineData("line\r\nbreak", 200, "line break")]
    [InlineData("abcdefghij", 4, "abcd")]
    [InlineData("   leading", 200, "leading")]
    public void PreviewIsCondensed(string input, int limit, string expected)
        => Assert.Equal(expected, MessageMapper.Condense(input, limit));
}
