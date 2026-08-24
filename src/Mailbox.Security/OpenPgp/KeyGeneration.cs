using MimeKit;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Bcpg.Sig;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Mailbox.Security.OpenPgp;

/// <summary>
/// Makes a key pair here, so a reader with no keys anywhere does not need another tool before
/// the Trust Center's switch means anything. Import assumes a key exists somewhere else; this is
/// for the reader whose answer to "where is your key?" is "what key?".
/// </summary>
/// <remarks>
/// The shape is the conventional two-key layout rather than the fixture's single key: an RSA
/// 3072 primary that certifies and signs, and an RSA 3072 subkey that encrypts, each saying so
/// in its key flags — one key doing both jobs is what lets breaking the encryption key spend the
/// signing identity with it. AES-256 and the SHA-2 family are stated as preferences so a
/// correspondent's client picks them rather than its own idea of a default.
/// <para>
/// <b>No expiry is set, and that is a decision to state rather than an oversight:</b> an expiry
/// is renewal pressure, and there is no renewal surface yet — a key that quietly stops working
/// in three years, in a mail client with no "extend" button, is a worse failure than a key that
/// lives until its owner replaces it. Revisit when key management grows beyond list, import and
/// make.
/// </para>
/// <para>
/// The passphrase may be empty, and an empty one still encrypts the secret half — the same
/// convention the vault already knows: such a key opens on <see cref="string.Empty"/> and
/// refuses everything else. Generation is CPU work measured in seconds; callers run it off the
/// UI thread, which is why nothing here touches one.
/// </para>
/// </remarks>
public static class KeyGeneration
{
    /// <summary>
    /// Generates a pair for the named identity, imports both halves into the ring, and returns
    /// the inventory's entry for it.
    /// </summary>
    public static KeyEntry Make(
        PgpContext ring,
        string name,
        string address,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(passphrase);

        if (!MailboxAddress.TryParse(address, out var parsed) || parsed.Address != address)
        {
            throw new ArgumentException($"'{address}' is not a plain address.", nameof(address));
        }

        var random = new SecureRandom();

        // Two pairs, and a cancellation check between them: BouncyCastle cannot be interrupted
        // mid-generation, so between the halves is the one place a cancel can land.
        var signing = Rsa(random);
        cancellationToken.ThrowIfCancellationRequested();
        var encryption = Rsa(random);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        var primary = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, signing, now);
        var subkey = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, encryption, now);

        var primaryPackets = new PgpSignatureSubpacketGenerator();
        primaryPackets.SetKeyFlags(false, KeyFlags.CertifyOther | KeyFlags.SignData);
        primaryPackets.SetPreferredSymmetricAlgorithms(false,
        [
            (int)SymmetricKeyAlgorithmTag.Aes256,
            (int)SymmetricKeyAlgorithmTag.Aes192,
            (int)SymmetricKeyAlgorithmTag.Aes128,
        ]);
        primaryPackets.SetPreferredHashAlgorithms(false,
        [
            (int)HashAlgorithmTag.Sha256,
            (int)HashAlgorithmTag.Sha384,
            (int)HashAlgorithmTag.Sha512,
        ]);

        var subkeyPackets = new PgpSignatureSubpacketGenerator();
        subkeyPackets.SetKeyFlags(false, KeyFlags.EncryptComms | KeyFlags.EncryptStorage);

        var generator = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification,
            primary,
            $"{name} <{address}>",
            SymmetricKeyAlgorithmTag.Aes256,
            passphrase.ToCharArray(),
            useSha1: true,
            hashedPackets: primaryPackets.Generate(),
            unhashedPackets: null,
            rand: random);

        generator.AddSubKey(subkey, subkeyPackets.Generate(), unhashedPackets: null);

        var publicRing = generator.GeneratePublicKeyRing();
        var secretRing = generator.GenerateSecretKeyRing();

        ring.Import(publicRing, cancellationToken);
        ring.Import(secretRing, cancellationToken);

        var fingerprint = Convert.ToHexString(publicRing.GetPublicKey().GetFingerprint());

        return KeyInventory.Read(ring).FirstOrDefault(
                   k => k.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   "The key was made and imported but the ring does not list it.");
    }

    private static Org.BouncyCastle.Crypto.AsymmetricCipherKeyPair Rsa(SecureRandom random)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(
            BigInteger.ValueOf(0x10001), random, 3072, 25));
        return generator.GenerateKeyPair();
    }
}
