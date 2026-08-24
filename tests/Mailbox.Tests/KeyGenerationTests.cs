using MimeKit;
using Mailbox.Security;
using Mailbox.Security.OpenPgp;

namespace Mailbox.Tests;

/// <summary>
/// Making a key here (§15). What matters is not that BouncyCastle can make one — it can — but
/// that what comes out is a key this application's own writer and reader agree about: listed as
/// ours, split into a signing primary and an encrypting subkey, locked with what the dialog
/// took, and able to carry a message all the way around through the real protection path.
/// </summary>
public class KeyGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-keygen-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private PgpContext Ring(string passphrase)
    {
        Directory.CreateDirectory(_root);
        return new PgpContext(_root, _ => passphrase);
    }

    [Fact]
    public void AMadeKeyIsListedAsOursAndUsable()
    {
        using var ring = Ring("open sesame");
        var entry = KeyGeneration.Make(
            ring, "N. Person", "n.person@example.org", "open sesame",
            TestContext.Current.CancellationToken);

        Assert.True(entry.HasSecret);
        Assert.True(entry.IsUsable(DateTimeOffset.Now));
        Assert.Contains("n.person@example.org", entry.Owner);

        // No expiry, which is the stated decision: there is no renewal surface yet, and a key
        // that quietly stops working in three years would be a worse failure than one that
        // lives until its owner replaces it.
        Assert.Null(entry.Expires);
    }

    [Fact]
    public void TheWorkIsSplitBetweenAPrimaryAndASubkey()
    {
        using var ring = Ring(string.Empty);
        var entry = KeyGeneration.Make(
            ring, "N. Person", "n.person@example.org", string.Empty,
            TestContext.Current.CancellationToken);

        var keys = ring.PublicRings()
            .Single(r => Convert.ToHexString(r.GetPublicKey().GetFingerprint())
                .Equals(entry.Fingerprint, StringComparison.OrdinalIgnoreCase))
            .GetPublicKeys()
            .ToList();

        // One certifying, signing primary and one encrypting subkey — one key doing both jobs
        // is what lets breaking the encryption key spend the signing identity with it.
        Assert.Equal(2, keys.Count);
        Assert.True(keys[0].IsMasterKey);
        Assert.False(keys[1].IsMasterKey);
        Assert.True(keys[1].IsEncryptionKey);
        Assert.NotEqual(keys[0].KeyId, keys[1].KeyId);
    }

    [Fact]
    public void TheSecretHalfOpensOnItsPassphraseAndRefusesAnother()
    {
        using var ring = Ring("right");
        var entry = KeyGeneration.Make(
            ring, "N. Person", "n.person@example.org", "right",
            TestContext.Current.CancellationToken);

        var secret = ring.SecretRings()
            .Single(r => Convert.ToHexString(r.GetPublicKey().GetFingerprint())
                .Equals(entry.Fingerprint, StringComparison.OrdinalIgnoreCase))
            .GetSecretKey();

        Assert.True(PassphraseVault.Opens(secret, "right"));
        Assert.False(PassphraseVault.Opens(secret, "wrong"));

        // An empty passphrase still encrypts the secret half, and such a key opens on empty —
        // the convention the vault and the seed already rely on.
        using var open = Ring(string.Empty);
        var unlocked = KeyGeneration.Make(
            open, "O. Person", "o.person@example.org", string.Empty,
            TestContext.Current.CancellationToken);
        var unprotected = open.SecretRings()
            .Single(r => Convert.ToHexString(r.GetPublicKey().GetFingerprint())
                .Equals(unlocked.Fingerprint, StringComparison.OrdinalIgnoreCase))
            .GetSecretKey();

        Assert.True(PassphraseVault.Opens(unprotected, string.Empty));
        Assert.False(PassphraseVault.Opens(unprotected, "anything"));
    }

    [Fact]
    public void AMadeKeyCarriesAMessageAllTheWayAround()
    {
        using var ring = Ring("open sesame");
        KeyGeneration.Make(
            ring, "N. Person", "n.person@example.org", "open sesame",
            TestContext.Current.CancellationToken);

        var who = new MailboxAddress("N. Person", "n.person@example.org");

        // Through the application's own writer — sign and encrypt, which needs the secret half
        // to open on the ring's answer and the subkey to be found for the address...
        var report = PgpProtection.Apply(
            new TextPart("plain") { Text = "A message to myself." },
            who, [who], Protection.Sign | Protection.Encrypt, ring,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.Applied, report.State);

        var message = new MimeMessage();
        message.From.Add(who);
        message.To.Add(who);
        message.Subject = "Round trip";
        message.Body = report.Body;

        // ...and back through the application's own reader, integrity check and all.
        var opened = PgpDecryption.Open(message, ring, TestContext.Current.CancellationToken);

        Assert.Equal(DecryptionState.Opened, opened.State);
        var text = Assert.IsType<TextPart>(opened.Content);
        Assert.Contains("A message to myself.", text.Text);
    }

    [Fact]
    public void AnAddressWithDisplayPartsIsRefused()
    {
        using var ring = Ring(string.Empty);

        Assert.Throws<ArgumentException>(() => KeyGeneration.Make(
            ring, "N. Person", "N. Person <n.person@example.org>", string.Empty,
            TestContext.Current.CancellationToken));
    }
}
