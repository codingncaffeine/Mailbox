using System.Globalization;
using Mailbox.Core.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.X509;

namespace Mailbox.Security.Smime;

/// <summary>
/// Checking an S/MIME signature, with the eight things §19 says a client must not get wrong.
/// </summary>
/// <remarks>
/// MimeKit does the cryptography and none of the judgement. Five of §19's blockers are answered
/// here, and each is a bug that shipped in a real client:
/// <list type="number">
/// <item><b>The root, or nothing.</b> A signature is reported only when the message's own root is
/// the signed part. A signed part buried inside — reachable by a <c>cid:</c> reference the reader
/// never sees — is what impersonated Phil Zimmermann with his own note, and what
/// CVE-2018-15587 and CVE-2017-17848 were.</item>
/// <item><b>A signer must be present.</b> A <c>SignedData</c> with no signers in it verifies
/// vacuously: nothing failed, so a loop that only watches for failures says it is signed.</item>
/// <item><b>The signer must be the sender.</b> Matched against the certificate's
/// <c>rfc822Name</c> — never its common name, never the subject DN alone — and the certificate
/// must say it is for e-mail at all (<c>id-kp-emailProtection</c>). MimeKit checks neither.</item>
/// <item><b>signingTime is the attacker's.</b> RFC 5652 §11.3 gives it no guarantee, and MimeKit
/// builds the chain as of it. A time that disagrees with the message's own date is refused rather
/// than believed — Thunderbird had this twice, in 2022 and again 18 months later.</item>
/// <item><b>Nothing is imported before it verifies.</b> No certificate is filed and no S/MIME
/// capability is recorded until every check above has passed, which is what RFC 8551 §2.5.2 says
/// and the opposite of what MimeKit does.</item>
/// </list>
/// <para>
/// The verifier is given its context rather than making one, so a test runs against a temporary
/// store and the application against its own — and so nothing here ever touches a keyring.
/// </para>
/// </remarks>
public static class SmimeVerification
{
    /// <summary>Whether the message's root is a signed part at all.</summary>
    public static bool IsSigned(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Body is MultipartSigned or ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.SignedData };
    }

    /// <summary>
    /// Checks the message's signature, if it has one at its root.
    /// </summary>
    /// <param name="context">
    /// The certificate store to verify against. Nothing is written to it here: importing is the
    /// caller's, and only after a report comes back <see cref="SignatureReport.Trustworthy"/>.
    /// </param>
    public static SignatureReport Verify(MimeMessage message, SecureMimeContext context)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        // §19: the root, or nothing. A signed part deeper in the message is not this message being
        // signed, however loudly it says so.
        if (message.Body is not MultipartSigned signed)
        {
            return message.Body is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.SignedData }
                ? new SignatureReport(SignatureState.Unknown, string.Empty, "This message is signed in a form Mailbox cannot check yet.")
                : SignatureReport.Unsigned;
        }

        DigitalSignatureCollection signatures;
        try
        {
            signatures = signed.Verify(context);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            return new SignatureReport(SignatureState.Unknown, string.Empty, "The signature is in a form Mailbox cannot check.");
        }

        // §19: a signer must be present. Nothing failing is not the same as something passing.
        if (signatures.Count == 0)
        {
            return new SignatureReport(SignatureState.Invalid, string.Empty, "This message says it is signed and carries no signature.");
        }

        var sender = SenderOf(message);
        SignatureReport? mismatch = null;

        foreach (var signature in signatures)
        {
            var who = AddressOf(signature.SignerCertificate);

            if (!Holds(signature, out var why))
            {
                return new SignatureReport(SignatureState.Invalid, who, why);
            }

            // §19: signingTime is the attacker's, and MimeKit builds the chain as of it.
            if (!SigningTime.Agrees(signature.CreationDate, message, out var timing))
            {
                return new SignatureReport(SignatureState.Invalid, who, timing);
            }

            // §19: the signer must be the sender, and the certificate must be for e-mail.
            if (!ForEmail(signature))
            {
                mismatch ??= new SignatureReport(
                    SignatureState.Mismatched, who,
                    "The certificate that signed this message is not a certificate for e-mail.");
                continue;
            }

            if (!Binds(signature, sender))
            {
                mismatch ??= new SignatureReport(
                    SignatureState.Mismatched, who,
                    who.Length > 0
                        ? $"This message is signed by {who}, which is not the address it says it is from."
                        : "The certificate that signed this message names no e-mail address.");
                continue;
            }

            return new SignatureReport(SignatureState.Valid, who, string.Empty);
        }

        return mismatch ?? new SignatureReport(SignatureState.Invalid, string.Empty, "The signature could not be checked.");
    }

    /// <summary>Whether the maths and the chain hold, which is MimeKit's half of the job.</summary>
    private static bool Holds(IDigitalSignature signature, out string why)
    {
        try
        {
            if (!signature.Verify())
            {
                why = "The signature does not match the message: it has been changed since it was signed.";
                return false;
            }
        }
        catch (Exception ex) when (ex is DigitalSignatureVerifyException or FormatException)
        {
            // The library's own words go to the log and not to the reader: they name internals a
            // reader cannot act on, and a message changed in transit reaches this path as often as
            // a malformed one does.
            Log.Warn("An S/MIME signature could not be checked.", ex);
            why = "The signature does not hold: it could not be checked against the certificate that signed it.";
            return false;
        }

        // A chain that will not build is not a signature a reader should be told held. The
        // property is on the S/MIME signature rather than on the interface, the two kinds of
        // signature this library makes having nothing else in common there.
        if (signature is SecureMimeDigitalSignature { Chain: null })
        {
            why = "The certificate that signed this message is not trusted here.";
            return false;
        }

        why = string.Empty;
        return true;
    }

    /// <summary>Whether the certificate says it is for e-mail at all (RFC 8550 §4.4.2).</summary>
    private static bool ForEmail(IDigitalSignature signature)
    {
        if (signature.SignerCertificate is not SecureMimeDigitalCertificate certificate) return false;

        var usages = certificate.Certificate?.GetExtendedKeyUsage();
        if (usages is null) return true;   // No EKU at all means no restriction, which RFC 5280 allows.

        foreach (var usage in usages)
        {
            var oid = usage?.ToString();
            if (oid is EmailProtection or AnyExtendedKeyUsage) return true;
        }

        return false;
    }

    private const string EmailProtection = "1.3.6.1.5.5.7.3.4";
    private const string AnyExtendedKeyUsage = "2.5.29.37.0";

    /// <summary>
    /// Whether the certificate names the address the message says it is from.
    /// </summary>
    /// <remarks>
    /// The subject alternative name's <c>rfc822Name</c>, or RFC 9598's UTF-8 mailbox — never the
    /// common name, which is a display string anybody may put anything in.
    /// </remarks>
    private static bool Binds(IDigitalSignature signature, string sender)
    {
        if (sender.Length == 0) return false;
        if (signature.SignerCertificate is not SecureMimeDigitalCertificate certificate) return false;

        foreach (var address in Addresses(certificate.Certificate))
        {
            if (string.Equals(address, sender, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Every e-mail address the certificate's subject alternative names carry.</summary>
    private static IEnumerable<string> Addresses(X509Certificate? certificate)
    {
        if (certificate is null) yield break;

        var names = certificate.GetSubjectAlternativeNames();
        if (names is null) yield break;

        foreach (var entry in names)
        {
            // Each entry is a two-item list: the tag, then the value. rfc822Name is tag 1.
            if (entry is not System.Collections.IList { Count: >= 2 } pair) continue;
            if (pair[0] is not int tag || tag != 1) continue;
            if (pair[1]?.ToString() is { Length: > 0 } value) yield return value;
        }
    }

    /// <summary>What the certificate says about who signed, for the line the reader sees.</summary>
    private static string AddressOf(IDigitalCertificate? certificate)
        => certificate?.Email is { Length: > 0 } email ? email : certificate?.Name ?? string.Empty;

    /// <summary>
    /// Who the message says sent it: <c>Sender</c> if it has one, else the first <c>From</c>.
    /// </summary>
    private static string SenderOf(MimeMessage message)
    {
        if (message.Sender is { Address.Length: > 0 } sender) return sender.Address;
        return message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
    }
}
