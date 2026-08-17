using Mailbox.Core.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Mailbox.Security.OpenPgp;

/// <summary>
/// Checking an OpenPGP signature, by the same rules S/MIME's is checked by.
/// </summary>
/// <remarks>
/// §19's findings are findings about a client, not about an algorithm, so the four that apply here
/// apply in the same words:
/// <list type="number">
/// <item><b>The root, or nothing.</b> A signature is reported only when the message's own root is
/// the signed part. A signed part buried inside — reachable by a <c>cid:</c> reference the reader
/// never sees — is what impersonated Phil Zimmermann with his own note, and what CVE-2018-15587
/// and CVE-2017-17848 were.</item>
/// <item><b>A signer must be present.</b> An empty signature list is not a signature that passed;
/// it is nothing to check, and a loop that only watches for failures calls it signed.</item>
/// <item><b>The signer must be the sender.</b> Matched against the addresses in the key's own user
/// IDs, and a key that names nobody binds to nobody.</item>
/// <item><b>The creation time is the signer's claim.</b> Held against the message's own date
/// through <see cref="SigningTime"/>, which is where the reasoning is.</item>
/// </list>
/// <para>
/// And one the other algorithm has no equivalent of: <b>a revoked key is not a valid signature.</b>
/// Revocation in OpenPGP travels with the key itself rather than through a responder to ask, so
/// there is no excuse for not looking — and none of it costs a network round trip, which §19 does
/// not allow on the render path anyway.
/// </para>
/// </remarks>
public static class PgpVerification
{
    /// <summary>What RFC 3156 calls an OpenPGP signature.</summary>
    public const string SignatureProtocol = "application/pgp-signature";

    /// <summary>Whether the message's root is an OpenPGP-signed part.</summary>
    public static bool IsSigned(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Body is MultipartSigned signed && IsPgp(signed);
    }

    /// <summary>
    /// Checks the message's signature, if it has an OpenPGP one at its root.
    /// </summary>
    /// <param name="context">
    /// The keys to check against. Nothing is written to it here: importing a key is something a
    /// reader does, and never a side effect of reading mail (§19, CVE-2020-12618 and -12619).
    /// </param>
    public static SignatureReport Verify(MimeMessage message, PgpContext context)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        // §19: the root, or nothing. A signed part deeper in the message is not this message being
        // signed, however loudly it says so.
        if (message.Body is not MultipartSigned signed || !IsPgp(signed)) return SignatureReport.Unsigned;

        DigitalSignatureCollection signatures;
        try
        {
            signatures = signed.Verify(context);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException
            or ArgumentException or PgpException or IOException)
        {
            Log.Warn("An OpenPGP signature could not be checked.", ex);
            return new SignatureReport(
                SignatureState.Unknown, string.Empty,
                "The signature is in a form Mailbox cannot check.");
        }

        // §19: a signer must be present. Nothing failing is not the same as something passing.
        if (signatures.Count == 0)
        {
            return new SignatureReport(
                SignatureState.Invalid, string.Empty,
                "This message says it is signed and carries no signature.");
        }

        SignatureReport? lesser = null;

        foreach (var signature in signatures)
        {
            var report = Judge(Reduce(signature), message);
            if (report.Trustworthy) return report;
            lesser ??= report;
        }

        return lesser ?? new SignatureReport(
            SignatureState.Invalid, string.Empty, "The signature could not be checked.");
    }

    /// <summary>
    /// What a signature means, once the maths is done: §19's questions, in one place.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="PgpDecryption"/>, because a signature carried inside an encrypted
    /// packet has to answer for itself exactly as a detached one does. A client that checks the
    /// binding on the outer signature and takes the inner one on trust has moved the attack rather
    /// than closed it.
    /// </remarks>
    public static SignatureReport Judge(PgpSigner? signer, MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (signer is null) return SignatureReport.Unsigned;

        var who = AddressOf(signer);

        switch (signer.Outcome)
        {
            case PgpSignerOutcome.Unavailable:
                return new SignatureReport(
                    SignatureState.Unknown, who,
                    "This message is signed with a key this computer has not got, so its signature "
                    + "could not be checked.");

            case PgpSignerOutcome.Unreadable:
                return new SignatureReport(
                    SignatureState.Unknown, who,
                    "This message is signed in a form Mailbox cannot check.");

            case PgpSignerOutcome.Failed:
                return new SignatureReport(
                    SignatureState.Invalid, who,
                    "The signature does not match the message: it has been changed since it was signed.");
        }

        // §19: the creation time is the signer's own claim, and believing it is how two CVEs
        // happened. It is held against the message's date before anything else is said.
        if (!SigningTime.Agrees(signer.Created, message, out var timing))
        {
            return new SignatureReport(SignatureState.Invalid, who, timing);
        }

        if (Revoked(signer))
        {
            return new SignatureReport(
                SignatureState.Invalid, who,
                "The key that signed this message has been revoked by its owner.");
        }

        // Expiry is judged as of the signature, not as of now: a key that has since expired signed
        // this while it was good, and calling that invalid would retire every signature its owner
        // ever made. A key that had *already* expired when it signed is the other thing.
        if (Expired(signer))
        {
            return new SignatureReport(
                SignatureState.Invalid, who,
                "The key that signed this message had already expired when it was used.");
        }

        // §19: the signer must be the sender. Its own state, neither valid nor invalid — folding it
        // into "valid" tells a reader an impostor's message is signed by the person it names.
        if (!Binds(signer, SenderOf(message)))
        {
            return new SignatureReport(
                SignatureState.Mismatched, who,
                who.Length > 0
                    ? $"This message is signed by {who}, which is not the address it says it is from."
                    : "The key that signed this message names no e-mail address.");
        }

        return new SignatureReport(SignatureState.Valid, who, string.Empty);
    }

    /// <summary>Whether a signed part is an OpenPGP one rather than an S/MIME one.</summary>
    private static bool IsPgp(MultipartSigned signed)
        => string.Equals(
            signed.ContentType.Parameters["protocol"]?.Trim(),
            SignatureProtocol,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The library's own signature, reduced to what <see cref="Judge"/> asks of it.</summary>
    private static PgpSigner Reduce(IDigitalSignature signature)
    {
        var certificate = signature.SignerCertificate as OpenPgpDigitalCertificate;

        if (certificate is null) return PgpSigner.Unavailable;

        try
        {
            return new PgpSigner(
                signature.Verify() ? PgpSignerOutcome.Held : PgpSignerOutcome.Failed,
                certificate.KeyRing,
                certificate.PublicKey,
                signature.CreationDate);
        }
        catch (Exception ex) when (ex is DigitalSignatureVerifyException or FormatException or PgpException)
        {
            // The library's own words go to the log and not to the reader: they name internals a
            // reader cannot act on, and a message changed in transit reaches this path as often as
            // a malformed one does.
            Log.Warn("An OpenPGP signature could not be checked.", ex);
            return new PgpSigner(
                PgpSignerOutcome.Unreadable, certificate.KeyRing, certificate.PublicKey, signature.CreationDate);
        }
    }

    /// <summary>
    /// Whether the key, or the master key it hangs from, has been revoked.
    /// </summary>
    /// <remarks>
    /// Both, because a subkey may be sound while its owner has withdrawn the identity above it —
    /// which is the case revocation exists for.
    /// </remarks>
    private static bool Revoked(PgpSigner signer)
    {
        if (signer.Key?.IsRevoked() == true) return true;

        var master = signer.Ring?.GetPublicKey();
        return master?.IsRevoked() == true;
    }

    /// <summary>Whether the key had already expired at the moment it signed.</summary>
    private static bool Expired(PgpSigner signer)
    {
        if (signer.Key is not { } key) return false;

        var seconds = key.GetValidSeconds();
        if (seconds <= 0) return false;   // No expiry set.

        var expires = key.CreationTime.ToUniversalTime().AddSeconds(seconds);
        var made = signer.Created == default ? DateTime.UtcNow : signer.Created.ToUniversalTime();
        return made > expires;
    }

    /// <summary>Whether one of the key's user IDs names the address the message says it is from.</summary>
    private static bool Binds(PgpSigner signer, string sender)
    {
        if (sender.Length == 0) return false;

        foreach (var address in Addresses(signer))
        {
            if (string.Equals(address, sender, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Every e-mail address the ring's user IDs carry.</summary>
    /// <remarks>
    /// The whole ring, because user IDs live on the master key and it is usually a subkey that
    /// signs. A user ID is free text — "A. Person (work) &lt;a.person@example.com&gt;" — so it is
    /// parsed as an address rather than compared as a string, and one that is not an address at all
    /// names nobody.
    /// </remarks>
    private static IEnumerable<string> Addresses(PgpSigner signer)
    {
        if (signer.Ring is not { } ring) yield break;

        foreach (var key in ring.GetPublicKeys())
        {
            foreach (var id in key.GetUserIds())
            {
                if (MailboxAddress.TryParse(id, out var mailbox) && mailbox.Address is { Length: > 0 } address)
                {
                    yield return address;
                }
            }
        }
    }

    /// <summary>What the key says about who signed, for the line the reader sees.</summary>
    private static string AddressOf(PgpSigner signer) => Addresses(signer).FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Who the message says sent it: <c>Sender</c> if it has one, else the first <c>From</c>.
    /// </summary>
    private static string SenderOf(MimeMessage message)
    {
        if (message.Sender is { Address.Length: > 0 } sender) return sender.Address;
        return message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
    }
}
