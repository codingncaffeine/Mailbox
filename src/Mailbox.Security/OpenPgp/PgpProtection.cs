using Mailbox.Core.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Mailbox.Security.OpenPgp;

/// <summary>
/// Signing and encrypting one message on the way out, in OpenPGP's own shapes.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="PgpVerification"/> and <see cref="PgpDecryption"/>, and much the
/// smaller half: the read side had to work around what the library does not check, and the write
/// side only has to produce what the read side would accept. That is not an assumption —
/// <c>PgpProtectionTests</c> puts every shape this builds straight back through
/// <see cref="PgpContext.Open"/>, which refuses a packet with no integrity protection, so a message
/// this application sends is one it would itself agree to open.
/// <para>
/// RFC 3156's two shapes and nothing else: a detached signature in a <c>multipart/signed</c>, and a
/// <c>multipart/encrypted</c>. <b>Not inline PGP</b> — armour pasted into a text body, with no MIME
/// to say where it starts or what it covers, is how a client ends up reporting a signature over part
/// of a message as a signature over the message (§19), and how Thunderbird came to decrypt armour it
/// found in an RSS feed.
/// </para>
/// </remarks>
public static class PgpProtection
{
    /// <summary>
    /// What everything is signed with, and why it is not the writer's choice.
    /// </summary>
    /// <remarks>
    /// SHA-1 is still what several clients default to and it has not been a defensible choice for a
    /// decade. Offering a menu here would be offering a way to get it wrong; anything that needs to
    /// change this can change it in one place.
    /// </remarks>
    public const DigestAlgorithm Digest = DigestAlgorithm.Sha256;

    /// <summary>Applies what was asked for, or explains why the message may not go.</summary>
    /// <param name="body">The message's body as it stands, which becomes what is signed or sealed.</param>
    /// <param name="sender">Whose key signs, and who the message is also encrypted to.</param>
    /// <param name="recipients">Everybody it must be readable by, the sender included.</param>
    public static ProtectionReport Apply(
        MimeEntity body,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients,
        Protection want,
        PgpContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(context);

        if (want == Protection.None) return ProtectionReport.Unprotected;

        try
        {
            // Signed inside the encryption rather than outside it, which is both what the library's
            // own one-pass form does and the order that means anything: a signature on the outside
            // of a sealed message says who posted the envelope, not who wrote the letter.
            MimeEntity built = want switch
            {
                Protection.Sign =>
                    MultipartSigned.Create(context, sender, Digest, body, cancellationToken),

                Protection.Encrypt =>
                    MultipartEncrypted.Encrypt(context, recipients, body, cancellationToken),

                _ => MultipartEncrypted.SignAndEncrypt(
                    context, sender, Digest, recipients, body, cancellationToken),
            };

            return new ProtectionReport(ProtectionState.Applied, built, string.Empty);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The key is here and would not open. Two exceptions for one situation, both the
            // library's: answering null — which is how the vault says "nobody has told me what
            // unlocks this" — comes back as a cancellation, and three wrong answers come back as
            // an access violation. Locked rather than failed either way: nothing is wrong with the
            // message, and the caller's next move is to ask (see PassphraseVault).
            return new ProtectionReport(
                ProtectionState.Locked, null,
                "This message could not be signed: the key for " + sender.Address + " would not unlock.");
        }
        catch (Exception ex) when (ex is PrivateKeyNotFoundException or PublicKeyNotFoundException)
        {
            // Checked before this was called, so reaching it means a key went away underneath us.
            Log.Warn("An OpenPGP key named by a message could not be found when it was used.", ex);
            return new ProtectionReport(
                ProtectionState.NoKey, null,
                "A key this message needs is no longer in the keyring.");
        }
        catch (Exception ex) when (ex is PgpException or IOException or FormatException)
        {
            Log.Warn("A message could not be protected with OpenPGP.", ex);
            return new ProtectionReport(
                ProtectionState.Failed, null,
                "This message could not be protected with OpenPGP: " + ex.Message);
        }
    }
}
