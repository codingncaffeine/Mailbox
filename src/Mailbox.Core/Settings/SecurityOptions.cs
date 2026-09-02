namespace Mailbox.Core.Settings;

/// <summary>
/// The Trust Center's switches, as the code that acts on them reads them.
/// </summary>
/// <remarks>
/// <b>The two crypto switches start off, and that is the design</b>: S/MIME and OpenPGP need
/// key material before they can do anything, and a client that says "signed" over a check it has
/// not made is worse than one that says nothing. Nothing about a message is checked, decrypted or
/// reported until the reader has said they want it.
/// <para>
/// The four reading-pane switches start <em>on</em>, because each names a protection that is
/// already the behaviour: pictures held back, the hosts a message reached for named, the sending
/// domain's own checks reported, and a display name that disagrees with its address called out.
/// They are keys rather than defaults so that turning one off does something — until they existed
/// each of those behaviours was unconditional and its row on the Trust Center page was a switch
/// that could never move.
/// </para>
/// <para>
/// Keyed explicitly rather than by the row's label, because these are the settings code reads: a
/// reworded label must not silently forget what the reader chose.
/// </para>
/// </remarks>
public sealed class SecurityOptions(SettingsStore settings)
{
    public const string SmimeKey = "security.smime.enabled";
    public const string OpenPgpKey = "security.openpgp.enabled";

    /// <summary>"Don't download pictures automatically in messages."</summary>
    public const string BlockRemotePicturesKey = "security.pictures.block";

    /// <summary>"Report the hosts a message tried to contact."</summary>
    public const string ReportTrackerHostsKey = "security.trackers.report";

    /// <summary>"Show DKIM, SPF and DMARC results in the reading pane."</summary>
    public const string ShowAuthenticationResultsKey = "security.authresults.show";

    /// <summary>"Warn me when a display name disagrees with the sending address."</summary>
    public const string WarnDisplayNameMismatchKey = "security.displayname.warn";

    /// <summary>"Use GnuPG and its agent for OpenPGP."</summary>
    public const string OpenPgpThroughGnuPgKey = "security.openpgp.gnupg";

    /// <summary>"Encrypt the mail, calendar and contacts kept on this computer."</summary>
    public const string EncryptStoreKey = "security.store.encrypt";

    private readonly SettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Whether S/MIME signatures are checked and encrypted mail is opened at all.</summary>
    public bool Smime
    {
        get => _settings.GetBool(SmimeKey, false);
        set => _settings.Set(SmimeKey, value);
    }

    public bool OpenPgp
    {
        get => _settings.GetBool(OpenPgpKey, false);
        set => _settings.Set(OpenPgpKey, value);
    }

    /// <summary>
    /// Whether OpenPGP's private-key work is handed to the reader's own GnuPG.
    /// </summary>
    /// <remarks>
    /// Off means the keyring kept beside the mail stores, which is the right answer for somebody
    /// who does not already use PGP: it is one place they can find, back up and delete, and it
    /// needs nothing installed. On means <c>gpg</c> signs, encrypts, decrypts and verifies —
    /// so <c>gpg-agent</c> holds the passphrase and asks for it through their own pinentry, the
    /// keyring is the one the rest of their system uses, and a revocation published anywhere else
    /// reaches this application because it is the same keyring.
    /// <para>
    /// Off by default, and deliberately: turning it on for somebody with no GnuPG would replace a
    /// working keyring with an error message. The switch says what it needs, and the reading pane
    /// says so again if the tool goes away.
    /// </para>
    /// </remarks>
    public bool OpenPgpThroughGnuPg
    {
        get => _settings.GetBool(OpenPgpThroughGnuPgKey, false);
        set => _settings.Set(OpenPgpThroughGnuPgKey, value);
    }

    /// <summary>
    /// Whether the databases on this computer are encrypted.
    /// </summary>
    /// <remarks>
    /// Off by default. Taking effect means rewriting every page of every database, which happens
    /// on the next start with nothing open rather than underneath a running interface — so this
    /// setting records what the reader asked for and the start-up makes it true.
    /// <para>
    /// What it protects and what it does not is written where the key lives, and it is worth
    /// reading before promising anybody anything: the key is in the desktop keyring, so this
    /// defends a disk without a login and not a login without a password.
    /// </para>
    /// </remarks>
    public bool EncryptStore
    {
        get => _settings.GetBool(EncryptStoreKey, false);
        set => _settings.Set(EncryptStoreKey, value);
    }

    /// <summary>
    /// Whether a message's remote pictures are held back until the reader asks for them. On.
    /// </summary>
    /// <remarks>
    /// Off means they are fetched as a message is opened — still through Mailbox's own client, so
    /// no cookies, no referer, a timeout and a size cap, and never by the rendering engine. The
    /// sanitizer's blocking is unchanged either way; what this decides is whether the pane then
    /// asks for what was blocked without waiting to be told to. A sender already on the
    /// safe-senders list is allowed whatever this says, which is what that list is for.
    /// </remarks>
    public bool BlockRemotePictures
    {
        get => _settings.GetBool(BlockRemotePicturesKey, true);
        set => _settings.Set(BlockRemotePicturesKey, value);
    }

    /// <summary>
    /// Whether the hosts a message reached for are named to the reader. On.
    /// </summary>
    /// <remarks>
    /// The blocked-content bar's host count and its Details list, and the Tracker Report command
    /// behind them. Off leaves the blocking exactly as it was and stops naming what was blocked —
    /// which is a preference about noise, not about safety.
    /// </remarks>
    public bool ReportTrackerHosts
    {
        get => _settings.GetBool(ReportTrackerHostsKey, true);
        set => _settings.Set(ReportTrackerHostsKey, value);
    }

    /// <summary>
    /// Whether the reading pane says anything about DKIM, SPF and DMARC on its own. On.
    /// </summary>
    /// <remarks>
    /// The trust bar's authentication warnings — the domain's own policy failing, a sender the
    /// domain does not recognise, a signature that does not match the copy that arrived. Off
    /// silences the bar, not the checks: the Authentication command still reports everything,
    /// because that one was asked for by the press.
    /// </remarks>
    public bool ShowAuthenticationResults
    {
        get => _settings.GetBool(ShowAuthenticationResultsKey, true);
        set => _settings.Set(ShowAuthenticationResultsKey, value);
    }

    /// <summary>
    /// Whether a display name that claims one address and was sent from another is called out. On.
    /// </summary>
    public bool WarnDisplayNameMismatch
    {
        get => _settings.GetBool(WarnDisplayNameMismatchKey, true);
        set => _settings.Set(WarnDisplayNameMismatchKey, value);
    }
}
