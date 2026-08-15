using Mailbox.Security;
using Mailbox.Security.Dns;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Mailbox.Tests;

/// <summary>
/// Signature checking end to end, against a key this file publishes itself.
/// </summary>
/// <remarks>
/// A real signed message would need a real key in real DNS, which would make the suite depend on
/// a third party's zone and on the network. So a key is generated, a message is signed with it,
/// and the public half is served by a lookup that answers from a dictionary. Everything between
/// those two points is the code that runs in the application.
/// </remarks>
public class DkimVerificationTests
{
    private const string Domain = "example.com";
    private const string Selector = "sel";

    /// <summary>
    /// One key pair for the whole class. Generating RSA is the slow part of these tests, and the
    /// tests are about what is done with a key rather than about making them.
    /// </summary>
    private static readonly AsymmetricCipherKeyPair Keys = Generate();

    private static AsymmetricCipherKeyPair Generate()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        return generator.GenerateKeyPair();
    }

    /// <summary>The public half as a domain would publish it.</summary>
    private static string PublishedKey()
    {
        var info = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(Keys.Public);
        return $"v=DKIM1; k=rsa; p={Convert.ToBase64String(info.GetDerEncoded())}";
    }

    /// <summary>A lookup that answers out of a dictionary and never touches a socket.</summary>
    private sealed class Zone(params (string Name, string Record)[] records) : ITxtLookup
    {
        private readonly Dictionary<string, string> _records =
            records.ToDictionary(r => r.Name, r => r.Record, StringComparer.OrdinalIgnoreCase);

        public int Lookups { get; private set; }

        public Task<DnsAnswer> TxtAsync(string name, CancellationToken cancellation = default)
        {
            Lookups++;

            return Task.FromResult(_records.TryGetValue(name, out var record)
                ? new DnsAnswer(DnsResponseCode.NoError, [record], 300)
                : new DnsAnswer(DnsResponseCode.NameError, [], 0));
        }
    }

    private static Zone PublishingTheKey()
        => new(($"{Selector}._domainkey.{Domain}", PublishedKey()));

    private static MimeMessage Message(string body = "The body, as sent.")
    {
        var message = new MimeMessage { Subject = "A signed message" };
        message.From.Add(new MailboxAddress("A. Person", $"a@{Domain}"));
        message.To.Add(new MailboxAddress("You", "you@example.net"));
        message.Date = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        message.MessageId = "signed@example.com";
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private static MimeMessage Signed(string body = "The body, as sent.")
    {
        var message = Message(body);

        new DkimSigner(Keys.Private, Domain, Selector)
            .Sign(message, [HeaderId.From, HeaderId.Subject, HeaderId.Date]);

        return message;
    }

    // ---- The happy path ----------------------------------------------------------------------

    [Fact]
    public async Task ASignatureThatMatchesItsKeyPasses()
    {
        var result = await new DkimVerification(PublishingTheKey())
            .VerifyAsync(Signed(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthVerdict.Pass, result.Verdict);
        Assert.Equal(Domain, result.SigningDomain);
        Assert.True(result.WasChecked);
    }

    [Fact]
    public async Task TheKeyIsAskedForUnderTheSelectorAndDomainTheSignatureNames()
    {
        var zone = PublishingTheKey();
        await new DkimVerification(zone).VerifyAsync(Signed(), TestContext.Current.CancellationToken);

        Assert.Equal(1, zone.Lookups);
    }

    // ---- What a failure is, and what it is not -----------------------------------------------

    /// <summary>
    /// The check that makes this worth doing at all: the bytes in the store are what is
    /// verified, so a message altered after it was signed does not verify here even if the
    /// server that delivered it said it did.
    /// </summary>
    [Fact]
    public async Task AMessageAlteredAfterSigningFails()
    {
        var message = Signed();
        ((TextPart)message.Body!).Text = "The body, as it did not leave.";

        var result = await new DkimVerification(PublishingTheKey())
            .VerifyAsync(message, TestContext.Current.CancellationToken);

        Assert.Equal(AuthVerdict.Fail, result.Verdict);
    }

    [Fact]
    public async Task ASignatureFromAnotherKeyFails()
    {
        var other = Generate();
        var info = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(other.Public);

        var zone = new Zone(($"{Selector}._domainkey.{Domain}",
            $"v=DKIM1; k=rsa; p={Convert.ToBase64String(info.GetDerEncoded())}"));

        var result = await new DkimVerification(zone)
            .VerifyAsync(Signed(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthVerdict.Fail, result.Verdict);
    }

    /// <summary>
    /// A key that cannot be reached is not a signature that failed, and telling a reader
    /// otherwise because their network was down would be worse than saying nothing.
    /// </summary>
    [Fact]
    public async Task ASignatureWhoseKeyIsNotPublishedIsAnErrorRatherThanAFailure()
    {
        var result = await new DkimVerification(new Zone())
            .VerifyAsync(Signed(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthVerdict.Error, result.Verdict);
        Assert.Equal(Domain, result.SigningDomain);
    }

    [Fact]
    public async Task ARevokedKeyIsAnErrorRatherThanAFailure()
    {
        // An empty p= is how a domain withdraws a selector, and it is what a real one returns
        // for a rotated key that is still published.
        var zone = new Zone(($"{Selector}._domainkey.{Domain}", "v=DKIM1; k=rsa; p="));

        var result = await new DkimVerification(zone)
            .VerifyAsync(Signed(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthVerdict.Error, result.Verdict);
    }

    [Fact]
    public async Task AnUnsignedMessageIsNotChecked()
    {
        var result = await new DkimVerification(PublishingTheKey())
            .VerifyAsync(Message(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthVerdict.None, result.Verdict);
        Assert.False(result.WasChecked);
    }

    /// <summary>Nothing is resolved for a message that carries no signature.</summary>
    [Fact]
    public async Task AnUnsignedMessageResolvesNothing()
    {
        var zone = PublishingTheKey();
        await new DkimVerification(zone).VerifyAsync(Message(), TestContext.Current.CancellationToken);

        Assert.Equal(0, zone.Lookups);
    }

    /// <summary>
    /// A message may be signed twice — by its author and again by a list that passed it on. One
    /// good signature is a pass, per RFC 6376 §6.1; treating any failure as failure would mark
    /// most mailing-list mail as forged.
    /// </summary>
    [Fact]
    public async Task OneGoodSignatureAmongSeveralIsAPass()
    {
        var message = Signed();

        // A second signature from a domain that publishes no key at all.
        new DkimSigner(Generate().Private, "list.example.net", "l1")
            .Sign(message, [HeaderId.From, HeaderId.Subject]);

        var result = await new DkimVerification(PublishingTheKey())
            .VerifyAsync(message, TestContext.Current.CancellationToken);

        Assert.Equal(AuthVerdict.Pass, result.Verdict);
    }

    // ---- How it reaches the reader ------------------------------------------------------------

    [Fact]
    public async Task ALocalPassSilencesTheCautionAboutAForwardedMessage()
    {
        var message = Signed();
        message.Headers.Add("Authentication-Results", "mx.example.net; spf=softfail");

        var verified = await new DkimVerification(PublishingTheKey())
            .VerifyAsync(message, TestContext.Current.CancellationToken);

        Assert.Empty(SenderTrust.Evaluate(message, [], verified).Warnings);
    }

    [Fact]
    public async Task ALocalFailIsSaidPlainlyButNotAsAnAlarm()
    {
        var message = Signed();
        ((TextPart)message.Body!).Text = "Altered.";

        var verified = await new DkimVerification(PublishingTheKey())
            .VerifyAsync(message, TestContext.Current.CancellationToken);

        var trust = SenderTrust.Evaluate(message, [], verified);

        Assert.Equal(TrustLevel.Caution, trust.Level);
        Assert.Contains("does not match the signature", trust.Headline, StringComparison.Ordinal);
    }

    /// <summary>A message nobody checked says nothing about having been checked.</summary>
    [Fact]
    public void NotCheckedIsNotAResult()
    {
        var trust = SenderTrust.Evaluate(Message(), []);

        Assert.Null(trust.Verified);
        Assert.Empty(trust.Warnings);
    }

    [Fact]
    public async Task TheVerdictSurvivesTheStore()
    {
        var verified = await new DkimVerification(PublishingTheKey())
            .VerifyAsync(Signed(), TestContext.Current.CancellationToken);

        // What the receiver writes and what the reading pane reads back, spelled the one way.
        var written = verified.Verdict.ToString().ToLowerInvariant();

        Assert.True(Enum.TryParse<AuthVerdict>(written, ignoreCase: true, out var read));
        Assert.Equal(verified.Verdict, read);
    }
}
