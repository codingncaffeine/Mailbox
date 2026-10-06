using MailKit.Security;
using Mailbox.Security.Dns;

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

    /// <summary>
    /// The authorization server an OAuth2 account signs in to — <c>microsoft</c> — or null. Carried
    /// in the answer because a domain recognised by its mail exchangers is one the sign-in cannot
    /// recognise by name.
    /// </summary>
    public string? OAuthProviderId { get; init; }

    /// <summary>
    /// The provider found behind a domain of the reader's own through its MX records, or null
    /// when the domain was in the table or nothing recognisable answered.
    /// </summary>
    public string? HostedBy { get; init; }
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
/// A table of the providers most people use, then a guess from the domain — no lookup service,
/// and nothing that stops working when someone else's database moves.
/// <para>
/// A domain of the reader's own is the one case the table cannot know, and the commonest of them
/// are hosted by a provider in it: a company on Microsoft 365, a family domain on Google. The
/// domain's MX records say which, so <see cref="DiscoverAsync"/> asks for them — one question, to
/// the resolver this machine already uses, about the domain the reader typed — and fills in that
/// provider's servers. It is what the reference and Thunderbird both do, and nothing about the
/// address goes anywhere a DNS question about its domain would not.
/// </para>
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

        /// <summary>What the provider is called, for a domain it was recognised behind.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// The mail exchangers that give this provider away, as a host or the domain a host sits
        /// under: a domain whose MX is one of them has its mail here. Empty for a provider that
        /// does not host other people's domains, or whose servers for them are not these.
        /// </summary>
        public string[] Exchangers { get; init; } = [];

        /// <summary>What to tell someone whose own domain this provider hosts, when it differs.</summary>
        public string? HostedGuidance { get; init; }

        /// <summary>The authorization server an OAuth2 account here signs in to.</summary>
        public string? SignIn { get; init; }
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
            "gmail.com", "googlemail.com")
        {
            PopIncoming = "pop.gmail.com",
            Name = "Google Workspace",

            // aspmx.l.google.com and its alt1–4, the aspmx2–5.googlemail.com backups, and the one
            // name a domain set up since 2023 is told to publish.
            Exchangers = ["aspmx.l.google.com", "googlemail.com", "smtp.google.com"],
            HostedGuidance =
                "Google Workspace no longer accepts your ordinary password. With two-step "
                + "verification on, create an App Password at myaccount.google.com/apppasswords "
                + "and use that here. Your administrator may have to allow IMAP for your account.",
        },

        new("outlook.office365.com", 993, "smtp.office365.com", 587, MailProtocolKind.Imap,
            AuthKind.OAuth2,
            "Microsoft accounts sign in through a browser. Mailbox will open one when you "
            + "continue.",
            "outlook.com", "hotmail.com", "live.com", "msn.com", "passport.com")
        {
            // Both services are on the same host here, so a POP3 account gets a name that
            // resolves rather than the "pop.<domain>" the guess would invent.
            PopIncoming = "outlook.office365.com",
            Name = "Microsoft 365",
            SignIn = "microsoft",

            // <tenant>.mail.protection.outlook.com, and <tenant>.<label>.mx.microsoft for the
            // domains set up with DNSSEC since 2024.
            Exchangers = ["mail.protection.outlook.com", "mx.microsoft"],
            HostedGuidance =
                "Microsoft 365 accounts sign in through a browser. Your organisation may have to "
                + "approve Mailbox, and have IMAP and SMTP sign-in switched on for you, before it "
                + "can collect and send mail.",
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
            "fastmail.com", "fastmail.fm")
        {
            Name = "Fastmail",
            Exchangers = ["messagingengine.com"],
        },

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
            Name = "Proton Mail",
            Exchangers = ["mail.protonmail.ch", "mailsec.protonmail.ch"],
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

    /// <summary>
    /// Settings for an address: from the table, from the provider its domain's mail exchangers
    /// name, or guessed from the domain.
    /// </summary>
    /// <param name="address">The address being added.</param>
    /// <param name="prefer">The protocol the reader chose, which a provider without it overrides.</param>
    /// <param name="exchangers">
    /// The domain's MX hosts, most preferred first, when they have been asked for; consulted only
    /// for a domain the table does not hold.
    /// </param>
    public static AutoconfigResult ForAddress(
        string address, MailProtocolKind prefer = MailProtocolKind.Imap, IReadOnlyList<string>? exchangers = null)
    {
        var domain = DomainOf(address);
        if (domain.Length == 0) return Guess(address, string.Empty, prefer);

        var provider = Listed(domain);
        var hosted = false;

        if (provider is null && exchangers is { Count: > 0 })
        {
            provider = HostOf(exchangers);
            hosted = provider is not null;
        }

        if (provider is null) return Guess(address, domain, prefer);

        var guidance = hosted
            ? $"Mail for {domain} is handled by {provider.Name}. {provider.HostedGuidance ?? provider.Guidance}"
            : provider.Guidance;
        var signIn = provider.Auth == AuthKind.OAuth2 ? provider.SignIn : null;

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
                    Guidance = guidance,
                    IsKnownProvider = false,
                    OAuthProviderId = signIn,
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
            Guidance = guidance,
            LocalService = provider.LocalService,
            OAuthProviderId = signIn,
            HostedBy = hosted ? provider.Name : null,
        };
    }

    /// <summary>
    /// Settings for an address, asking the domain's MX records who hosts it when the table does
    /// not know the domain.
    /// </summary>
    /// <remarks>
    /// Nothing is asked about a domain in the table, and a lookup that fails or finds nothing
    /// recognisable answers exactly what <see cref="ForAddress"/> does without one: the guess.
    /// </remarks>
    public static async Task<AutoconfigResult> DiscoverAsync(
        string address, MailProtocolKind prefer, IMxLookup lookup, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var domain = DomainOf(address);
        if (domain.Length == 0 || Listed(domain) is not null) return ForAddress(address, prefer);

        var answer = await lookup.MxAsync(domain, cancellation).ConfigureAwait(false);
        return ForAddress(address, prefer, answer.Resolved ? answer.Records : null);
    }

    /// <summary>Whether the table holds a domain, so there is nothing to ask about it.</summary>
    public static bool IsListed(string address) => Listed(DomainOf(address)) is not null;

    private static Provider? Listed(string domain)
        => Known.FirstOrDefault(p => p.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The provider behind a set of mail exchangers, trying them in preference order.
    /// </summary>
    /// <remarks>
    /// A whole label match — the host itself, or a host under it — so that a name merely ending
    /// in the same letters is not taken for the provider.
    /// </remarks>
    private static Provider? HostOf(IReadOnlyList<string> exchangers)
    {
        foreach (var raw in exchangers)
        {
            var host = raw.Trim().TrimEnd('.');
            if (host.Length == 0) continue;

            foreach (var provider in Known)
            {
                foreach (var suffix in provider.Exchangers)
                {
                    if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                        || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return provider;
                    }
                }
            }
        }

        return null;
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
