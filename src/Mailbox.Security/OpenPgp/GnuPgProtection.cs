using MimeKit;
using MimeKit.Cryptography;
using MimeKit.IO;

namespace Mailbox.Security.OpenPgp;

/// <summary>
/// RFC 3156's two shapes, built from what the reader's own GnuPG produced.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="PgpProtection"/>, and the same two shapes for the same reasons:
/// a detached signature in a <c>multipart/signed</c>, a <c>multipart/encrypted</c>, and never
/// inline armour. What differs is only who holds the key — here it is <c>gpg</c>, so the
/// passphrase reaches <c>gpg-agent</c>'s own pinentry and never this process.
/// <para>
/// <b>The bytes the signature covers are the bytes that travel.</b> A detached signature is over
/// one exact serialisation, and getting that wrong produces a message that verifies here and
/// fails everywhere else — the classic way to ship a client whose signatures nobody can check.
/// So the part is prepared for 7-bit transport <em>first</em>, written once with CRLF endings,
/// and the very same object is then put in the multipart: what was signed and what is sent are
/// one serialisation of one prepared entity, not two serialisations that ought to agree.
/// </para>
/// </remarks>
public static class GnuPgProtection
{
    /// <summary>What the signature is made with, and what the <c>micalg</c> parameter must say.</summary>
    /// <remarks>
    /// One digest, pinned, for the reason the library path pins it: SHA-1 is still several
    /// clients' default and has not been defensible for a decade. The two must agree — a
    /// <c>micalg</c> that names a different digest from the signature is what makes a correct
    /// signature unverifiable in strict clients.
    /// </remarks>
    public const string Digest = "pgp-sha256";

    /// <summary>Applies what was asked for through GnuPG, or explains why the message may not go.</summary>
    public static async Task<ProtectionReport> ApplyAsync(
        MimeEntity body,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients,
        Protection want,
        GnuPgAgent agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(agent);

        if (want == Protection.None) return ProtectionReport.Unprotected;

        // Signed inside the encryption rather than outside it: a signature on the outside of a
        // sealed message says who posted the envelope, not who wrote the letter.
        if (want.HasFlag(Protection.Encrypt))
        {
            return await EncryptAsync(
                body, sender, recipients, want.HasFlag(Protection.Sign), agent, cancellationToken);
        }

        return await SignAsync(body, sender, agent, cancellationToken);
    }

    private static async Task<ProtectionReport> SignAsync(
        MimeEntity body, MailboxAddress sender, GnuPgAgent agent, CancellationToken cancellationToken)
    {
        var covered = Canonical(body);
        var signature = await agent.SignAsync(covered, sender.Address, cancellationToken);

        if (!signature.Worked)
        {
            return new ProtectionReport(
                Locked(signature) ? ProtectionState.Locked : ProtectionState.Failed,
                null,
                $"This message could not be signed by GnuPG: {signature.Problem}");
        }

        var signed = new MultipartSigned();
        signed.ContentType.Parameters["protocol"] = "application/pgp-signature";
        signed.ContentType.Parameters["micalg"] = Digest;
        signed.Add(body);
        signed.Add(new ApplicationPgpSignature(new MemoryStream(signature.Output, writable: false)));

        return new ProtectionReport(ProtectionState.Applied, signed, string.Empty);
    }

    private static async Task<ProtectionReport> EncryptAsync(
        MimeEntity body,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients,
        bool alsoSign,
        GnuPgAgent agent,
        CancellationToken cancellationToken)
    {
        var plaintext = Canonical(body);
        var sealed_ = await agent.EncryptAsync(
            plaintext,
            [.. recipients.Select(r => r.Address)],
            alsoSign ? sender.Address : null,
            cancellationToken);

        if (!sealed_.Worked)
        {
            return new ProtectionReport(
                Locked(sealed_) ? ProtectionState.Locked : ProtectionState.Failed,
                null,
                $"This message could not be encrypted by GnuPG: {sealed_.Problem}");
        }

        var encrypted = new MultipartEncrypted();
        encrypted.ContentType.Parameters["protocol"] = "application/pgp-encrypted";

        // The version part is required and its content is fixed. A recipient reads it to know
        // which protocol the second part is in, and a multipart/encrypted without it is not one.
        encrypted.Add(new ApplicationPgpEncrypted());

        encrypted.Add(new MimePart("application", "octet-stream")
        {
            // The name matters less than that there is one: several clients label the attachment
            // with it when they cannot decrypt, and "encrypted.asc" is what the reference and
            // every other client write.
            FileName = "encrypted.asc",
            ContentDisposition = new ContentDisposition("inline") { FileName = "encrypted.asc" },
            Content = new MimeContent(new MemoryStream(sealed_.Output, writable: false)),
        });

        return new ProtectionReport(ProtectionState.Applied, encrypted, string.Empty);
    }

    /// <summary>
    /// One entity as the bytes that will travel: prepared for 7-bit transport, CRLF throughout.
    /// </summary>
    /// <remarks>
    /// <see cref="MimeEntity.Prepare"/> settles the transfer encodings before anything is
    /// measured, which is what stops the serialisation changing underneath a signature that has
    /// already been made over it. The line endings are forced to CRLF because that is what a
    /// message is on the wire and what every other client will canonicalise to before checking.
    /// </remarks>
    internal static byte[] Canonical(MimeEntity entity)
    {
        entity.Prepare(EncodingConstraint.SevenBit);

        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;

        using var memory = new MemoryBlockStream();
        entity.WriteTo(options, memory);
        memory.Position = 0;
        return memory.ToArray();
    }

    /// <summary>
    /// Whether the refusal was the key not opening rather than something being wrong.
    /// </summary>
    /// <remarks>
    /// The difference the caller acts on: locked means nothing is wrong with the message and the
    /// next move is to try again once the reader has answered their agent, which is a prompt on
    /// their own desktop and not one this application can raise or wait on.
    /// </remarks>
    private static bool Locked(GnuPgResult result)
        => result.Said("MISSING_PASSPHRASE")
           || result.Said("BAD_PASSPHRASE")
           || result.Said("CANCELED")
           || (result.Problem?.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ?? false);
}
