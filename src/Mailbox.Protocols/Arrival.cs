using MimeKit;
using Mailbox.Core.Diagnostics;
using Mailbox.Security;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>
/// What happens to a message the moment it arrives, whichever protocol brought it.
/// </summary>
/// <remarks>
/// Signature verification lives here rather than in the reading pane because verifying resolves
/// a name the sender chose, and §19 does not allow a lookup on the path that draws a message.
/// Arrival is already network work on a background thread, and it is also the only moment the
/// signing key is certain to still be published — a key checked months later may have rotated,
/// and reporting a rotation as a forgery would be worse than not checking at all.
/// </remarks>
internal static class Arrival
{
    /// <summary>
    /// Checks a stored message's own DKIM signatures and records the verdict, or does nothing
    /// when there is no verifier. Never throws for a check that could not be made: the message
    /// is already stored, so the worst this costs is a message with no recorded verdict — which
    /// reads as "not checked", which is the truth.
    /// </summary>
    public static async Task RecordSignatureAsync(
        MailRepository repository,
        DkimVerification? verifier,
        long messageId,
        MimeMessage message,
        DateTimeOffset now,
        CancellationToken cancellation)
    {
        if (verifier is null) return;

        try
        {
            var result = await verifier.VerifyAsync(message, cancellation);
            if (result.Verdict is AuthVerdict.None) return;

            repository.RecordAuthentication(
                messageId, result.Verdict.ToString().ToLowerInvariant(), result.SigningDomain, now);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not check a message's signature as it arrived.", ex);
        }
    }
}
