using Mailbox.Security;
using MimeKit;

namespace Mailbox.Tests;

public class AuthenticationResultsTests
{
    [Fact]
    public void ReadsTheThreeMethods()
    {
        var results = AuthenticationResults.Parse(
            "mx.example.net; dkim=pass header.d=example.com; spf=pass smtp.mailfrom=example.com; "
            + "dmarc=pass header.from=example.com");

        Assert.Equal(AuthVerdict.Pass, results.Dkim);
        Assert.Equal(AuthVerdict.Pass, results.Spf);
        Assert.Equal(AuthVerdict.Pass, results.Dmarc);
        Assert.Equal("example.com", results.SigningDomain);
        Assert.True(results.WasChecked);
        Assert.False(results.Failed);
    }

    [Fact]
    public void AMessageWithNoHeaderIsNotAFailure()
    {
        var results = AuthenticationResults.Read(new MimeMessage());

        Assert.False(results.WasChecked);
        Assert.False(results.Failed);
    }

    /// <summary>
    /// A header may carry two DKIM results — one signature good, another stale. The
    /// specification says a pass on any of them is a pass.
    /// </summary>
    [Fact]
    public void OneGoodSignatureAmongSeveralIsAPass()
    {
        var results = AuthenticationResults.Parse("mx; dkim=fail header.d=a.example; dkim=pass");

        Assert.Equal(AuthVerdict.Pass, results.Dkim);
    }

    [Theory]
    [InlineData("softfail", AuthVerdict.SoftFail)]
    [InlineData("neutral", AuthVerdict.Neutral)]
    [InlineData("temperror", AuthVerdict.Error)]
    [InlineData("permerror", AuthVerdict.Error)]
    [InlineData("nonsense", AuthVerdict.None)]
    public void EachVerdictWordIsUnderstood(string word, AuthVerdict expected)
        => Assert.Equal(expected, AuthenticationResults.Parse($"mx; spf={word}").Spf);

    /// <summary>
    /// Only the topmost header is trusted: it was written by the last hop, which is the
    /// provider we authenticated to. Anything below it came from a machine the sender may run.
    /// </summary>
    [Fact]
    public void OnlyTheTopmostHeaderIsRead()
    {
        var message = new MimeMessage();
        message.Headers.Add("Authentication-Results", "mx.provider.example; dmarc=fail");
        message.Headers.Add("Authentication-Results", "mx.sender.example; dmarc=pass");

        Assert.Equal(AuthVerdict.Fail, AuthenticationResults.Read(message).Dmarc);
    }

    /// <summary>
    /// DMARC is the domain owner's own policy, so its failure carries the weight. A bare SPF
    /// failure is what forwarding and mailing lists look like.
    /// </summary>
    [Fact]
    public void SpfAloneFailingIsNotTheSameAsFailingAuthentication()
    {
        Assert.False(AuthenticationResults.Parse("mx; spf=fail; dkim=pass").Failed);
        Assert.True(AuthenticationResults.Parse("mx; dmarc=fail").Failed);
        Assert.True(AuthenticationResults.Parse("mx; spf=fail; dkim=fail").Failed);
    }
}

public class LookalikeDomainTests
{
    private static readonly string[] Familiar = ["example.com", "yourbank.example", "work.example"];

    [Theory]
    [InlineData("xn--pypal-4ve.com")]
    [InlineData("exаmple.com")]           // Cyrillic а
    public void HomographsAreCaught(string domain)
        => Assert.True(LookalikeDomains.IsHomograph(domain));

    [Theory]
    [InlineData("example.com")]
    [InlineData("mail.example.co.uk")]
    public void OrdinaryDomainsAreNot(string domain)
        => Assert.False(LookalikeDomains.IsHomograph(domain));

    [Theory]
    [InlineData("exarnple.com")]          // rn for m
    [InlineData("examp1e.com")]           // 1 for l
    [InlineData("example.c0m")]           // 0 for o
    public void ConfusableRunsAreFoldedTogether(string domain)
        => Assert.NotNull(LookalikeDomains.Imitates(domain, Familiar));

    [Theory]
    [InlineData("exampl.com")]
    [InlineData("examplle.com")]
    [InlineData("exemple.com")]
    public void OneEditAwayFromAFamiliarDomainIsSuspicious(string domain)
        => Assert.NotNull(LookalikeDomains.Imitates(domain, Familiar));

    [Fact]
    public void AFamiliarDomainIsNotSuspiciousOfItself()
        => Assert.Null(LookalikeDomains.Imitates("example.com", Familiar));

    [Fact]
    public void AnUnrelatedDomainIsNotFlagged()
        => Assert.Null(LookalikeDomains.Imitates("something-else.org", Familiar));

    /// <summary>
    /// Short names are within one edit of each other by accident, so they are left alone —
    /// a false alarm on ordinary mail costs more than this check is worth.
    /// </summary>
    [Fact]
    public void ShortDomainsAreLeftAlone()
        => Assert.Null(LookalikeDomains.Imitates("bt.com", ["bt.co"]));

    [Fact]
    public void NothingFamiliarMeansNothingToImitate()
        => Assert.Null(LookalikeDomains.Imitates("exarnple.com", []));
}

public class SenderTrustTests
{
    private static MimeMessage From(string name, string address, string? authentication = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(name, address));
        if (authentication is not null) message.Headers.Add("Authentication-Results", authentication);
        return message;
    }

    [Fact]
    public void OrdinaryMailSaysNothing()
    {
        var trust = SenderTrust.Evaluate(
            From("A. Person", "person@example.com", "mx; dkim=pass; spf=pass; dmarc=pass"));

        Assert.Equal(TrustLevel.Quiet, trust.Level);
        Assert.Null(trust.Headline);
        Assert.Empty(trust.Warnings);
    }

    /// <summary>
    /// Most personal mail carries no Authentication-Results at all. Absence is not failure, and
    /// warning about it would put a bar on nearly every message.
    /// </summary>
    [Fact]
    public void MailWithNoResultsAtAllIsQuiet()
        => Assert.Equal(TrustLevel.Quiet, SenderTrust.Evaluate(From("A. Person", "person@example.com")).Level);

    [Fact]
    public void FailingTheDomainsOwnPolicyIsAnAlarm()
    {
        var trust = SenderTrust.Evaluate(From("A. Person", "person@example.com", "mx; dmarc=fail"));

        Assert.Equal(TrustLevel.Alarm, trust.Level);
        Assert.Contains("failed", trust.Headline!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASoftFailAloneIsOnlyCaution()
    {
        var trust = SenderTrust.Evaluate(From("A. Person", "person@example.com", "mx; spf=softfail"));

        Assert.Equal(TrustLevel.Caution, trust.Level);
    }

    [Fact]
    public void ASoftFailWithAGoodSignatureSaysNothing()
        => Assert.Equal(TrustLevel.Quiet,
            SenderTrust.Evaluate(From("A. Person", "p@example.com", "mx; spf=softfail; dkim=pass")).Level);

    [Fact]
    public void ADisplayNameClaimingAnotherDomainIsAnAlarm()
    {
        var trust = SenderTrust.Evaluate(From("billing@yourbank.example", "attacker@elsewhere.invalid"));

        Assert.Equal(TrustLevel.Alarm, trust.Level);
        Assert.Contains("disagrees", trust.Headline!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADisplayNameShowingTheSameAddressIsFine()
        => Assert.Equal(TrustLevel.Quiet,
            SenderTrust.Evaluate(From("person@example.com", "person@example.com")).Level);

    [Fact]
    public void ALookalikeOfAFamiliarDomainIsAnAlarm()
    {
        var trust = SenderTrust.Evaluate(
            From("Billing", "billing@yourbank.exarnple"), ["yourbank.example"]);

        Assert.Equal(TrustLevel.Alarm, trust.Level);
        Assert.Contains("yourbank.example", trust.Headline!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoudestWarningIsTheOneShown()
    {
        var trust = SenderTrust.Evaluate(
            From("billing@yourbank.example", "attacker@elsewhere.invalid", "mx; spf=softfail"));

        Assert.Equal(TrustLevel.Alarm, trust.Level);
        Assert.Equal(2, trust.Warnings.Count);
    }
}
