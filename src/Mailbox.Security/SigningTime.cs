using System.Globalization;
using MimeKit;

namespace Mailbox.Security;

/// <summary>
/// Whether the time a signature claims it was made can be believed.
/// </summary>
/// <remarks>
/// §19, and the same finding against both algorithms, which is why it is stated once. The time a
/// signature carries is the signer's own claim and nothing checks it: RFC 5652 §11.3 says
/// <c>signingTime</c> carries no guarantee, and OpenPGP's creation time is a subpacket like any
/// other. MimeKit then builds an S/MIME chain <em>as of</em> that value, so a far-future date picks
/// a moment when a since-revoked certificate was still good, and pins an S/MIME capability besides.
/// Thunderbird had this twice — CVE-2022-2226 in OpenPGP and CVE-2023-50761 in S/MIME eighteen
/// months later.
/// </remarks>
public static class SigningTime
{
    /// <summary>How far a signature's own time may be from the message's before it is refused.</summary>
    /// <remarks>
    /// A day either way: clocks disagree, and a message may sit in a queue. What this stops is the
    /// far-future or far-past value, not the ordinary drift between two machines.
    /// </remarks>
    public static readonly TimeSpan Tolerance = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether a signature's claimed time is close enough to the message's date to be believed.
    /// </summary>
    /// <param name="why">One sentence for the reader when it is not. Empty when it is.</param>
    public static bool Agrees(DateTime claimed, MimeMessage message, out string why)
    {
        ArgumentNullException.ThrowIfNull(message);

        why = string.Empty;

        // No claim is not a false claim: a signature that says nothing about when it was made is
        // judged on everything else.
        if (claimed == default) return true;

        var made = claimed.ToUniversalTime();
        var sent = message.Date == default ? DateTimeOffset.UtcNow : message.Date.ToUniversalTime();

        if ((made - sent).Duration() <= Tolerance) return true;

        why = "The time this message says it was signed — "
              + made.ToString("u", CultureInfo.InvariantCulture)
              + " — disagrees with the time it was sent.";
        return false;
    }
}
