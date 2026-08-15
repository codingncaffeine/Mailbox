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
}
