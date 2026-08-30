using MimeKit;
using Mailbox.Core.Diagnostics;
using Mailbox.Security;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>
/// What happens to a message the moment it has been filed, whichever protocol brought it.
/// </summary>
/// <remarks>
/// The junk filter and the rules both act here: the receiver stores the message where the
/// server had it, then hands it over, and the handler may move it, delete it, flag it or leave
/// it be. One hook for both protocols, so a rule behaves the same for POP3 and IMAP — on an IMAP
/// account the move it makes is journalled to the server like any other, which is why the
/// message is stored first rather than filed straight into its destination.
/// <para>
/// Synchronous on purpose: everything a handler does is a store operation, and the receivers
/// are already off the UI thread.
/// </para>
/// </remarks>
public interface IArrivalHandler
{
    /// <summary>
    /// Acts on a message that has just been stored in <paramref name="folder"/>.
    /// </summary>
    /// <returns>The id of the folder the message is in afterwards, or null when it was deleted.</returns>
    long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message);
}

/// <summary>
/// Acts on the copy of a message this machine has just sent, filed in Sent Items.
/// </summary>
/// <remarks>
/// Its own interface rather than <see cref="IArrivalHandler"/> so the two cannot be wired into
/// each other by accident: the handlers that act on arriving mail — the junk filter, the Focused
/// Inbox, a plugin's arrival hook — are all wrong about a message the reader wrote themselves,
/// and a single interface is an invitation to hand one this.
/// </remarks>
public interface ISentHandler
{
    /// <returns>The id of the folder the copy is in afterwards, or null when it was deleted.</returns>
    long? Handle(MailRepository mail, Folder sent, long messageId, MimeMessage message);
}

/// <summary>Runs several handlers in order, each seeing where the last one left the message.</summary>
public sealed class ArrivalPipeline : IArrivalHandler
{
    private readonly IReadOnlyList<IArrivalHandler> _handlers;

    public ArrivalPipeline(params IArrivalHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        // Refused at construction rather than survived per message: a null here is a field read
        // before its assignment, and the per-handler catch below would otherwise turn that
        // wiring mistake into a warning on every arriving message — a sync that looks like it
        // works while one handler silently never runs.
        for (var i = 0; i < handlers.Length; i++)
        {
            if (handlers[i] is null)
            {
                throw new ArgumentException(
                    $"Arrival handler {i} is null — a handler wired before it was constructed.",
                    nameof(handlers));
            }
        }

        _handlers = handlers;
    }

    public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
    {
        var current = folder;
        foreach (var handler in _handlers)
        {
            long? next;
            try
            {
                next = handler.Handle(mail, current, messageId, message);
            }
            catch (Exception ex)
            {
                // A handler that throws must not cost the message: it is stored already, and the
                // worst outcome is that it stays where it was.
                Log.Warn($"An arrival handler failed on message {messageId}; leaving it where it is.", ex);
                continue;
            }

            if (next is null) return null;
            if (next != current.Id) current = mail.GetFolder(next.Value) ?? current;
        }

        return current.Id;
    }
}

/// <summary>What happens to a message the moment it arrives, whichever protocol brought it.</summary>
/// <remarks>
/// Signature verification lives here rather than in the reading pane because verifying resolves
/// a name the sender chose, and no lookup is allowed on the path that draws a message.
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

    /// <summary>
    /// Hands a stored message to the handler, or leaves it where it is when there is none.
    /// </summary>
    /// <returns>The folder the message ended up in, or null when a handler deleted it.</returns>
    public static long? Handle(
        IArrivalHandler? handler,
        MailRepository repository,
        Folder folder,
        long messageId,
        MimeMessage message)
    {
        if (handler is null) return folder.Id;

        try
        {
            return handler.Handle(repository, folder, messageId, message);
        }
        catch (Exception ex)
        {
            Log.Warn($"The arrival handler failed on message {messageId}; leaving it where it is.", ex);
            return folder.Id;
        }
    }
}
