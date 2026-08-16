using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

public class RibbonDisplaySettingsTests
{
    [Fact]
    public void OpensSimplifiedAndShownWhenNothingIsStored()
    {
        var display = new RibbonDisplaySettings(SettingsStore.Transient());

        var shell = display.Get(RibbonWindow.Shell);
        Assert.Equal(RibbonDisplayMode.Simplified, shell.Layout);
        Assert.False(shell.TabsOnly);
        Assert.Equal(RibbonDisplayMode.Simplified, shell.Mode);
    }

    [Fact]
    public void RemembersClassic()
    {
        var settings = SettingsStore.Transient();
        new RibbonDisplaySettings(settings).Set(RibbonWindow.Shell, RibbonDisplayMode.Classic, tabsOnly: false);

        var back = new RibbonDisplaySettings(settings).Get(RibbonWindow.Shell);
        Assert.Equal(RibbonDisplayMode.Classic, back.Layout);
        Assert.Equal(RibbonDisplayMode.Classic, back.Mode);
        Assert.Equal("classic", settings.GetString(RibbonDisplaySettings.ShellLayoutKey));
        Assert.Equal("always", settings.GetString(RibbonDisplaySettings.ShellShowKey));
    }

    [Fact]
    public void ACollapsedRibbonComesBackInTheLayoutItWasCollapsedFrom()
    {
        var settings = SettingsStore.Transient();
        new RibbonDisplaySettings(settings).Set(RibbonWindow.Shell, RibbonDisplayMode.Classic, tabsOnly: true);

        var back = new RibbonDisplaySettings(settings).Get(RibbonWindow.Shell);
        Assert.Equal(RibbonDisplayMode.Collapsed, back.Mode);
        Assert.Equal(RibbonDisplayMode.Classic, back.Layout);
        Assert.True(back.TabsOnly);
    }

    [Fact]
    public void CollapsedIsNotALayout()
    {
        var display = new RibbonDisplaySettings(SettingsStore.Transient());
        Assert.Throws<ArgumentException>(
            () => display.Set(RibbonWindow.Shell, RibbonDisplayMode.Collapsed, tabsOnly: true));
    }

    [Fact]
    public void TheShellAndAMessageWindowAreRememberedApart()
    {
        var settings = SettingsStore.Transient();
        var display = new RibbonDisplaySettings(settings);

        display.Set(RibbonWindow.Shell, RibbonDisplayMode.Classic, tabsOnly: false);

        Assert.Equal(RibbonDisplayMode.Classic, display.Get(RibbonWindow.Shell).Layout);
        Assert.Equal(RibbonDisplayMode.Simplified, display.Get(RibbonWindow.Compose).Layout);

        display.Set(RibbonWindow.Compose, RibbonDisplayMode.Simplified, tabsOnly: true);

        Assert.False(display.Get(RibbonWindow.Shell).TabsOnly);
        Assert.True(display.Get(RibbonWindow.Compose).TabsOnly);
    }

    [Fact]
    public void AHandEditedValueThatIsNeitherWordReadsAsTheDefault()
    {
        var settings = SettingsStore.Transient();
        settings.Set(RibbonDisplaySettings.ShellLayoutKey, "tall");
        settings.Set(RibbonDisplaySettings.ShellShowKey, "sometimes");

        var back = new RibbonDisplaySettings(settings).Get(RibbonWindow.Shell);
        Assert.Equal(RibbonDisplayMode.Simplified, back.Layout);
        Assert.False(back.TabsOnly);
    }

    [Fact]
    public void TheWordsAreReadWithoutRegardToCase()
    {
        var settings = SettingsStore.Transient();
        settings.Set(RibbonDisplaySettings.ShellLayoutKey, "Classic");
        settings.Set(RibbonDisplaySettings.ShellShowKey, "TABS");

        var back = new RibbonDisplaySettings(settings).Get(RibbonWindow.Shell);
        Assert.Equal(RibbonDisplayMode.Classic, back.Layout);
        Assert.True(back.TabsOnly);
    }
}
