using Mailbox.Core.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Mailbox.Security.OpenPgp;

/// <summary>
/// The keys this machine holds, and the one thing MimeKit will not do with them.
/// </summary>
/// <remarks>
/// <b>Why this class exists at all.</b> §19's first crypto blocker: MimeKit decrypts an OpenPGP
/// packet by draining BouncyCastle's data stream and handing back what came out, and it never asks
/// whether the packet was protected against modification. BouncyCastle offers both halves of that
/// question — <see cref="PgpEncryptedData.IsIntegrityProtected"/> and
/// <see cref="PgpEncryptedData.Verify"/> — but <c>Verify</c> must be called explicitly, <i>after</i>
/// the stream is drained, and it <b>returns false rather than throwing</b>. So a packet with no
/// modification detection code at all, and a packet whose code fails, both come back as plaintext
/// with nothing said. That contradicts RFC 9580 §13.7, it is the EFAIL family, and rPGP shipped the
/// same class of bug into 2026 (GHSA-c7ph-f7jm-xv4w).
/// <para>
/// The library's own <c>Decrypt</c> that carries signatures is not virtual, so overriding is not
/// enough to close it. The packet is opened here instead — which the protected key lookups on
/// <see cref="OpenPgpContext"/> are what make possible — and <b>nothing is released until both
/// checks pass</b>. There is deliberately no way to ask for it anyway: a button that shows the
/// content regardless is the bug the warning is about.
/// </para>
/// <para>
/// <b>Where the keys are.</b> Mailbox's own keyring directory, beside the mail stores, for the
/// reason the certificate store is there: what this application keeps is in one place a reader can
/// find, back up and delete. <b>Divergence, stated:</b> this is not the desktop's own GnuPG
/// keyring. GnuPG 2.1 and later keep public keys in <c>pubring.kbx</c> and secret keys as separate
/// files under <c>private-keys-v1.d</c>, neither of which the library reads — pointing this at
/// <c>~/.gnupg</c> would quietly find nothing on any current system, which is worse than a store
/// that plainly holds what was put into it. Importing from GnuPG is an action a reader takes, not
/// something that happens behind them.
/// </para>
/// </remarks>
public sealed class PgpContext : GnuPGContext
{
    /// <summary>
    /// The most plaintext one packet may expand to before it is refused.
    /// </summary>
    /// <remarks>
    /// An OpenPGP packet may carry a compressed data packet, and a compressed data packet is a
    /// decompression bomb waiting for somebody to read it without looking. This is the looking.
    /// </remarks>
    public const long MostPlaintext = 128L * 1024 * 1024;

    /// <summary>How deeply one packet may nest inside another before it is refused.</summary>
    /// <remarks>The same bomb wearing a different shape: compression inside compression.</remarks>
    private const int MostNesting = 8;

    private readonly Func<PgpSecretKey, string?> _passphrase;

    /// <param name="directory">Where the keyrings live.</param>
    /// <param name="passphrase">
    /// What unlocks a secret key, asked per key. Answering null is how a reader declines, and it
    /// stops the attempt rather than retrying with the same answer.
    /// </param>
    public PgpContext(string directory, Func<PgpSecretKey, string?>? passphrase = null)
        : base(directory)
    {
        _passphrase = passphrase ?? (_ => string.Empty);

        // §19: nothing is fetched to display a message. Key discovery is something a reader asks
        // for (see WebKeyDirectory), never something a keyserver is asked for mid-render.
        AutoKeyRetrieve = false;
    }

    /// <inheritdoc/>
    protected override string GetPasswordForKey(PgpSecretKey key) => _passphrase(key)!;

    /// <summary>The secret key with that id, or null when this ring has not got it.</summary>
    /// <remarks>
    /// The library keeps this lookup to itself, and what needs it is the dialog that asks for a
    /// passphrase: a <see cref="PassphraseRequest"/> names a key by id, and finding out whether an
    /// answer opens it means having the key in hand. Asking before keeping anything is what stops a
    /// mistyped passphrase being filed as a right one and failing at the send instead.
    /// </remarks>
    public PgpSecretKey? SecretKey(long keyId, CancellationToken cancellationToken = default)
    {
        try
        {
            return GetSecretKey(keyId, cancellationToken);
        }
        catch (PrivateKeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The secret key that would sign for that address, or null when the ring has none.</summary>
    /// <remarks>
    /// The library keeps this lookup protected as well. What needs it is the harness: the passphrase
    /// dialog is the one surface a capture run cannot reach by itself, because reaching it means an
    /// operation meeting a key that will not open, and a run that could arrange that would be a run
    /// with a locked key in its own store.
    /// </remarks>
    public PgpSecretKey? SigningKey(MailboxAddress who, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(who);

        try
        {
            return GetSigningKey(who, cancellationToken);
        }
        catch (Exception ex) when (ex is PrivateKeyNotFoundException or PublicKeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens one OpenPGP packet, and refuses to hand back anything that is not integrity-protected.
    /// </summary>
    /// <param name="encrypted">The packet, armoured or binary.</param>
    /// <remarks>
    /// A signature carried inside the packet — the one-pass form, which is what "sign and encrypt"
    /// produces when it is not two nested MIME layers — comes back with it as the maths alone.
    /// Whether that signer is <em>the sender</em> is a question about the message, and this has
    /// only the packet; <see cref="PgpDecryption"/> asks it.
    /// </remarks>
    public (DecryptionReport Report, PgpSigner? Signer) Open(
        Stream encrypted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encrypted);

        try
        {
            return Unpack(encrypted, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The reader declined to unlock the key, which is an answer rather than a failure.
            return (new DecryptionReport(DecryptionState.Locked, null, "This message was not unlocked."), null);
        }
        catch (UnauthorizedAccessException)
        {
            // The key is here and it would not open: the passphrase offered was not its. Locked
            // rather than failed, because nothing is wrong with the message — found by running the
            // harness against a seeded keyring whose keys carry one and being told the mail was
            // malformed.
            return (new DecryptionReport(
                DecryptionState.Locked, null,
                "This message is encrypted to a key on this computer that could not be unlocked."), null);
        }
        catch (Exception ex) when (ex is PgpException or IOException
            or FormatException or InvalidOperationException)
        {
            Log.Warn("An OpenPGP message could not be read.", ex);
            return (new DecryptionReport(
                DecryptionState.Failed, null,
                "This message is encrypted in a way Mailbox cannot open."), null);
        }
    }

    private (DecryptionReport, PgpSigner?) Unpack(Stream encrypted, CancellationToken cancellationToken)
    {
        var packets = new PgpObjectFactory(PgpUtilities.GetDecoderStream(encrypted));

        var list = First<PgpEncryptedDataList>(packets);
        if (list is null)
        {
            return (Failed("This message carries no encrypted data."), null);
        }

        if (!Mine(list, cancellationToken, out var packet, out var key))
        {
            return (new DecryptionReport(
                DecryptionState.Locked, null,
                "This message is encrypted to a key this computer has not got."), null);
        }

        var inside = new Contents();
        using (var clear = packet.GetDataStream(key))
        {
            Walk(new PgpObjectFactory(clear), inside, MostNesting, cancellationToken);
        }

        if (inside.Refused)
        {
            // Either bomb: more plaintext than will be held, or packets nested deeper than will be
            // followed. One sentence for both, because the reader's position is the same.
            return (Failed("This message is bigger or more deeply nested than Mailbox will open."), null);
        }

        // Everything above this line ran on bytes nothing has vouched for yet. The checks are here,
        // after the packet has been read to its end, because that is the only place they can be
        // made — the modification detection code sits after the data, inside the same packet, so a
        // reader that stops at the end of what it wanted never sees the bytes that say whether it
        // was tampered with. Nothing below returns content unless both hold.
        if (!packet.IsIntegrityProtected())
        {
            Log.Warn("An OpenPGP message arrived with no integrity protection and was refused.");
            return (new DecryptionReport(
                DecryptionState.Unprotected, null,
                "This message carries nothing to show it was not altered while it was encrypted, "
                + "so Mailbox will not open it."), null);
        }

        if (!packet.Verify())
        {
            Log.Warn("An OpenPGP message failed its integrity check and was refused.");
            return (new DecryptionReport(
                DecryptionState.Unprotected, null,
                "This message was altered after it was encrypted, so Mailbox will not open it."), null);
        }

        if (inside.Literal is not { } literal)
        {
            return (Failed("This message decrypted to nothing readable."), null);
        }

        var entity = Parse(literal);
        if (entity is null)
        {
            return (Failed("This message decrypted to nothing readable."), null);
        }

        var signer = Signed(inside, literal, cancellationToken);
        return (new DecryptionReport(DecryptionState.Opened, entity, string.Empty), signer);
    }

    private static DecryptionReport Failed(string detail) => new(DecryptionState.Failed, null, detail);

    /// <summary>What one packet turned out to hold.</summary>
    private sealed class Contents
    {
        public byte[]? Literal { get; set; }
        public PgpOnePassSignature? OnePass { get; set; }
        public PgpSignature? Signature { get; set; }
        public bool Refused { get; set; }
    }

    /// <summary>The first packet of a kind, or null when the stream holds none.</summary>
    private static T? First<T>(PgpObjectFactory factory) where T : PgpObject
    {
        PgpObject? packet;
        while ((packet = factory.NextPgpObject()) is not null)
        {
            if (packet is T wanted) return wanted;
        }

        return null;
    }

    /// <summary>
    /// The first sub-packet this machine holds a key for, and that key.
    /// </summary>
    /// <remarks>
    /// A message may be encrypted to several people; being one of them is what makes it ours.
    /// Symmetric (passphrase) sub-packets are skipped: a shared passphrase is not a key this
    /// application knows the reader has, and prompting for one on arriving mail is a habit worth
    /// not teaching.
    /// </remarks>
    private bool Mine(
        PgpEncryptedDataList list,
        CancellationToken cancellationToken,
        out PgpPublicKeyEncryptedData packet,
        out PgpPrivateKey key)
    {
        foreach (var data in list.GetEncryptedDataObjects())
        {
            if (data is not PgpPublicKeyEncryptedData encrypted) continue;

            PgpSecretKey? secret;
            try
            {
                secret = GetSecretKey(encrypted.KeyId, cancellationToken);
            }
            catch (PrivateKeyNotFoundException)
            {
                continue;
            }

            if (secret is null) continue;

            packet = encrypted;
            key = GetPrivateKey(secret);
            return true;
        }

        packet = null!;
        key = null!;
        return false;
    }

    /// <summary>
    /// Reads every packet in a stream, and every packet inside those, to the end.
    /// </summary>
    /// <remarks>
    /// Recursive rather than a loop that switches streams, because coming back out of a compressed
    /// packet is the point: the outer stream has to be read past it, or the integrity bytes that
    /// follow it are never pulled through and the check below has nothing to check.
    /// </remarks>
    private static void Walk(
        PgpObjectFactory factory, Contents inside, int depth, CancellationToken cancellationToken)
    {
        if (depth <= 0)
        {
            Log.Warn("An OpenPGP message nested deeper than Mailbox will follow and was refused.");
            inside.Refused = true;
            return;
        }

        PgpObject? packet;
        while ((packet = factory.NextPgpObject()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inside.Refused) return;

            switch (packet)
            {
                case PgpCompressedData compressed:
                    Walk(new PgpObjectFactory(compressed.GetDataStream()), inside, depth - 1, cancellationToken);
                    break;

                case PgpOnePassSignatureList { IsEmpty: false } list:
                    inside.OnePass ??= list[0];
                    break;

                case PgpSignatureList { IsEmpty: false } list:
                    inside.Signature ??= list[0];
                    break;

                case PgpLiteralData data when inside.Literal is null:
                    inside.Literal = Read(data.GetInputStream(), out var truncated);
                    inside.Refused = truncated;
                    break;
            }
        }
    }

    /// <summary>Reads a stream out, stopping rather than filling memory with a bomb.</summary>
    private static byte[]? Read(Stream stream, out bool truncated)
    {
        truncated = false;

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;

        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MostPlaintext)
            {
                Log.Warn("An OpenPGP message decrypted to more than Mailbox will hold and was refused.");
                truncated = true;
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The decrypted bytes as a MIME entity, or null when they are not one.</summary>
    private static MimeEntity? Parse(byte[] literal)
    {
        try
        {
            using var stream = new MemoryStream(literal, writable: false);
            return MimeEntity.Load(stream);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Log.Warn("An OpenPGP message decrypted to something that is not MIME.", ex);
            return null;
        }
    }

    /// <summary>
    /// What the one-pass signature inside the packet came to, as far as the maths goes.
    /// </summary>
    /// <remarks>
    /// Run over the plaintext that was kept rather than over the stream, the stream having gone by:
    /// a one-pass signature is a hash of the literal data, and the bytes hash the same from a buffer.
    /// </remarks>
    private PgpSigner? Signed(Contents inside, byte[] literal, CancellationToken cancellationToken)
    {
        if (inside.OnePass is not { } onePass || inside.Signature is not { } signature) return null;

        PgpPublicKeyRing? ring;
        try
        {
            ring = GetPublicKeyRing(onePass.KeyId, cancellationToken);
        }
        catch (PublicKeyNotFoundException)
        {
            ring = null;
        }

        var key = ring?.GetPublicKey(onePass.KeyId);
        if (ring is null || key is null) return PgpSigner.Unavailable;

        try
        {
            onePass.InitVerify(key);
            onePass.Update(literal);
            return new PgpSigner(
                onePass.Verify(signature) ? PgpSignerOutcome.Held : PgpSignerOutcome.Failed,
                ring, key, signature.CreationTime);
        }
        catch (Exception ex) when (ex is PgpException or InvalidOperationException or IOException)
        {
            Log.Warn("An OpenPGP signature inside an encrypted message could not be checked.", ex);
            return new PgpSigner(PgpSignerOutcome.Unreadable, ring, key, signature.CreationTime);
        }
    }
}
