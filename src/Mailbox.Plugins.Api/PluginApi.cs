namespace Mailbox.Plugins.Api;

/// <summary>
/// The contract version this build of the API carries.
/// </summary>
/// <remarks>
/// A manifest states the version its plugin was compiled against, and the host refuses one newer
/// than its own — a plugin asking for calls that are not there would otherwise fail somewhere in
/// the middle of running rather than at the door, with a message nobody can act on. Growth is
/// additive within a major version: a 1.0 plugin loads on every 1.x host.
/// </remarks>
public static class PluginApi
{
    public static Version Version { get; } = new(1, 0);
}

/// <summary>
/// A plugin's entry point. The host finds the implementing type in the manifest's assembly,
/// constructs it with its parameterless constructor, and calls <see cref="Initialize"/> once.
/// </summary>
/// <remarks>
/// Everything a plugin contributes — commands, hooks, bars — is registered from inside
/// <see cref="Initialize"/> through the host it is handed. A plugin that also implements
/// <see cref="IDisposable"/> is disposed when it is disabled, which is its chance to stop any
/// work of its own; the host revokes every registration itself, so a plugin does not unregister
/// anything.
/// <para>
/// A plugin that throws — here or from any hook it registered — is disabled with a visible
/// report rather than taking the application down. There is no sandbox to catch anything else:
/// a plugin runs in-process with the application's own reach, and installing one is trusting it
/// exactly as far as the application itself is trusted. The manifest's permission list is
/// disclosure, not enforcement.
/// </para>
/// </remarks>
public interface IPlugin
{
    void Initialize(IPluginHost host);
}
