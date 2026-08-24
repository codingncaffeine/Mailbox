using Mailbox.Plugins.Api;

namespace Mailbox.TestPlugin;

/// <summary>
/// A well-behaved plugin exercising every surface: a command, a tab, both pipeline hooks, an
/// info bar. The hooks key off magic words in the subject so a test states its intent in the
/// message it builds.
/// </summary>
public sealed class GoodPlugin : IPlugin, IDisposable
{
    public void Initialize(IPluginHost host)
    {
        host.Log("initialized");

        host.Commands.Register(
            new PluginCommand
            {
                Name = "hello",
                Label = "Say Hello",
                Description = "Writes a setting the test reads back.",
            },
            () => host.Settings.Set("pressed", true));

        host.Commands.AddRibbonTab(new PluginRibbonTab
        {
            Name = "tools",
            Label = "Test Tools",
            Groups =
            [
                new PluginRibbonGroup { Label = "Testing", Commands = ["hello"] },
            ],
        });

        // A second tab on another module, so the host's per-module filtering has something to
        // filter: this one rides People and must never appear on Mail.
        host.Commands.Register(
            new PluginCommand
            {
                Name = "wave",
                Label = "Wave",
                Description = "Waves at the selected person.",
            },
            () => host.Log("waved"));

        host.Commands.AddRibbonTab(new PluginRibbonTab
        {
            Name = "peopletools",
            Label = "People Tools",
            Module = "people",
            Groups =
            [
                new PluginRibbonGroup { Label = "Waving", Commands = ["wave"] },
            ],
        });

        host.Pipeline.OnArrival(arriving =>
        {
            if (arriving.Subject.Contains("plugin-crash")) throw new InvalidOperationException("deliberate hook crash");
            if (arriving.Subject.Contains("plugin-delete")) return ArrivalAction.Delete;
            if (arriving.Subject.Contains("plugin-archive")) return ArrivalAction.MoveTo("Archive");
            if (arriving.Subject.Contains("plugin-nowhere")) return ArrivalAction.MoveTo("No Such Folder");
            return ArrivalAction.None;
        });

        host.Pipeline.OnSending(outgoing =>
            outgoing.Subject.Contains("plugin-veto")
                ? SendDecision.Stop("the test said no")
                : SendDecision.Allow);

        host.ReadingPane.AddInfoBar(message =>
            message.Subject.Contains("plugin-bar")
                ? new PluginInfoBar("The test plugin saw this message.")
                : null);

        // A column, so the host's column plumbing has something to draw: the subject shouted,
        // which a test can predict from the row it staged.
        host.Columns.Add(
            new PluginColumn { Name = "shout", Label = "Shout", Width = 140 },
            row => row.Subject.ToUpperInvariant());
    }

    public void Dispose()
    {
        // Nothing of our own to stop; present so the host's dispose-on-disable path runs.
    }
}

/// <summary>Throws from Initialize, for the crashed-with-a-report path.</summary>
public sealed class ThrowingPlugin : IPlugin
{
    public void Initialize(IPluginHost host)
        => throw new InvalidOperationException("deliberate failure");
}

/// <summary>Names a module that is not one, for the refused-at-registration path.</summary>
public sealed class SidewaysPlugin : IPlugin
{
    public void Initialize(IPluginHost host)
        => host.Commands.AddRibbonTab(new PluginRibbonTab
        {
            Name = "nowhere",
            Label = "Nowhere",
            Module = "ribbon",
            Groups = [],
        });
}

/// <summary>Writes one appointment through the PIM surface, so a test can read the columns back.</summary>
public sealed class PimPlugin : IPlugin
{
    private const string Vevent = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Mailbox tests//EN
        BEGIN:VEVENT
        UID:evt-1
        DTSTART:20260901T090000Z
        DTEND:20260901T100000Z
        SUMMARY:Planning
        END:VEVENT
        END:VCALENDAR
        """;

    public void Initialize(IPluginHost host)
    {
        var calendar = host.Pim.Collections().First(c => c.Kind == "calendar");
        host.Pim.Save(calendar.Id, "evt-1", Vevent);
    }
}

/// <summary>
/// Declares no permissions and reaches for mail anyway, then survives the refusal — the
/// undeclared use is recorded while the plugin stays up, which is the pair the test wants.
/// </summary>
public sealed class GreedyPlugin : IPlugin
{
    public void Initialize(IPluginHost host)
    {
        try
        {
            _ = host.Mail.Accounts();
            host.Log("mail was handed over, which is wrong");
        }
        catch (PluginPermissionException refused)
        {
            host.Log($"refused: {refused.Permission}");
        }
    }
}
