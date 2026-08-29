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
/// The certificate store the application actually opens, rather than a temporary one.
/// </summary>
/// <remarks>
/// <b>Why this file exists.</b> Every other S/MIME test in this tree builds a
/// <c>TemporarySecureMimeContext</c>, and a temporary context answers a different question from the
/// one that matters: it has no database under it, so it never noticed that the store on disk could
/// not be constructed at all. Signing, encrypting, verifying and decrypting each threw before they
/// began, in a green suite. These tests hold the file-backed store to the same claims, and they are
/// the reason the class can be believed.
/// <para>
/// The chain states are the second half. A verdict is only worth anything if a good chain, an
/// expired one and one nobody has vouched for come out differently, so all three are built here
/// from certificates made at test time — nothing touches the machine's own store, and no key
/// material outlives the test.
/// </para>
/// </remarks>
public class CertificateStoreTests
{
    // ---- The store itself ---------------------------------------------------------------------

    [Fact]
    public void TheStoreOpensAndPutsItsFileWhereItWasAsked()
    {
        using var home = new TemporaryDirectory();

        using (var context = CertificateStore.Open(home.Path))
        {
            Assert.IsType<DefaultSecureMimeContext>(context);
        }

        Assert.True(File.Exists(Path.Combine(home.Path, CertificateStore.FileName)));
    }

    [Fact]
    public void AnImportedCertificateIsStillThereOnTheNextOpen()
    {
        using var home = new TemporaryDirectory();
        var root = Chain.Root("Mailbox Test Root");
        var person = Chain.Leaf("a.person@example.com", root);

        using (var context = CertificateStore.Open(home.Path))
        {
            Chain.Trust(context, root);
            Chain.Hold(context, person);
        }

        using var reopened = CertificateStore.Open(home.Path);
        Assert.True(reopened.CanSign(person.Mailbox, TestContext.Current.CancellationToken));
        Assert.True(reopened.CanEncrypt(person.Mailbox, TestContext.Current.CancellationToken));
    }

    // ---- Sealing, through that store -----------------------------------------------------------

    [Fact]
    public void AMessageSignedThroughTheStoreVerifiesAsItsSender()
    {
        using var home = new TemporaryDirectory();
        using var context = CertificateStore.Open(home.Path);

        var root = Chain.Root("Mailbox Test Root");
        var person = Chain.Leaf("a.person@example.com", root);
        Chain.Trust(context, root);
        Chain.Hold(context, person);

        var message = Chain.Message("a.person@example.com");
        message.Body = MultipartSigned.Create(
            context, person.Mailbox, DigestAlgorithm.Sha256, message.Body!,
            TestContext.Current.CancellationToken);

        var report = SmimeVerification.Verify(message, context);

        Assert.Equal(SignatureState.Valid, report.State);
        Assert.Equal("a.person@example.com", report.Signer);
    }

    [Fact]
    public void AMessageEncryptedThroughTheStoreOpensAgain()
    {
        using var home = new TemporaryDirectory();
        using var context = CertificateStore.Open(home.Path);

        var root = Chain.Root("Mailbox Test Root");
        var person = Chain.Leaf("a.person@example.com", root);
        Chain.Trust(context, root);
        Chain.Hold(context, person);

        var message = Chain.Message("a.person@example.com");
        message.Body = ApplicationPkcs7Mime.Encrypt(
            context, [person.Mailbox], message.Body!, TestContext.Current.CancellationToken);

        Assert.Equal("application/pkcs7-mime", message.Body.ContentType.MimeType);

        var report = SmimeDecryption.Open(message, context);

        Assert.Equal(DecryptionState.Opened, report.State);
        Assert.Equal("The quarterly figures are attached.", ((TextPart)report.Content!).Text);
    }

    // ---- The three chain states ---------------------------------------------------------------

    [Fact]
    public void AGoodChainIsCalledSigned()
    {
        var report = Verdict(rootIsTrusted: true, days: (from: -30, to: 365));

        Assert.Equal(SignatureState.Valid, report.State);
        Assert.Equal(string.Empty, report.Detail);
    }

    /// <summary>
    /// A certificate whose own root nobody here has vouched for. The signature's arithmetic is
    /// perfect; there is simply nothing behind it, and the reader is told that rather than being
    /// told it is signed.
    /// </summary>
    [Fact]
    public void AnUntrustedRootIsNotCalledSigned()
    {
        var report = Verdict(rootIsTrusted: false, days: (from: -30, to: 365));

        Assert.NotEqual(SignatureState.Valid, report.State);
        Assert.Equal("The certificate that signed this message is not trusted here.", report.Detail);
    }

    /// <summary>
    /// A certificate that had already run out when the message was signed. Trusted root, sound
    /// arithmetic, and still not a signature worth reporting as one.
    /// </summary>
    [Fact]
    public void AnExpiredCertificateIsNotCalledSigned()
    {
        var report = Verdict(rootIsTrusted: true, days: (from: -800, to: -400));

        Assert.NotEqual(SignatureState.Valid, report.State);
        Assert.NotEqual(string.Empty, report.Detail);
    }

    /// <summary>One message signed under one chain, and what the application makes of it.</summary>
    private static SignatureReport Verdict(bool rootIsTrusted, (int from, int to) days)
    {
        using var home = new TemporaryDirectory();
        using var context = CertificateStore.Open(home.Path);

        var root = Chain.Root("Mailbox Test Root");
        var person = Chain.Leaf("a.person@example.com", root, days.from, days.to);

        if (rootIsTrusted) Chain.Trust(context, root);

        // The sender signs through a context of their own: the message arrives from outside, and
        // the overload that takes no context builds MimeKit's default one, whose SQLite check is
        // the very thing CertificateStore exists to route around.
        using var sender = new TemporarySecureMimeContext();

        var message = Chain.Message("a.person@example.com");
        message.Body = MultipartSigned.Create(
            sender, new CmsSigner(person.Certificate, person.Key), message.Body!,
            TestContext.Current.CancellationToken);

        return SmimeVerification.Verify(message, context);
    }
}

/// <summary>A directory that goes away with the test that made it.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mailbox-certstore-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a store still holding the file on a slow machine */ }
    }
}

/// <summary>
/// A little certificate authority, so a chain can be good, expired or unvouched-for on demand.
/// </summary>
/// <remarks>
/// Separate from <see cref="SmimeKeys"/>, which makes self-signed certificates that are their own
/// anchor. That shape is enough for a temporary context and not enough here: the store on disk will
/// not sign with a certificate that is a certificate authority, so a real chain — a root and a leaf
/// beneath it — is the only way to reach the paths the application takes.
/// </remarks>
internal static class Chain
{
    private const string Password = "test";

    internal sealed record Identity(string Address, X509Certificate Certificate, AsymmetricKeyParameter Key)
    {
        public MailboxAddress Mailbox => new("A. Person", Address);
    }

    /// <summary>A root certificate authority, valid for long enough not to be the variable.</summary>
    public static Identity Root(string name)
    {
        var random = new SecureRandom();
        var pair = KeyPair(random);
        var dn = new X509Name($"CN={name}");

        var generator = Generator(random, dn, dn, pair.Public, -1000, 3000);
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        generator.AddExtension(
            X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));

        return new Identity(
            string.Empty,
            generator.Generate(new Asn1SignatureFactory("SHA256WithRSA", pair.Private, random)),
            pair.Private);
    }

    /// <summary>One person's certificate, issued by a root.</summary>
    public static Identity Leaf(string address, Identity issuer, int notBefore = -30, int notAfter = 365)
    {
        var random = new SecureRandom();
        var pair = KeyPair(random);

        var generator = Generator(
            random, issuer.Certificate.SubjectDN, new X509Name($"CN=A. Person, E={address}"),
            pair.Public, notBefore, notAfter);

        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
        generator.AddExtension(
            X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment | KeyUsage.NonRepudiation));

        // §19 reads the address from the subject alternative name and from nowhere else.
        generator.AddExtension(
            X509Extensions.SubjectAlternativeName, false,
            new GeneralNames(new GeneralName(GeneralName.Rfc822Name, address)));

        generator.AddExtension(
            X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(KeyPurposeID.id_kp_emailProtection));

        return new Identity(
            address,
            generator.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Key, random)),
            pair.Private);
    }

    /// <summary>Files a root and says it may be believed — which is a decision, never a default.</summary>
    public static void Trust(SecureMimeContext context, Identity root)
        => ((DefaultSecureMimeContext)context).Import(
            root.Certificate, trusted: true, TestContext.Current.CancellationToken);

    /// <summary>Files a certificate and its private key, which is what signing and decrypting need.</summary>
    public static void Hold(SecureMimeContext context, Identity identity)
    {
        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry(
            identity.Address,
            new AsymmetricKeyEntry(identity.Key),
            [new X509CertificateEntry(identity.Certificate)]);

        using var stream = new MemoryStream();
        store.Save(stream, Password.ToCharArray(), new SecureRandom());
        stream.Position = 0;
        context.Import(stream, Password, TestContext.Current.CancellationToken);
    }

    /// <summary>An invented message with a body worth reading back.</summary>
    public static MimeMessage Message(string from)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("A. Person", from));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.Subject = "The quarterly figures";
        message.Date = DateTimeOffset.Now;
        message.Body = new TextPart("plain") { Text = "The quarterly figures are attached." };
        return message;
    }

    private static AsymmetricCipherKeyPair KeyPair(SecureRandom random)
    {
        var keys = new RsaKeyPairGenerator();
        keys.Init(new KeyGenerationParameters(random, 2048));
        return keys.GenerateKeyPair();
    }

    private static X509V3CertificateGenerator Generator(
        SecureRandom random, X509Name issuer, X509Name subject,
        AsymmetricKeyParameter publicKey, int notBefore, int notAfter)
    {
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigInteger.ProbablePrime(64, random));
        generator.SetIssuerDN(issuer);
        generator.SetSubjectDN(subject);
        generator.SetNotBefore(DateTime.UtcNow.AddDays(notBefore));
        generator.SetNotAfter(DateTime.UtcNow.AddDays(notAfter));
        generator.SetPublicKey(publicKey);
        return generator;
    }
}
