using MimeKit;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Mailbox.Security.OpenPgp;

/// <summary>One secret key that would not open, and enough about it to ask a reader for.</summary>
/// <param name="KeyId">Which key. What the vault is keyed by, a key having one and an address many.</param>
/// <param name="Address">Who the key's user IDs say it belongs to, for the sentence the dialog asks in.</param>
/// <param name="Fingerprint">
/// The whole fingerprint, spaced in fours as every other tool prints it. A reader who checks
/// anything checks this, and an address alone is what an attacker can also write on a key.
/// </param>
public sealed record PassphraseRequest(long KeyId, string Address, string Fingerprint);

/// <summary>
/// What has been said to unlock this machine's own secret keys, for as long as the application runs.
/// </summary>
/// <remarks>
/// <b>Why a vault and not a prompt.</b> MimeKit asks for a passphrase through a synchronous callback,
/// from inside the cryptography, on whatever thread is doing the work — and a dialog is asynchronous
/// and belongs to the UI thread. Blocking one on the other is a deadlock in every framework that has
/// ever tried it. So nothing is asked from in there: the callback answers out of what is already
/// known, and a key it has no answer for is <em>recorded</em> and refused. The caller then holds a
/// <see cref="ProtectionState.Locked"/> or <see cref="DecryptionState.Locked"/> report and a list of
/// exactly which keys to ask about, asks properly on its own thread, and runs the operation again.
/// <para>
/// <b>What it keeps and for how long.</b> In memory, for the session, and never written anywhere —
/// this is the one secret in the application that does not go to the keyring, because the keyring is
/// what it opens. <see cref="Forget"/> empties it; so does closing the application, that being the
/// only lifetime worth promising. A reader who declines to have it remembered gets
/// <see cref="Once"/>, and the answer is dropped the moment it has been used.
/// </para>
/// <para>
/// Passphrases are held as <see cref="string"/> because the library's own callback returns one:
/// pinning and zeroing a buffer here would be theatre while the value is copied into a string on the
/// way out regardless. Stated rather than dressed up.
/// </para>
/// </remarks>
public sealed class PassphraseVault
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, string> _known = [];
    private readonly Dictionary<long, string> _once = [];
    private readonly Dictionary<long, PassphraseRequest> _wanted = [];

    /// <summary>
    /// The callback a <see cref="PgpContext"/> is built with. Never asks anybody anything.
    /// </summary>
    /// <remarks>
    /// Answering null is how a reader declines, and MimeKit turns that into the refusal the caller
    /// sees. A key asked for and not answered is remembered in <see cref="Wanted"/>, which is the
    /// whole of how the prompt learns what to prompt for.
    /// </remarks>
    public string? For(PgpSecretKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            var id = key.KeyId;

            // A once-only answer is spent by being used. Asked for the same key twice in one
            // operation — a message signed and encrypted to the same identity — it answers twice
            // and is gone after; the alternative is a second prompt mid-send.
            if (_once.TryGetValue(id, out var once)) return once;
            if (_known.TryGetValue(id, out var known)) return known;

            // A key whose secret half is a stub — GnuPG leaves those behind when the real one is on
            // a card — has nothing a passphrase would open, so it is refused without being asked
            // about. Everything else: an unprotected key opens on an empty passphrase, and asking
            // about one would be a dialog with nothing behind it.
            if (key.IsPrivateKeyEmpty) return null;
            if (Opens(key, string.Empty)) return string.Empty;

            _wanted[id] = RequestFor(key);
            return null;
        }
    }

    /// <summary>Every key that has been asked for and had no answer, since the last <see cref="Clear"/>.</summary>
    public IReadOnlyList<PassphraseRequest> Wanted
    {
        get { lock (_gate) return [.. _wanted.Values]; }
    }

    /// <summary>Forgets what is outstanding, so a second run's list is that run's own.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _wanted.Clear();
            _once.Clear();
        }
    }

    /// <summary>Keeps an answer for as long as the application runs.</summary>
    public void Remember(long keyId, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);

        lock (_gate)
        {
            _known[keyId] = passphrase;
            _wanted.Remove(keyId);
        }
    }

    /// <summary>Keeps an answer for the next operation only, for a reader who said not to keep it.</summary>
    public void Once(long keyId, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);

        lock (_gate)
        {
            _once[keyId] = passphrase;
            _wanted.Remove(keyId);
        }
    }

    /// <summary>Empties it. What a reader locking the application asks for.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _known.Clear();
            _once.Clear();
            _wanted.Clear();
        }
    }

    /// <summary>What a dialog needs in order to ask about one key, as the vault records it itself.</summary>
    /// <remarks>
    /// Public so that a caller with a key in hand can ask about it without waiting for an operation to
    /// refuse first — which is what the harness does, a locked key being the one state no capture run
    /// can arrive at on its own.
    /// </remarks>
    public static PassphraseRequest RequestFor(PgpSecretKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new PassphraseRequest(key.KeyId, AddressOf(key), FingerprintOf(key));
    }

    /// <summary>Whether that passphrase opens that key, asked before anything is kept.</summary>
    /// <remarks>
    /// So a mistyped passphrase is refused by the dialog that took it rather than by a send that
    /// then has to be explained, and so a wrong answer is never filed as a right one.
    /// </remarks>
    public static bool Opens(PgpSecretKey key, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(key);

        try
        {
            return key.ExtractPrivateKey(passphrase?.ToCharArray()) is not null;
        }
        catch (PgpException)
        {
            return false;
        }
    }

    /// <summary>The first address the key's user IDs carry, for the sentence the dialog asks in.</summary>
    private static string AddressOf(PgpSecretKey key)
    {
        foreach (var id in key.PublicKey.GetUserIds())
        {
            if (MailboxAddress.TryParse(id, out var mailbox) && mailbox.Address is { Length: > 0 } address)
            {
                return address;
            }
        }

        return string.Empty;
    }

    /// <summary>The fingerprint as every other tool prints it: hexadecimal, in fours.</summary>
    private static string FingerprintOf(PgpSecretKey key)
    {
        var hex = Convert.ToHexString(key.PublicKey.GetFingerprint());
        var groups = new List<string>(hex.Length / 4 + 1);

        for (var at = 0; at < hex.Length; at += 4)
        {
            groups.Add(hex.Substring(at, Math.Min(4, hex.Length - at)));
        }

        return string.Join(' ', groups);
    }
}
