using MailKit.Security;
using Mailbox.Protocols;
using Mailbox.Protocols.OAuth;
using Mailbox.Security.Dns;

namespace Mailbox.Tests;

/// <summary>
/// Setting up an account is where most people give up, so what is tested here is that the
/// common providers are right, the guess for everything else is sane, and the cases that need
/// an app password say so rather than failing at connect time with "authentication failed".
/// </summary>
public class AutoconfigTests
{
    [Theory]
    [InlineData("someone@gmail.com", "imap.gmail.com", "smtp.gmail.com")]
    [InlineData("someone@googlemail.com", "imap.gmail.com", "smtp.gmail.com")]
    [InlineData("someone@outlook.com", "outlook.office365.com", "smtp.office365.com")]
    [InlineData("someone@hotmail.com", "outlook.office365.com", "smtp.office365.com")]
    [InlineData("someone@yahoo.co.uk", "imap.mail.yahoo.com", "smtp.mail.yahoo.com")]
    [InlineData("someone@icloud.com", "imap.mail.me.com", "smtp.mail.me.com")]
    [InlineData("someone@fastmail.com", "imap.fastmail.com", "smtp.fastmail.com")]
    public void KnownProvidersAreRecognised(string address, string incoming, string outgoing)
    {
        var found = Autoconfig.ForAddress(address);

        Assert.True(found.IsKnownProvider);
        Assert.Equal(incoming, found.Incoming.Host);
        Assert.Equal(outgoing, found.Outgoing.Host);
        Assert.Equal(address, found.Incoming.UserName);
    }

    /// <summary>
    /// The failure people hit hardest: Gmail rejects the account password and says only
    /// "authentication failed". Saying so up front is the whole value of recognising it.
    /// </summary>
    [Fact]
    public void GmailSaysAnAppPasswordIsNeeded()
    {
        var found = Autoconfig.ForAddress("someone@gmail.com");

        Assert.Equal(AuthKind.AppPassword, found.Auth);
        Assert.Contains("App Password", found.Guidance);
    }

    [Fact]
    public void MicrosoftAccountsAreMarkedAsNeedingABrowser()
    {
        var found = Autoconfig.ForAddress("someone@outlook.com");

        Assert.Equal(AuthKind.OAuth2, found.Auth);
        Assert.Contains("browser", found.Guidance);
    }

    /// <summary>
    /// Proton has no public mail server: Bridge serves the mailbox on this machine. The entry
    /// used to name <c>imap.mail.proton.me</c> and <c>smtp.mail.proton.me</c>, which have never
    /// resolved, and a test that checked only the port and the word "Bridge" passed against them.
    /// </summary>
    [Theory]
    [InlineData("someone@proton.me")]
    [InlineData("someone@protonmail.com")]
    [InlineData("someone@protonmail.ch")]
    [InlineData("someone@pm.me")]
    public void ProtonGoesThroughTheBridgeOnThisMachine(string address)
    {
        var found = Autoconfig.ForAddress(address);

        Assert.True(found.IsKnownProvider);
        Assert.Equal(MailProtocolKind.Imap, found.Protocol);
        Assert.Equal("127.0.0.1", found.Incoming.Host);
        Assert.Equal(1143, found.Incoming.Port);
        Assert.Equal("127.0.0.1", found.Outgoing.Host);
        Assert.Equal(1025, found.Outgoing.Port);
        Assert.Equal(address, found.Incoming.UserName);
        Assert.Equal(AuthKind.Password, found.Auth);
        Assert.Equal("Proton Mail Bridge", found.LocalService);
        Assert.Contains("password Bridge shows", found.Guidance);
    }

    /// <summary>
    /// Bridge has no POP3, and the wizard opens on POP. Guessing <c>pop.proton.me</c> — which is
    /// what asking for POP used to produce — named another server that does not exist; the
    /// answer is the IMAP the provider has, and the wizard follows the protocol it is given.
    /// </summary>
    [Fact]
    public void AskingProtonForPopGetsTheBridgesImap()
    {
        var found = Autoconfig.ForAddress("someone@proton.me", MailProtocolKind.Pop3);

        Assert.True(found.IsKnownProvider);
        Assert.Equal(MailProtocolKind.Imap, found.Protocol);
        Assert.Equal("127.0.0.1", found.Incoming.Host);
        Assert.Equal(1143, found.Incoming.Port);
        Assert.Contains("IMAP only", found.Guidance);
    }

    /// <summary>Only a provider reached through a program here names one.</summary>
    [Fact]
    public void AProviderWithItsOwnServersNamesNoLocalProgram()
    {
        Assert.Null(Autoconfig.ForAddress("someone@gmail.com").LocalService);
        Assert.Null(Autoconfig.ForAddress("someone@example.org").LocalService);
    }

    /// <summary>
    /// An account saved with one of the names that never existed is read as Bridge's address;
    /// every other host, the conventional guesses included, is left as the reader saved it.
    /// </summary>
    [Theory]
    [InlineData("imap.mail.proton.me", "127.0.0.1")]
    [InlineData("smtp.mail.proton.me", "127.0.0.1")]
    [InlineData("IMAP.Mail.Proton.Me", "127.0.0.1")]
    [InlineData("imap.gmail.com", "imap.gmail.com")]
    [InlineData("pop.proton.me", "pop.proton.me")]
    [InlineData("", "")]
    public void HostsThatNeverExistedAreReadAsTheBridge(string saved, string expected)
        => Assert.Equal(expected, Autoconfig.CurrentHost(saved));

    [Fact]
    public void AnUnknownDomainGetsTheConventionalNames()
    {
        var found = Autoconfig.ForAddress("someone@example.org");

        Assert.False(found.IsKnownProvider);
        Assert.Equal("imap.example.org", found.Incoming.Host);
        Assert.Equal("smtp.example.org", found.Outgoing.Host);
    }

    [Fact]
    public void AskingForPop3GetsPopNames()
    {
        var found = Autoconfig.ForAddress("someone@example.org", MailProtocolKind.Pop3);

        Assert.Equal("pop.example.org", found.Incoming.Host);
        Assert.Equal(995, found.Incoming.Port);
    }

    /// <summary>
    /// A provider in the table is only listed with its IMAP host. Asked for POP, the honest
    /// answer is a guess flagged as one, not an invented hostname presented as known.
    /// </summary>
    [Fact]
    public void AProviderWhosePopHostIsKnownGivesItRatherThanAGuess()
    {
        var found = Autoconfig.ForAddress("someone@gmail.com", MailProtocolKind.Pop3);

        Assert.True(found.IsKnownProvider);
        Assert.Equal("pop.gmail.com", found.Incoming.Host);
        Assert.Equal(995, found.Incoming.Port);
        Assert.Equal(MailProtocolKind.Pop3, found.Protocol);
        Assert.Equal(AuthKind.AppPassword, found.Auth);
        Assert.Contains("App Password", found.Guidance);
    }

    /// <summary>
    /// Both of Microsoft's services are on the one host, and the conventional guess —
    /// <c>pop.outlook.com</c> — resolves to nothing at all. An account added on the default
    /// protocol would otherwise be pointed at a server that does not exist.
    /// </summary>
    [Fact]
    public void ConsumerMailOverPopIsTheSameHostAsOverImap()
    {
        var found = Autoconfig.ForAddress("someone@outlook.com", MailProtocolKind.Pop3);

        Assert.True(found.IsKnownProvider);
        Assert.Equal("outlook.office365.com", found.Incoming.Host);
        Assert.Equal("smtp.office365.com", found.Outgoing.Host);
        Assert.Equal(AuthKind.OAuth2, found.Auth);
    }

    /// <summary>
    /// The rest still guess, which is the honest answer: inventing a hostname that fails at
    /// connect time is worse than saying these are a guess and opening the server settings.
    /// </summary>
    [Fact]
    public void AProviderWhosePopHostIsNotKnownStillGuessesAndSaysSo()
    {
        var found = Autoconfig.ForAddress("someone@yahoo.com", MailProtocolKind.Pop3);

        Assert.False(found.IsKnownProvider);
        Assert.Equal("pop.yahoo.com", found.Incoming.Host);
        Assert.Equal(AuthKind.AppPassword, found.Auth);
    }

    [Theory]
    [InlineData(993, SecureSocketOptions.SslOnConnect)]
    [InlineData(995, SecureSocketOptions.SslOnConnect)]
    [InlineData(465, SecureSocketOptions.SslOnConnect)]
    [InlineData(587, SecureSocketOptions.StartTls)]
    [InlineData(143, SecureSocketOptions.StartTls)]
    [InlineData(2525, SecureSocketOptions.Auto)]
    public void StandardPortsImplyTheirEncryption(int port, SecureSocketOptions expected)
        => Assert.Equal(expected, Autoconfig.Security(port));

    [Theory]
    [InlineData("someone@example.com", true)]
    [InlineData("a@b.co", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("two@at@signs.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("someone@", false)]
    [InlineData("someone@nodot", false)]
    [InlineData("has space@example.com", false)]
    public void AddressesAreCheckedLoosely(string address, bool expected)
        => Assert.Equal(expected, Autoconfig.LooksLikeAnAddress(address));

    [Fact]
    public void AnEmptyAddressDoesNotProduceHostsCalledNothing()
    {
        var found = Autoconfig.ForAddress("nonsense");

        Assert.Equal(string.Empty, found.Incoming.Host);
        Assert.False(found.Incoming.IsComplete);
    }
    // ---- A domain of the reader's own, found by its mail exchangers -------------------------

    /// <summary>
    /// A company on Microsoft 365: the MX names the tenant under mail.protection.outlook.com, and
    /// the account signs in through Microsoft rather than taking a password at a guessed host.
    /// </summary>
    [Theory]
    [InlineData("contoso-com.mail.protection.outlook.com")]
    [InlineData("contoso-com.l-v1.mx.microsoft")]
    public void ADomainOnMicrosoft365SignsInThroughMicrosoft(string exchanger)
    {
        var found = Autoconfig.ForAddress("pat@contoso.com", MailProtocolKind.Imap, [exchanger]);

        Assert.True(found.IsKnownProvider);
        Assert.Equal("Microsoft 365", found.HostedBy);
        Assert.Equal("outlook.office365.com", found.Incoming.Host);
        Assert.Equal("smtp.office365.com", found.Outgoing.Host);
        Assert.Equal("pat@contoso.com", found.Incoming.UserName);
        Assert.Equal(AuthKind.OAuth2, found.Auth);
        Assert.Same(OAuthProviders.Microsoft, OAuthProviders.For(found));
        Assert.Contains("Mail for contoso.com is handled by Microsoft 365", found.Guidance);
    }

    [Theory]
    [InlineData("aspmx.l.google.com")]
    [InlineData("alt3.aspmx.l.google.com")]
    [InlineData("aspmx2.googlemail.com")]
    [InlineData("smtp.google.com")]
    public void ADomainOnGoogleWorkspaceGetsGmailsServers(string exchanger)
    {
        var found = Autoconfig.ForAddress("pat@family.example", MailProtocolKind.Imap, [exchanger]);

        Assert.Equal("Google Workspace", found.HostedBy);
        Assert.Equal("imap.gmail.com", found.Incoming.Host);
        Assert.Equal(AuthKind.AppPassword, found.Auth);
        Assert.Null(OAuthProviders.For(found));
        Assert.Contains("Google Workspace", found.Guidance);
    }

    /// <summary>A custom domain on Proton goes through Bridge exactly as a proton.me address does.</summary>
    [Fact]
    public void ADomainOnProtonGoesThroughTheBridge()
    {
        var found = Autoconfig.ForAddress(
            "pat@mine.example", MailProtocolKind.Pop3, ["mail.protonmail.ch", "mailsec.protonmail.ch"]);

        Assert.Equal("Proton Mail", found.HostedBy);
        Assert.Equal(MailProtocolKind.Imap, found.Protocol);
        Assert.Equal("127.0.0.1", found.Incoming.Host);
        Assert.Equal("Proton Mail Bridge", found.LocalService);
    }

    [Fact]
    public void ADomainOnFastmailGetsFastmailsServers()
    {
        var found = Autoconfig.ForAddress("pat@mine.example", MailProtocolKind.Imap, ["in1-smtp.messagingengine.com"]);

        Assert.Equal("Fastmail", found.HostedBy);
        Assert.Equal("imap.fastmail.com", found.Incoming.Host);
    }

    /// <summary>
    /// A whole-label match only. A name that merely ends in the same letters, or carries the
    /// provider's name further up someone else's domain, is not the provider.
    /// </summary>
    [Theory]
    [InlineData("notaspmx.l.google.com")]
    [InlineData("aspmx.l.google.com.example.net")]
    [InlineData("mail.protection.outlook.com.example.net")]
    [InlineData("mx.example.net")]
    public void ExchangersThatAreNotTheProviderLeaveTheGuess(string exchanger)
    {
        var found = Autoconfig.ForAddress("pat@mine.example", MailProtocolKind.Imap, [exchanger]);

        Assert.False(found.IsKnownProvider);
        Assert.Null(found.HostedBy);
        Assert.Equal("imap.mine.example", found.Incoming.Host);
    }

    /// <summary>
    /// A filtering service usually comes first; the provider behind it is still found further
    /// down the list, in preference order.
    /// </summary>
    [Fact]
    public void TheFirstRecognisableExchangerInPreferenceOrderWins()
    {
        var found = Autoconfig.ForAddress("pat@mine.example", MailProtocolKind.Imap,
            ["mx1.filter.example.net", "aspmx.l.google.com", "mine-example.mail.protection.outlook.com"]);

        Assert.Equal("Google Workspace", found.HostedBy);
    }

    /// <summary>A domain the table holds is answered from the table, whatever its MX says.</summary>
    [Fact]
    public void AListedDomainIgnoresItsExchangers()
    {
        var found = Autoconfig.ForAddress("pat@gmail.com", MailProtocolKind.Imap, ["x.mail.protection.outlook.com"]);

        Assert.Equal("imap.gmail.com", found.Incoming.Host);
        Assert.Null(found.HostedBy);
    }

    [Fact]
    public async Task DiscoveryAsksAboutTheDomainAndUsesTheAnswer()
    {
        var lookup = new CountingMx(["contoso-com.mail.protection.outlook.com"]);

        var found = await Autoconfig.DiscoverAsync("pat@contoso.com", MailProtocolKind.Imap, lookup, Ct);

        Assert.Equal(["contoso.com"], lookup.Asked);
        Assert.Equal("Microsoft 365", found.HostedBy);
    }

    /// <summary>Nothing is asked about a domain in the table.</summary>
    [Fact]
    public async Task DiscoveryAsksNothingAboutAListedDomain()
    {
        var lookup = new CountingMx(["contoso-com.mail.protection.outlook.com"]);

        var found = await Autoconfig.DiscoverAsync("pat@outlook.com", MailProtocolKind.Imap, lookup, Ct);

        Assert.Empty(lookup.Asked);
        Assert.Null(found.HostedBy);
        Assert.Same(OAuthProviders.Microsoft, OAuthProviders.For(found));
    }

    /// <summary>A lookup that fails answers what no lookup would: the guess.</summary>
    [Fact]
    public async Task AFailedLookupLeavesTheGuess()
    {
        var lookup = new CountingMx(["contoso-com.mail.protection.outlook.com"], DnsResponseCode.ServerFailure);

        var found = await Autoconfig.DiscoverAsync("pat@contoso.com", MailProtocolKind.Imap, lookup, Ct);

        Assert.Null(found.HostedBy);
        Assert.Equal("imap.contoso.com", found.Incoming.Host);
    }

    /// <summary>The domains that sign in are read from the one table.</summary>
    [Theory]
    [InlineData("pat@outlook.com", true)]
    [InlineData("pat@passport.com", true)]
    [InlineData("pat@gmail.com", false)]
    [InlineData("pat@contoso.com", false)]
    public void ForMailReadsTheTable(string address, bool signsIn)
        => Assert.Equal(signsIn, OAuthProviders.ForMail(address) is not null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CountingMx(string[] hosts, DnsResponseCode code = DnsResponseCode.NoError) : IMxLookup
    {
        public List<string> Asked { get; } = [];

        public Task<DnsAnswer> MxAsync(string domain, CancellationToken cancellation = default)
        {
            Asked.Add(domain);
            return Task.FromResult(new DnsAnswer(code, code == DnsResponseCode.NoError ? hosts : [], 300));
        }
    }
}
