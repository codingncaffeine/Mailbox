using Mailbox.Security.OpenPgp;
using Mailbox.Security.Smime;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailbox.Security;

/// <summary>
/// Protecting an outgoing message, and choosing which algorithm carries it.
/// </summary>
/// <remarks>
/// <b>The writer asks for signing and encryption, not for an algorithm.</b> The reference's bar has
/// one Sign button and one Encrypt button, and a person composing a message has no way to know which
/// of two cryptosystems the people they are writing to happen to use — so the question this answers
/// is "what can carry what was asked for", and the answer is whichever of the two holds keys for
/// everybody involved. Where both do, S/MIME goes first: it is the reference's own, and a
/// correspondent with both is likelier to be reading in a client that prefers it.
/// <para>
/// Nothing here is reachable unless the reader turned the algorithm on in the Trust Center (§14) —
/// a context is null when its switch is off, and two nulls mean nothing is offered at all.
/// </para>
/// </remarks>
public static class MessageProtection
{
    /// <summary>
    /// Applies what the writer asked for to a message about to be sent.
    /// </summary>
    /// <remarks>
    /// On <see cref="ProtectionState.Applied"/> the message's body has been replaced with the
    /// protected one. On anything else the message is left exactly as it came in, so a caller that
    /// gets a refusal still holds something it could save or send in the clear if its user says so.
    /// </remarks>
    /// <param name="smime">The certificate store, or null when S/MIME is switched off.</param>
    /// <param name="openpgp">The keyring, or null when OpenPGP is switched off.</param>
    public static ProtectionReport Apply(
        MimeMessage message,
        Protection want,
        SecureMimeContext? smime,
        PgpContext? openpgp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (want == Protection.None) return ProtectionReport.Unprotected;

        return Protect(message, want, Recipients(message), smime, openpgp, cancellationToken);
    }

    /// <summary>
    /// Applies what may be applied to a <em>draft</em>, which is not the same thing (§19).
    /// </summary>
    /// <remarks>
    /// Two rules, and both are about the same attack. A <c>mailto:</c> link — or anything else that
    /// can open a compose window with fields already filled — chooses the recipient, so:
    /// <list type="bullet">
    /// <item><description><b>A draft is never signed.</b> A signature is a statement, and a
    /// statement is made when a person decides to send something, not every few minutes while they
    /// are still writing it. Signing happens immediately before the message goes and nowhere
    /// else.</description></item>
    /// <item><description><b>A draft is encrypted to its author alone</b>, never to the recipients
    /// in the fields, because those fields are the part an attacker got to choose. The Drafts folder
    /// is on a server; what lands there must be readable by the one person whose message it
    /// is.</description></item>
    /// </list>
    /// A draft with neither toggle down is stored as it always was. A draft the writer marked
    /// Encrypt is stored encrypted — being able to read one's own drafts back is what makes that
    /// worth doing at all.
    /// </remarks>
    public static ProtectionReport ApplyToDraft(
        MimeMessage message,
        Protection want,
        SecureMimeContext? smime,
        PgpContext? openpgp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var draft = want & ~Protection.Sign;
        if (draft == Protection.None) return ProtectionReport.Unprotected;

        return SenderOf(message) is { } author
            ? Protect(message, draft, [author], smime, openpgp, cancellationToken)
            : NoSender();
    }

    private static ProtectionReport Protect(
        MimeMessage message,
        Protection want,
        IReadOnlyList<MailboxAddress> recipients,
        SecureMimeContext? smime,
        PgpContext? openpgp,
        CancellationToken cancellationToken)
    {
        if (SenderOf(message) is not { } sender) return NoSender();
        if (message.Body is not { } body)
        {
            return new ProtectionReport(
                ProtectionState.Failed, null, "This message has nothing in it to protect.");
        }

        // Whichever can carry the whole of what was asked for. Where neither can, the one that is
        // missing the fewest people is what the writer is told about, that being the one they have
        // the least to do about — and S/MIME breaks the tie, as it does when both work.
        Candidate? best = null;

        foreach (var candidate in Candidates(smime, openpgp))
        {
            var missing = Missing(candidate.Context, want, sender, recipients, cancellationToken);
            if (missing.Count == 0)
            {
                return Run(message, candidate, body, sender, recipients, want, cancellationToken);
            }

            best ??= candidate with { Missing = missing };
            if (missing.Count < best.Missing.Count) best = candidate with { Missing = missing };
        }

        return best is null
            ? new ProtectionReport(
                ProtectionState.Failed, null,
                "Neither S/MIME nor OpenPGP is switched on, so this message cannot be signed or encrypted.")
            : new ProtectionReport(ProtectionState.NoKey, null, Sentence(best, want));
    }

    /// <summary>Builds the protected body and, only if that worked, puts it on the message.</summary>
    private static ProtectionReport Run(
        MimeMessage message,
        Candidate candidate,
        MimeEntity body,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients,
        Protection want,
        CancellationToken cancellationToken)
    {
        var report = candidate.Context switch
        {
            SecureMimeContext certificates =>
                SmimeProtection.Apply(body, sender, recipients, want, certificates, cancellationToken),
            PgpContext keys =>
                PgpProtection.Apply(body, sender, recipients, want, keys, cancellationToken),
            _ => ProtectionReport.Unprotected,
        };

        if (report is { State: ProtectionState.Applied, Body: { } protectedBody })
        {
            message.Body = protectedBody;
        }

        return report;
    }

    /// <summary>The two algorithms in the order they are tried, skipping the ones switched off.</summary>
    private static IEnumerable<Candidate> Candidates(SecureMimeContext? smime, PgpContext? openpgp)
    {
        if (smime is not null) yield return new Candidate("S/MIME", smime, []);
        if (openpgp is not null) yield return new Candidate("OpenPGP", openpgp, []);
    }

    /// <summary>Everybody this algorithm has no key for, for what was asked of it.</summary>
    /// <remarks>
    /// Asked before anything is built, so a message that cannot go encrypted is refused with the
    /// names of the people it could not have reached rather than half-built and thrown away.
    /// </remarks>
    private static IReadOnlyList<string> Missing(
        CryptographyContext context,
        Protection want,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients,
        CancellationToken cancellationToken)
    {
        var missing = new List<string>();

        if (want.HasFlag(Protection.Sign) && !Can(() => context.CanSign(sender, cancellationToken)))
        {
            missing.Add(sender.Address);
        }

        if (want.HasFlag(Protection.Encrypt))
        {
            foreach (var recipient in recipients)
            {
                if (!Can(() => context.CanEncrypt(recipient, cancellationToken))
                    && !missing.Contains(recipient.Address, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(recipient.Address);
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// Whether the store can answer at all, a store that will not open being a "no" like any other.
    /// </summary>
    /// <remarks>
    /// A missing or unreadable certificate database throws from the question rather than answering
    /// it, and a writer pressing Send is owed "there is no key for this person", not a stack trace.
    /// </remarks>
    private static bool Can(Func<bool> ask)
    {
        try
        {
            return ask();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Data.Common.DbException or InvalidOperationException)
        {
            Core.Diagnostics.Log.Warn("A key store could not say whether it holds a key.", ex);
            return false;
        }
    }

    /// <summary>One sentence naming who is missing, which is the only part a writer can act on.</summary>
    private static string Sentence(Candidate candidate, Protection want)
    {
        var people = string.Join(", ", candidate.Missing);

        // Signing fails for one person and encryption for a list, and the two read differently
        // enough to be worth separate sentences.
        if (want == Protection.Sign)
        {
            return $"There is no {candidate.Name} key for {people} on this computer, "
                + "so this message cannot be signed.";
        }

        return candidate.Missing.Count == 1
            ? $"There is no {candidate.Name} key for {people}, so this message cannot be encrypted."
            : $"There are no {candidate.Name} keys for {people}, so this message cannot be encrypted.";
    }

    private static ProtectionReport NoSender() => new(
        ProtectionState.Failed, null,
        "This message has no From address, so there is nothing to sign or encrypt as.");

    /// <summary>
    /// Everybody the message must be readable by: its recipients, and its author.
    /// </summary>
    /// <remarks>
    /// The author is in the list on purpose. The copy that goes to Sent Items is the one that went
    /// out, encrypted as it went, and a client that leaves its user unable to read their own sent
    /// mail has quietly made encryption something to be avoided. Bcc recipients are here too — they
    /// have to be able to open it — and no header outside the encryption names them, the sender
    /// handling Bcc as it always has.
    /// </remarks>
    private static IReadOnlyList<MailboxAddress> Recipients(MimeMessage message)
    {
        var recipients = new List<MailboxAddress>();

        foreach (var mailbox in message.To.Mailboxes
            .Concat(message.Cc.Mailboxes)
            .Concat(message.Bcc.Mailboxes))
        {
            Add(recipients, mailbox);
        }

        if (SenderOf(message) is { } sender) Add(recipients, sender);
        return recipients;
    }

    private static void Add(List<MailboxAddress> recipients, MailboxAddress mailbox)
    {
        if (mailbox.Address is not { Length: > 0 }) return;

        foreach (var already in recipients)
        {
            if (string.Equals(already.Address, mailbox.Address, StringComparison.OrdinalIgnoreCase)) return;
        }

        recipients.Add(mailbox);
    }

    /// <summary>Who the message says sent it, read exactly as the verifiers read it.</summary>
    private static MailboxAddress? SenderOf(MimeMessage message)
        => message.Sender ?? message.From.Mailboxes.FirstOrDefault();

    /// <summary>One algorithm, the store that answers for it, and who it has no key for.</summary>
    private sealed record Candidate(string Name, CryptographyContext Context, IReadOnlyList<string> Missing);
}
