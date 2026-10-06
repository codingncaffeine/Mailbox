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

    /// <summary>
    /// The program on this machine the account goes through — Proton Mail Bridge — or null for a
    /// provider whose servers are its own. Named so a refused connection can say which program is
    /// not running instead of sending the reader to correct settings that are right.
    /// </summary>
    public string? LocalService { get; init; }
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
        params string[] Domains)
    {
        /// <summary>
        /// The POP3 host, for a provider whose IMAP host is not it. Empty means "guess", which
        /// is right for most of them and wrong for the few whose two services are named
        /// differently.
        /// </summary>
        public string PopIncoming { get; init; } = string.Empty;

        public int PopPort { get; init; } = 995;

        /// <summary>
        /// True for a provider that has no POP3 service at all, so asking for POP gets its IMAP
        /// settings rather than a guessed <c>pop.</c> name that resolves to nothing.
        /// </summary>
        public bool ImapOnly { get; init; }

        /// <summary>The program on this machine that serves the account, for one reached through one.</summary>
        public string? LocalService { get; init; }
    }

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
            "gmail.com", "googlemail.com") { PopIncoming = "pop.gmail.com" },

        new("outlook.office365.com", 993, "smtp.office365.com", 587, MailProtocolKind.Imap,
            AuthKind.OAuth2,
            "Microsoft accounts sign in through a browser. Mailbox will open one when you "
            + "continue.",
            "outlook.com", "hotmail.com", "live.com", "msn.com")
        {
            // Both services are on the same host here, so a POP3 account gets a name that
            // resolves rather than the "pop.<domain>" the guess would invent.
            PopIncoming = "outlook.office365.com",
        },

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

        // Proton has no public mail server. Bridge, a program on this machine, decrypts the
        // mailbox and serves it over IMAP and SMTP on the loopback address, STARTTLS on both
        // ports with a certificate of its own making — which the wizard's certificate question
        // pins once, like any other self-signed server. Bridge has no POP3.
        new(BridgeHost, 1143, BridgeHost, 1025, MailProtocolKind.Imap,
            AuthKind.Password,
            "Proton Mail is reached through Proton Mail Bridge, which runs on this machine and "
            + "comes with Proton's paid plans. Start Bridge and sign in to it first; use the "
            + "password Bridge shows for this address, not your Proton password. Bridge offers "
            + "IMAP only.",
            "proton.me", "protonmail.com", "protonmail.ch", "pm.me")
        {
            ImapOnly = true,
            LocalService = "Proton Mail Bridge",
        },
    ];

    /// <summary>Where Proton Mail Bridge listens: this machine, by address rather than by name.</summary>
    private const string BridgeHost = "127.0.0.1";

    /// <summary>
    /// Host names this table once handed out for servers that never existed, and what they stand for.
    /// </summary>
    /// <remarks>
    /// Before Bridge's own address was in the table, a Proton account was given these two names,
    /// neither of which has ever resolved. An account saved with one has never collected a message,
    /// so reading it as the address it was meant to be cannot break anything that worked.
    /// </remarks>
    private static readonly Dictionary<string, string> Superseded = new(StringComparer.OrdinalIgnoreCase)
    {
        ["imap.mail.proton.me"] = BridgeHost,
        ["smtp.mail.proton.me"] = BridgeHost,
    };

    /// <summary>The host an account saved with <paramref name="host"/> should use now.</summary>
    public static string CurrentHost(string host)
        => Superseded.TryGetValue(host.Trim(), out var current) ? current : host;

    /// <summary>Settings for an address, from the table or guessed from its domain.</summary>
    public static AutoconfigResult ForAddress(string address, MailProtocolKind prefer = MailProtocolKind.Imap)
    {
        var domain = DomainOf(address);
        if (domain.Length == 0) return Guess(address, string.Empty, prefer);

        var provider = Known.FirstOrDefault(
            p => p.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));

        if (provider is null) return Guess(address, domain, prefer);

        // A known provider's IMAP host is not always its POP host. The ones whose POP name is
        // documented and stable carry it; for the rest, asking for POP where the table holds IMAP
        // falls back to guessing rather than inventing a hostname that will fail at connect time.
        // A provider with no POP service answers with what it does have, and says which: the
        // wizard follows the protocol in the answer, so the account is not added as POP against a
        // server that will never speak it.
        if (prefer == MailProtocolKind.Pop3 && provider.Protocol == MailProtocolKind.Imap && !provider.ImapOnly)
        {
            if (provider.PopIncoming.Length == 0)
            {
                return Guess(address, domain, prefer) with
                {
                    Auth = provider.Auth,
                    Guidance = provider.Guidance,
                    IsKnownProvider = false,
                };
            }

            provider = provider with
            {
                Incoming = provider.PopIncoming,
                IncomingPort = provider.PopPort,
                Protocol = MailProtocolKind.Pop3,
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
            LocalService = provider.LocalService,
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
    /// <remarks>
    /// Public because it is the answer for a port a <em>reader</em> typed as much as for one this
    /// table guessed. The account wizard offers a port box and no encryption box, so the port is
    /// the only thing there that can decide the question; every result in this file is built from
    /// this method, so asking it again about a corrected port gives the same answer wherever the
    /// port was not corrected.
    /// </remarks>
    public static SecureSocketOptions Security(int port) => port switch
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
