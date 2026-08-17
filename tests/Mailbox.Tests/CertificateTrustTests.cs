using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mailbox.Core.Settings;
using Mailbox.Security.Tls;

namespace Mailbox.Tests;

/// <summary>
/// Which server certificates a reader has agreed to, and what happens when one changes.
/// </summary>
/// <remarks>
/// The whole of the security here is in two decisions: what is remembered (a fingerprint, not a
/// waiver) and when the question is asked again (the moment the certificate is not the one that
/// was agreed to). Both have tests, because both are the sort of thing that reads as working
/// whichever way round it is.
/// </remarks>
public class CertificateTrustTests
{
    /// <summary>A certificate made here, so a test can hold one without a server.</summary>
    private static X509Certificate2 Certificate(string name, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());

        return request.CreateSelfSigned(
            from ?? DateTimeOffset.UtcNow.AddDays(-1),
            to ?? DateTimeOffset.UtcNow.AddDays(30));
    }

    private static SettingsStore Settings()
        => new(Path.Combine(Directory.CreateTempSubdirectory("mailbox-tls-").FullName, "settings.json"));

    // ---- What is read off a certificate ----

    [Fact]
    public void ACertificateIsDescribedByWhatAReaderWouldCheck()
    {
        using var certificate = Certificate("d8.my-control-panel.com");
        var facts = CertificateFacts.Read(certificate);

        Assert.Equal("d8.my-control-panel.com", facts.CommonName);
        Assert.Contains("d8.my-control-panel.com", facts.Names);
        Assert.Equal(64, facts.Fingerprint.Length);
        Assert.Equal(facts.Fingerprint, facts.PrettyFingerprint.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.True(facts.NotAfter > facts.NotBefore);
    }

    // ---- Telling one problem from another ----

    /// <summary>
    /// A name that does not match on an otherwise sound chain is shared hosting, and it is a much
    /// smaller thing to accept than a key nobody has vouched for. The dialog says which it is
    /// asking about, so this has to tell them apart.
    /// </summary>
    [Fact]
    public void ANameMismatchIsToldFromAnUntrustedRoot()
    {
        using var certificate = Certificate("d8.my-control-panel.com");
        var facts = CertificateFacts.Read(certificate);

        var mismatch = CertificateTrust.Classify(SslPolicyErrors.RemoteCertificateNameMismatch, null, facts);
        Assert.Equal(CertificateFault.NameMismatch, mismatch);

        var refusal = new CertificateRefusal("mail.emutastic.com", 993, facts, mismatch);
        Assert.True(refusal.NameOnly);
        Assert.Contains("is for d8.my-control-panel.com, not for mail.emutastic.com", Assert.Single(refusal.Problems));
    }

    [Fact]
    public void AnExpiredCertificateSaysSoAndIsNotJustANameProblem()
    {
        using var certificate = Certificate(
            "old.example.net", DateTimeOffset.UtcNow.AddDays(-400), DateTimeOffset.UtcNow.AddDays(-10));

        var facts = CertificateFacts.Read(certificate);
        var faults = CertificateTrust.Classify(SslPolicyErrors.RemoteCertificateChainErrors, null, facts);

        Assert.True(faults.HasFlag(CertificateFault.Expired));

        var refusal = new CertificateRefusal("old.example.net", 993, facts, faults);
        Assert.False(refusal.NameOnly);
        Assert.Contains(refusal.Problems, p => p.StartsWith("It expired on", StringComparison.Ordinal));
    }

    [Fact]
    public void SeveralThingsWrongAreAllSaid()
    {
        using var certificate = Certificate(
            "old.example.net", DateTimeOffset.UtcNow.AddDays(-400), DateTimeOffset.UtcNow.AddDays(-10));

        var facts = CertificateFacts.Read(certificate);
        var faults = CertificateTrust.Classify(
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors,
            null,
            facts);

        var refusal = new CertificateRefusal("mail.example.com", 993, facts, faults);

        Assert.True(refusal.Problems.Count >= 2);
        Assert.False(refusal.NameOnly);
    }

    /// <summary>A server that offered nothing at all is its own answer, and never a name problem.</summary>
    [Fact]
    public void NoCertificateAtAllIsRefusedAndRecorded()
    {
        var trust = new CertificateTrust();

        Assert.False(trust.Allows("mail.example.com", 993, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));

        var refusal = Assert.Single(trust.Refused);
        Assert.True(refusal.Faults.HasFlag(CertificateFault.Absent));
    }

    // ---- The decision itself ----

    [Fact]
    public void AGoodCertificateNeedsNoDecision()
    {
        var trust = new CertificateTrust();
        using var certificate = Certificate("mail.example.com");

        Assert.True(trust.Allows("mail.example.com", 993, certificate, null, SslPolicyErrors.None));
        Assert.Empty(trust.Refused);
    }

    /// <summary>
    /// The callback never asks anybody anything: it refuses, and records what it refused so the
    /// caller can ask on a thread where asking is possible. Same shape as the passphrase vault,
    /// and for the same reason — a synchronous callback and an asynchronous dialog cannot wait on
    /// each other.
    /// </summary>
    [Fact]
    public void ARefusalIsRecordedRatherThanAsked()
    {
        var trust = new CertificateTrust();
        using var certificate = Certificate("d8.my-control-panel.com");

        var allowed = trust.Allows(
            "mail.emutastic.com", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.False(allowed);

        var refusal = Assert.Single(trust.Refused);
        Assert.Equal("mail.emutastic.com", refusal.Host);
        Assert.Equal(993, refusal.Port);
        Assert.Equal(CertificateFacts.Read(certificate).Fingerprint, refusal.Certificate.Fingerprint);
        Assert.NotNull(trust.RefusalFor("mail.emutastic.com", 993));
    }

    [Fact]
    public void OncePinnedTheSameCertificateIsAllowed()
    {
        var trust = new CertificateTrust();
        using var certificate = Certificate("d8.my-control-panel.com");

        Assert.False(trust.Allows("mail.emutastic.com", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));

        trust.Pin(trust.RefusalFor("mail.emutastic.com", 993)!);

        Assert.True(trust.Allows("mail.emutastic.com", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Empty(trust.Refused);
    }

    /// <summary>
    /// The one that matters most. What is remembered is the certificate, not a standing waiver
    /// for the host — so the day the server presents a different key, whether that is a renewal
    /// or somebody in the way, the question is asked again.
    /// </summary>
    [Fact]
    public void ADifferentCertificateOnAPinnedHostIsAskedAboutAgain()
    {
        var trust = new CertificateTrust();
        using var agreed = Certificate("d8.my-control-panel.com");
        using var somebodyElse = Certificate("d8.my-control-panel.com");

        Assert.False(trust.Allows("mail.emutastic.com", 993, agreed, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        trust.Pin(trust.RefusalFor("mail.emutastic.com", 993)!);
        Assert.True(trust.Allows("mail.emutastic.com", 993, agreed, null, SslPolicyErrors.RemoteCertificateNameMismatch));

        // Same subject, same name, different key — which is exactly what an attacker's would be.
        Assert.False(trust.Allows("mail.emutastic.com", 993, somebodyElse, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Equal(
            CertificateFacts.Read(somebodyElse).Fingerprint,
            trust.RefusalFor("mail.emutastic.com", 993)!.Certificate.Fingerprint);
    }

    /// <summary>A decision about one host is not a decision about another, nor about another port.</summary>
    [Fact]
    public void APinCoversOneHostAndPortAndNothingElse()
    {
        var trust = new CertificateTrust();
        using var certificate = Certificate("shared.example.net");

        Assert.False(trust.Allows("mail.one.example", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        trust.Pin(trust.RefusalFor("mail.one.example", 993)!);

        Assert.True(trust.Allows("mail.one.example", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(trust.Allows("mail.two.example", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(trust.Allows("mail.one.example", 465, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void AHostIsJudgedOnItsMeritsAgainOnceForgotten()
    {
        var trust = new CertificateTrust();
        using var certificate = Certificate("shared.example.net");

        Assert.False(trust.Allows("mail.one.example", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        trust.Pin(trust.RefusalFor("mail.one.example", 993)!);
        Assert.True(trust.Allows("mail.one.example", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));

        Assert.True(trust.Forget("mail.one.example:993"));
        Assert.False(trust.Allows("mail.one.example", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    // ---- Where the decision lives ----

    /// <summary>
    /// A pin survives a restart, and it is somewhere a reader can find it. A trust decision that
    /// cannot be reviewed or revoked is not one anybody really made.
    /// </summary>
    [Fact]
    public void APinIsWrittenDownAndReadBack()
    {
        var settings = Settings();
        using var certificate = Certificate("d8.my-control-panel.com");
        var fingerprint = CertificateFacts.Read(certificate).Fingerprint;

        var first = new CertificateTrust(settings);
        Assert.False(first.Allows("mail.emutastic.com", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        first.Pin(first.RefusalFor("mail.emutastic.com", 993)!);

        // A second run of the application, reading the same settings.
        var later = new CertificateTrust(settings);
        Assert.True(later.Allows("mail.emutastic.com", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));

        var (host, pinned) = Assert.Single(later.Pins);
        Assert.Equal("mail.emutastic.com:993", host);
        Assert.Equal(fingerprint, pinned);
    }

    /// <summary>
    /// A settings file somebody has edited into nonsense trusts nothing, rather than starting up
    /// as though there were no pins and quietly refusing everything without saying why.
    /// </summary>
    [Fact]
    public void AnUnreadableListTrustsNothing()
    {
        var settings = Settings();
        settings.Set(CertificateTrust.SettingKey, "{not json");

        var trust = new CertificateTrust(settings);

        Assert.Empty(trust.Pins);
    }

    [Fact]
    public void RefusalsCanBeClearedWithoutAgreeingToAnything()
    {
        var trust = new CertificateTrust();
        using var certificate = Certificate("shared.example.net");

        Assert.False(trust.Allows("mail.one.example", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        trust.ClearRefusals();

        Assert.Empty(trust.Refused);
        Assert.Empty(trust.Pins);
        Assert.False(trust.Allows("mail.one.example", 993, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }
}
