using Mailbox.Core.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailbox.Security.Smime;

/// <summary>
/// Signing and encrypting one message on the way out, in S/MIME's own shapes.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="SmimeVerification"/> and <see cref="SmimeDecryption"/>.
/// <para>
/// <b>A detached signature, not an opaque one.</b> RFC 8551 allows either — <c>multipart/signed</c>
/// with the content beside the signature, or <c>application/pkcs7-mime</c> with the content wrapped
/// inside it. The detached form is what this sends, because a recipient whose client does no S/MIME
/// at all still reads the message and merely sees an attachment it cannot use; the wrapped form
/// leaves them a page of base64. That is the same reasoning the reference's own default follows.
/// </para>
/// </remarks>
public static class SmimeProtection
{
    /// <summary>What everything is signed with. Not the writer's choice — see the OpenPGP note.</summary>
    public const DigestAlgorithm Digest = DigestAlgorithm.Sha256;

    /// <summary>Applies what was asked for, or explains why the message may not go.</summary>
    /// <param name="body">The message's body as it stands, which becomes what is signed or sealed.</param>
    /// <param name="sender">Whose certificate signs, and who the message is also encrypted to.</param>
    /// <param name="recipients">Everybody it must be readable by, the sender included.</param>
    public static ProtectionReport Apply(
        MimeEntity body,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients,
        Protection want,
        SecureMimeContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(context);

        if (want == Protection.None) return ProtectionReport.Unprotected;

        try
        {
            // Sign first, then encrypt what was signed — RFC 8551 §3.7's order, and the one that
            // means anything: a signature outside the encryption is a statement about an envelope
            // by somebody who need not have been able to read what is in it.
            var signed = want.HasFlag(Protection.Sign)
                ? MultipartSigned.Create(context, sender, Digest, body, cancellationToken)
                : body;

            MimeEntity built = want.HasFlag(Protection.Encrypt)
                ? ApplicationPkcs7Mime.Encrypt(context, recipients, signed, cancellationToken)
                : signed;

            return new ProtectionReport(ProtectionState.Applied, built, string.Empty);
        }
        catch (CertificateNotFoundException ex)
        {
            // Checked before this was called, so reaching it means a certificate went away
            // underneath us — or that one is present and not usable for what was asked.
            Log.Warn("An S/MIME certificate named by a message could not be found when it was used.", ex);
            return new ProtectionReport(
                ProtectionState.NoKey, null,
                "A certificate this message needs is no longer in the certificate store.");
        }
        catch (PrivateKeyNotFoundException ex)
        {
            // The certificate is filed and its private half is not, which is what importing a
            // certificate out of a message leaves behind: enough to encrypt to somebody, never
            // enough to sign as them.
            Log.Warn("An S/MIME certificate has no private key to sign with.", ex);
            return new ProtectionReport(
                ProtectionState.NoKey, null,
                "The certificate for " + sender.Address + " has no private key on this computer, "
                + "so this message cannot be signed with it.");
        }
        catch (Exception ex) when (ex is Org.BouncyCastle.Security.SecurityUtilityException
            or Org.BouncyCastle.Crypto.CryptoException or IOException or FormatException)
        {
            Log.Warn("A message could not be protected with S/MIME.", ex);
            return new ProtectionReport(
                ProtectionState.Failed, null,
                "This message could not be protected with S/MIME: " + ex.Message);
        }
    }
}
