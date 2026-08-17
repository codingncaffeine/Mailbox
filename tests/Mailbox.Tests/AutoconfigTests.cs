using MailKit.Security;
using Mailbox.Protocols;

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

    [Fact]
    public void ProtonIsDescribedAsNeedingTheBridge()
    {
        var found = Autoconfig.ForAddress("someone@proton.me");

        Assert.Contains("Bridge", found.Guidance);
        Assert.Equal(1143, found.Incoming.Port);
    }

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
    public void OutlookOverPopIsTheSameHostAsOverImap()
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
}
