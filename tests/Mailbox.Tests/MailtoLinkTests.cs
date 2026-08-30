using Mailbox.Core.Compose;

namespace Mailbox.Tests;

/// <summary>
/// The mailto: parser (RFC 6068). What is tested is the ordinary shape of a link, the awkward
/// encodings, and the two security rules that live here — attach is dropped, and no header a
/// stranger writes is honoured beyond to/cc/bcc/subject/body.
/// </summary>
public class MailtoLinkTests
{
    [Fact]
    public void ANotMailtoLinkIsRejected()
    {
        Assert.Null(MailtoLink.Parse("https://example.com"));
        Assert.Null(MailtoLink.Parse(""));
        Assert.Null(MailtoLink.Parse(null));
    }

    [Fact]
    public void BareMailtoIsAnEmptyDraft()
    {
        var link = MailtoLink.Parse("mailto:")!;
        Assert.Empty(link.To);
        Assert.Empty(link.Cc);
        Assert.Equal(string.Empty, link.Subject);
    }

    [Fact]
    public void ThePathIsTheRecipient()
    {
        var link = MailtoLink.Parse("mailto:priya@example.net")!;
        Assert.Equal("priya@example.net", Assert.Single(link.To));
    }

    [Fact]
    public void RecipientsComeFromBothThePathAndTheToParameter()
    {
        var link = MailtoLink.Parse("mailto:a@example.com?to=b@example.com&cc=c@example.com&bcc=d@example.com")!;

        Assert.Equal(["a@example.com", "b@example.com"], link.To);
        Assert.Equal("c@example.com", Assert.Single(link.Cc));
        Assert.Equal("d@example.com", Assert.Single(link.Bcc));
    }

    [Fact]
    public void SeveralRecipientsAreCommaSeparated()
    {
        var link = MailtoLink.Parse("mailto:a@example.com,b@example.com")!;
        Assert.Equal(["a@example.com", "b@example.com"], link.To);
    }

    [Fact]
    public void SubjectAndBodyArePercentDecoded()
    {
        var link = MailtoLink.Parse("mailto:x@example.com?subject=Meeting%20on%20Friday&body=Hi%2C%0Alet%27s%20talk.")!;

        Assert.Equal("Meeting on Friday", link.Subject);
        Assert.Equal("Hi,\nlet's talk.", link.Body);
    }

    [Fact]
    public void APlusInAnAddressIsALiteralPlusNotASpace()
    {
        // RFC 6068 is not form encoding: a tag address keeps its plus.
        var link = MailtoLink.Parse("mailto:you+list@example.com")!;
        Assert.Equal("you+list@example.com", Assert.Single(link.To));
    }

    [Fact]
    public void AttachIsDropped()
    {
        // A link that would attach a local file is an exfiltration primitive; it is ignored.
        var link = MailtoLink.Parse("mailto:x@example.com?subject=Hi&attach=/etc/passwd")!;
        Assert.Equal("Hi", link.Subject);
        // Nothing carries the file — there is no attachment field, and no header holds the path.
        Assert.DoesNotContain("passwd", link.Body);
        Assert.DoesNotContain("passwd", link.Subject);
    }

    [Fact]
    public void UnknownHeadersAreIgnored()
    {
        // A link cannot smuggle in a header the writer would not see, like a hidden Bcc under
        // another name, or set arbitrary headers.
        var link = MailtoLink.Parse("mailto:x@example.com?x-bcc=secret@example.com&from=spoof@example.com")!;
        Assert.Empty(link.Bcc);
        Assert.Equal("x@example.com", Assert.Single(link.To));
    }

    [Fact]
    public void ANewlineInTheSubjectIsFlattened()
    {
        // A newline in a subject would be header injection when the message is built.
        var link = MailtoLink.Parse("mailto:x@example.com?subject=Line%20one%0ALine%20two")!;
        Assert.DoesNotContain('\n', link.Subject);
        Assert.Equal("Line one Line two", link.Subject);
    }

    [Fact]
    public void ADuplicateRecipientIsAddedOnce()
    {
        var link = MailtoLink.Parse("mailto:a@example.com?to=A@Example.com")!;
        Assert.Single(link.To);
    }
}
