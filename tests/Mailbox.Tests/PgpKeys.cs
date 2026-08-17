using System.Text;
using Mailbox.Security.OpenPgp;
using MimeKit;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Mailbox.Tests;

/// <summary>
/// Keys made at test time, and the packets to try them on.
/// </summary>
/// <remarks>
/// Nothing here touches the machine's own keyring, and no key outlives the test that used it —
/// the same rule the S/MIME fixtures follow. Two identities are generated once and shared, because
/// generating an RSA key is the slowest thing in the suite by a wide margin and none of these tests
/// care which key they got, only whose it is.
/// </remarks>
internal static class PgpKeys
{
    /// <summary>What unlocks every secret key made here.</summary>
    public const string Passphrase = "not-a-real-passphrase";

    private static readonly Lazy<PgpIdentity> LazySender =
        new(() => Generate("A. Person", "a.person@example.com"), isThreadSafe: true);

    private static readonly Lazy<PgpIdentity> LazyOther =
        new(() => Generate("B. Other", "b.other@example.net"), isThreadSafe: true);

    private static readonly Lazy<PgpIdentity> LazyReader =
        new(() => Generate("You", "work@example.net", protect: false), isThreadSafe: true);

    /// <summary>The identity a message says it is from.</summary>
    public static PgpIdentity Sender => LazySender.Value;

    /// <summary>Somebody else — whose signature over the sender's message is the attack.</summary>
    public static PgpIdentity Other => LazyOther.Value;

    /// <summary>
    /// The reader's own, whose secret half is the only one this machine is supposed to hold.
    /// </summary>
    /// <remarks>
    /// The seed's default account, so a capture that poses nothing but the store lands on the
    /// folder this mail is in. Generated only when something asks for it — the seed does, the
    /// tests do not.
    /// </remarks>
    public static PgpIdentity Reader => LazyReader.Value;

    /// <summary>A context over a directory of its own, holding whichever identities are named.</summary>
    public static PgpContext Context(string directory, params PgpIdentity[] identities)
    {
        Directory.CreateDirectory(directory);
        var context = new PgpContext(directory, _ => Passphrase);

        foreach (var identity in identities)
        {
            context.Import(identity.Public, TestContext.Current.CancellationToken);
            context.Import(identity.Secret, TestContext.Current.CancellationToken);
        }

        return context;
    }

    /// <summary>A context holding only public keys — somebody whose mail arrives but cannot be opened.</summary>
    public static PgpContext PublicOnly(string directory, params PgpIdentity[] identities)
    {
        Directory.CreateDirectory(directory);
        var context = new PgpContext(directory, _ => Passphrase);

        foreach (var identity in identities)
        {
            context.Import(identity.Public, TestContext.Current.CancellationToken);
        }

        return context;
    }

    /// <summary>An ordinary message with a body long enough to corrupt a byte in the middle of.</summary>
    public static MimeEntity Content(string text = "The quiet part.")
        => new TextPart("plain") { Text = text + "\n" + new string('A', 4096) };

    /// <summary>
    /// An RFC 3156 encrypted message built by hand, so the integrity packet is ours to leave out.
    /// </summary>
    /// <param name="integrity">
    /// Whether the packet carries a modification detection code at all. False is the case §19 is
    /// about: BouncyCastle will decrypt it and MimeKit will hand back the plaintext without a word.
    /// </param>
    /// <param name="corrupt">
    /// A byte to flip in the ciphertext, or null to leave it alone. Everything is written with
    /// fixed packet lengths and no compression, so a byte in the middle lands in the encrypted body
    /// rather than in a length header — which is what makes "the integrity check failed" the
    /// outcome under test rather than "the packet would not parse".
    /// </param>
    public static MimeMessage Encrypted(
        PgpIdentity recipient, MimeEntity content, bool integrity = true, int? corrupt = null)
        => Message(Packet(recipient, content, integrity, corrupt));

    /// <summary>The armoured packet on its own, for a caller building its own envelope round it.</summary>
    public static string Packet(
        PgpIdentity recipient, MimeEntity content, bool integrity = true, int? corrupt = null)
    {
        var plaintext = Bytes(content);

        // The literal data packet, written at a known length.
        using var literal = new MemoryStream();
        var literals = new PgpLiteralDataGenerator();
        using (var stream = literals.Open(literal, PgpLiteralData.Binary, "", plaintext.Length, DateTime.UtcNow))
        {
            stream.Write(plaintext, 0, plaintext.Length);
        }

        var inner = literal.ToArray();

        // ...and the encrypted packet around it, again at a known length: no partial-length headers
        // interspersed through the ciphertext, so corrupting a middle byte corrupts data.
        using var binary = new MemoryStream();
        var encryptor = new PgpEncryptedDataGenerator(
            SymmetricKeyAlgorithmTag.Aes256, integrity, new SecureRandom());
        encryptor.AddMethod(recipient.Encryption);

        using (var stream = encryptor.Open(binary, inner.Length))
        {
            stream.Write(inner, 0, inner.Length);
        }

        var packet = binary.ToArray();
        if (corrupt is { } at) packet[at] ^= 0xFF;

        return Armour(packet);
    }

    /// <summary>The packet as a client would put it on the wire.</summary>
    private static string Armour(byte[] packet)
    {
        using var text = new MemoryStream();
        using (var armour = new ArmoredOutputStream(text))
        {
            armour.Write(packet, 0, packet.Length);
        }

        return Encoding.ASCII.GetString(text.ToArray());
    }

    /// <summary>
    /// The two-part shape RFC 3156 asks for, round-tripped through the parser.
    /// </summary>
    /// <remarks>
    /// Written out and read back rather than handed over as it was built, so what the tests act on
    /// is a message off the wire — which is the only kind there is.
    /// </remarks>
    private static MimeMessage Message(string armoured)
    {
        var message = new MimeMessage
        {
            Subject = "Encrypted",
            Date = DateTimeOffset.UtcNow,
            Body = EncryptedBody(armoured),
        };
        message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.To.Add(new MailboxAddress("A. Person", "a.person@example.com"));

        return Reload(message);
    }

    /// <summary>The two parts RFC 3156 asks for, round an armoured packet.</summary>
    public static MimeKit.Cryptography.MultipartEncrypted EncryptedBody(string armoured)
    {
        var encrypted = new MimeKit.Cryptography.MultipartEncrypted();
        encrypted.ContentType.Parameters["protocol"] = PgpDecryption.EncryptionProtocol;

        encrypted.Add(new MimePart("application", "pgp-encrypted")
        {
            Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes("Version: 1\n"))),
        });

        encrypted.Add(new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(armoured))),
        });

        return encrypted;
    }

    /// <summary>A message written out and parsed back, which is the only kind that arrives.</summary>
    public static MimeMessage Reload(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream, TestContext.Current.CancellationToken);
        stream.Position = 0;
        return MimeMessage.Load(stream, TestContext.Current.CancellationToken);
    }

    private static byte[] Bytes(MimeEntity entity)
    {
        using var stream = new MemoryStream();
        entity.WriteTo(stream, TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    /// <param name="protect">
    /// Whether the secret key carries a passphrase. The seed's reader does not, because nothing in
    /// the application can ask for one yet — the Trust Center has no prompt — so a protected key
    /// would make "could not unlock it" the only state a harness run could ever reach. The tests'
    /// own keys do carry one, which is what exercises the provider.
    /// </param>
    private static PgpIdentity Generate(string name, string address, bool protect = true)
    {
        var random = new SecureRandom();
        var rsa = new RsaKeyPairGenerator();
        rsa.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), random, 2048, 25));

        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, rsa.GenerateKeyPair(), DateTime.UtcNow);

        var rings = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification,
            pair,
            $"{name} <{address}>",
            SymmetricKeyAlgorithmTag.Aes256,
            protect ? Passphrase.ToCharArray() : [],
            useSha1: true,
            hashedPackets: null,
            unhashedPackets: null,
            random);

        return new PgpIdentity(address, rings.GenerateSecretKeyRing(), rings.GeneratePublicKeyRing());
    }
}

/// <summary>One key pair and the address its user ID names.</summary>
internal sealed record PgpIdentity(string Address, PgpSecretKeyRing Secret, PgpPublicKeyRing Public)
{
    /// <summary>The key a message to this identity is encrypted to.</summary>
    public PgpPublicKey Encryption => Public.GetPublicKey();

    /// <summary>The key this identity signs with.</summary>
    public PgpSecretKey Signing => Secret.GetSecretKey();

    public MailboxAddress Mailbox => new(string.Empty, Address);
}
