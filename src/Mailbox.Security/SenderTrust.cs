using MimeKit;

namespace Mailbox.Security;

/// <summary>How loudly the reading pane should say something.</summary>
public enum TrustLevel
{
    /// <summary>Nothing to say. Most mail, most of the time.</summary>
    Quiet,

    /// <summary>Worth a line, not worth alarming anyone.</summary>
    Caution,

    /// <summary>Say it prominently: this looks like an attempt to be someone else.</summary>
    Alarm,
}

/// <summary>Something specific that is wrong, in words a reader can act on.</summary>
public sealed record TrustWarning(TrustLevel Level, string Headline, string Detail);

/// <summary>
/// What a message claims about who sent it, and whether it holds up.
/// </summary>
/// <remarks>
/// The reference hides all of this; showing it is rule 4. The bar is quiet when everything
/// passes — a warning shown on every message is one nobody reads — and prominent when the
/// pattern is one that is nearly always an attack.
/// </remarks>
public sealed record SenderTrust(
    AuthenticationResults Authentication,
    IReadOnlyList<TrustWarning> Warnings)
{
    /// <summary>
    /// What checking the signature here came to, when that has been done.
    /// </summary>
    /// <remarks>
    /// Null for every message received before the check existed, or received with no resolver
    /// to hand. Null means <em>not checked</em>, which is not a result and is never shown as one.
    /// </remarks>
    public DkimResult? Verified { get; init; }

    public TrustLevel Level => Warnings.Count == 0
        ? TrustLevel.Quiet
        : Warnings.Max(w => w.Level);

    /// <summary>The line the bar shows, or null when there is nothing to say.</summary>
    public string? Headline => Warnings.Count == 0
        ? null
        : Warnings.OrderByDescending(w => w.Level).First().Headline;

    /// <summary>
    /// Evaluates a message.
    /// </summary>
    /// <param name="familiarDomains">
    /// Domains this reader deals with, for the typosquat check. Their own accounts and everyone
    /// they have exchanged mail with; see <see cref="LookalikeDomains"/> for why a list of
    /// famous brands is the wrong input.
    /// </param>
    /// <param name="verified">
    /// What checking the signature here came to, or null for a message that has not been
    /// checked. Never resolved from inside this method: the lookup happens when mail is
    /// received, and this is called to draw a message. See §19.
    /// </param>
    public static SenderTrust Evaluate(
        MimeMessage message,
        IEnumerable<string>? familiarDomains = null,
        DkimResult? verified = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        var results = AuthenticationResults.Read(message);
        var warnings = new List<TrustWarning>();

        var from = message.From.Mailboxes.FirstOrDefault();
        var domain = DomainOf(from?.Address);

        // A signature checked here outranks the same claim read out of a header, because the
        // header was written by a server and this was checked against the bytes in the store.
        var signedHere = verified?.Verdict == AuthVerdict.Pass;

        if (results.Failed)
        {
            warnings.Add(new TrustWarning(
                TrustLevel.Alarm,
                "This message failed the sending domain's own checks.",
                $"{domain} publishes a policy saying which servers may send its mail, and this "
                + "message did not come from one. Treat any request in it as unverified."));
        }
        else if (results.Spf is AuthVerdict.Fail or AuthVerdict.SoftFail
                 && results.Dkim is not AuthVerdict.Pass && !signedHere)
        {
            warnings.Add(new TrustWarning(
                TrustLevel.Caution,
                "This message was not sent from an address the domain recognises.",
                "That is normal for mail forwarded from another address or sent through a "
                + "mailing list, and is worth knowing about otherwise."));
        }

        // Checked here and it did not verify. Caution rather than alarm on purpose: a mailing
        // list that appends a footer breaks the signature of every message it passes on, and
        // that is the commonest cause by a wide margin. It is still worth saying, because the
        // other cause is that the message is not what it says it is.
        if (verified?.Verdict == AuthVerdict.Fail)
        {
            warnings.Add(new TrustWarning(
                TrustLevel.Caution,
                "The copy of this message here does not match the signature it carries.",
                $"{verified.SigningDomain ?? "The signing domain"} signed this message, and the "
                + "copy that arrived does not match that signature. A mailing list or forwarder "
                + "that adds a footer does this to every message it passes on. So does a message "
                + "that has been altered."));
        }

        if (SpoofedDisplayName(from) is { } spoofed)
        {
            warnings.Add(new TrustWarning(
                TrustLevel.Alarm,
                "The sender's name disagrees with their address.",
                $"This message displays as \"{from!.Name}\" but was sent from "
                + $"{from.Address}. {spoofed}"));
        }

        if (domain is { Length: > 0 })
        {
            if (LookalikeDomains.IsHomograph(domain))
            {
                warnings.Add(new TrustWarning(
                    TrustLevel.Alarm,
                    "The sender's domain is not written in the alphabet it appears to be.",
                    $"{domain} mixes characters from more than one script, or is an encoded "
                    + "name. Domains that do this in mail are almost always imitating another."));
            }
            else if (LookalikeDomains.Imitates(domain, familiarDomains ?? []) is { } imitated)
            {
                warnings.Add(new TrustWarning(
                    TrustLevel.Alarm,
                    $"This looks like {imitated}, but it is not.",
                    $"The message was sent from {domain}, which is one character away from "
                    + $"{imitated} — a domain you correspond with."));
            }
        }

        return new SenderTrust(results, warnings) { Verified = verified };
    }

    /// <summary>
    /// The single most common phishing pattern, and trivially detectable: a display name that
    /// is itself an address, disagreeing with the address it was sent from.
    /// </summary>
    private static string? SpoofedDisplayName(MailboxAddress? from)
    {
        if (from?.Name is not { Length: > 0 } name) return null;
        if (!name.Contains('@', StringComparison.Ordinal)) return null;

        var claimed = name.Split(['<', '>', ' ', '\t', '"'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.Contains('@', StringComparison.Ordinal));

        if (claimed is null) return null;

        var claimedDomain = DomainOf(claimed);
        var actualDomain = DomainOf(from.Address);

        if (claimedDomain.Length == 0 || actualDomain.Length == 0) return null;

        return string.Equals(claimedDomain, actualDomain, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The name claims {claimedDomain}; the message came from {actualDomain}.";
    }

    private static string DomainOf(string? address)
    {
        if (address is null) return string.Empty;

        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? string.Empty : address[(at + 1)..].Trim();
    }
}
