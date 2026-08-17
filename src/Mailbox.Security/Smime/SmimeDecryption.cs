using Mailbox.Core.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailbox.Security.Smime;

/// <summary>
/// Opening an encrypted S/MIME message.
/// </summary>
/// <remarks>
/// The decryption itself is MimeKit's. What is here is the part §19 says a client must not get
/// wrong: <b>what comes out is rendered in a document of its own</b>. CVE-2026-0818 exfiltrated
/// decrypted content from a client that spliced it into the outer message — through the cascade,
/// not through a fetch: <c>@font-face</c> and CSS animations in the outer part read the plaintext
/// out of the inner one. Stripping remote content does not close that, because the channel is the
/// style sheet.
/// <para>
/// So this hands back the decrypted entity and nothing else, and the caller renders <em>that</em>
/// — never the message it arrived in, and never both. <see cref="Isolated"/> is what the renderer
/// is told, and what makes the document refuse the constructs the attack is built out of.
/// </para>
/// </remarks>
public static class SmimeDecryption
{
    /// <summary>Whether the message's root is an encrypted part.</summary>
    public static bool IsEncrypted(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Body is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData };
    }

    /// <summary>
    /// Opens the message, if it is encrypted and this machine holds a key for it.
    /// </summary>
    /// <remarks>
    /// Nothing is imported and nothing is trusted as a side effect: opening a message says only
    /// that it was addressed to a key this machine holds, which is not a claim about who sent it.
    /// A signature inside is checked separately, by the verifier, on the decrypted entity.
    /// </remarks>
    public static DecryptionReport Open(MimeMessage message, SecureMimeContext context)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        if (message.Body is not ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData } envelope)
        {
            return DecryptionReport.Unencrypted;
        }

        try
        {
            return new DecryptionReport(DecryptionState.Opened, envelope.Decrypt(context), string.Empty);
        }
        catch (Org.BouncyCastle.Cms.CmsException ex)
        {
            Log.Warn("An encrypted message could not be opened.", ex);
            return new DecryptionReport(
                DecryptionState.Locked, null,
                "This message is encrypted to a key this computer has not got.");
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            Log.Warn("An encrypted message could not be read.", ex);
            return new DecryptionReport(
                DecryptionState.Failed, null,
                "This message is encrypted in a way Mailbox cannot open.");
        }
    }
}
