using MailKit.Security;

namespace Mailbox.Protocols;

/// <summary>How an account authenticates.</summary>
public enum AuthKind
{
    /// <summary>Ordinary username and password.</summary>
    Password,

    /// <summary>
    /// A password the provider issues for one application, used in place of the account
    /// password. Gmail and iCloud both require this once two-factor is on.
    /// </summary>
    AppPassword,

    /// <summary>OAuth2. Needs a browser round trip.</summary>
    OAuth2,
}

/// <summary>A guess at how to reach a provider, and how sure we are.</summary>
public sealed record AutoconfigResult(
    ServerSettings Incoming,
    ServerSettings Outgoing,
    MailProtocolKind Protocol,
    AuthKind Auth,
    bool IsKnownProvider)
{
    /// <summary>
    /// What the user has to be told before this will work. Empty for providers that take an
    /// ordinary password.
    /// </summary>
    public string? Guidance { get; init; }
}

/// <summary>Which incoming protocol a provider is configured for.</summary>
public enum MailProtocolKind
{
    Pop3,
    Imap,
}

/// <summary>
/// Works out server settings from an email address.
/// </summary>
/// <remarks>
/// Entirely local. A table of the providers most people use, then a guess from the domain —
/// no lookup service, no network round trip before the user has even typed a password, and
/// nothing that stops working when someone else's database moves.
/// <para>
/// The guess is deliberately conservative: implicit TLS on the standard ports, which is what
/// essentially every provider has offered for a decade. Where it is wrong the user corrects it
/// once, which is better than a wrong answer arrived at slowly.
/// </para>
/// </remarks>
public static class Autoconfig
{
    private sealed record Provider(
        string Incoming,
        int IncomingPort,
        string Outgoing,
        int OutgoingPort,
        MailProtocolKind Protocol,
        AuthKind Auth,
        string? Guidance = null,
        params string[] Domains);

    /// <summary>
    /// The providers worth knowing by heart. Ordered so the common ones are found first; the
    /// list is short on purpose, because a stale entry is worse than no entry.
    /// </summary>
    private static readonly Provider[] Known =
    [
        new("imap.gmail.com", 993, "smtp.gmail.com", 465, MailProtocolKind.Imap,
            AuthKind.AppPassword,
            "Gmail no longer accepts your ordinary password. With two-step verification on, "
            + "create an App Password at myaccount.google.com/apppasswords and use that here. "
            + "IMAP or POP also has to be switched on in Gmail's own settings.",
            "gmail.com", "googlemail.com"),

        new("outlook.office365.com", 993, "smtp.office365.com", 587, MailProtocolKind.Imap,
            AuthKind.OAuth2,
            "Microsoft accounts sign in through a browser. Mailbox will open one when you "
            + "continue.",
            "outlook.com", "hotmail.com", "live.com", "msn.com"),

        new("imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465, MailProtocolKind.Imap,
            AuthKind.AppPassword,
            "Yahoo requires an App Password, created under Account Security.",
            "yahoo.com", "yahoo.co.uk", "ymail.com"),

        new("imap.aol.com", 993, "smtp.aol.com", 465, MailProtocolKind.Imap,
            AuthKind.AppPassword,
            "AOL requires an App Password, created under Account Security.",
            "aol.com"),

        new("imap.fastmail.com", 993, "smtp.fastmail.com", 465, MailProtocolKind.Imap,
            AuthKind.AppPassword,
            "Fastmail requires an app password, created under Settings, Privacy & Security.",
            "fastmail.com", "fastmail.fm"),

        new("imap.mail.me.com", 993, "smtp.mail.me.com", 587, MailProtocolKind.Imap,
            AuthKind.AppPassword,
            "iCloud requires an app-specific password, created at appleid.apple.com.",
            "icloud.com", "me.com", "mac.com"),

        new("imap.gmx.com", 993, "mail.gmx.com", 465, MailProtocolKind.Imap, AuthKind.Password,
            null, "gmx.com", "gmx.net", "gmx.co.uk"),

        new("imap.zoho.com", 993, "smtp.zoho.com", 465, MailProtocolKind.Imap, AuthKind.Password,
            null, "zoho.com", "zohomail.com"),

        new("imap.mail.proton.me", 1143, "smtp.mail.proton.me", 1025, MailProtocolKind.Imap,
            AuthKind.Password,
            "Proton Mail is reached through Proton Mail Bridge, running on this machine. "
            + "The Bridge shows the username and password to use here.",
            "proton.me", "protonmail.com", "pm.me"),
    ];

    /// <summary>Settings for an address, from the table or guessed from its domain.</summary>
    public static AutoconfigResult ForAddress(string address, MailProtocolKind prefer = MailProtocolKind.Imap)
    {
        var domain = DomainOf(address);
        if (domain.Length == 0) return Guess(address, string.Empty, prefer);

        var provider = Known.FirstOrDefault(
            p => p.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));

        if (provider is null) return Guess(address, domain, prefer);

        // A known provider's IMAP host is not its POP host, and only a couple differ predictably,
        // so asking for POP where the table holds IMAP falls back to guessing rather than
        // inventing a hostname that will fail at connect time.
        if (prefer == MailProtocolKind.Pop3 && provider.Protocol == MailProtocolKind.Imap)
        {
            return Guess(address, domain, prefer) with
            {
                Auth = provider.Auth,
                Guidance = provider.Guidance,
                IsKnownProvider = false,
            };
        }

        return new AutoconfigResult(
            new ServerSettings(provider.Incoming, provider.IncomingPort,
                Security(provider.IncomingPort), address),
            new ServerSettings(provider.Outgoing, provider.OutgoingPort,
                Security(provider.OutgoingPort), address),
            provider.Protocol,
            provider.Auth,
            IsKnownProvider: true)
        {
            Guidance = provider.Guidance,
        };
    }

    /// <summary>
    /// The conventional names, for a domain nobody recognises. Right often enough to be worth
    /// offering, and presented as a guess rather than an answer.
    /// </summary>
    private static AutoconfigResult Guess(string address, string domain, MailProtocolKind prefer)
    {
        var incomingHost = domain.Length == 0
            ? string.Empty
            : (prefer == MailProtocolKind.Pop3 ? $"pop.{domain}" : $"imap.{domain}");

        var incomingPort = prefer == MailProtocolKind.Pop3 ? 995 : 993;

        return new AutoconfigResult(
            new ServerSettings(incomingHost, incomingPort, Security(incomingPort), address),
            new ServerSettings(domain.Length == 0 ? string.Empty : $"smtp.{domain}", 465,
                Security(465), address),
            prefer,
            AuthKind.Password,
            IsKnownProvider: false);
    }

    /// <summary>
    /// Encryption for a port. The standard ports are unambiguous, and guessing "auto" on them
    /// makes MailKit probe, which is slower and occasionally picks the wrong one on a server
    /// that advertises both.
    /// </summary>
    internal static SecureSocketOptions Security(int port) => port switch
    {
        465 or 993 or 995 => SecureSocketOptions.SslOnConnect,
        587 or 143 or 110 => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.Auto,
    };

    /// <summary>The part after the @, or empty when there is not one.</summary>
    public static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? string.Empty : address[(at + 1)..].Trim();
    }

    /// <summary>True when the address looks like one, which is all the wizard needs to know.</summary>
    public static bool LooksLikeAnAddress(string address)
    {
        var at = address.IndexOf('@');
        return at > 0
               && at == address.LastIndexOf('@')
               && at < address.Length - 1
               && address.AsSpan(at + 1).Contains('.')
               && !address.Any(char.IsWhiteSpace);
    }
}
