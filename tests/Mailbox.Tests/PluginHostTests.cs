using System.Runtime.CompilerServices;
using System.Text.Json;
using MimeKit;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;
using Mailbox.Plugins;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The plugin host, exercised through a real plugin assembly staged into scratch directories and
/// loaded through the real discovery — never through the test runner's own context. What is
/// tested is the §13 contract: a manifest is honoured, a newer API is refused at the door, a
/// throwing plugin is disabled with its report while the rest keep running, an undeclared
/// permission is refused and recorded, and disabling really unloads the code.
/// </summary>
public class PluginHostTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-plugin-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string FixtureAssembly =>
        Path.Combine(AppContext.BaseDirectory, "Mailbox.TestPlugin.dll");

    private string PluginsRoot => Path.Combine(_root, "plugins");

    /// <summary>Stages one plugin directory: the fixture assembly beside a manifest naming a type.</summary>
    private void Stage(
        string directory,
        string id,
        string type,
        string[] permissions,
        string api = "1.0",
        string name = "Test Plugin",
        string assembly = "Mailbox.TestPlugin.dll")
    {
        var home = Path.Combine(PluginsRoot, directory);
        Directory.CreateDirectory(home);
        File.Copy(FixtureAssembly, Path.Combine(home, "Mailbox.TestPlugin.dll"));

        File.WriteAllText(Path.Combine(home, "plugin.json"), JsonSerializer.Serialize(new
        {
            id,
            name,
            version = "1.0.0",
            api,
            assembly,
            author = "The tests",
            description = "Loaded by PluginHostTests.",
            type = $"Mailbox.TestPlugin.{type}",
            permissions,
        }));
    }

    private static readonly string[] Everything =
        ["ui", "mail", "mail-write", "pim", "pim-write", "arrival", "sending"];

    private PluginHost Host(
        SettingsStore? settings = null,
        CommandCatalog? catalog = null,
        IReadOnlyList<(string, MailRepository)>? mailboxes = null,
        PimRepository? pim = null)
        => new(PluginsRoot, new PluginHostServices
        {
            Commands = catalog ?? new CommandCatalog(),
            Settings = settings ?? SettingsStore.Transient(),
            Mailboxes = () => mailboxes ?? [],
            Pim = pim,
        });

    // ---- The manifest --------------------------------------------------------------------------

    [Fact]
    public void AManifestNamingAPathIsRefused()
    {
        Assert.False(PluginManifest.TryRead(
            """{"id":"a.b","name":"A","version":"1.0","api":"1.0","assembly":"../../outside.dll"}""",
            out _, out var error));

        Assert.Contains("file name", error);
    }

    [Fact]
    public void AManifestKeepsPermissionsItDoesNotKnow()
    {
        Assert.True(PluginManifest.TryRead(
            """{"id":"a.b","name":"A","version":"1.0","api":"1.0","assembly":"a.dll","permissions":["mail","time-travel"]}""",
            out var manifest, out _));

        Assert.Equal(["mail", "time-travel"], manifest!.Permissions);
        Assert.True(manifest.Declares("time-travel"));
    }

    [Fact]
    public void AnUppercaseIdIsRefused()
    {
        Assert.False(PluginManifest.TryRead(
            """{"id":"A.B","name":"A","version":"1.0","api":"1.0","assembly":"a.dll"}""",
            out _, out var error));

        Assert.Contains("lowercase", error);
    }

    // ---- Loading -------------------------------------------------------------------------------

    [Fact]
    public void AGoodPluginLoadsAndItsCommandPressesThroughTheCatalogue()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var settings = SettingsStore.Transient();
        var catalog = new CommandCatalog();
        var host = Host(settings, catalog);
        host.Start();

        var record = Assert.Single(host.Plugins);
        Assert.Equal(PluginState.Enabled, record.State);

        // The command is an ordinary catalogue entry, owned by its plugin.
        var id = new CommandId("plugin.test.good.hello");
        Assert.True(catalog.TryGet(id, out var command));
        Assert.Equal("test.good", command!.OwningPluginId);
        Assert.False(command.InDefaultLayout);

        // Pressing it through the host is the dispatcher's path, and the effect is read back
        // from the store the plugin wrote, not from the plugin's word for it.
        Assert.True(host.TryRun(id));
        Assert.True(settings.GetBool("plugins.test.good.pressed"));
    }

    [Fact]
    public void ANewerApiIsRefusedAtTheDoorWithBothVersionsNamed()
    {
        Stage("future", "test.future", "GoodPlugin", Everything, api: "9.0");

        var host = Host();
        host.Start();

        var record = Assert.Single(host.Plugins);
        Assert.Equal(PluginState.Incompatible, record.State);
        Assert.Contains("9.0", record.Error);
        Assert.Contains("1.0", record.Error);
    }

    [Fact]
    public void AThrowingPluginIsDisabledWithItsReportAndTheRestKeepRunning()
    {
        Stage("bad", "test.bad", "ThrowingPlugin", []);
        Stage("good", "test.good", "GoodPlugin", Everything);

        var host = Host();
        host.Start();

        var bad = host.Plugins.Single(p => p.Id == "test.bad");
        Assert.Equal(PluginState.Crashed, bad.State);
        Assert.Contains("deliberate failure", bad.Error);

        Assert.Equal(PluginState.Enabled, host.Plugins.Single(p => p.Id == "test.good").State);
    }

    [Fact]
    public void TwoPluginsWithOneIdIsOnePluginAndOneRefusal()
    {
        Stage("first", "test.twin", "GoodPlugin", Everything);
        Stage("second", "test.twin", "GoodPlugin", Everything);

        var host = Host();
        host.Start();

        Assert.Equal(PluginState.Enabled, host.Plugins.Single(p => p.Directory.EndsWith("first")).State);

        var refused = host.Plugins.Single(p => p.Directory.EndsWith("second"));
        Assert.Equal(PluginState.Broken, refused.State);
        Assert.Contains("already", refused.Error);
    }

    [Fact]
    public void TheLoadSwitchOffLoadsNothing()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var settings = SettingsStore.Transient();
        settings.Set(PluginHost.LoadKey, false);

        var host = Host(settings);
        host.Start();

        Assert.Equal(PluginState.Disabled, Assert.Single(host.Plugins).State);
    }

    [Fact]
    public void ADisabledPluginStaysDisabledAcrossAStart()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var settings = SettingsStore.Transient();
        settings.Set(PluginHost.EnabledKey("test.good"), false);

        var host = Host(settings);
        host.Start();

        Assert.Equal(PluginState.Disabled, Assert.Single(host.Plugins).State);
    }

    // ---- Permissions ---------------------------------------------------------------------------

    [Fact]
    public void AnUndeclaredPermissionIsRefusedAndRecorded()
    {
        Stage("greedy", "test.greedy", "GreedyPlugin", permissions: []);

        var host = Host();
        host.Start();

        var record = Assert.Single(host.Plugins);

        // The plugin caught the refusal and carried on: the use is recorded, the plugin is up.
        Assert.Equal(PluginState.Enabled, record.State);
        Assert.Equal(["mail"], record.UndeclaredUses);
    }

    // ---- Contributions -------------------------------------------------------------------------

    [Fact]
    public void ThePluginsTabRidesBothRibbonRenderings()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var host = Host();
        host.Start();

        var layout = host.InjectRibbon(DefaultRibbonLayouts.Mail);

        var tab = layout.FindTab("plugin.test.good.tools");
        Assert.NotNull(tab);
        Assert.Equal("Test Tools", tab!.Label);
        Assert.Equal("Testing", Assert.Single(tab.Groups).Label);

        // The Simplified bar is what a first run shows, so the tab exists there too.
        Assert.True(layout.SimplifiedRows.ContainsKey("plugin.test.good.tools"));

        // And the shipped tabs are untouched — first run parity is protected by what injection
        // appends, never edits.
        Assert.Equal(DefaultRibbonLayouts.Mail.Tabs.Count + 1, layout.Tabs.Count);
    }

    [Fact]
    public void ATabRidesItsOwnModuleAndNoOther()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var host = Host();
        host.Start();

        // The People tab is on People's ribbon and nowhere near Mail's; the Mail tab likewise.
        var people = host.InjectRibbon(DefaultRibbonLayouts.People);
        Assert.NotNull(people.FindTab("plugin.test.good.peopletools"));
        Assert.Null(people.FindTab("plugin.test.good.tools"));

        var mail = host.InjectRibbon(DefaultRibbonLayouts.Mail);
        Assert.NotNull(mail.FindTab("plugin.test.good.tools"));
        Assert.Null(mail.FindTab("plugin.test.good.peopletools"));
    }

    [Fact]
    public void AColumnListsLabelsAndComputesPerRowAndDisablingBlanksIt()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var host = Host();
        host.Start();

        var column = Assert.Single(host.Columns());
        Assert.Equal("plugin.test.good.shout", column.Id);
        Assert.Equal("Shout", column.Label);
        Assert.Equal(140, column.Width);
        Assert.Equal("Shout", host.ColumnLabel(column.Id));

        var row = new Mailbox.Plugins.Api.PluginMessageSummary(
            "a@example.net", 1, 1, "quiet words", "s@example.org", DateTimeOffset.UtcNow, true);
        Assert.Equal("QUIET WORDS", host.ColumnValue(column.Id, row));

        // Disabled means blank, never broken: a saved view naming the id keeps rendering.
        host.Disable("test.good");
        Assert.Empty(host.Columns());
        Assert.Null(host.ColumnLabel(column.Id));
        Assert.Equal(string.Empty, host.ColumnValue(column.Id, row));
    }

    [Fact]
    public void AModuleThatIsNotOneIsRefusedWithTheWordsNamed()
    {
        Stage("sideways", "test.sideways", "SidewaysPlugin", ["ui"]);

        var host = Host();
        host.Start();

        var record = Assert.Single(host.Plugins);
        Assert.Equal(PluginState.Crashed, record.State);
        Assert.Contains("names no module", record.Error);
    }

    [Fact]
    public void DisablingRevokesEverythingAndEnablingBringsItBack()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var catalog = new CommandCatalog();
        var host = Host(catalog: catalog);
        host.Start();

        var id = new CommandId("plugin.test.good.hello");

        host.Disable("test.good");
        Assert.Equal(PluginState.Disabled, Assert.Single(host.Plugins).State);
        Assert.False(catalog.TryGet(id, out _));
        Assert.False(host.TryRun(id));
        Assert.Same(DefaultRibbonLayouts.Mail, host.InjectRibbon(DefaultRibbonLayouts.Mail));

        host.Enable("test.good");
        Assert.Equal(PluginState.Enabled, Assert.Single(host.Plugins).State);
        Assert.True(catalog.TryGet(id, out _));
        Assert.NotNull(host.InjectRibbon(DefaultRibbonLayouts.Mail).FindTab("plugin.test.good.tools"));
    }

    [Fact]
    public void AnInfoBarIsAskedForAndNamesItsPlugin()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var host = Host();
        host.Start();

        var seen = host.InfoBarsFor(Summary("plugin-bar: hello"));
        var (plugin, bar) = Assert.Single(seen);
        Assert.Equal("Test Plugin", plugin);
        Assert.Contains("saw this message", bar.Text);

        Assert.Empty(host.InfoBarsFor(Summary("an ordinary subject")));
    }

    [Fact]
    public void ASendingHookStopsTheMessageAndNamesItself()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var host = Host();
        host.Start();

        var stopped = host.BeforeSend("a@example.net", Mime("plugin-veto: stop me"));
        Assert.NotNull(stopped);
        Assert.Equal("Test Plugin", stopped!.Value.PluginName);
        Assert.Equal("the test said no", stopped.Value.Reason);

        Assert.Null(host.BeforeSend("a@example.net", Mime("an ordinary subject")));
    }

    // ---- The arrival pipeline ------------------------------------------------------------------

    [Fact]
    public void AnArrivalHookMovesDeletesAndSurvivesAMissingFolder()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        using var store = new MailStore(Path.Combine(_root, "mail.db"));
        var mail = new MailRepository(store);
        var account = mail.AddAccount("a@example.net", "A", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var inbox = mail.FolderWithRole(account.Id, FolderRole.Inbox)!;
        var archive = mail.FolderWithRole(account.Id, FolderRole.Archive)!;

        var host = Host(mailboxes: [("a@example.net", mail)]);
        host.Start();

        // Moved to the folder the hook named.
        var moved = Deliver(mail, inbox, "plugin-archive this");
        Assert.Equal(archive.Id, host.Arrivals.Handle(mail, inbox, moved, Mime("plugin-archive this")));
        Assert.Equal(archive.Id, mail.GetMessage(moved)!.FolderId);

        // Deleted, which ends the chain.
        var doomed = Deliver(mail, inbox, "plugin-delete this");
        Assert.Null(host.Arrivals.Handle(mail, inbox, doomed, Mime("plugin-delete this")));

        // A name that is no folder leaves the message where it is rather than growing the tree.
        var lost = Deliver(mail, inbox, "plugin-nowhere this");
        Assert.Equal(inbox.Id, host.Arrivals.Handle(mail, inbox, lost, Mime("plugin-nowhere this")));
        Assert.Equal(inbox.Id, mail.GetMessage(lost)!.FolderId);
    }

    [Fact]
    public void AHookThatThrowsDisablesItsPluginAndLeavesTheMessage()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        using var store = new MailStore(Path.Combine(_root, "mail.db"));
        var mail = new MailRepository(store);
        var account = mail.AddAccount("a@example.net", "A", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var inbox = mail.FolderWithRole(account.Id, FolderRole.Inbox)!;

        var host = Host(mailboxes: [("a@example.net", mail)]);
        host.Start();

        var id = Deliver(mail, inbox, "plugin-crash now");
        Assert.Equal(inbox.Id, host.Arrivals.Handle(mail, inbox, id, Mime("plugin-crash now")));
        Assert.Equal(inbox.Id, mail.GetMessage(id)!.FolderId);

        var record = Assert.Single(host.Plugins);
        Assert.Equal(PluginState.Crashed, record.State);
        Assert.Contains("deliberate hook crash", record.Error);
    }

    // ---- PIM -----------------------------------------------------------------------------------

    [Fact]
    public void ASavedItemGetsRealColumnsThroughTheCodecs()
    {
        Stage("pim", "test.pim", "PimPlugin", ["pim", "pim-write"]);

        using var pimStore = new PimStore(Path.Combine(_root, "pim.db"));
        var pim = new PimRepository(pimStore);
        var calendar = pim.AddCollection(CollectionKind.Events, "Calendar");

        var queued = new List<long>();
        var host = new PluginHost(PluginsRoot, new PluginHostServices
        {
            Commands = new CommandCatalog(),
            Settings = SettingsStore.Transient(),
            Mailboxes = () => [],
            Pim = pim,
            QueuePut = item => queued.Add(item.Id),
        });
        host.Start();

        Assert.Equal(PluginState.Enabled, Assert.Single(host.Plugins).State);

        var item = Assert.Single(pim.Items(calendar.Id));
        Assert.Equal("evt-1", item.Uid);
        Assert.Equal("Planning", item.Summary);
        Assert.NotNull(item.StartsUtc);
        Assert.Equal([item.Id], queued);
    }

    // ---- Unloading -----------------------------------------------------------------------------

    [Fact]
    public void DisablingReallyUnloadsThePluginsCode()
    {
        Stage("good", "test.good", "GoodPlugin", Everything);

        var reference = LoadAndUnload();

        for (var i = 0; i < 10 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // The context going collectible is the claim §13 makes with "without a restart": were a
        // delegate still held anywhere — the catalogue, a pipeline, the actions map — this would
        // stay alive and the assert would say so.
        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference LoadAndUnload()
    {
        var host = Host();
        host.Start();
        host.Disable("test.good");
        return Assert.Single(host.UnloadedContexts);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static long Deliver(MailRepository mail, Folder folder, string subject)
    {
        var id = mail.AddMessage(folder.Id, new MessageSummary(
            0, folder.Id, null, null, "Sender", "s@example.org", subject, "hi",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 100, false, false, false));

        Assert.NotNull(id);
        return id!.Value;
    }

    private static MimeMessage Mime(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "s@example.org"));
        message.To.Add(new MailboxAddress("You", "a@example.net"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "hello" };
        return message;
    }

    private static Mailbox.Plugins.Api.PluginMessageSummary Summary(string subject)
        => new("a@example.net", 1, 1, subject, "Sender", DateTimeOffset.UtcNow, false);
}

/// <summary>§13's "add Quick Steps actions": any catalogue command as a step's action.</summary>
public class QuickStepRunCommandTests
{
    [Fact]
    public void ARunCommandActionSurvivesTheStoreAndSaysItsLabel()
    {
        var settings = Mailbox.Core.Settings.SettingsStore.Transient();
        var steps = new Mailbox.Core.Settings.QuickSteps(settings);

        steps.Upsert(new Mailbox.Core.Settings.QuickStep
        {
            Id = "pluginstep",
            Name = "Plugin Step",
            Actions = [new Mailbox.Core.Settings.QuickStepAction(Mailbox.Core.Settings.QuickStepKind.RunCommand)
            {
                Values = ["plugin.sample.tools.hello", "Say Hello"],
            }],
        });

        // Read back through a fresh instance over the same store: the kind serializes by name,
        // so appending it cannot renumber what older files hold.
        var reread = new Mailbox.Core.Settings.QuickSteps(settings);
        var step = Assert.Single(reread.All, s => s.Id == "pluginstep");
        var action = Assert.Single(step.Actions);

        Assert.Equal(Mailbox.Core.Settings.QuickStepKind.RunCommand, action.Kind);
        Assert.False(action.NeedsSetup);
        Assert.Contains("Say Hello", action.Describe());

        // The step itself does not demand a selection: the command it presses decides.
        Assert.False(step.ToCommand().RequiresSelection);

        // Unchosen is still-to-set-up, which is what First Time Setup keys on.
        Assert.True(new Mailbox.Core.Settings.QuickStepAction(Mailbox.Core.Settings.QuickStepKind.RunCommand).NeedsSetup);
    }
}
