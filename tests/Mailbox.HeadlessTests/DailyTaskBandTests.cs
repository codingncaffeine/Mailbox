using Mailbox.App.Views;
using Mailbox.Core.Settings;
using Mailbox.Store.Pim;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The Daily Task List can be switched on and off over a time grid without taking the window
/// with it.
/// </summary>
/// <remarks>
/// The time grid is one control with two homes — the view host on its own, and the band's dock
/// panel when the band is on — and Avalonia throws when a control that already has a parent is
/// added to another. So every crossing between those two homes was an unhandled exception on the
/// UI thread: switching the band on while a week was showing, switching it off again, and the
/// start-up case where the option says Normal and the workspace has already put the grid in the
/// host. Found by a posed run whose log ended in "already has a visual parent".
/// </remarks>
public class DailyTaskBandTests
{
    private static CalendarWorkspace Workspace(string root, DailyTaskListMode band, CalendarViewKind view)
    {
        var settings = new SettingsStore(Path.Combine(root, "settings.json"));
        settings.Set(CalendarOptions.DefaultViewKey, view.ToString().ToLowerInvariant());

        var workspace = new CalendarWorkspace(
            new PimRepository(PimStore.Transient()),
            new CalendarOptions(settings),
            new DateOnly(2026, 8, 16),
            null)
        {
            // As the shell sets it: an object initializer runs after the constructor, so the
            // grid is already in the host by the time the band is asked for.
            DailyTasks = band,
        };

        return workspace;
    }

    [Theory]
    [InlineData(DailyTaskListMode.Normal, CalendarViewKind.Week)]
    [InlineData(DailyTaskListMode.Minimized, CalendarViewKind.Day)]
    [InlineData(DailyTaskListMode.Off, CalendarViewKind.Week)]
    [InlineData(DailyTaskListMode.Normal, CalendarViewKind.Month)]
    public void TheBandCanBeOnAtStartUp(DailyTaskListMode band, CalendarViewKind view)
    {
        var root = Temp();
        try
        {
            var kind = HeadlessApp.OnUiThread(() => Workspace(root, band, view).Kind);
            Assert.Equal(view, kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// And the reader's own path: the menu, over and over. Each crossing hands the grid from one
    /// home to the other, and the view changing underneath does it again.
    /// </summary>
    [Fact]
    public void TheBandCanBeTurnedOnAndOffAndOnAgain()
    {
        var root = Temp();
        try
        {
            HeadlessApp.OnUiThread(() =>
            {
                var workspace = Workspace(root, DailyTaskListMode.Off, CalendarViewKind.Week);

                workspace.DailyTasks = DailyTaskListMode.Normal;
                workspace.DailyTasks = DailyTaskListMode.Off;
                workspace.DailyTasks = DailyTaskListMode.Minimized;

                // A view the band does not belong under, and back: a month cell has no room for
                // one, so the grid goes home and comes back again.
                workspace.SetView(CalendarViewKind.Month);
                workspace.SetView(CalendarViewKind.Week);
                workspace.DailyTasks = DailyTaskListMode.Off;
                workspace.SetView(CalendarViewKind.Day);

                Assert.Equal(DailyTaskListMode.Off, workspace.DailyTasks);
                return 0;
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Temp()
    {
        var root = Path.Combine(Path.GetTempPath(), "mailbox-band-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }
}
