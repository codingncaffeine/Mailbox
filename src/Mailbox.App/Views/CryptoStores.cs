using Mailbox.Security.OpenPgp;
using Mailbox.Security.Smime;
using MimeKit.Cryptography;

namespace Mailbox.App.Views;

/// <summary>
/// Where this machine's key material is, and the one rule about when it may be touched.
/// </summary>
/// <remarks>
/// One place rather than two because both halves of the application now open these stores — the
/// reading pane to check and decrypt, the compose window to sign and encrypt — and a rule that has
/// to be written twice is a rule that will eventually only be written once.
/// <para>
/// Both stores sit beside the application's own data rather than in the libraries' default homes,
/// for the reason the mail stores do: what this application keeps is in one place a reader can find,
/// back up and delete. The OpenPGP ring is <b>not</b> the desktop's own — see
/// <see cref="PgpContext"/> for why pointing at <c>~/.gnupg</c> would quietly find nothing.
/// </para>
/// </remarks>
public static class CryptoStores
{
    /// <summary>
    /// What has been said to unlock this machine's secret keys, for as long as the application runs.
    /// </summary>
    /// <remarks>
    /// Shared by every context handed out here, so a key unlocked to read a message is still
    /// unlocked when the reply is signed. Emptied by <see cref="PassphraseVault.Forget"/> and by
    /// closing the application, which is the only lifetime worth promising.
    /// </remarks>
    public static PassphraseVault Passphrases { get; } = new();

    /// <summary>The machine's certificate store — or a throwaway one under the harness.</summary>
    /// <remarks>
    /// Through <see cref="CertificateStore"/> rather than MimeKit's file-name constructor, which
    /// looks for a SQLite provider this tree does not carry and threw before it opened anything —
    /// and which, having no single-argument file-name overload, would have taken the path as the
    /// database password and put the file in the library's own home. See that class.
    /// </remarks>
    public static SecureMimeContext Certificates()
        => Throwaway ? new TemporarySecureMimeContext() : CertificateStore.Open(App.StoreDirectory);

    /// <summary>The machine's OpenPGP keys — or an empty throwaway ring under the harness.</summary>
    public static PgpContext KeyRing()
    {
        var directory = Throwaway
            ? Path.Combine(Path.GetTempPath(), "mailbox-capture-keyring")
            : Path.Combine(App.StoreDirectory, "openpgp");

        Directory.CreateDirectory(directory);
        return new PgpContext(directory, Passphrases.For);
    }

    /// <summary>The certificate store, or null when the reader has not switched S/MIME on (§14).</summary>
    /// <remarks>
    /// Null rather than an empty context, because "off" and "has no keys" are different answers and
    /// the second one names people who could not be written to.
    /// </remarks>
    public static SecureMimeContext? CertificatesIfEnabled()
        => App.Security.Smime ? Certificates() : null;

    /// <summary>The keyring, or null when the reader has not switched OpenPGP on (§14).</summary>
    public static PgpContext? KeyRingIfEnabled()
        => App.Security.OpenPgp ? KeyRing() : null;

    /// <summary>
    /// Whether this run must not go near the reader's own key material.
    /// </summary>
    /// <remarks>
    /// A capture run gets throwaway stores, for the reason it gets an in-memory credential store:
    /// photographing a window is no reason to open a keyring. <b>Unless a store is posed</b> —
    /// <c>MAILBOX_STORE</c> names a scratch directory that brings its own everything, keys included,
    /// and that is how a claim about an encrypted message gets pressed and read back at all.
    /// Without the exception the only provable state is "could not open it".
    /// </remarks>
    public static bool Throwaway
        => Theming.WindowCapture.IsRequested
           && Environment.GetEnvironmentVariable("MAILBOX_STORE") is not { Length: > 0 };
}
