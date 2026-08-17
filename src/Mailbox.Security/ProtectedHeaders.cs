using System.Globalization;
using MimeKit;

namespace Mailbox.Security;

/// <summary>What the composer of a protected message said it wanted for its header fields.</summary>
/// <remarks>
/// The two values RFC 9788's <c>hp</c> parameter can take. It is the composer's <em>intent</em>
/// rather than a description of the message in hand, which is the distinction §2.1.1 insists on: a
/// message can be encrypted in transit by something that is not its author, so
/// <see cref="Cipher"/> is the only thing that says the author meant a header field to be
/// confidential, and the presence of an encryption layer is never taken for it.
/// </remarks>
public enum HeaderProtectionIntent
{
    /// <summary>Signed with header protection, and nothing about it was meant to be confidential.</summary>
    Clear,

    /// <summary>Signed with header protection, encrypted to its recipients, and some of it hidden.</summary>
    Cipher,
}

/// <summary>What protection one header field of a message actually got.</summary>
/// <remarks>
/// RFC 9788 §4.3's four states. A field's state can be weaker than the message's: a subject that was
/// copied unaltered to the outside of an encrypted message is signed, and not confidential, however
/// well encrypted the body it sits above is.
/// </remarks>
public enum HeaderFieldProtection
{
    /// <summary>No end-to-end protection at all — an unprotected message, or a field added in transit.</summary>
    Unprotected,

    /// <summary>Inside the signature, and visible the whole way.</summary>
    SignedOnly,

    /// <summary>Inside the encryption, under a signature that did not hold or was not there.</summary>
    EncryptedOnly,

    /// <summary>Inside the encryption and under a signature that held. The strongest of the four.</summary>
    SignedAndEncrypted,
}

/// <summary>One header field as it was found, in the section it was found in.</summary>
public sealed record ProtectedField(string Name, string Value);

/// <summary>
/// The header fields of a message that carried its own, and what became of them on the way out.
/// </summary>
/// <param name="Stated">
/// True when the composer said so with RFC 9788's own <c>hp</c> parameter. False when it was inferred
/// from the shape of the message instead — an older scheme (§4.10, §4.11), where the inference rests
/// on nothing an attacker could not have arranged, and which is why the two are told apart at all.
/// </param>
/// <param name="Rendered">
/// The entity whose content is the message: the cryptographic payload itself, or for the older
/// wrapped scheme the message inside it. Never the envelope it arrived in.
/// </param>
/// <param name="Fields">
/// The protected header fields, in the order the payload carries them. RFC 9788's
/// <c>refprotected</c>.
/// </param>
/// <param name="Outer">
/// What the composer put <em>outside</em> the encryption, as the payload itself records it.
/// RFC 9788's <c>refouter</c>, read from the <c>HP-Outer</c> fields — and so, unlike the message's
/// actual outer header section, covered by the same signature as everything else.
/// </param>
public sealed record ProtectedHeaders(
    HeaderProtectionIntent Intent,
    bool Stated,
    MimeEntity Rendered,
    IReadOnlyList<ProtectedField> Fields,
    IReadOnlyList<ProtectedField> Outer)
{
    /// <summary>The first protected value of that field, or null when the payload carries none.</summary>
    public string? Value(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        foreach (var field in Fields)
        {
            if (field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return field.Value;
        }

        return null;
    }

    /// <summary>
    /// Whether that field was kept from anyone who watched the message travel.
    /// </summary>
    /// <remarks>
    /// §4.5.2: a field is confidential when the payload records no <c>HP-Outer</c> copy of it with
    /// the same value — either because the composer removed it from the outside or because what they
    /// left there says something else. Only ever true of an encrypted message: for
    /// <see cref="HeaderProtectionIntent.Clear"/> nothing was meant to be hidden, and an encryption
    /// layer somebody else added does not make it so.
    /// </remarks>
    public bool Confidential(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Intent != HeaderProtectionIntent.Cipher) return false;

        if (Value(name) is not { } value) return false;

        foreach (var outer in Outer)
        {
            if (outer.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(outer.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every field this message kept confidential, which is what an answer must not leak.</summary>
    public IReadOnlyList<string> ConfidentialFields
    {
        get
        {
            var names = new List<string>();
            foreach (var carried in Fields)
            {
                if (Confidential(carried.Name)
                    && !names.Contains(carried.Name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(carried.Name);
                }
            }

            return names;
        }
    }

    /// <summary>
    /// What protection one field came to, given whether the message's signature held.
    /// </summary>
    /// <remarks>
    /// RFC 9788 §4.3.1, and the note under it that matters: a signature that fails lowers the answer
    /// silently rather than raising a second warning, the failed signature being the thing to say.
    /// </remarks>
    public HeaderFieldProtection ProtectionOf(string name, bool signatureHeld)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (Value(name) is null) return HeaderFieldProtection.Unprotected;

        if (Confidential(name))
        {
            return signatureHeld
                ? HeaderFieldProtection.SignedAndEncrypted
                : HeaderFieldProtection.EncryptedOnly;
        }

        return signatureHeld ? HeaderFieldProtection.SignedOnly : HeaderFieldProtection.Unprotected;
    }

    /// <summary>
    /// Whether the From inside disagrees with the From the transport saw.
    /// </summary>
    /// <remarks>
    /// §4.4.1.1. The comparison is against the <em>actual</em> outer header field rather than the
    /// payload's own <c>HP-Outer</c> copy of it: the whole point is to catch a message whose outside
    /// was altered after it was written, and the copy inside is what the author wrote.
    /// <para>
    /// A client that leans on its transport to authenticate the outer From — which is every client
    /// in a modern mail system, DKIM and SPF being where that assurance comes from — has to notice
    /// when the address it is about to draw is not the address that was checked. §10.1 is the attack:
    /// without this, header protection would be a way to make a spoof look better than an ordinary one.
    /// </para>
    /// </remarks>
    public bool FromMismatch(MimeMessage envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (Value("From") is not { } inner) return false;
        if (!InternetAddressList.TryParse(inner, out var inside)) return false;

        var protectedAddress = inside.Mailboxes.FirstOrDefault()?.Address;
        var outerAddress = envelope.From.Mailboxes.FirstOrDefault()?.Address;

        // Nothing to disagree with. A message with no outer From is malformed rather than spoofed,
        // and the trust bar has its own thing to say about that.
        if (protectedAddress is null || outerAddress is null) return false;

        return !SameAddress(protectedAddress, outerAddress);
    }

    /// <summary>
    /// Whether two addresses are the same one, by §4.4.5.
    /// </summary>
    /// <remarks>
    /// The domain is compared as ASCII — a name written in another script is converted to its A-label
    /// form first, so <c>münchen.example</c> and <c>xn--mnchen-3ya.example</c> are one domain and a
    /// message that writes it one way inside and the other way outside is not reported as a spoof.
    /// The local part is compared case-insensitively, which the RFC calls the simplest and most
    /// common choice and warns is not universally right; anything cleverer needs knowledge of the
    /// domain that a mail client does not have.
    /// </remarks>
    private static bool SameAddress(string left, string right)
    {
        var (leftLocal, leftDomain) = Split(left);
        var (rightLocal, rightDomain) = Split(right);

        return string.Equals(Ascii(leftDomain), Ascii(rightDomain), StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftLocal, rightLocal, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Local, string Domain) Split(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? (address, string.Empty) : (address[..at], address[(at + 1)..]);
    }

    /// <summary>The domain in A-label form, or as it stands when it is not a name IDNA can map.</summary>
    private static string Ascii(string domain)
    {
        if (domain.Length == 0) return domain;

        try
        {
            return new IdnMapping { AllowUnassigned = true, UseStd3AsciiRules = false }.GetAscii(domain);
        }
        catch (ArgumentException)
        {
            return domain;
        }
    }
}
