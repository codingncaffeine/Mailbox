using System.Text.RegularExpressions;
using MimeKit;

namespace Mailbox.Security;

/// <summary>What a check said, in the vocabulary RFC 8601 uses.</summary>
public enum AuthVerdict
{
    /// <summary>Not checked, or the header does not mention it.</summary>
    None,

    Pass,
    Fail,

    /// <summary>SPF's <c>~all</c>: the domain says it is probably not theirs.</summary>
    SoftFail,

    Neutral,

    /// <summary>The check could not run. Not the sender's fault and not a pass.</summary>
    Error,
}

/// <summary>
/// The results a receiving server recorded, read out of <c>Authentication-Results</c>.
/// </summary>
/// <remarks>
/// Reading the header rather than verifying locally, for now. DKIM verification needs the
/// signing domain's public key from DNS, and a reading pane that resolves a name chosen by the
/// sender is a reading pane that talks to the network on the sender's behalf — which is the one
/// thing §11 exists to prevent. Doing it properly means a resolver that is ours, on our
/// schedule, and that is Phase 8 work.
/// <para>
/// The header is only as good as the server that wrote it, so only the topmost one is read: it
/// was added by the last hop, which for a POP3 account is the provider we authenticated to.
/// Anything below it was written by a machine the sender may control.
/// </para>
/// </remarks>
public sealed partial record AuthenticationResults(
    AuthVerdict Dkim,
    AuthVerdict Spf,
    AuthVerdict Dmarc,
    string? SigningDomain)
{
    public static AuthenticationResults None { get; } =
        new(AuthVerdict.None, AuthVerdict.None, AuthVerdict.None, null);

    [GeneratedRegex(@"\b(?<method>dkim|spf|dmarc)\s*=\s*(?<verdict>[a-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Method { get; }

    [GeneratedRegex(@"\bheader\.d\s*=\s*(?<domain>[^\s;()]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SigningDomainOf { get; }

    /// <summary>True when the sending domain was checked at all.</summary>
    public bool WasChecked =>
        Dkim != AuthVerdict.None || Spf != AuthVerdict.None || Dmarc != AuthVerdict.None;

    /// <summary>
    /// True when a check the sender's own domain asked for came back against them.
    /// </summary>
    /// <remarks>
    /// DMARC is the one that carries weight: it is the domain owner's own policy, and a failure
    /// means the domain says this message is not theirs. A bare SPF failure is common in
    /// forwarded and mailing-list mail and does not mean the same thing.
    /// </remarks>
    public bool Failed => Dmarc is AuthVerdict.Fail
                          || (Dkim is AuthVerdict.Fail && Spf is AuthVerdict.Fail);

    public static AuthenticationResults Read(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        foreach (var header in message.Headers)
        {
            if (!string.Equals(header.Field, "Authentication-Results", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parsed = Parse(header.Value);
            if (parsed.WasChecked) return parsed;
        }

        return None;
    }

    internal static AuthenticationResults Parse(string header)
    {
        var dkim = AuthVerdict.None;
        var spf = AuthVerdict.None;
        var dmarc = AuthVerdict.None;

        foreach (Match match in Method.Matches(header))
        {
            var verdict = Verdict(match.Groups["verdict"].Value);

            switch (match.Groups["method"].Value.ToLowerInvariant())
            {
                case "dkim": dkim = Stronger(dkim, verdict); break;
                case "spf": spf = Stronger(spf, verdict); break;
                case "dmarc": dmarc = Stronger(dmarc, verdict); break;
            }
        }

        var domain = SigningDomainOf.Match(header) is { Success: true } d
            ? d.Groups["domain"].Value.Trim().Trim('"')
            : null;

        return new AuthenticationResults(dkim, spf, dmarc, domain);
    }

    /// <summary>
    /// A header may carry the same method twice — two DKIM signatures, one good and one not.
    /// A pass on any of them is a pass, which is what the specification says.
    /// </summary>
    private static AuthVerdict Stronger(AuthVerdict current, AuthVerdict next)
        => current == AuthVerdict.Pass || next == AuthVerdict.Pass ? AuthVerdict.Pass
            : current == AuthVerdict.None ? next
            : current;

    private static AuthVerdict Verdict(string value) => value.ToLowerInvariant() switch
    {
        "pass" => AuthVerdict.Pass,
        "fail" => AuthVerdict.Fail,
        "softfail" => AuthVerdict.SoftFail,
        "neutral" or "policy" => AuthVerdict.Neutral,
        "temperror" or "permerror" or "error" => AuthVerdict.Error,
        _ => AuthVerdict.None,
    };
}
