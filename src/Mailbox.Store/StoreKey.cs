using System.Security.Cryptography;

namespace Mailbox.Store;

/// <summary>
/// The key the stores on this machine are encrypted with, for as long as the application runs.
/// </summary>
/// <remarks>
/// <b>What this protects against, and what it does not.</b> Worth stating plainly, because
/// encryption that sounds stronger than it is is worse than none: a reader who believes their
/// mail is unreadable will keep it on a laptop they would otherwise have been careful with.
/// <list type="bullet">
/// <item><description>
/// <b>Protected:</b> the database files read by somebody who has the disk but not the login — a
/// stolen or lost machine that was switched off, a drive pulled out, a backup copied somewhere
/// else, a file recovered from free space. The key is not in them and cannot be derived from
/// them.
/// </description></item>
/// <item><description>
/// <b>Not protected:</b> anything readable by the logged-in account. The key lives in the desktop
/// keyring, so a program running as the reader can ask for it exactly as this one does. That is
/// the same bargain the browsers make with saved passwords, and pretending otherwise would be the
/// dishonest part.
/// </description></item>
/// <item><description>
/// <b>Not covered at all:</b> what does not live in the databases. The reading pane's engine
/// keeps a scratch cache, an opened attachment is written out for the program that opens it, and
/// the log records what the application did. Those are separate problems and are named in the
/// queue rather than quietly implied to be solved.
/// </description></item>
/// </list>
/// <para>
/// <b>One key for the profile rather than one per account.</b> The store is one file per account
/// so that a corrupt file costs one account and a backup can be taken of one — that split is
/// about the blast radius of damage, not of compromise. Per-file keys would all sit in the same
/// keyring behind the same login, so anybody who could read one could read them all: the
/// complexity would buy a reassurance rather than a defence.
/// </para>
/// <para>
/// The key is random and is never derived from anything a person types. A passphrase-derived key
/// sounds stronger and is weaker in practice — it is as good as the passphrase, it has to be
/// typed at every start or cached somewhere that is exactly this keyring, and the version that is
/// cached protects nothing the keyring did not already protect.
/// </para>
/// </remarks>
public static class StoreKey
{
    /// <summary>How many bytes the cipher is keyed with. 256 bits.</summary>
    public const int Bytes = 32;

    /// <summary>What the keyring files this under.</summary>
    public const string Purpose = "store";

    /// <summary>What the keyring calls the account it is stored against.</summary>
    public const string Account = "mailbox-store";

    private static string? _hex;

    /// <summary>Whether the stores opened from now on are encrypted.</summary>
    public static bool IsSet => _hex is not null;

    /// <summary>
    /// The key as SQLite wants to be told it: raw bytes in hexadecimal, so nothing is derived
    /// from it a second time.
    /// </summary>
    /// <remarks>
    /// <c>PRAGMA key = 'password'</c> would run the text through a key derivation of the
    /// library's choosing; <c>PRAGMA key = "x'…'"</c> uses the bytes as the key. What is stored
    /// is already 32 random bytes, so deriving from it again would add work and no entropy.
    /// </remarks>
    internal static string? Hex => _hex;

    /// <summary>Uses this key for every store opened from now on.</summary>
    /// <param name="key">32 bytes, or null to go back to opening stores in the clear.</param>
    public static void Use(byte[]? key)
    {
        if (key is null)
        {
            _hex = null;
            return;
        }

        if (key.Length != Bytes)
        {
            throw new ArgumentException($"A store key is {Bytes} bytes, not {key.Length}.", nameof(key));
        }

        _hex = Convert.ToHexString(key);
    }

    /// <summary>A new key, from the operating system's own randomness.</summary>
    public static byte[] Make() => RandomNumberGenerator.GetBytes(Bytes);

    /// <summary>Reads a key back from what the keyring returned, or null when it is not one.</summary>
    /// <remarks>
    /// A keyring entry that has been truncated, replaced or was never written is answered as "no
    /// key" rather than as a key that will not open anything: the caller's next move is different
    /// for the two, and a malformed entry must not be offered to the cipher.
    /// </remarks>
    public static byte[]? Parse(string? stored)
    {
        if (stored is not { Length: Bytes * 2 }) return null;

        try
        {
            return Convert.FromHexString(stored);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>How a key is written for the keyring.</summary>
    public static string Format(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexString(key);
    }
}
