using System.Text;
using Mailbox.Security.OpenPgp;

namespace Mailbox.Tests;

/// <summary>
/// Handing the private-key operations to the reader's own GnuPG.
/// </summary>
/// <remarks>
/// Two halves. The first reads GnuPG's machine-readable output — the colon listing and the
/// status stream — and needs no GnuPG to check, which matters because it is where a
/// misunderstanding would be silent: an integrity check read wrongly is a modified message shown
/// as an intact one.
/// <para>
/// The second runs the real thing. It builds a keyring of its own under a temporary
/// <c>GNUPGHOME</c> with a passphraseless key in it and works only against that — never the
/// machine's own, which would mean a test asking somebody's agent for their passphrase. Skipped,
/// not passed, where GnuPG is not installed: a green test that did nothing is worse than no test.
/// </para>
/// </remarks>
public class GnuPgAgentTests
{
    // ---- Reading what GnuPG says ---------------------------------------------------------------

    private static GnuPgResult Listing(string text)
        => new(Encoding.UTF8.GetBytes(text), null, []);

    private static GnuPgResult Status(params string[] lines) => new([], null, lines);

    [Fact]
    public void TheAddressesComeOutOfTheColonListing()
    {
        var addresses = GnuPgAgent.Addresses(Listing(
            "sec:u:255:22:A267DB6F:1788377481:::u:::scESC:::+::ed25519:::0:\n"
            + "uid:u::::1788377481::4E35181E::A. Person <a.person@example.org>::::::::::0:\n"
            + "uid:u::::1788377481::5F46292F::A. Person (work) <a.person@example.net>::::::::::0:\n"));

        Assert.Equal(["a.person@example.org", "a.person@example.net"], addresses);
    }

    /// <summary>
    /// Field 2 is the validity. A key its owner has revoked, or one that has expired, is not one
    /// to offer to sign with or encrypt to.
    /// </summary>
    [Fact]
    public void RevokedAndExpiredKeysAreNotOffered()
    {
        var addresses = GnuPgAgent.Addresses(Listing(
            "uid:r::::1::A::Revoked <revoked@example.org>::::::::::0:\n"
            + "uid:e::::1::B::Expired <expired@example.org>::::::::::0:\n"
            + "uid:d::::1::C::Disabled <disabled@example.org>::::::::::0:\n"
            + "uid:i::::1::D::Invalid <invalid@example.org>::::::::::0:\n"
            + "uid:u::::1::E::Good <good@example.org>::::::::::0:\n"));

        Assert.Equal(["good@example.org"], addresses);
    }

    [Fact]
    public void AUserIdWithNoAddressBelongsToNobody()
    {
        Assert.Empty(GnuPgAgent.Addresses(Listing("uid:u::::1::A::Just A Name::::::::::0:\n")));
        Assert.Empty(GnuPgAgent.Addresses(Listing("tru::1:1:12:1:5:01\ncfg:version:2.4.9\n")));
    }

    [Fact]
    public void AnAddressIsOfferedOnceHoweverManyRecordsCarryIt()
    {
        var addresses = GnuPgAgent.Addresses(Listing(
            "pub:u::::1::A::A. Person <a.person@example.org>::::::::::0:\n"
            + "uid:u::::1::A::A. Person <A.Person@Example.ORG>::::::::::0:\n"));

        Assert.Single(addresses);
    }

    /// <summary>
    /// The rule the whole delegation turns on: plaintext is released only where GnuPG proved the
    /// ciphertext had not been altered. Two constructions count and nothing else does.
    /// </summary>
    [Fact]
    public void IntegrityIsProvenByTheModificationCodeOrByAead()
    {
        // The classic packet: a modification detection code, checked and good.
        Assert.True(GnuPgAgent.IsIntegrityProven(Status("DECRYPTION_OKAY", "GOODMDC")));

        // The modern one: integrity is part of the cipher, so there is no separate code to
        // report and the third field of DECRYPTION_INFO names the AEAD algorithm instead.
        Assert.True(GnuPgAgent.IsIntegrityProven(Status("DECRYPTION_INFO 0 9 2", "DECRYPTION_OKAY")));

        // A zero there means the packet was not AEAD, which throws the question back to the
        // modification code — and there is none.
        Assert.False(GnuPgAgent.IsIntegrityProven(Status("DECRYPTION_INFO 2 9 0", "DECRYPTION_OKAY")));

        // Decrypted, and nothing at all said about whether it was tampered with. This is the
        // EFAIL shape, and it is what must not be shown.
        Assert.False(GnuPgAgent.IsIntegrityProven(Status("DECRYPTION_OKAY", "PLAINTEXT 62 1788")));
        Assert.False(GnuPgAgent.IsIntegrityProven(Status()));

        // A code that was checked and failed is not a code that passed.
        Assert.False(GnuPgAgent.IsIntegrityProven(Status("DECRYPTION_OKAY", "BADMDC")));
    }

    [Fact]
    public void TheFailuresWorthTellingApartEachGetTheirOwnSentence()
    {
        Assert.Contains(
            "no usable key for nobody@example.invalid",
            GnuPgAgent.Explain(["INV_RECP 0 nobody@example.invalid"], []),
            StringComparison.Ordinal);

        Assert.Contains(
            "secret half",
            GnuPgAgent.Explain(["NO_SECKEY ABCDEF"], []),
            StringComparison.Ordinal);

        Assert.Contains("expired", GnuPgAgent.Explain(["KEYEXPIRED 1788377481"], []), StringComparison.Ordinal);
        Assert.Contains("revoked", GnuPgAgent.Explain(["KEYREVOKED"], []), StringComparison.Ordinal);
        Assert.Contains("passphrase", GnuPgAgent.Explain(["BAD_PASSPHRASE ABCDEF"], []), StringComparison.Ordinal);

        // GnuPG's own words for "that was not OpenPGP" are "Unknown system error".
        Assert.Contains("no OpenPGP data", GnuPgAgent.Explain(["NODATA 1"], []), StringComparison.Ordinal);

        // Nothing recognised: what it told a person, rather than nothing.
        Assert.Contains(
            "signing failed",
            GnuPgAgent.Explain([], ["gpg: skipped: No secret key", "gpg: signing failed: No secret key"]),
            StringComparison.Ordinal);

        Assert.Contains("said nothing", GnuPgAgent.Explain([], []), StringComparison.Ordinal);
    }

    [Fact]
    public void AStatusKeywordIsMatchedWholeRatherThanAsAPrefix()
    {
        var result = Status("GOODMDC", "DECRYPTION_OKAY");

        Assert.True(result.Said("GOODMDC"));
        Assert.False(result.Said("GOOD"));
        Assert.Null(result.After("BADMDC"));
    }

    // ---- Against the real thing ------------------------------------------------------------------

    /// <summary>
    /// A keyring of this test's own, with one passphraseless key, thrown away afterwards.
    /// </summary>
    /// <remarks>
    /// Its own <c>GNUPGHOME</c> and nothing else: the machine's real keyring is never opened, so
    /// nothing here can prompt for somebody's passphrase or leave a key behind. Passphraseless
    /// because a test cannot answer pinentry — what is being checked is the delegation, not that
    /// GnuPG can hold a secret.
    /// </remarks>
    private sealed class Keyring : IDisposable
    {
        public string Home { get; }

        public GnuPgAgent Agent { get; }

        public const string Address = "a.person@example.org";

        public Keyring()
        {
            Home = Path.Combine(Path.GetTempPath(), $"mailbox-gpg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Home);

            // 0700, which GnuPG insists on and warns about loudly otherwise. Guarded because
            // the mode bits are a Unix idea; on anything else GnuPG has its own arrangement.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Run("--pinentry-mode", "loopback", "--passphrase", string.Empty,
                "--quick-generate-key", $"A. Person <{Address}>", "default", "default", "never");

            Agent = new GnuPgAgent(Home, TimeSpan.FromSeconds(60));
        }

        /// <summary>Makes a ciphertext GnuPG itself would not make: no modification detection.</summary>
        public byte[] WithoutIntegrityProtection(byte[] plaintext)
        {
            var file = Path.Combine(Home, "plain");
            File.WriteAllBytes(file, plaintext);
            Run("--armor", "--encrypt", "--rfc2440", "--trust-model", "always",
                "--recipient", Address, "--output", Path.Combine(Home, "nomdc.asc"), file);
            return File.ReadAllBytes(Path.Combine(Home, "nomdc.asc"));
        }

        private void Run(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo("gpg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            start.ArgumentList.Add("--batch");
            start.ArgumentList.Add("--yes");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GNUPGHOME"] = Home;

            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit(TimeSpan.FromSeconds(60));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Home, recursive: true);
            }
            catch (IOException)
            {
                // A throwaway keyring in the temporary directory is the operating system's to
                // clean up if this could not.
            }
        }
    }

    private static readonly byte[] Content =
        Encoding.UTF8.GetBytes("Content-Type: text/plain\r\n\r\nThe quarterly figures are attached.\r\n");

    [Fact]
    public async Task GnuPgSignsAndTheSignatureChecksOut()
    {
        Assert.SkipUnless(GnuPgAgent.IsAvailable, "GnuPG is not installed on this machine.");
        using var keyring = new Keyring();

        Assert.Equal([Keyring.Address], await keyring.Agent.SignersAsync(TestContext.Current.CancellationToken));

        var signed = await keyring.Agent.SignAsync(
            Content, Keyring.Address, TestContext.Current.CancellationToken);

        Assert.True(signed.Worked, signed.Problem);
        Assert.True(signed.Said("SIG_CREATED"));
        Assert.StartsWith("-----BEGIN PGP SIGNATURE-----", Encoding.UTF8.GetString(signed.Output), StringComparison.Ordinal);

        var checked_ = await keyring.Agent.VerifyAsync(
            Content, signed.Output, TestContext.Current.CancellationToken);

        Assert.True(checked_.Worked, checked_.Problem);
        Assert.True(checked_.Said("GOODSIG"));
    }

    [Fact]
    public async Task AChangedMessageFailsItsSignature()
    {
        Assert.SkipUnless(GnuPgAgent.IsAvailable, "GnuPG is not installed on this machine.");
        using var keyring = new Keyring();

        var signed = await keyring.Agent.SignAsync(
            Content, Keyring.Address, TestContext.Current.CancellationToken);

        var altered = Encoding.UTF8.GetBytes(
            "Content-Type: text/plain\r\n\r\nThe quarterly figures are NOT attached.\r\n");

        var checked_ = await keyring.Agent.VerifyAsync(
            altered, signed.Output, TestContext.Current.CancellationToken);

        Assert.False(checked_.Worked);
        Assert.True(checked_.Said("BADSIG"));
    }

    [Fact]
    public async Task AMessageGoesRoundThroughGnuPgAndComesBackWhole()
    {
        Assert.SkipUnless(GnuPgAgent.IsAvailable, "GnuPG is not installed on this machine.");
        using var keyring = new Keyring();

        var sealed_ = await keyring.Agent.EncryptAsync(
            Content, [Keyring.Address], signAs: Keyring.Address,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(sealed_.Worked, sealed_.Problem);

        var opened = await keyring.Agent.DecryptAsync(sealed_.Output, TestContext.Current.CancellationToken);

        Assert.True(opened.Worked, opened.Problem);
        Assert.Equal(Content, opened.Output);

        // The signature made inside the encryption, which is what says the message was written
        // by the person it is from rather than merely forwarded by them.
        Assert.True(opened.Said("GOODSIG"));
    }

    /// <summary>
    /// The invariant this whole path has to hold: no plaintext leaves a decryption whose
    /// integrity GnuPG did not prove.
    /// </summary>
    /// <remarks>
    /// Asserted as the invariant rather than as one fixture's outcome, because what a given
    /// GnuPG will <em>make</em> when asked for a legacy packet is not stable across versions —
    /// current builds refuse to decrypt one at all ("decryption forced to fail"), and some will
    /// not produce one in the first place. Either answer is correct and the test must accept
    /// both; what must never happen is bytes coming back with nothing vouching for them. So the
    /// packet is built the old way, decrypted, and the two allowed outcomes are checked: refused
    /// with nothing released, or opened with the integrity actually proven.
    /// </remarks>
    [Fact]
    public async Task NoPlaintextIsReleasedWithoutProofOfIntegrity()
    {
        Assert.SkipUnless(GnuPgAgent.IsAvailable, "GnuPG is not installed on this machine.");
        using var keyring = new Keyring();

        var legacy = keyring.WithoutIntegrityProtection(Content);
        Assert.NotEmpty(legacy);

        var opened = await keyring.Agent.DecryptAsync(legacy, TestContext.Current.CancellationToken);

        if (opened.Worked)
        {
            // It came back, so this GnuPG made a packet that does carry integrity protection —
            // and the proof has to be there, or the rule leaked.
            Assert.True(
                GnuPgAgent.IsIntegrityProven(opened),
                "plaintext was released from a packet whose integrity was not proven");
            return;
        }

        // Refused, and nothing came out with it.
        Assert.Empty(opened.Output);
        Assert.NotNull(opened.Problem);
    }

    [Fact]
    public async Task ThereIsNoUsableKeyForAStranger()
    {
        Assert.SkipUnless(GnuPgAgent.IsAvailable, "GnuPG is not installed on this machine.");
        using var keyring = new Keyring();

        var refused = await keyring.Agent.EncryptAsync(
            Content, ["nobody@example.invalid"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(refused.Worked);
        Assert.Contains("nobody@example.invalid", refused.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RubbishIsNotOpenPgp()
    {
        Assert.SkipUnless(GnuPgAgent.IsAvailable, "GnuPG is not installed on this machine.");
        using var keyring = new Keyring();

        var refused = await keyring.Agent.DecryptAsync(
            Encoding.UTF8.GetBytes("not an openpgp packet at all"), TestContext.Current.CancellationToken);

        Assert.False(refused.Worked);
        Assert.Contains("no OpenPGP data", refused.Problem!, StringComparison.Ordinal);
    }
}
