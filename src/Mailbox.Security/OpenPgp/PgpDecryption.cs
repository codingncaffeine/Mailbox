using Mailbox.Core.Diagnostics;
using MimeKit;

namespace Mailbox.Security.OpenPgp;

/// <summary>
/// Opening an OpenPGP-encrypted message.
/// </summary>
/// <remarks>
/// The MIME half of the job. <see cref="PgpContext.Open"/> does the packet half, and does the one
/// thing §19 says the library will not: it refuses anything that arrived without integrity
/// protection, or whose protection does not hold.
/// <para>
/// What is here is the same rule S/MIME's decryption is written to. <b>The root, or nothing</b> —
/// an encrypted part buried inside a message is not the message being encrypted, and rendering one
/// would put attacker-chosen markup where a reader expects plaintext. And what comes out is handed
/// back <em>on its own</em>, never spliced into the message it arrived in: CVE-2026-0818 read
/// decrypted OpenPGP out of Thunderbird through the cascade — <c>@font-face</c> and CSS animations
/// in the outer part, not an <c>&lt;img&gt;</c> — so stripping remote content does not close it and
/// only separate documents do.
/// </para>
/// <para>
/// A signature carried inside the packet is judged here, by the same
/// <see cref="PgpVerification.Judge"/> a detached one goes through. Signing inside the encryption is
/// the ordinary shape for OpenPGP rather than an exotic one, so a client that reports the outer
/// layers and stays quiet about the inner signature is silent about most signed mail it sees.
/// </para>
/// </remarks>
public static class PgpDecryption
{
    /// <summary>What RFC 3156 calls an OpenPGP-encrypted message.</summary>
    public const string EncryptionProtocol = "application/pgp-encrypted";

    /// <summary>Whether the message's root is an OpenPGP-encrypted part.</summary>
    public static bool IsEncrypted(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Root(message) is not null;
    }

    /// <summary>
    /// Opens the message, if it is encrypted and this machine holds a key for it.
    /// </summary>
    /// <remarks>
    /// Nothing is imported and nothing is trusted as a side effect: opening a message says only
    /// that it was addressed to a key this machine holds, which is not a claim about who sent it.
    /// </remarks>
    public static DecryptionReport Open(
        MimeMessage message, PgpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        if (Root(message) is not { } root) return DecryptionReport.Unencrypted;

        // Malformed rather than absent: the message says it is encrypted and there is no packet in
        // it. Saying "not encrypted" here would draw the version part as if it were the mail.
        if (root.Count < 2 || root[1] is not MimePart { Content: { } encoded })
        {
            return new DecryptionReport(
                DecryptionState.Failed, null, "This message says it is encrypted and carries nothing to open.");
        }

        using var packet = new MemoryStream();
        try
        {
            encoded.DecodeTo(packet, cancellationToken);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Log.Warn("An OpenPGP message's own encoding could not be read.", ex);
            return new DecryptionReport(
                DecryptionState.Failed, null, "This message is encrypted in a way Mailbox cannot open.");
        }

        packet.Position = 0;
        var (report, signer) = context.Open(packet, cancellationToken);

        // The signature inside is judged against the *envelope's* sender, which is the only claim
        // about who sent it a reader ever sees. Judging it against the decrypted part's own headers
        // would let the message vouch for itself.
        return report.Opened
            ? report with { Signature = PgpVerification.Judge(signer, message) }
            : report;
    }

    /// <summary>
    /// The message's root, when it is an RFC 3156 encrypted one.
    /// </summary>
    /// <remarks>
    /// The shape the standard asks for is two parts: a version part naming the protocol, then the
    /// packet itself. The version part's contents are not checked — it says <c>Version: 1</c> and
    /// has since 2001 — but the declared protocol is, because that is what separates this from a
    /// <c>multipart/encrypted</c> carrying somebody else's scheme.
    /// </remarks>
    private static MimeKit.Cryptography.MultipartEncrypted? Root(MimeMessage message)
    {
        if (message.Body is not MimeKit.Cryptography.MultipartEncrypted encrypted) return null;

        var protocol = encrypted.ContentType.Parameters["protocol"]?.Trim();
        return string.Equals(protocol, EncryptionProtocol, StringComparison.OrdinalIgnoreCase)
            ? encrypted
            : null;
    }
}
