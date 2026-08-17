using Mailbox.Security;
using Mailbox.Security.Smime;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;

namespace Mailbox.Tests;

/// <summary>
/// Checking an S/MIME signature — and the five things §19 says a client must not get wrong,
/// each of which is a bug that shipped somewhere real.
/// </summary>
/// <remarks>
/// The certificates are made here rather than checked in: a test that needs a key needs a key
/// nobody else has, and one that expires is a test that fails in a year for no reason.
/// </remarks>
public class SmimeVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASignatureFromTheSenderHolds()
    {
        using var context = new TemporarySecureMimeContext();
        var signer = Import(context, "a.person@example.com");

        var message = Signed(context, signer, from: "a.person@example.com");
        var report = SmimeVerification.Verify(message, context);

        Assert.True(report.State == SignatureState.Valid, $"{report.State}: {report.Detail}");
        Assert.Empty(report.Detail);
        Assert.True(report.Trustworthy);
        Assert.Equal("a.person@example.com", report.Signer);
    }

    [Fact]
    public void ASignatureFromSomebodyElseIsItsOwnState()
    {
        // The whole of the attack: the maths holds, the certificate is real, and the person it
        // belongs to is not the person the message says sent it. Folded into "valid" it tells the
        // reader a lie; folded into "invalid" it teaches them to ignore the word.
        using var context = new TemporarySecureMimeContext();
        var signer = Import(context, "somebody.else@example.net");

        var message = Signed(context, signer, from: "a.person@example.com");
        var report = SmimeVerification.Verify(message, context);

        Assert.Equal(SignatureState.Mismatched, report.State);
        Assert.False(report.Trustworthy);
        Assert.Contains("somebody.else@example.net", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ACertificateThatIsNotForEmailDoesNotSignMail()
    {
        using var context = new TemporarySecureMimeContext();
        var signer = Import(context, "a.person@example.com", emailProtection: false);

        var message = Signed(context, signer, from: "a.person@example.com");
        var report = SmimeVerification.Verify(message, context);

        Assert.Equal(SignatureState.Mismatched, report.State);
        Assert.Contains("not a certificate for e-mail", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageChangedAfterItWasSignedDoesNotHold()
    {
        using var context = new TemporarySecureMimeContext();
        var signer = Import(context, "a.person@example.com");

        var message = Signed(context, signer, from: "a.person@example.com");
        var signed = Assert.IsType<MultipartSigned>(message.Body);
        signed[0] = new TextPart("plain") { Text = "Something else entirely." };

        var report = SmimeVerification.Verify(message, context);

        // Either way it does not hold: the library returns false for some kinds of tampering and
        // throws for others, and a reader is told the same thing by both.
        Assert.Equal(SignatureState.Invalid, report.State);
        Assert.False(report.Trustworthy);
        Assert.NotEmpty(report.Detail);
    }

    [Fact]
    public void ASignedPartBuriedInsideIsNotThisMessageBeingSigned()
    {
        // CVE-2018-15587 and CVE-2017-17848, and the note that impersonated Phil Zimmermann: a
        // signed part the reader never sees, reported as though the message were signed.
        using var context = new TemporarySecureMimeContext();
        var signer = Import(context, "a.person@example.com");
        var inner = MultipartSigned.Create(context, signer, new TextPart("plain") { Text = "Trust me." }, TestContext.Current.CancellationToken);

        var message = new MimeMessage { Subject = "Not signed at all", Date = Now };
        message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.Body = new Multipart("related") { new TextPart("plain") { Text = "Hello." }, inner };

        Assert.False(SmimeVerification.IsSigned(message));
        Assert.Equal(SignatureState.None, SmimeVerification.Verify(message, context).State);
    }

    [Fact]
    public void ASigningTimeThatDisagreesWithTheMessageIsRefused()
    {
        // RFC 5652 §11.3: the value carries no guarantee, and MimeKit builds the chain as of it.
        // Thunderbird had this twice — CVE-2022-2226 and CVE-2023-50761.
        using var context = new TemporarySecureMimeContext();
        var signer = Import(context, "a.person@example.com");

        var message = Signed(context, signer, from: "a.person@example.com");
        message.Date = DateTimeOffset.Now.AddYears(-3);

        var report = SmimeVerification.Verify(message, context);

        Assert.Equal(SignatureState.Invalid, report.State);
        Assert.Contains("disagrees with the time it was sent", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageWithNoSignatureSaysSoAndNothingMore()
    {
        using var context = new TemporarySecureMimeContext();

        var message = new MimeMessage { Subject = "Ordinary", Date = Now };
        message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.Body = new TextPart("plain") { Text = "Hello." };

        var report = SmimeVerification.Verify(message, context);

        Assert.Equal(SignatureState.None, report.State);
        Assert.Equal(string.Empty, report.Detail);
        Assert.False(report.Trustworthy);
    }

    // ---- The material ---------------------------------------------------------------------------

    /// <remarks>
    /// Dated now rather than at the pinned moment: the library signs with the real clock, and a
    /// fixture whose message is a day older than its own signature is testing the signing-time
    /// check by accident. That check has a test of its own.
    /// </remarks>
    private static MimeMessage Signed(SecureMimeContext context, CmsSigner signer, string from)
    {
        var message = new MimeMessage { Subject = "Signed", Date = DateTimeOffset.Now };
        message.From.Add(new MailboxAddress("Whoever", from));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.Body = MultipartSigned.Create(context, signer, new TextPart("plain") { Text = "Hello." }, TestContext.Current.CancellationToken);
        return message;
    }

    /// <summary>A self-signed certificate for one address, trusted by the temporary context.</summary>
    /// <remarks>
    /// The public half only, which is all a verifier ever has: what arrives in a message is a
    /// certificate, never a key.
    /// </remarks>
    private static CmsSigner Import(TemporarySecureMimeContext context, string address, bool emailProtection = true)
        => SmimeKeys.Generate(address, emailProtection).Trust(context).Signer;
}
