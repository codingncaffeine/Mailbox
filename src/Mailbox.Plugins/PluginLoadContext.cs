using System.Reflection;
using System.Runtime.Loader;

namespace Mailbox.Plugins;

/// <summary>
/// The load context one plugin's assemblies live in: collectible, so disabling the plugin can
/// unload its code without a restart.
/// </summary>
/// <remarks>
/// Two rules decide where an assembly comes from, and their order is the point. The API package
/// and anything else the application itself has loaded resolve
/// to the application's own copy — a plugin that carried its own <c>Mailbox.Plugins.Api.dll</c>
/// would otherwise implement a private <c>IPlugin</c> no cast here could see. Everything else the
/// plugin depends on resolves from the plugin's own directory through its <c>.deps.json</c>, so
/// two plugins may bring two different versions of the same library without meeting each other.
/// </remarks>
internal sealed class PluginLoadContext(string pluginAssemblyPath)
    : AssemblyLoadContext($"plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The API package always unifies with the host's copy. Checked by name before the
        // resolver is asked, because the plugin's directory may well contain a copy — the SDK
        // copies references locally by default — and that copy is the wrong one to load.
        if (string.Equals(assemblyName.Name, "Mailbox.Plugins.Api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // An assembly the application already carries is shared rather than doubled. This keeps a
        // plugin from shadowing the host's own assemblies as the API rule does, and returns null
        // (fall through to the default context) rather than loading eagerly, so the runtime's own
        // resolution — and its version unification — stays in charge.
        if (AssemblyLoadContext.Default.Assemblies.Any(
                a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
