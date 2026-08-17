using Mailbox.Security.OpenPgp;

namespace Mailbox.Tests;

/// <summary>
/// What the Trust Center's key list says about a ring.
/// </summary>
/// <remarks>
/// The page exists because "this message will not encrypt" had no visible cause: a correspondent's
/// key being absent, expired or revoked all looked the same from outside. So these are mostly about
/// telling those apart, and about the one thing the page must not do — ask for a passphrase to
/// answer a question that is about the ring rather than about a key's contents.
/// </remarks>
public class KeyInventoryTests
{
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static string Fresh() => Directory.CreateTempSubdirectory("mailbox-keys-").FullName;

    [Fact]
    public void AnEmptyRingListsNothing()
    {
        var directory = Fresh();
        using var ring = new Mailbox.Security.OpenPgp.PgpContext(directory);

        Assert.Empty(KeyInventory.Read(ring));

        Directory.Delete(directory, true);
    }

    /// <summary>
    /// The distinction the whole page turns on: a key this machine can sign and decrypt with,
    /// against one it can only encrypt to.
    /// </summary>
    [Fact]
    public void AKeyWithItsSecretHalfIsToldFromOneWithout()
    {
        var directory = Fresh();
        using var ring = PgpKeys.Ring(directory, PgpKeys.Reader, null, PgpKeys.Sender);

        var keys = KeyInventory.Read(ring);

        Assert.Equal(2, keys.Count);

        var mine = Assert.Single(keys, k => k.HasSecret);
        Assert.Contains(PgpKeys.Reader.Address, mine.Owner);

        var theirs = Assert.Single(keys, k => !k.HasSecret);
        Assert.Contains(PgpKeys.Sender.Address, theirs.Owner);

        Directory.Delete(directory, true);
    }

    /// <summary>
    /// Reading the ring is a question about the ring, not about a key's contents, so nothing may
    /// ask for a passphrase to answer it — a page that summoned a prompt on being opened would be
    /// unusable, and this is the ring whose keys really are locked.
    /// </summary>
    [Fact]
    public void ReadingTheRingAsksForNoPassphrase()
    {
        var directory = Fresh();
        var asked = 0;

        using var ring = PgpKeys.Ring(directory, PgpKeys.Reader, _ => { asked++; return null; }, PgpKeys.Sender);

        var keys = KeyInventory.Read(ring);

        Assert.Equal(2, keys.Count);
        Assert.Equal(0, asked);

        Directory.Delete(directory, true);
    }

    [Fact]
    public void AKeyIsNamedByItsFingerprintAndItsOwner()
    {
        var directory = Fresh();
        using var ring = PgpKeys.Ring(directory, PgpKeys.Reader);

        var key = Assert.Single(KeyInventory.Read(ring));

        Assert.Equal(40, key.Fingerprint.Length);
        Assert.Equal(8, key.ShortId.Length);
        Assert.EndsWith(key.ShortId, key.Fingerprint, StringComparison.Ordinal);
        Assert.Contains(' ', key.PrettyFingerprint);
        Assert.Equal(key.Fingerprint, key.PrettyFingerprint.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Equal("RSA", key.Algorithm);
        Assert.True(key.Bits >= 2048);

        Directory.Delete(directory, true);
    }

    /// <summary>
    /// The three reasons a key will not be used, told apart. Absent is the caller's problem; the
    /// other two are what this list exists to make visible.
    /// </summary>
    [Fact]
    public void ExpiredAndRevokedAreDifferentAnswersAndBothAreUnusable()
    {
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        var live = new KeyEntry("AA", ["A <a@example.com>"], now.AddYears(-1), null, false, false, "RSA", 2048);
        var expired = live with { Expires = now.AddDays(-1) };
        var revoked = live with { IsRevoked = true };
        var expiring = live with { Expires = now.AddDays(30) };

        Assert.True(live.IsUsable(now));
        Assert.False(expired.IsUsable(now));
        Assert.False(revoked.IsUsable(now));
        Assert.True(expiring.IsUsable(now));

        Assert.Equal("no expiry", live.State(now));
        Assert.Equal("revoked", revoked.State(now));
        Assert.StartsWith("expired ", expired.State(now), StringComparison.Ordinal);
        Assert.StartsWith("expires ", expiring.State(now), StringComparison.Ordinal);
    }

    /// <summary>A key with nothing to say about itself is still listed, by its short id.</summary>
    [Fact]
    public void AKeyWithNoUserIdIsStillNamed()
    {
        var anonymous = new KeyEntry("0123456789ABCDEF", [], DateTimeOffset.UtcNow, null, false, false, "RSA", 2048);

        Assert.Equal("89ABCDEF", anonymous.Owner);
    }

    [Fact]
    public void TheOwnKeyIsFoundByAddress()
    {
        var directory = Fresh();
        using var ring = PgpKeys.Ring(directory, PgpKeys.Reader, null, PgpKeys.Sender);
        var keys = KeyInventory.Read(ring);

        var mine = KeyInventory.Own(keys, PgpKeys.Reader.Address);

        Assert.NotNull(mine);
        Assert.True(mine.HasSecret);

        // Somebody whose public key is here is not somebody this machine can sign as.
        Assert.Null(KeyInventory.Own(keys, PgpKeys.Sender.Address));

        Directory.Delete(directory, true);
    }

    // ---- Taking keys in ----

    /// <summary>
    /// A file holding both halves imports both. Counted by reading the ring before and after,
    /// because a file may hold keys that are already here and "imported 2" for two that were
    /// would be a number the reader acts on.
    /// </summary>
    [Fact]
    public void ImportingAFileTakesBothHalvesAndCountsWhatIsNew()
    {
        var source = Fresh();
        var target = Fresh();

        using (var from = PgpKeys.Ring(source, PgpKeys.Reader, null, PgpKeys.Sender))
        {
            using var stream = new MemoryStream();
            PgpKeys.Reader.Public.Encode(stream);
            PgpKeys.Reader.Secret.Encode(stream);
            stream.Position = 0;

            using var ring = new Mailbox.Security.OpenPgp.PgpContext(target);
            var (added, secret) = ring.Take(stream, Stop);

            Assert.Equal(1, added);
            Assert.Equal(1, secret);

            var key = Assert.Single(KeyInventory.Read(ring));
            Assert.True(key.HasSecret);

            // The same file again adds nothing, which is what a reader pressing Import twice sees.
            stream.Position = 0;
            var (again, againSecret) = ring.Take(stream, Stop);
            Assert.Equal(0, again);
            Assert.Equal(0, againSecret);
        }

        Directory.Delete(source, true);
        Directory.Delete(target, true);
    }

    /// <summary>A file of public keys alone is not a failure; there is simply no secret half in it.</summary>
    [Fact]
    public void ImportingPublicKeysAloneWorks()
    {
        var directory = Fresh();

        using var stream = new MemoryStream();
        PgpKeys.Sender.Public.Encode(stream);
        stream.Position = 0;

        using var ring = new Mailbox.Security.OpenPgp.PgpContext(directory);
        var (added, secret) = ring.Take(stream, Stop);

        Assert.Equal(1, added);
        Assert.Equal(0, secret);
        Assert.False(Assert.Single(KeyInventory.Read(ring)).HasSecret);

        Directory.Delete(directory, true);
    }

    [Fact]
    public void ImportingSomethingThatIsNotAKeyChangesNothing()
    {
        var directory = Fresh();

        using var stream = new MemoryStream("this is not a keyring"u8.ToArray());
        using var ring = new Mailbox.Security.OpenPgp.PgpContext(directory);

        var (added, secret) = ring.Take(stream, Stop);

        Assert.Equal(0, added);
        Assert.Equal(0, secret);
        Assert.Empty(KeyInventory.Read(ring));

        Directory.Delete(directory, true);
    }

    /// <summary>
    /// The import says what happened in a sentence, because the button that ran it has nowhere
    /// else to put the answer.
    /// </summary>
    [Fact]
    public void TheImportSaysWhatItDid()
    {
        Assert.Contains("1 public key and 2 secret keys", new GnuPgImportResult(1, 2, null).Summary, StringComparison.Ordinal);
        Assert.Contains("no keys that were not already here", new GnuPgImportResult(0, 0, null).Summary, StringComparison.Ordinal);
        Assert.Equal("GnuPG said no.", new GnuPgImportResult(0, 0, "GnuPG said no.").Summary);
        Assert.False(new GnuPgImportResult(0, 0, "x").Worked);
        Assert.True(new GnuPgImportResult(0, 0, null).Worked);
    }
}
