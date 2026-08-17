namespace Mailbox.Core.Settings;

/// <summary>
/// The Trust Center's crypto switches, as the code that acts on them reads them.
/// </summary>
/// <remarks>
/// <b>Both start off, and that is the design</b> (§14): S/MIME and OpenPGP need key material
/// before they can do anything, and a client that says "signed" over a check it has not made is
/// worse than one that says nothing. Nothing about a message is checked, decrypted or reported
/// until the reader has said they want it.
/// <para>
/// Keyed explicitly rather than by the row's label, because these are the settings code reads: a
/// reworded label must not silently forget what the reader chose.
/// </para>
/// </remarks>
public sealed class SecurityOptions(SettingsStore settings)
{
    public const string SmimeKey = "security.smime.enabled";
    public const string OpenPgpKey = "security.openpgp.enabled";

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
}
