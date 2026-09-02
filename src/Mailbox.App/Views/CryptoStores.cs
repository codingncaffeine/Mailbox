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

    /// <summary>The certificate store, or null when the reader has not switched S/MIME on.</summary>
    /// <remarks>
    /// Null rather than an empty context, because "off" and "has no keys" are different answers and
    /// the second one names people who could not be written to.
    /// </remarks>
    public static SecureMimeContext? CertificatesIfEnabled()
        => App.Security.Smime ? Certificates() : null;

    /// <summary>
    /// The keyring, or null when the reader has not switched OpenPGP on — or when GnuPG is doing
    /// the OpenPGP work, in which case the ring kept here is not the one to use.
    /// </summary>
    /// <remarks>
    /// Null rather than "both", deliberately. Two rings each holding half of somebody's keys is
    /// the parallel world the delegation exists to end, and a caller handed both would have to
    /// decide which one a message belongs to — a question with no right answer.
    /// </remarks>
    public static PgpContext? KeyRingIfEnabled()
        => App.Security.OpenPgp && !UsingGnuPg ? KeyRing() : null;

    /// <summary>
    /// The reader's own GnuPG, or null when the delegation is off or the tool is not installed.
    /// </summary>
    /// <remarks>
    /// A missing <c>gpg</c> answers null rather than throwing, so a reader who switched this on
    /// and then removed GnuPG gets the keyring kept here back instead of an application that
    /// cannot send. The switch's own row says what it needs.
    /// </remarks>
    public static GnuPgAgent? AgentIfEnabled()
        => UsingGnuPg ? new GnuPgAgent() : null;

    /// <summary>Whether OpenPGP means the reader's own GnuPG on this machine, right now.</summary>
    public static bool UsingGnuPg
        => App.Security.OpenPgp && App.Security.OpenPgpThroughGnuPg && GnuPgAgent.IsAvailable;

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
