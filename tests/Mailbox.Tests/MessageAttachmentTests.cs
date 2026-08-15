using Mailbox.Rendering;
using MimeKit;

namespace Mailbox.Tests;

public class MessageAttachmentTests
{
    private static MimePart File(string name, string type = "application/pdf", int size = 32)
        => new(ContentType.Parse(type))
        {
            FileName = name,
            Content = new MimeContent(new MemoryStream(new byte[size])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
        };

    private static MimeMessage With(params MimeEntity[] parts)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("A. Person", "you@example.com"));

        var body = new Multipart("mixed") { new TextPart("plain") { Text = "See attached." } };
        foreach (var part in parts) body.Add(part);

        message.Body = body;
        return message;
    }

    [Fact]
    public void AttachmentsAreListedWithTheirNameAndSize()
    {
        var found = MessageAttachments.List(With(File("agenda.pdf", size: 2048)));

        var only = Assert.Single(found);
        Assert.Equal("agenda.pdf", only.Name);
        Assert.Equal(2048, only.Size);
        Assert.Equal("2 KB", only.Describe());
        Assert.False(only.FromTnef);
    }

    /// <summary>
    /// An inline image is already on screen. Listing it again turns every newsletter into a
    /// message with nine attachments.
    /// </summary>
    [Fact]
    public void InlineImagesAreNotAttachments()
    {
        var logo = new MimePart("image", "png")
        {
            ContentId = "logo",
            Content = new MimeContent(new MemoryStream([1, 2, 3])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
        };

        Assert.Empty(MessageAttachments.List(With(logo)));
    }

    [Fact]
    public void APartWithAFileNameAndNoDispositionCounts()
    {
        var part = new MimePart("application", "zip")
        {
            FileName = "logs.zip",
            Content = new MimeContent(new MemoryStream(new byte[10])),
        };

        Assert.Equal("logs.zip", Assert.Single(MessageAttachments.List(With(part))).Name);
    }

    [Fact]
    public void AMessageWithNothingAttachedListsNothing()
    {
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "Hello." } };
        Assert.Empty(MessageAttachments.List(message));
    }

    /// <summary>
    /// The size a reader means is the size of the file that comes out, not of the base64 that
    /// carried it — which is a third larger.
    /// </summary>
    [Fact]
    public void SizeIsMeasuredDecoded()
    {
        var part = File("photo.jpg", "image/jpeg", size: 3000);
        part.ContentTransferEncoding = ContentEncoding.Base64;

        Assert.Equal(3000, Assert.Single(MessageAttachments.List(With(part))).Size);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void SizesReadTheWayPeopleReadThem(int bytes, string expected)
        => Assert.Equal(expected, new Attachment("x", "application/octet-stream", bytes,
            new MimePart()).Describe());

    [Fact]
    public void SavingWritesTheDecodedBytes()
    {
        var part = File("notes.txt", "text/plain");
        part.Content = new MimeContent(new MemoryStream("hello"u8.ToArray()));

        var attachment = Assert.Single(MessageAttachments.List(With(part)));

        using var buffer = new MemoryStream();
        attachment.SaveTo(buffer);

        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    // ---- A message carried inside a message ---------------------------------------------

    private static MessagePart Carried(string subject, string body = "The original.")
    {
        var inner = new MimeMessage { Subject = subject };
        inner.From.Add(new MailboxAddress("B. Person", "b@example.net"));
        inner.To.Add(new MailboxAddress("A. Person", "you@example.com"));
        inner.Body = new TextPart("plain") { Text = body };

        return new MessagePart
        {
            Message = inner,
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
        };
    }

    /// <summary>
    /// Forwarding as an attachment produces a <c>message/rfc822</c> part, which is a
    /// <c>MessagePart</c> and not a <c>MimePart</c> — so matching only the latter listed nothing
    /// at all, and a forwarded message showed an empty strip.
    /// </summary>
    [Fact]
    public void ACarriedMessageIsAnAttachment()
    {
        var found = Assert.Single(MessageAttachments.List(With(Carried("Q3 numbers"))));

        Assert.Equal("Q3 numbers.eml", found.Name);
        Assert.Equal("message/rfc822", found.MimeType);
        Assert.True(found.IsMessage);
        Assert.True(found.Size > 0);
    }

    [Fact]
    public void ACarriedMessagePrefersTheNameItWasGiven()
    {
        var carried = Carried("Q3 numbers");
        carried.ContentDisposition!.FileName = "forwarded.eml";

        Assert.Equal("forwarded.eml", Assert.Single(MessageAttachments.List(With(carried))).Name);
    }

    [Fact]
    public void ACarriedMessageWithNoSubjectStillHasAName()
        => Assert.Equal("message.eml",
            Assert.Single(MessageAttachments.List(With(Carried(string.Empty)))).Name);

    /// <summary>Saved as the RFC822 it is, so what lands on disk is an .eml that opens.</summary>
    [Fact]
    public void SavingACarriedMessageWritesTheWholeMessage()
    {
        var attachment = Assert.Single(MessageAttachments.List(With(Carried("Q3 numbers"))));

        using var buffer = new MemoryStream();
        attachment.SaveTo(buffer);
        buffer.Position = 0;

        Assert.Equal("Q3 numbers",
            MimeMessage.Load(buffer, TestContext.Current.CancellationToken).Subject);
    }

    /// <summary>What the forwarded message carried belongs to it, not to the message quoting it.</summary>
    [Fact]
    public void WhatIsInsideACarriedMessageIsNotHoistedOut()
    {
        var carried = Carried("Q3 numbers");
        carried.Message!.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "The original." },
            File("inner.zip", "application/zip"),
        };

        var found = Assert.Single(MessageAttachments.List(With(carried)));
        Assert.Equal("Q3 numbers.eml", found.Name);
    }

    // ---- Names are text a stranger wrote --------------------------------------------------

    [Theory]
    [InlineData("../../.bashrc", ".bashrc")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts", "hosts")]
    [InlineData("re\nport.pdf", "report.pdf")]
    [InlineData("..", "attachment")]
    [InlineData("../", "attachment")]
    [InlineData("   ", "attachment")]
    [InlineData("", "attachment")]
    public void ASuggestedNameCannotNameSomewhereElse(string name, string expected)
        => Assert.Equal(expected,
            new Attachment(name, "application/octet-stream", 1, new MimePart()).SafeName);

    [Fact]
    public void AnOrdinaryNameIsLeftAlone()
        => Assert.Equal("agenda.pdf",
            new Attachment("agenda.pdf", "application/pdf", 1, new MimePart()).SafeName);
}
