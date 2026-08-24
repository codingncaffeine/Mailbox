using Avalonia.Controls;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// What the All Apps button opens: the installed plugins' commands, grouped by plugin — §20's
/// answer for a button whose reference counterpart reaches a cloud catalogue this project
/// deliberately has none of. Ours lists what is actually installed here, which is the honest
/// reading of the name.
/// </summary>
public static class AllAppsMenu
{
    /// <summary>
    /// Builds the menu. Every entry runs through the caller's own dispatcher, so a command
    /// pressed here is the command pressed anywhere; the last entry opens the Add-ins page.
    /// The contents are logged as the menu is built, a flyout being a surface no capture shows.
    /// </summary>
    public static MenuFlyout Build(Action<CommandId> run, Action manageAddIns)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(manageAddIns);

        var flyout = new MenuFlyout();

        var plugins = App.Plugins.Plugins
            .Where(p => p.State == Mailbox.Plugins.PluginState.Enabled)
            .ToList();

        var any = false;

        foreach (var plugin in plugins)
        {
            var commands = App.Commands.All
                .Where(c => string.Equals(c.OwningPluginId, plugin.Id, StringComparison.Ordinal))
                .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (commands.Count == 0) continue;
            any = true;

            // The plugin's name as a heading rather than a submenu: two plugins is the common
            // most, and a submenu per plugin would put every command two clicks away.
            var heading = new MenuItem { Header = plugin.Name, IsEnabled = false };
            flyout.Items.Add(heading);

            foreach (var command in commands)
            {
                var item = new MenuItem { Header = command.Label };
                ToolTip.SetTip(item, command.Description);
                var id = command.Id;
                item.Click += (_, _) => run(id);
                flyout.Items.Add(item);
                Log.Info($"All Apps: {plugin.Id} — {command.Label} ({command.Id}).");
            }
        }

        if (!any)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = plugins.Count == 0
                    ? "No plugins are installed"
                    : "The installed plugins add no commands",
                IsEnabled = false,
            });
            Log.Info("All Apps: nothing to list.");
        }

        flyout.Items.Add(new Separator());

        var manage = new MenuItem { Header = "Manage Add-ins…" };
        manage.Click += (_, _) => manageAddIns();
        flyout.Items.Add(manage);

        return flyout;
    }
}
