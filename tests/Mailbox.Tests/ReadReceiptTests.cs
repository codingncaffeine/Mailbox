using Mailbox.Core.Settings;
using Mailbox.Protocols;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// The answer to a read-receipt request: who it goes to and what the wire carries.
/// </summary>
public class ReadReceiptTests
{
    private static MimeMessage Original(string? notifyTo = "nosy@sender.example")
    {
        var message = new MimeMessage
        {
            Subject = "Did you read this yet",
            MessageId = "receipt-test-1@sender.example",
            Body = new TextPart("plain") { Text = "Please confirm." },
        };
        message.From.Add(new MailboxAddress("Nosy Sender", "nosy@sender.example"));
        message.To.Add(new MailboxAddress(string.Empty, "you@example.com"));
        if (notifyTo is not null) message.Headers.Add("Disposition-Notification-To", notifyTo);
        return message;
    }

    [Fact]
    public void AMessageThatNeverAskedGetsNoReceipt()
    {
        Assert.Empty(ReadReceipt.RequestedBy(Original(notifyTo: null)));
        Assert.Null(ReadReceipt.Build(Original(notifyTo: null),
            new MailboxAddress(string.Empty, "you@example.com"), DateTimeOffset.Now));
    }

    [Fact]
    public void TheReceiptIsAnRfc8098DispositionNotification()
    {
        var receipt = ReadReceipt.Build(Original(),
            new MailboxAddress("A. Person", "you@example.com"), DateTimeOffset.Now);

        Assert.NotNull(receipt);
        Assert.Equal("Read: Did you read this yet", receipt!.Subject);
        Assert.Equal("nosy@sender.example", receipt.To.Mailboxes.Single().Address);
        Assert.Equal("you@example.com", receipt.From.Mailboxes.Single().Address);

        var report = Assert.IsType<MultipartReport>(receipt.Body);
        Assert.Equal("disposition-notification", report.ReportType);

        var disposition = report.OfType<MessageDispositionNotification>().Single();
        Assert.Equal("rfc822;you@example.com", disposition.Fields["Final-Recipient"]);
        Assert.Equal("<receipt-test-1@sender.example>", disposition.Fields["Original-Message-ID"]);
        Assert.Equal("manual-action/MDN-sent-manually; displayed", disposition.Fields["Disposition"]);

        // The other half is for a person: a sentence, not just machine fields.
        Assert.Contains("displayed", report.OfType<TextPart>().Single().Text);
    }

    [Fact]
    public void TheRequestHeaderIsParsedAsAddresses()
    {
        var asked = ReadReceipt.RequestedBy(Original("One <one@example.net>, two@example.net"));
        Assert.Equal(["one@example.net", "two@example.net"], asked.Select(a => a.Address));
    }

    /// <summary>The Tracking radios persist their label; the accessor names what each means.</summary>
    [Theory]
    [InlineData("Always send a read receipt", ReadReceiptAnswer.Always)]
    [InlineData("Never send a read receipt", ReadReceiptAnswer.Never)]
    [InlineData("Ask each time whether to send a read receipt", ReadReceiptAnswer.Ask)]
    public void TheTrackingRadiosMapToTheirMeanings(string stored, ReadReceiptAnswer meant)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailbox-receipt-{Guid.NewGuid():n}.json");
        try
        {
            var settings = new SettingsStore(path);
            settings.Set(MailOptions.ReadReceiptAnswerKey, stored);
            Assert.Equal(meant, new MailOptions(settings).ReadReceiptAnswer);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFreshInstallAsksEachTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailbox-receipt-{Guid.NewGuid():n}.json");
        try
        {
            Assert.Equal(ReadReceiptAnswer.Ask, new MailOptions(new SettingsStore(path)).ReadReceiptAnswer);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
