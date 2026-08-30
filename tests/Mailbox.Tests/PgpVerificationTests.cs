using Mailbox.Security;
using Mailbox.Security.OpenPgp;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailbox.Tests;

/// <summary>
/// Checking an OpenPGP signature, and the four things a client must not get wrong —
/// the same four the S/MIME verifier answers, in the same words.
/// </summary>
public class PgpVerificationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mailbox-pgpsig-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void ASignatureFromTheSenderHolds()
    {
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = Signed(context, PgpKeys.Sender, from: PgpKeys.Sender.Address);
        var report = PgpVerification.Verify(message, context);

        Assert.True(report.State == SignatureState.Valid, report.Detail);
        Assert.True(report.Trustworthy);
        Assert.Equal(PgpKeys.Sender.Address, report.Signer);
        Assert.Empty(report.Detail);
    }

    [Fact]
    public void ASignerWhoIsNotTheSenderIsItsOwnState()
    {
        // The attack, and the reason this is neither Valid nor Invalid: the maths holds perfectly.
        // Somebody else signed a message claiming to be from A. Person, and a client that reports
        // that as "signed" has told the reader the impostor is the person it names.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender, PgpKeys.Other);

        var message = Signed(context, PgpKeys.Other, from: PgpKeys.Sender.Address);
        var report = PgpVerification.Verify(message, context);

        Assert.Equal(SignatureState.Mismatched, report.State);
        Assert.False(report.Trustworthy);
        Assert.Equal(PgpKeys.Other.Address, report.Signer);
        Assert.Contains(PgpKeys.Other.Address, report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageChangedAfterSigningDoesNotHold()
    {
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = Signed(context, PgpKeys.Sender, from: PgpKeys.Sender.Address);
        var signed = Assert.IsType<MultipartSigned>(message.Body);
        var body = Assert.IsType<TextPart>(signed[0]);
        body.Text += " ...and one word more.";

        var report = PgpVerification.Verify(PgpKeys.Reload(message), context);

        Assert.Equal(SignatureState.Invalid, report.State);
        Assert.NotEmpty(report.Detail);
    }

    [Fact]
    public void ASignatureWhoseKeyIsMissingIsNotAVerdict()
    {
        // Nothing was checked, so nothing is claimed. Reporting this as invalid would teach a
        // reader that an unknown correspondent is a suspicious one.
        using var signing = PgpKeys.Context(Path.Combine(_root, "theirs"), PgpKeys.Sender);
        var message = Signed(signing, PgpKeys.Sender, from: PgpKeys.Sender.Address);

        using var mine = PgpKeys.Context(Path.Combine(_root, "mine"), PgpKeys.Other);
        var report = PgpVerification.Verify(message, mine);

        Assert.Equal(SignatureState.Unknown, report.State);
        Assert.False(report.Trustworthy);
    }

    [Fact]
    public void ASignatureMadeAtATimeThatDisagreesWithTheMessageIsRefused()
    {
        // The creation time is the signer's own claim and RFC 5652 §11.3 gives it no
        // guarantee. Thunderbird believed it twice — CVE-2022-2226 and CVE-2023-50761.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = Signed(context, PgpKeys.Sender, from: PgpKeys.Sender.Address);
        message.Date = DateTimeOffset.UtcNow.AddDays(-30);

        var report = PgpVerification.Verify(PgpKeys.Reload(message), context);

        Assert.Equal(SignatureState.Invalid, report.State);
        Assert.Contains("disagrees with the time it was sent", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignedPartBuriedInsideIsNotThisMessageBeingSigned()
    {
        // CVE-2018-15587 and CVE-2017-17848, and the note that impersonated Phil Zimmermann with
        // his own real signature: a signed part the reader never sees says nothing about the
        // message wrapped round it.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var inner = Signed(context, PgpKeys.Sender, from: PgpKeys.Sender.Address).Body!;

        var message = new MimeMessage { Subject = "Not signed at all", Date = DateTimeOffset.UtcNow };
        message.From.Add(new MailboxAddress("A. Person", PgpKeys.Sender.Address));
        message.Body = new Multipart("related") { new TextPart("plain") { Text = "Hello." }, inner };

        var arrived = PgpKeys.Reload(message);

        Assert.False(PgpVerification.IsSigned(arrived));
        Assert.Equal(SignatureState.None, PgpVerification.Verify(arrived, context).State);
    }

    [Fact]
    public void AnSmimeSignedMessageIsNotAnOpenPgpOne()
    {
        // Both wear multipart/signed. The protocol parameter is what says whose it is, and handing
        // one verifier the other's message must not produce a verdict about it.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = Signed(context, PgpKeys.Sender, from: PgpKeys.Sender.Address);
        message.Body!.ContentType.Parameters["protocol"] = "application/pkcs7-signature";

        var arrived = PgpKeys.Reload(message);

        Assert.False(PgpVerification.IsSigned(arrived));
        Assert.Equal(SignatureState.None, PgpVerification.Verify(arrived, context).State);
    }

    [Fact]
    public void AMessageSignedInsideItsOwnEncryptionIsCheckedToo()
    {
        // The ordinary shape for OpenPGP, unlike S/MIME: one packet that signs and encrypts at
        // once. The signature is inside the ciphertext, so a client that only looks at MIME layers
        // reports nothing at all — which is most signed OpenPGP mail there is.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = SignedAndEncrypted(context, PgpKeys.Sender, PgpKeys.Sender);
        var report = PgpDecryption.Open(message, context, TestContext.Current.CancellationToken);

        Assert.True(report.State == DecryptionState.Opened, report.Detail);
        Assert.NotNull(report.Signature);
        Assert.True(report.Signature.State == SignatureState.Valid, report.Signature.Detail);
        Assert.Equal(PgpKeys.Sender.Address, report.Signature.Signer);
    }

    [Fact]
    public void ASignatureInsideAnEncryptedMessageIsBoundToItsSenderToo()
    {
        // Judged against the envelope's own From, which is the only claim about who sent it the
        // reader ever sees — never against the decrypted part's headers, which would let the
        // message vouch for itself.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender, PgpKeys.Other);

        var message = SignedAndEncrypted(context, signer: PgpKeys.Other, recipient: PgpKeys.Sender);
        var report = PgpDecryption.Open(message, context, TestContext.Current.CancellationToken);

        Assert.True(report.State == DecryptionState.Opened, report.Detail);
        Assert.NotNull(report.Signature);
        Assert.Equal(SignatureState.Mismatched, report.Signature.State);
    }

    /// <summary>An RFC 3156 detached signature over a body, as a client would send it.</summary>
    private static MimeMessage Signed(PgpContext context, PgpIdentity signer, string from)
    {
        var body = MultipartSigned.Create(
            context,
            signer.Signing,
            DigestAlgorithm.Sha256,
            new TextPart("plain") { Text = "The signed part.\n" },
            TestContext.Current.CancellationToken);

        // MimeKit dates its own signature by the real clock, so the message is dated by it too:
        // pinning the message to a fixed moment would fail the timing check by accident, and that
        // check has a test of its own that moves the date on purpose.
        var message = new MimeMessage { Subject = "Signed", Date = DateTimeOffset.Now, Body = body };
        message.From.Add(new MailboxAddress("A. Person", from));

        return PgpKeys.Reload(message);
    }

    /// <summary>
    /// One packet that signs and encrypts together — the one-pass form, whose signature never
    /// appears as a MIME layer at all.
    /// </summary>
    private static MimeMessage SignedAndEncrypted(
        PgpContext context, PgpIdentity signer, PgpIdentity recipient)
    {
        var body = MultipartEncrypted.SignAndEncrypt(
            context,
            signer.Signing,
            DigestAlgorithm.Sha256,
            [recipient.Encryption],
            new TextPart("plain") { Text = "Signed and sealed.\n" },
            TestContext.Current.CancellationToken);

        var message = new MimeMessage { Subject = "Sealed", Date = DateTimeOffset.Now, Body = body };
        message.From.Add(new MailboxAddress("A. Person", PgpKeys.Sender.Address));
        message.To.Add(new MailboxAddress("A. Person", recipient.Address));

        return PgpKeys.Reload(message);
    }

    /// <summary>
    /// A message signed by a key that had already run out when it was used. Trustworthy chain,
    /// sound maths, and still not a signature to report as one.
    /// </summary>
    /// <remarks>
    /// This verdict is receive-only and cannot be produced through the ordinary signing path:
    /// MimeKit refuses to sign with an expired key — the reason the harness door for this case
    /// documents it rather than manufacturing it. So the signature is built here at the
    /// BouncyCastle layer, over the same canonical bytes MimeKit re-verifies, with the expired
    /// secret key, which is exactly the shape a message from another client would carry.
    /// </remarks>
    [Fact]
    public void AnExpiredKeySignatureIsNotCalledSigned()
    {
        var expired = ExpiredIdentity("a.person@example.com");
        using var context = PgpKeys.PublicOnly(_root, expired);

        var message = ExpiredSigned(expired, from: "a.person@example.com");
        var report = PgpVerification.Verify(message, context);

        Assert.Equal(SignatureState.Invalid, report.State);
        Assert.Contains("had already expired", report.Detail);
    }

    /// <summary>A key made in the past with a short life, so it is expired by now.</summary>
    private static PgpIdentity ExpiredIdentity(string address)
    {
        var random = new Org.BouncyCastle.Security.SecureRandom();
        var rsa = new Org.BouncyCastle.Crypto.Generators.RsaKeyPairGenerator();
        rsa.Init(new Org.BouncyCastle.Crypto.Parameters.RsaKeyGenerationParameters(
            Org.BouncyCastle.Math.BigInteger.ValueOf(0x10001), random, 2048, 25));

        var made = DateTime.UtcNow.AddDays(-400);
        var pair = new Org.BouncyCastle.Bcpg.OpenPgp.PgpKeyPair(
            Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.RsaGeneral, rsa.GenerateKeyPair(), made);

        var packets = new Org.BouncyCastle.Bcpg.OpenPgp.PgpSignatureSubpacketGenerator();
        packets.SetKeyExpirationTime(false, (long)TimeSpan.FromDays(30).TotalSeconds);

        var rings = new Org.BouncyCastle.Bcpg.OpenPgp.PgpKeyRingGenerator(
            Org.BouncyCastle.Bcpg.OpenPgp.PgpSignature.PositiveCertification,
            pair,
            $"A. Person <{address}>",
            Org.BouncyCastle.Bcpg.SymmetricKeyAlgorithmTag.Aes256,
            Array.Empty<char>(),
            useSha1: true,
            hashedPackets: packets.Generate(),
            unhashedPackets: null,
            random);

        return new PgpIdentity(address, rings.GenerateSecretKeyRing(), rings.GeneratePublicKeyRing());
    }

    /// <summary>
    /// A multipart/signed whose detached signature is made by an expired key, over the exact
    /// canonical bytes MimeKit will re-verify.
    /// </summary>
    private static MimeMessage ExpiredSigned(PgpIdentity signer, string from)
    {
        var content = new TextPart("plain") { Text = "The signed part.\n" };

        // The bytes RFC 3156 signs: the part in canonical CRLF form, which is what MimeKit
        // re-derives when it verifies. Sign those with the expired secret key directly —
        // BouncyCastle does not refuse an expired key the way MimeKit's wrapper does.
        using var canonical = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        content.WriteTo(options, canonical);
        canonical.Position = 0;

        var secret = signer.Secret.GetSecretKey();
        var privateKey = secret.ExtractPrivateKey([]);

        var generator = new Org.BouncyCastle.Bcpg.OpenPgp.PgpSignatureGenerator(
            secret.PublicKey.Algorithm, Org.BouncyCastle.Bcpg.HashAlgorithmTag.Sha256);
        generator.InitSign(Org.BouncyCastle.Bcpg.OpenPgp.PgpSignature.CanonicalTextDocument, privateKey);

        int b;
        while ((b = canonical.ReadByte()) >= 0) generator.Update((byte)b);

        using var armored = new MemoryStream();
        using (var bcpg = new Org.BouncyCastle.Bcpg.BcpgOutputStream(
            new Org.BouncyCastle.Bcpg.ArmoredOutputStream(armored)))
        {
            generator.Generate().Encode(bcpg);
        }

        armored.Position = 0;
        var signaturePart = new MimePart("application", "pgp-signature")
        {
            Content = new MimeContent(new MemoryStream(armored.ToArray())),
        };

        var signed = new MultipartSigned();
        signed.ContentType.Parameters["protocol"] = "application/pgp-signature";
        signed.ContentType.Parameters["micalg"] = "pgp-sha256";
        signed.Add(content);
        signed.Add(signaturePart);

        var message = new MimeMessage { Subject = "Signed", Date = DateTimeOffset.Now, Body = signed };
        message.From.Add(new MailboxAddress("A. Person", from));

        return PgpKeys.Reload(message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
