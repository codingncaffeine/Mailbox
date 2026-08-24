using System.Globalization;
using MimeKit;
using MimeKit.Utils;

namespace Mailbox.Security;

/// <summary>
/// Which header fields an encrypted message keeps out of its own outer header section.
/// </summary>
/// <remarks>
/// RFC 9788 §3 calls this a Header Confidentiality Policy, and the three named here are the three it
/// registers. A policy is only ever needed for encryption: integrity and authenticity are applied to
/// every header field alike, and it is confidentiality that has to choose, because a message with no
/// header section at all is one some transport agents drop.
/// <para>
/// <b>The reader is never asked which one.</b> §3.3 says an MUA must have a default that hides the
/// subject at least and should not make its user pick, so <see cref="Baseline"/> is what the
/// application uses and the other two exist to be compared against it — <see cref="Shy"/> because it
/// is registered and cheap to offer if a reader ever wants it, and
/// <see cref="NoConfidentiality"/> because it is what every client that has no policy at all is
/// doing, and naming that is more useful than pretending it is not a policy.
/// </para>
/// </remarks>
public enum HeaderConfidentiality
{
    /// <summary>
    /// The recommended default: the subject is obscured, Comments and Keywords are removed.
    /// </summary>
    /// <remarks>
    /// Conservative on purpose. Most messages have a subject and some filtering engines object to one
    /// that has none, so the subject is replaced rather than removed; Comments and Keywords are rare
    /// enough that removing them outright costs nothing. All three are RFC 5322 §3.6.5's
    /// informational fields — human-readable content that no transport agent has any business in,
    /// which is exactly what a reader assumes about a subject and is usually wrong about.
    /// </remarks>
    Baseline,

    /// <summary>
    /// Baseline, and also no display names on the addresses and no local time zone on the date.
    /// </summary>
    /// <remarks>
    /// More ambitious, and more parsing: it rewrites structured fields rather than replacing them.
    /// Not the default — §3.2.2 says as much — because the failure mode is a message an MTA mangles
    /// or refuses rather than one that merely says less.
    /// </remarks>
    Shy,

    /// <summary>
    /// Nothing is made confidential. What a client with no policy does, named so it can be refused.
    /// </summary>
    /// <remarks>
    /// §3.2.3: a conformant MUA must not use this by default. It is here so the sending side can be
    /// tested against a policy that hides nothing, which is the case where every header field's
    /// protection state comes out signed-only rather than signed-and-encrypted.
    /// </remarks>
    NoConfidentiality,
}

/// <summary>The three registered policies, as the one function RFC 9788 §3.1 defines them by.</summary>
public static class HeaderConfidentialityPolicy
{
    /// <summary>What an obscured subject is replaced with, verbatim from §3.2.1.</summary>
    public const string Obscured = "[...]";

    /// <summary>The informational fields a policy removes rather than obscures.</summary>
    private static readonly string[] Removed = ["comments", "keywords"];

    /// <summary>
    /// What one header field becomes outside the encryption, or null when it is removed entirely.
    /// </summary>
    /// <remarks>
    /// RFC 9788's <c>hcp(name, val_in) -&gt; val_out</c>. The answer must be the value it was handed,
    /// null, or printable ASCII (§3.1) — anything else and the copy of it that goes inside the
    /// encryption cannot be written as a header field value at all.
    /// <para>
    /// <b>The From address itself is never altered</b> (§3.1.1): a rendering client that finds the
    /// inner and outer From disagreeing has to treat the message as a possible spoof, so a policy
    /// that rewrites the address rather than the display name would make every message it touched
    /// look like an attack.
    /// </para>
    /// </remarks>
    public static string? Outside(this HeaderConfidentiality policy, string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        if (policy == HeaderConfidentiality.NoConfidentiality) return value;

        var field = name.ToLowerInvariant();

        if (field == "subject") return Obscured;
        if (Array.IndexOf(Removed, field) >= 0) return null;
        if (policy != HeaderConfidentiality.Shy) return value;

        return field switch
        {
            "from" or "sender" => Addresses(value, one: true) ?? value,
            "to" or "cc" => Addresses(value, one: false) ?? value,
            "date" => Utc(value) ?? value,
            _ => value,
        };
    }

    /// <summary>Whether this policy makes that header field confidential at all.</summary>
    /// <remarks>
    /// What answers the other half of §6.1: a value that was confidential in the message being
    /// answered must not go out in the clear in the answer, and the way to know whether it would is
    /// to ask this policy whether it would have hidden it too.
    /// </remarks>
    public static bool Hides(this HeaderConfidentiality policy, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Compared against a value it cannot leave alone, because "does this policy touch this field"
        // is not answerable for the structured ones without a value to try it on. The date probe
        // carries a fixed non-UTC offset on purpose: a policy that hides Date does it by taking
        // the zone off, and probing with this machine's own "now" made the answer depend on the
        // machine — on a UTC host the rewrite returned the probe unchanged and Date read as not
        // hidden, which is the wrong answer precisely where servers run.
        var probe = name.Equals("date", StringComparison.OrdinalIgnoreCase)
            ? DateUtils.FormatDate(new DateTimeOffset(2021, 2, 20, 10, 9, 2, TimeSpan.FromHours(-5)))
            : "a";

        var outside = policy.Outside(name, probe);
        return outside is null || !string.Equals(outside, probe, StringComparison.Ordinal);
    }

    /// <summary>The addresses in a field with the display names taken off, or null if it will not parse.</summary>
    /// <remarks>
    /// A field that does not parse as an address list is left exactly as it is: rewriting something
    /// this does not understand is how a policy turns a deliverable message into an undeliverable one.
    /// </remarks>
    private static string? Addresses(string value, bool one)
    {
        if (!InternetAddressList.TryParse(value, out var list) || list.Count == 0) return null;

        var addresses = new List<string>();
        foreach (var address in list)
        {
            // A group has no address of its own, and taking the names off the people in it would
            // change who the field says it is going to.
            if (address is not MailboxAddress mailbox) return null;
            addresses.Add(mailbox.Address);
        }

        if (one && addresses.Count != 1) return null;
        return string.Join(", ", addresses);
    }

    /// <summary>The same instant written in UTC, which is the date without the composer's zone.</summary>
    private static string? Utc(string value)
        => DateUtils.TryParse(value, out var date)
            ? date.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)
            : null;
}
