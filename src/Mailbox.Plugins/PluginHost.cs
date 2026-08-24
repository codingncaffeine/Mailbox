using MimeKit;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;
using Mailbox.Plugins.Api;
using Mailbox.Protocols;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Plugins;

/// <summary>Where a plugin stands, as the Add-ins page reports it.</summary>
public enum PluginState
{
    /// <summary>Loaded and running.</summary>
    Enabled,

    /// <summary>Present and not loaded — the reader's choice, or plugins are off altogether.</summary>
    Disabled,

    /// <summary>Was running and threw; disabled with the report kept. §13's visible report.</summary>
    Crashed,

    /// <summary>Written against a newer API than this build carries. Never loaded.</summary>
    Incompatible,

    /// <summary>The manifest or the assembly could not be read. Never loaded.</summary>
    Broken,
}

/// <summary>One plugin as the UI lists it: the manifest's claims, and what became of them.</summary>
public sealed record PluginRecord(
    string Directory,
    PluginManifest? Manifest,
    PluginState State,
    string? Error,
    IReadOnlyList<string> UndeclaredUses)
{
    public string Id => Manifest?.Id ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));

    public string Name => Manifest?.Name ?? Id;
}

/// <summary>
/// What the host needs from the application, as delegates rather than a reference to it — the
/// host is a library and the tests are its second caller.
/// </summary>
public sealed class PluginHostServices
{
    public required CommandCatalog Commands { get; init; }

    public required SettingsStore Settings { get; init; }

    /// <summary>Every open account's store with its address — the shape of <c>App.Mailboxes()</c>.</summary>
    public required Func<IReadOnlyList<(string Address, MailRepository Mail)>> Mailboxes { get; init; }

    /// <summary>The PIM store, or null in a host that has none (some tests).</summary>
    public PimRepository? Pim { get; init; }

    /// <summary>Queues a saved PIM item for its server — <c>App.PimSync.QueuePut</c>. Null skips.</summary>
    public Action<PimItem>? QueuePut { get; init; }

    /// <summary>Marshals a plugin's UI-facing callback onto the UI thread. Null runs it in place.</summary>
    public Action<Action>? RunOnUiThread { get; init; }
}

/// <summary>
/// The plugin manager: discovery, loading, enabling and disabling, and the one place a plugin's
/// contributions enter the application's own registries — and leave them again.
/// </summary>
/// <remarks>
/// Every contribution is tracked against the plugin that made it, because disabling is the
/// reverse of loading: commands out of the catalogue, tabs off the ribbon, hooks out of the
/// pipelines, and then the load context unloaded. A delegate kept anywhere after that would keep
/// the plugin's assembly alive, which is why revocation clears every collection rather than
/// trusting the plugin to have registered nothing else.
/// <para>
/// A plugin that throws — from <c>Initialize</c> or from any hook — is disabled with the report
/// kept and shown, per §13. The application carries on; the other plugins carry on.
/// </para>
/// </remarks>
public sealed class PluginHost
{
    /// <summary>The Add-ins page's "Load plugins at startup".</summary>
    public const string LoadKey = "plugins.load";

    /// <summary>The Add-ins page's "Warn me when a plugin requests a permission it did not declare".</summary>
    public const string WarnUndeclaredKey = "plugins.warn.undeclared";

    public static string EnabledKey(string pluginId) => $"plugins.{pluginId}.enabled";

    private readonly object _gate = new();
    private readonly List<Entry> _plugins = [];
    private readonly PluginHostServices _services;
    private bool _quiet;

    public PluginHost(string root, PluginHostServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Root = root;
        _services = services;
        Arrivals = new ArrivalBridge(this);
    }

    /// <summary>The directory the plugins live in, one subdirectory each.</summary>
    public string Root { get; }

    /// <summary>Raised when the set changed — loaded, enabled, disabled, crashed — for the UI and the ribbon.</summary>
    public event EventHandler? Changed;

    /// <summary>The arrival pipeline's plugin stage. Appended after the application's own handlers.</summary>
    public IArrivalHandler Arrivals { get; }

    /// <summary>Where plugins live by default: beside the data, because a plugin is installed, not configured.</summary>
    public static string DefaultRoot()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(data, "mailbox", "plugins");
    }

    // ---- Discovery and loading ---------------------------------------------------------------

    /// <summary>
    /// Reads the plugins directory and loads what is enabled. Called once at startup; safe to
    /// call again, which is what the Add-ins page's refresh does — already-loaded plugins stay
    /// as they are and newly arrived directories join the list.
    /// </summary>
    public void Start()
    {
        var load = _services.Settings.GetBool(LoadKey, fallback: true);

        lock (_gate)
        {
            _quiet = true;
            try
            {
                foreach (var directory in Discover())
                {
                    if (_plugins.Any(p => PathsEqual(p.Directory, directory))) continue;

                    var entry = Examine(directory);
                    _plugins.Add(entry);

                    if (entry.State == PluginState.Disabled
                        && load
                        && _services.Settings.GetBool(EnabledKey(entry.Manifest!.Id), fallback: true))
                    {
                        LoadAndInitialize(entry);
                    }
                }
            }
            finally
            {
                _quiet = false;
            }
        }

        var report = Plugins;
        Log.Info(report.Count == 0
            ? "No plugins."
            : $"Plugins: {string.Join(", ", report.Select(p => $"{p.Id} {p.State.ToString().ToLowerInvariant()}"))}");

        RaiseChanged();
    }

    /// <summary>The plugins as the Add-ins page lists them. A snapshot; order is discovery order.</summary>
    public IReadOnlyList<PluginRecord> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _plugins.Select(p => new PluginRecord(
                    p.Directory, p.Manifest, p.State, p.Error, [.. p.Undeclared]))];
            }
        }
    }

    /// <summary>Enables a plugin: remembered in the settings, and loaded now.</summary>
    public void Enable(string pluginId)
    {
        _services.Settings.Set(EnabledKey(pluginId), true);

        lock (_gate)
        {
            var entry = _plugins.FirstOrDefault(p => p.Manifest?.Id == pluginId);
            if (entry is null) return;

            // A crashed plugin may be tried again — the report made its point — and a broken or
            // incompatible one is re-examined, in case the reader replaced the files.
            if (entry.State is PluginState.Broken or PluginState.Incompatible or PluginState.Crashed)
            {
                var again = Examine(entry.Directory);
                entry.Manifest = again.Manifest;
                entry.State = again.State;
                entry.Error = again.Error;
                entry.Undeclared.Clear();
            }

            if (entry.State == PluginState.Disabled) LoadAndInitialize(entry);
        }

        RaiseChanged();
    }

    /// <summary>Disables a plugin: contributions revoked, code unloaded, remembered in the settings.</summary>
    public void Disable(string pluginId)
    {
        _services.Settings.Set(EnabledKey(pluginId), false);

        lock (_gate)
        {
            var entry = _plugins.FirstOrDefault(p => p.Manifest?.Id == pluginId);
            if (entry is null || entry.State != PluginState.Enabled) return;

            Teardown(entry);
            entry.State = PluginState.Disabled;
            entry.Error = null;
        }

        RaiseChanged();
    }

    private IEnumerable<string> Discover()
    {
        if (!Directory.Exists(Root)) return [];

        try
        {
            return Directory.EnumerateDirectories(Root)
                .Where(d => File.Exists(Path.Combine(d, "plugin.json")))
                .OrderBy(d => d, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warn($"The plugins directory could not be read: {Root}", ex);
            return [];
        }
    }

    /// <summary>Reads a directory's manifest and decides what may be done with it. Loads no code.</summary>
    private Entry Examine(string directory)
    {
        var entry = new Entry { Directory = directory };

        string json;
        try
        {
            json = File.ReadAllText(Path.Combine(directory, "plugin.json"));
        }
        catch (Exception ex)
        {
            entry.State = PluginState.Broken;
            entry.Error = $"plugin.json could not be read: {ex.Message}";
            return entry;
        }

        if (!PluginManifest.TryRead(json, out var manifest, out var error))
        {
            entry.State = PluginState.Broken;
            entry.Error = error;
            return entry;
        }

        entry.Manifest = manifest;

        if (_plugins.Any(p => p.Manifest?.Id == manifest!.Id))
        {
            entry.State = PluginState.Broken;
            entry.Error = $"Another plugin already carries the id '{manifest!.Id}'.";
            return entry;
        }

        // Newer major, or a newer minor within this major, both mean calls this host has not
        // got. Refused at the door with the two versions named, which is the only message a
        // reader can act on.
        if (manifest!.Api.Major != PluginApi.Version.Major || manifest.Api > PluginApi.Version)
        {
            entry.State = PluginState.Incompatible;
            entry.Error = $"Wants API {manifest.Api}; this build carries {PluginApi.Version}.";
            return entry;
        }

        if (!File.Exists(Path.Combine(directory, manifest.Assembly)))
        {
            entry.State = PluginState.Broken;
            entry.Error = $"The manifest names '{manifest.Assembly}', which is not beside it.";
            return entry;
        }

        entry.State = PluginState.Disabled;
        return entry;
    }

    /// <summary>Loads an examined plugin and runs its <c>Initialize</c>. Under the gate.</summary>
    private void LoadAndInitialize(Entry entry)
    {
        var manifest = entry.Manifest!;
        var path = Path.Combine(entry.Directory, manifest.Assembly);

        try
        {
            var context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(path);

            var type = manifest.Type is { Length: > 0 } named
                ? assembly.GetType(named, throwOnError: false)
                : SingleEntryPoint(assembly, out var ambiguity) ?? throw new InvalidOperationException(ambiguity);

            if (type is null || !typeof(IPlugin).IsAssignableFrom(type))
            {
                throw new InvalidOperationException(manifest.Type is null
                    ? "The assembly holds no public IPlugin."
                    : $"'{manifest.Type}' is not a public IPlugin in the assembly.");
            }

            entry.Context = context;
            entry.Instance = (IPlugin)Activator.CreateInstance(type)!;
            entry.Facade = new HostFacade(this, entry, _services);
            entry.State = PluginState.Enabled;
            entry.Error = null;

            entry.Instance.Initialize(entry.Facade);
            Log.Info($"Plugin {manifest.Id} {manifest.PluginVersion} loaded.");
        }
        catch (Exception ex)
        {
            Fail(entry, "Loading", ex);
        }
    }

    private static Type? SingleEntryPoint(System.Reflection.Assembly assembly, out string ambiguity)
    {
        var candidates = assembly.GetExportedTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsClass: true })
            .ToList();

        ambiguity = candidates.Count switch
        {
            0 => "The assembly holds no public IPlugin.",
            > 1 => "The assembly holds more than one IPlugin; the manifest's \"type\" must name one.",
            _ => string.Empty,
        };

        return candidates.Count == 1 ? candidates[0] : null;
    }

    // ---- Failure and teardown ------------------------------------------------------------------

    /// <summary>
    /// §13's rule: a plugin that throws is disabled with a visible report rather than taking the
    /// application down. Safe to call from inside one of the plugin's own hooks — the unload is
    /// cooperative and completes once the plugin's frames have returned.
    /// </summary>
    private void Fail(Entry entry, string stage, Exception ex)
    {
        Log.Warn($"Plugin {entry.Manifest?.Id ?? entry.Directory} failed and is disabled.", ex);

        lock (_gate)
        {
            Teardown(entry);
            entry.State = PluginState.Crashed;
            entry.Error = $"{stage}: {ex.Message}";
        }

        RaiseChanged();
    }

    /// <summary>Reverses everything a load did. Under the gate, or from Fail which takes it.</summary>
    private void Teardown(Entry entry)
    {
        var id = entry.Manifest?.Id;
        if (id is not null) _services.Commands.UnregisterPlugin(id);

        // Every delegate that points into the plugin's assembly is dropped here; one kept
        // anywhere would keep the whole load context alive past its unload.
        entry.Actions.Clear();
        entry.Tabs.Clear();
        entry.SimplifiedTabs.Clear();
        entry.ArrivalHooks = [];
        entry.SendingHooks = [];
        entry.BarProviders = [];

        if (entry.Instance is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Plugin {id} threw while being disposed.", ex);
            }
        }

        entry.Instance = null;
        entry.Facade = null;

        if (entry.Context is { } context)
        {
            entry.Context = null;
            _unloaded.Add(new WeakReference(context));
            context.Unload();
        }
    }

    /// <summary>For the test that proves a disabled plugin's code really unloads.</summary>
    internal IReadOnlyList<WeakReference> UnloadedContexts => _unloaded;

    private readonly List<WeakReference> _unloaded = [];

    private void RaiseChanged()
    {
        if (_quiet) return;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- What the shell asks -------------------------------------------------------------------

    /// <summary>
    /// Runs a plugin's command, if the id names one. The shell's dispatcher asks here after its
    /// own handlers, exactly as it asks the Quick Steps.
    /// </summary>
    public bool TryRun(CommandId id)
    {
        Entry? owner = null;
        Action? action = null;

        lock (_gate)
        {
            foreach (var entry in _plugins)
            {
                if (entry.State == PluginState.Enabled && entry.Actions.TryGetValue(id, out action))
                {
                    owner = entry;
                    break;
                }
            }
        }

        if (owner is null || action is null) return false;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Fail(owner, $"Running {id}", ex);
        }

        return true;
    }

    /// <summary>
    /// The shipped layout with the enabled plugins' tabs appended, in both renderings. Applied
    /// before the reader's own edits, so Customize Ribbon sees plugin tabs as part of the
    /// furniture — hideable, reorderable, and their commands placeable anywhere.
    /// </summary>
    public RibbonLayout InjectRibbon(RibbonLayout shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        List<RibbonTab> tabs;
        Dictionary<string, SimplifiedBar> bars;

        lock (_gate)
        {
            var enabled = _plugins.Where(p => p.State == PluginState.Enabled && p.Tabs.Count > 0).ToList();
            if (enabled.Count == 0) return shipped;

            tabs = [.. enabled.SelectMany(p => p.Tabs)];
            bars = enabled.SelectMany(p => p.SimplifiedTabs)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        var simplified = shipped.Simplified.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (tabId, bar) in bars) simplified[tabId] = bar;

        return shipped with
        {
            Tabs = [.. shipped.Tabs, .. tabs],
            Simplified = simplified,
        };
    }

    /// <summary>
    /// Asks every enabled sending hook about an outgoing message. The first refusal wins and
    /// names its plugin; null means the message goes.
    /// </summary>
    public (string PluginName, string Reason)? BeforeSend(string account, MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var outgoing = new OutgoingMessage(
            account,
            message.Subject ?? string.Empty,
            message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
            [.. message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
                .Select(m => m.Address)]);

        foreach (var (entry, hooks) in Snapshot(p => p.SendingHooks))
        {
            foreach (var hook in hooks)
            {
                SendDecision decision;
                try
                {
                    decision = hook(outgoing);
                }
                catch (Exception ex)
                {
                    Fail(entry, "A sending hook", ex);
                    break;
                }

                if (decision is { Allowed: false })
                {
                    return (entry.Manifest!.Name, decision.Reason ?? "no reason given");
                }
            }
        }

        return null;
    }

    /// <summary>What the enabled plugins want said above a rendered message, with who said it.</summary>
    public IReadOnlyList<(string PluginName, PluginInfoBar Bar)> InfoBarsFor(PluginMessageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var bars = new List<(string, PluginInfoBar)>();

        foreach (var (entry, providers) in Snapshot(p => p.BarProviders))
        {
            foreach (var provider in providers)
            {
                try
                {
                    if (provider(summary) is not { } bar) continue;

                    // The button is pressed long after this call returns, so its failure is
                    // caught here and charged to its plugin — the pane should never need to
                    // know whose delegate it is holding.
                    if (bar.ButtonPressed is { } pressed)
                    {
                        var owner = entry;
                        bar = bar with
                        {
                            ButtonPressed = () =>
                            {
                                try
                                {
                                    pressed();
                                }
                                catch (Exception ex)
                                {
                                    Fail(owner, "A bar's button", ex);
                                }
                            },
                        };
                    }

                    bars.Add((entry.Manifest!.Name, bar));
                }
                catch (Exception ex)
                {
                    Fail(entry, "An info bar", ex);
                    break;
                }
            }
        }

        return bars;
    }

    /// <summary>Enabled entries paired with one of their hook lists, snapshotted under the gate.</summary>
    private List<(Entry Entry, IReadOnlyList<T> Hooks)> Snapshot<T>(Func<Entry, IReadOnlyList<T>> hooks)
    {
        lock (_gate)
        {
            return [.. _plugins
                .Where(p => p.State == PluginState.Enabled)
                .Select(p => (p, hooks(p)))
                .Where(x => x.Item2.Count > 0)];
        }
    }

    // ---- Registration, called by the facade ----------------------------------------------------

    internal void AddCommand(Entry entry, MailboxCommand command, Action execute)
    {
        lock (_gate)
        {
            _services.Commands.Register(command);
            entry.Actions[command.Id] = execute;
        }

        RaiseChanged();
    }

    internal void AddTab(Entry entry, RibbonTab classic, SimplifiedBar simplified)
    {
        lock (_gate)
        {
            entry.Tabs.Add(classic);
            entry.SimplifiedTabs[classic.Id] = simplified;
        }

        RaiseChanged();
    }

    internal void AddArrivalHook(Entry entry, Func<ArrivingMessage, ArrivalAction> hook)
    {
        lock (_gate) entry.ArrivalHooks = [.. entry.ArrivalHooks, hook];
    }

    internal void AddSendingHook(Entry entry, Func<OutgoingMessage, SendDecision> hook)
    {
        lock (_gate) entry.SendingHooks = [.. entry.SendingHooks, hook];
    }

    internal void AddBarProvider(Entry entry, Func<PluginMessageSummary, PluginInfoBar?> provider)
    {
        lock (_gate) entry.BarProviders = [.. entry.BarProviders, provider];
    }

    /// <summary>
    /// Records a call whose permission the manifest does not declare. The call itself was
    /// refused; this is the part the Add-ins page shows, and the warning the Options row asks
    /// for.
    /// </summary>
    internal void RecordUndeclared(Entry entry, string permission)
    {
        var warn = false;

        lock (_gate)
        {
            if (!entry.Undeclared.Contains(permission, StringComparer.OrdinalIgnoreCase))
            {
                entry.Undeclared.Add(permission);
                warn = _services.Settings.GetBool(WarnUndeclaredKey, fallback: true);
            }
        }

        if (warn)
        {
            Log.Warn($"Plugin {entry.Manifest?.Id} used '{permission}' without declaring it. " +
                     "The call was refused; the Add-ins page says so.");
            RaiseChanged();
        }
    }

    // ---- The arrival bridge --------------------------------------------------------------------

    /// <summary>
    /// The plugin stage of the arrival pipeline. One stage for all plugins, appended after the
    /// application's own handlers, so a hook sees where the junk filter and the rules left a
    /// message — and each plugin's failure is its own, not the stage's.
    /// </summary>
    private sealed class ArrivalBridge(PluginHost host) : IArrivalHandler
    {
        public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
        {
            var hooks = host.Snapshot(p => p.ArrivalHooks);
            if (hooks.Count == 0) return folder.Id;

            var account = mail.GetAccount(folder.AccountId)?.Address ?? string.Empty;
            var current = folder;

            var arriving = new ArrivingMessage(
                account,
                messageId,
                current.Name,
                message.Subject ?? string.Empty,
                message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
                [.. message.To.Mailboxes.Select(m => m.Address)]);

            foreach (var (entry, list) in hooks)
            {
                foreach (var hook in list)
                {
                    ArrivalAction action;
                    try
                    {
                        action = hook(arriving with { Folder = current.Name });
                    }
                    catch (Exception ex)
                    {
                        host.Fail(entry, "An arrival hook", ex);
                        break;
                    }

                    switch (action?.Kind)
                    {
                        case ArrivalActionKind.Delete:
                            mail.DeleteMessage(messageId);
                            return null;

                        case ArrivalActionKind.Move when action.Folder is { Length: > 0 } name:
                            var target = mail.Folders(current.AccountId).FirstOrDefault(
                                f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                            if (target is null)
                            {
                                // Creating a folder a hook merely mentioned would let a typo
                                // grow the tree; the miss is recorded instead, and the message
                                // stays findable where it is.
                                Log.Warn($"Plugin {entry.Manifest?.Id} moved a message to " +
                                         $"'{name}', which names no folder. It stays where it is.");
                                break;
                            }

                            if (target.Id != current.Id)
                            {
                                mail.MoveMessage(messageId, target.Id);
                                current = target;
                            }

                            break;
                    }
                }
            }

            return current.Id;
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            StringComparison.Ordinal);

    /// <summary>One plugin's whole life: what was found, what was loaded, what it contributed.</summary>
    internal sealed class Entry
    {
        public required string Directory { get; init; }

        public PluginManifest? Manifest { get; set; }

        public PluginState State { get; set; } = PluginState.Disabled;

        public string? Error { get; set; }

        public List<string> Undeclared { get; } = [];

        public PluginLoadContext? Context { get; set; }

        public IPlugin? Instance { get; set; }

        public HostFacade? Facade { get; set; }

        public Dictionary<CommandId, Action> Actions { get; } = [];

        public List<RibbonTab> Tabs { get; } = [];

        public Dictionary<string, SimplifiedBar> SimplifiedTabs { get; } = [];

        // Replaced whole on registration and cleared on teardown, so a reader iterating a
        // snapshot never sees a list change under it.
        public IReadOnlyList<Func<ArrivingMessage, ArrivalAction>> ArrivalHooks { get; set; } = [];

        public IReadOnlyList<Func<OutgoingMessage, SendDecision>> SendingHooks { get; set; } = [];

        public IReadOnlyList<Func<PluginMessageSummary, PluginInfoBar?>> BarProviders { get; set; } = [];
    }
}
