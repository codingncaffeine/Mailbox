using System.Globalization;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Mailbox.Security.OpenPgp;

/// <summary>One key as the Trust Center lists it.</summary>
/// <remarks>
/// Flattened out of BouncyCastle's rings on purpose: what a reader is being shown is "which keys
/// are here and are they any good", and answering that from live key objects would mean the page
/// held the key material open for as long as it was on screen.
/// </remarks>
public sealed record KeyEntry(
    string Fingerprint,
    IReadOnlyList<string> UserIds,
    DateTimeOffset Created,
    DateTimeOffset? Expires,
    bool HasSecret,
    bool IsRevoked,
    string Algorithm,
    int Bits)
{
    /// <summary>The last eight of the fingerprint, which is how a key is named in conversation.</summary>
    public string ShortId => Fingerprint.Length >= 8 ? Fingerprint[^8..] : Fingerprint;

    /// <summary>The fingerprint in fours, as every other tool prints it.</summary>
    public string PrettyFingerprint => string.Join(
        ' ', Enumerable.Range(0, (Fingerprint.Length + 3) / 4)
            .Select(i => Fingerprint.Substring(i * 4, Math.Min(4, Fingerprint.Length - (i * 4)))));

    /// <summary>Whoever the key says it belongs to, or its short id when it says nothing.</summary>
    public string Owner => UserIds.Count > 0 ? UserIds[0] : ShortId;

    public bool IsExpired(DateTimeOffset now) => Expires is { } when && when <= now;

    /// <summary>
    /// True when this key would be used. A revoked or expired key is listed rather than hidden —
    /// a reader wondering why a message will not encrypt is owed the reason, and the reason is
    /// usually sitting in this list.
    /// </summary>
    public bool IsUsable(DateTimeOffset now) => !IsRevoked && !IsExpired(now);

    /// <summary>What the page says about a key in one line.</summary>
    public string State(DateTimeOffset now) => IsRevoked
        ? "revoked"
        : IsExpired(now)
            ? $"expired {Expires!.Value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}"
            : Expires is { } when
                ? $"expires {when.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}"
                : "no expiry";
}

/// <summary>
/// What is in the keyring, for a reader who wants to know.
/// </summary>
/// <remarks>
/// Until this existed the ring was whatever a seeded store or another tool had put there, and
/// nothing in the application would say so — a message that would not encrypt gave the same
/// answer whether the correspondent's key was missing, expired or revoked. Reading it is not a
/// cryptographic operation and needs no passphrase: a secret key's presence is a fact about the
/// ring, and opening it is not.
/// </remarks>
public static class KeyInventory
{
    /// <summary>Every key in the ring, public and secret, newest last.</summary>
    public static IReadOnlyList<KeyEntry> Read(PgpContext ring)
    {
        ArgumentNullException.ThrowIfNull(ring);

        // Which fingerprints the ring holds a secret half for. Asked once rather than per key,
        // and by fingerprint rather than by key id: a long key id is 64 bits and collisions in
        // them have been demonstrated, which is the whole reason fingerprints are what tools
        // print.
        var secret = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in ring.SecretRings())
        {
            foreach (PgpSecretKey key in bundle.GetSecretKeys())
            {
                secret.Add(Hex(key.PublicKey.GetFingerprint()));
            }
        }

        var entries = new List<KeyEntry>();

        foreach (var bundle in ring.PublicRings())
        {
            // The master key is the identity; sub-keys are how it does its work, and listing them
            // separately would show one person as four keys.
            if (bundle.GetPublicKey() is not { } master) continue;

            var fingerprint = Hex(master.GetFingerprint());

            entries.Add(new KeyEntry(
                fingerprint,
                [.. master.GetUserIds().OfType<string>()],
                DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(master.CreationTime).ToUnixTimeSeconds()),
                master.GetValidSeconds() > 0
                    ? new DateTimeOffset(master.CreationTime).AddSeconds(master.GetValidSeconds())
                    : null,
                secret.Contains(fingerprint),
                master.IsRevoked(),
                Name(master.Algorithm),
                master.BitStrength));
        }

        return entries;
    }

    /// <summary>Whichever entry holds the secret half for an address, for "this is you".</summary>
    public static KeyEntry? Own(IReadOnlyList<KeyEntry> keys, string address)
        => keys.FirstOrDefault(k => k.HasSecret
                                    && k.UserIds.Any(id => id.Contains(address, StringComparison.OrdinalIgnoreCase)));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static string Name(Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag algorithm) => algorithm switch
    {
        Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.RsaGeneral
            or Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.RsaEncrypt
            or Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.RsaSign => "RSA",
        Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.ECDsa => "ECDSA",
        Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.ECDH => "ECDH",
        Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.EdDsa_Legacy => "EdDSA",
        Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.Dsa => "DSA",
        Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.ElGamalEncrypt
            or Org.BouncyCastle.Bcpg.PublicKeyAlgorithmTag.ElGamalGeneral => "ElGamal",
        _ => algorithm.ToString(),
    };
}
