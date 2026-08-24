namespace Mailbox.Plugins.Api;

/// <summary>
/// What a plugin may do, handed to <see cref="IPlugin.Initialize"/>. Every surface hangs off
/// this one interface, so what the API offers can be read in one place.
/// </summary>
/// <remarks>
/// Each surface is gated on the manifest: a call whose permission the plugin did not declare
/// throws <see cref="PluginPermissionException"/> and is recorded, and the Add-ins page says so.
/// That is a courtesy to well-behaved plugins and their readers, not a boundary — an in-process
/// assembly can reach anything reflection reaches, which is why the manifest is described as
/// disclosure throughout.
/// </remarks>
public interface IPluginHost
{
    /// <summary>The plugin's own id, as the manifest declared it.</summary>
    string PluginId { get; }

    /// <summary>The directory the plugin was loaded from, for files of its own.</summary>
    string PluginDirectory { get; }

    /// <summary>The API version the host is running. Never newer than the manifest asked for.</summary>
    Version ApiVersion { get; }

    /// <summary>Writes one line to the application log, prefixed with the plugin's id.</summary>
    void Log(string message);

    /// <summary>Settings of the plugin's own, kept with the application's under its id.</summary>
    IPluginSettings Settings { get; }

    /// <summary>Commands and their place on the ribbon. Permission: <c>ui</c>.</summary>
    IPluginCommands Commands { get; }

    /// <summary>Mail, read and acted on. Permissions: <c>mail</c>, <c>mail-write</c>.</summary>
    IPluginMail Mail { get; }

    /// <summary>Calendars, tasks, notes and contacts. Permissions: <c>pim</c>, <c>pim-write</c>.</summary>
    IPluginPim Pim { get; }

    /// <summary>Arriving and outgoing mail. Permissions: <c>arrival</c>, <c>sending</c>.</summary>
    IPluginPipeline Pipeline { get; }

    /// <summary>Bars above a rendered message. Permission: <c>ui</c>.</summary>
    IPluginReadingPane ReadingPane { get; }

    /// <summary>Columns on the message list's table views. Permission: <c>ui</c>.</summary>
    IPluginColumns Columns { get; }

    /// <summary>Account providers the wizard consults. Permission: <c>accounts</c>.</summary>
    IPluginAccounts Accounts { get; }
}

/// <summary>
/// Providers the Add Account wizard consults as an address is typed. Permission:
/// <c>accounts</c>.
/// </summary>
/// <remarks>
/// A provider recognises addresses and answers with the servers they want — what the built-in
/// autoconfiguration does for the well-known ones, extended to whatever a plugin knows. The
/// first enabled provider that answers wins, its settings fill the wizard's boxes, and its
/// guidance is the line under them; the reader can still open the boxes and disagree.
/// Authentication is the ordinary password path — a provider that needs its own sign-in dance
/// is a later API, and saying so here beats half-pretending.
/// </remarks>
public interface IPluginAccounts
{
    void RegisterProvider(PluginAccountProvider provider);
}

/// <summary>One provider: a name for the guidance line, and the recogniser.</summary>
public sealed record PluginAccountProvider
{
    public required string Name { get; init; }

    /// <summary>
    /// Answers for an address it recognises, null for one it does not. Called as the reader
    /// types, so answer from the string alone — never the network.
    /// </summary>
    public required Func<string, PluginAccountSettings?> Recognize { get; init; }
}

/// <summary>The servers a recognised address should use.</summary>
public sealed record PluginAccountSettings(
    string IncomingHost, int IncomingPort, string OutgoingHost, int OutgoingPort)
{
    /// <summary><c>imap</c> (the default) or <c>pop3</c>.</summary>
    public string Protocol { get; init; } = "imap";

    /// <summary>The wizard's line under the boxes, beside the provider's name.</summary>
    public string? Guidance { get; init; }
}

/// <summary>
/// A few values of the plugin's own, stored beside the application's settings under keys the
/// host namespaces by plugin id — one plugin cannot read or clobber another's, or the
/// application's.
/// </summary>
public interface IPluginSettings
{
    string GetString(string key, string fallback = "");
    bool GetBool(string key, bool fallback = false);
    double GetNumber(string key, double fallback = 0);
    void Set(string key, string value);
    void Set(string key, bool value);
    void Set(string key, double value);
    void Remove(string key);
}

/// <summary>
/// Thrown when a call's permission is not in the plugin's manifest. The use is recorded and
/// shown on the Add-ins page; the plugin itself keeps running unless the throw unwinds it.
/// </summary>
public sealed class PluginPermissionException(string permission)
    : InvalidOperationException(
        $"This call needs the '{permission}' permission, which the plugin's manifest does not declare.")
{
    public string Permission { get; } = permission;
}

/// <summary>
/// The permission names a manifest may declare. Unknown names are kept and shown rather than
/// refused, so a newer plugin's manifest still reads on an older host.
/// </summary>
public static class PluginPermission
{
    /// <summary>Read accounts, folders and messages.</summary>
    public const string Mail = "mail";

    /// <summary>Move, delete and mark messages.</summary>
    public const string MailWrite = "mail-write";

    /// <summary>Read PIM collections and items.</summary>
    public const string Pim = "pim";

    /// <summary>Write PIM items.</summary>
    public const string PimWrite = "pim-write";

    /// <summary>Act on mail as it arrives.</summary>
    public const string Arrival = "arrival";

    /// <summary>Inspect and stop outgoing mail.</summary>
    public const string Sending = "sending";

    /// <summary>Commands, ribbon tabs and reading-pane bars.</summary>
    public const string Ui = "ui";

    /// <summary>Account providers the Add Account wizard consults.</summary>
    public const string Accounts = "accounts";

    /// <summary>
    /// Declares that the plugin talks to the network on its own. Nothing in the API does
    /// network I/O for a plugin, so this is disclosure in its purest form: a statement on the
    /// Add-ins page, for the reader deciding whether to trust the file.
    /// </summary>
    public const string Network = "network";
}
