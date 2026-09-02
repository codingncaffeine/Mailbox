using Mailbox.Core.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailbox.Security.OpenPgp;

/// <summary>
/// Reading a signed or encrypted message through the reader's own GnuPG.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="PgpVerification"/> and <see cref="PgpDecryption"/>, and it keeps
/// their rules rather than inventing its own: the encrypted part must be the message's root
/// rather than something buried inside it, what comes out is handed back on its own and never
/// spliced into the message it arrived in, a signature is judged against the envelope's sender,
/// and nothing whose integrity GnuPG did not prove is released at all.
/// <para>
/// The last of those is the one worth restating. <see cref="GnuPgAgent.DecryptAsync"/> refuses to
/// return plaintext whose modification detection is absent or failed, so there is no path from
/// here to content that has not been checked — the same guarantee the library path gets from
/// <see cref="PgpContext.Open"/>, arrived at through GnuPG's status stream rather than through
/// BouncyCastle.
/// </para>
/// </remarks>
public static class GnuPgReading
{
    /// <summary>
    /// Checks a <c>multipart/signed</c> through GnuPG.
    /// </summary>
    /// <param name="message">The message, whose sender the signer is judged against.</param>
    public static async Task<SignatureReport> VerifyAsync(
        MimeMessage message, GnuPgAgent agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(agent);

        if (message.Body is not MultipartSigned signed) return SignatureReport.Unsigned;

        var protocol = signed.ContentType.Parameters["protocol"]?.Trim();
        if (!string.Equals(protocol, PgpVerification.SignatureProtocol, StringComparison.OrdinalIgnoreCase))
        {
            return SignatureReport.Unsigned;
        }

        if (signed.Count < 2 || signed[1] is not MimePart { Content: { } armour })
        {
            return new SignatureReport(
                SignatureState.Invalid, string.Empty,
                "This message says it is signed and carries no signature.");
        }

        byte[] signature;
        using (var held = new MemoryStream())
        {
            try
            {
                armour.DecodeTo(held, cancellationToken);
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                Log.Warn("An OpenPGP signature's own encoding could not be read.", ex);
                return new SignatureReport(
                    SignatureState.Unknown, string.Empty, "This signature is in a shape Mailbox cannot read.");
            }

            signature = held.ToArray();
        }

        var covered = GnuPgProtection.Canonical(signed[0]);
        var checked_ = await agent.VerifyAsync(covered, signature, cancellationToken);

        return Judge(checked_, message);
    }

    /// <summary>
    /// What GnuPG's status stream says about a signature, in the vocabulary the reader is told
    /// things in.
    /// </summary>
    /// <remarks>
    /// The status keywords are the whole answer and the exit code is not: a signature by an
    /// expired key exits non-zero on some versions and zero on others, and a good signature by
    /// somebody who is not the sender exits zero everywhere. Judged in the order that matters —
    /// bad maths first, because nothing else about a forged signature is worth saying.
    /// <para>
    /// A signature that holds but was made by somebody other than the message's sender is its own
    /// state, deliberately: folding it into "valid" tells a reader that a message from an
    /// impostor is signed by the person it names, which is the whole of the attack.
    /// </para>
    /// </remarks>
    internal static SignatureReport Judge(GnuPgResult result, MimeMessage message)
    {
        if (result.Said("BADSIG"))
        {
            return new SignatureReport(
                SignatureState.Invalid, Signer(result, "BADSIG"),
                "This message was changed after it was signed, or the signature does not belong to it.");
        }

        if (result.Said("ERRSIG"))
        {
            // ERRSIG's sixth field is the reason; 9 is "no public key", which is the ordinary
            // case of a stranger's signature rather than a fault.
            var fields = result.After("ERRSIG")?.Split(' ') ?? [];
            var noKey = fields.Length > 5 && fields[5] == "9";
            return new SignatureReport(
                SignatureState.Unknown, string.Empty,
                noKey
                    ? "This message is signed by a key GnuPG does not have, so the signature could not be checked."
                    : "This signature could not be checked.");
        }

        if (result.Said("REVKEYSIG"))
        {
            return new SignatureReport(
                SignatureState.Invalid, Signer(result, "REVKEYSIG"),
                "The key that signed this message has been revoked.");
        }

        if (result.Said("EXPKEYSIG"))
        {
            return new SignatureReport(
                SignatureState.Invalid, Signer(result, "EXPKEYSIG"),
                "The key that signed this message has expired.");
        }

        if (!result.Said("GOODSIG")) return SignatureReport.Unsigned;

        var who = Signer(result, "GOODSIG");
        var from = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;

        if (who.Length > 0 && from.Length > 0
            && !string.Equals(who, from, StringComparison.OrdinalIgnoreCase))
        {
            return new SignatureReport(
                SignatureState.Mismatched, who,
                $"This message is signed by {who}, who is not the sender.");
        }

        return new SignatureReport(SignatureState.Valid, who, string.Empty);
    }

    /// <summary>
    /// The address out of a status line whose first field is a key id and whose rest is a user id.
    /// </summary>
    /// <remarks>
    /// GOODSIG, BADSIG, EXPKEYSIG and REVKEYSIG all carry <c>&lt;keyid&gt; &lt;user id&gt;</c>,
    /// and a user id is <c>Name (comment) &lt;address&gt;</c> — so the address is what stands
    /// between the last angle brackets. A user id with no address names nobody a message can be
    /// compared against, and comes back empty rather than as the whole name.
    /// </remarks>
    private static string Signer(GnuPgResult result, string keyword)
    {
        if (result.After(keyword) is not { } line) return string.Empty;

        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var uid = space > 0 ? line[(space + 1)..] : string.Empty;

        var open = uid.LastIndexOf('<');
        var close = uid.LastIndexOf('>');
        return open >= 0 && close > open ? uid[(open + 1)..close].Trim() : string.Empty;
    }

    /// <summary>Opens an RFC 3156 encrypted message through GnuPG.</summary>
    public static async Task<DecryptionReport> OpenAsync(
        MimeMessage message, GnuPgAgent agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(agent);

        // The root, or nothing: an encrypted part buried inside a message is not the message
        // being encrypted, and rendering one would put attacker-chosen markup where a reader
        // expects plaintext.
        if (message.Body is not MultipartEncrypted encrypted) return DecryptionReport.Unencrypted;

        var protocol = encrypted.ContentType.Parameters["protocol"]?.Trim();
        if (!string.Equals(protocol, PgpDecryption.EncryptionProtocol, StringComparison.OrdinalIgnoreCase))
        {
            return DecryptionReport.Unencrypted;
        }

        if (encrypted.Count < 2 || encrypted[1] is not MimePart { Content: { } encoded })
        {
            return new DecryptionReport(
                DecryptionState.Failed, null, "This message says it is encrypted and carries nothing to open.");
        }

        byte[] packet;
        using (var held = new MemoryStream())
        {
            try
            {
                encoded.DecodeTo(held, cancellationToken);
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                Log.Warn("An OpenPGP message's own encoding could not be read.", ex);
                return new DecryptionReport(
                    DecryptionState.Failed, null, "This message is encrypted in a way Mailbox cannot open.");
            }

            packet = held.ToArray();
        }

        var opened = await agent.DecryptAsync(packet, cancellationToken);

        if (!opened.Worked)
        {
            // Nothing came out and the sentence says why. The integrity refusal has a state of
            // its own, because "it opened and nothing vouches for what came out" is a different
            // thing to tell a reader from "it would not open".
            if (!GnuPgAgent.IsIntegrityProven(opened) && opened.Said("DECRYPTION_OKAY"))
            {
                return new DecryptionReport(DecryptionState.Unprotected, null, opened.Problem ?? string.Empty);
            }

            var state = opened.Said("NO_SECKEY") ? DecryptionState.Locked : DecryptionState.Failed;
            return new DecryptionReport(state, null, opened.Problem ?? string.Empty);
        }

        MimeEntity content;
        try
        {
            using var plaintext = new MemoryStream(opened.Output, writable: false);
            content = MimeEntity.Load(plaintext, cancellationToken);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Log.Warn("What GnuPG decrypted could not be read as MIME.", ex);
            return new DecryptionReport(
                DecryptionState.Failed, null, "What was inside this message could not be read.");
        }

        // The signature inside is judged against the envelope's sender, which is the only claim
        // about who sent it a reader ever sees. Judging it against the decrypted part's own
        // headers would let the message vouch for itself.
        return new DecryptionReport(
            DecryptionState.Opened, content, string.Empty, Judge(opened, message));
    }
}
