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
/// Certificates made at test time, and the two ways of putting one into a context.
/// </summary>
/// <remarks>
/// Nothing here touches the machine's own certificate store, and nothing outlives the test that used
/// it — the same rule <see cref="PgpKeys"/> follows.
/// <para>
/// <b>Which import matters.</b> <see cref="Trust"/> files the public half, which is what a
/// certificate arriving in a message amounts to: enough to check a signature, enough to encrypt
/// <i>to</i> somebody, and never enough to sign or decrypt as them. <see cref="Hold"/> puts the
/// private key in as well, through PKCS#12 because that is the one import every context has. Using
/// the first where the second was needed is a whole afternoon: the message encrypts perfectly and
/// then will not open, and nothing anywhere says why.
/// </para>
/// </remarks>
internal static class SmimeKeys
{
    /// <summary>What the PKCS#12 stream is sealed with on the way in. It never leaves this file.</summary>
    private const string Password = "test";

    /// <summary>A self-signed certificate for one address, and the key that goes with it.</summary>
    /// <param name="emailProtection">
    /// Whether the certificate says it is for mail at all (EKU <c>id-kp-emailProtection</c>). The one
    /// difference between a certificate for mail and one for something else, and §19 asks for it to
    /// be checked.
    /// </param>
    public static SmimeIdentity Generate(string address, bool emailProtection = true)
    {
        var random = new SecureRandom();
        var keys = new RsaKeyPairGenerator();
        keys.Init(new KeyGenerationParameters(random, 2048));
        var pair = keys.GenerateKeyPair();

        var name = new X509Name($"CN=Test, E={address}");
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigInteger.ProbablePrime(64, random));
        generator.SetIssuerDN(name);
        generator.SetSubjectDN(name);
        generator.SetNotBefore(DateTimeOffset.Now.AddYears(-1).UtcDateTime);
        generator.SetNotAfter(DateTimeOffset.Now.AddYears(5).UtcDateTime);
        generator.SetPublicKey(pair.Public);

        // The address as a SAN rfc822Name, which is the only place §19 will read one from: never
        // the CN, never the Subject DN alone (RFC 8550 §3).
        generator.AddExtension(
            X509Extensions.SubjectAlternativeName, false,
            new GeneralNames(new GeneralName(GeneralName.Rfc822Name, address)));

        // KeyCertSign as well, because a self-signed certificate is its own root: the context trusts
        // an imported certificate as an anchor only if it is allowed to sign one. Without it the
        // chain build throws from inside BouncyCastle about a "non-empty set required", which reads
        // like a library bug and is a fixture one.
        generator.AddExtension(
            X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment | KeyUsage.KeyCertSign));

        generator.AddExtension(
            X509Extensions.ExtendedKeyUsage, false,
            new ExtendedKeyUsage(emailProtection ? KeyPurposeID.id_kp_emailProtection : KeyPurposeID.id_kp_codeSigning));

        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));

        var certificate = generator.Generate(new Asn1SignatureFactory("SHA256WithRSA", pair.Private, random));
        return new SmimeIdentity(address, certificate, pair.Private);
    }

    /// <summary>Files the public half, and trusts it: a self-signed certificate is its own chain.</summary>
    public static SmimeIdentity Trust(this SmimeIdentity identity, SecureMimeContext context)
    {
        context.Import(identity.Certificate, TestContext.Current.CancellationToken);
        return identity;
    }

    /// <summary>Files the certificate <i>and</i> its private key, which is what signing needs.</summary>
    public static SmimeIdentity Hold(this SmimeIdentity identity, SecureMimeContext context)
    {
        var random = new SecureRandom();
        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry(
            identity.Address,
            new AsymmetricKeyEntry(identity.Key),
            [new X509CertificateEntry(identity.Certificate)]);

        using var stream = new MemoryStream();
        store.Save(stream, Password.ToCharArray(), random);
        stream.Position = 0;
        context.Import(stream, Password, TestContext.Current.CancellationToken);

        return identity;
    }
}

/// <summary>One certificate and the address its subject alternative name carries.</summary>
internal sealed record SmimeIdentity(string Address, X509Certificate Certificate, AsymmetricKeyParameter Key)
{
    /// <summary>The signer a caller passes when it holds the key itself rather than the store.</summary>
    public CmsSigner Signer => new(Certificate, Key);

    public MailboxAddress Mailbox => new("Test", Address);
}
