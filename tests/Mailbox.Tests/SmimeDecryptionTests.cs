using Mailbox.Security;
using Mailbox.Security.Smime;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Mailbox.Tests;

/// <summary>
/// Opening an encrypted message — and §19's second blocker, which is not about the cryptography
/// at all: what comes out is rendered on its own, never spliced into the message it arrived in.
/// </summary>
public class SmimeDecryptionTests
{
    [Fact]
    public void AnEncryptedMessageOpensToWhatWasInside()
    {
        using var context = new TemporarySecureMimeContext();
        var recipient = Import(context, "you@example.com");

        var message = Encrypted(context, recipient, "The quiet part.");
        var report = SmimeDecryption.Open(message, context);

        Assert.True(report.State == DecryptionState.Opened, report.Detail);
        Assert.True(report.Opened);
        var text = Assert.IsType<TextPart>(report.Content);
        Assert.Equal("The quiet part.", text.Text);
    }

    [Fact]
    public void AMessageEncryptedToSomebodyElseStaysShut()
    {
        // Not an error to report loudly: mail encrypted to a key this computer has not got is
        // ordinary, and the reader is told that rather than shown a failure.
        using var theirs = new TemporarySecureMimeContext();
        var recipient = Import(theirs, "someone@example.net");
        var message = Encrypted(theirs, recipient, "Not for you.");

        using var mine = new TemporarySecureMimeContext();
        var report = SmimeDecryption.Open(message, mine);

        Assert.Equal(DecryptionState.Locked, report.State);
        Assert.Null(report.Content);
        Assert.NotEmpty(report.Detail);
    }

    [Fact]
    public void AnOrdinaryMessageIsNotEncrypted()
    {
        using var context = new TemporarySecureMimeContext();

        var message = new MimeMessage { Subject = "Ordinary" };
        message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.Body = new TextPart("plain") { Text = "Hello." };

        Assert.False(SmimeDecryption.IsEncrypted(message));
        Assert.Equal(DecryptionState.None, SmimeDecryption.Open(message, context).State);
    }

    [Fact]
    public void AnEncryptedPartBuriedInsideIsNotThisMessageBeingEncrypted()
    {
        // The same rule as the signature's, for the same reason: a part the reader never sees is
        // not a claim about the message. Reporting it as encrypted lends the envelope a padlock
        // it has not earned, and rendering it would put attacker-chosen markup where the
        // plaintext goes.
        using var context = new TemporarySecureMimeContext();
        var recipient = Import(context, "you@example.com");
        var inner = ApplicationPkcs7Mime.Encrypt(
            context, [recipient], new TextPart("plain") { Text = "Inside." }, TestContext.Current.CancellationToken);

        var message = new MimeMessage { Subject = "Not encrypted at all" };
        message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.Body = new Multipart("related") { new TextPart("plain") { Text = "Hello." }, inner };

        Assert.False(SmimeDecryption.IsEncrypted(message));
        Assert.Equal(DecryptionState.None, SmimeDecryption.Open(message, context).State);
    }

    // ---- The material ---------------------------------------------------------------------------

    private static MimeMessage Encrypted(SecureMimeContext context, MailboxAddress recipient, string body)
    {
        var message = new MimeMessage { Subject = "Encrypted", Date = DateTimeOffset.Now };
        message.From.Add(new MailboxAddress("Whoever", "whoever@example.com"));
        message.To.Add(recipient);
        message.Body = ApplicationPkcs7Mime.Encrypt(
            context, [recipient], new TextPart("plain") { Text = body }, TestContext.Current.CancellationToken);
        return message;
    }

    /// <summary>
    /// A self-signed certificate and its private key, in a store the context can decrypt with.
    /// </summary>
    /// <remarks>
    /// Through PKCS#12 because that is the one import every context has: the key has to go in, and
    /// a public certificate on its own opens nothing.
    /// </remarks>
    private static MailboxAddress Import(SecureMimeContext context, string address)
        => SmimeKeys.Generate(address).Hold(context).Mailbox;
}
