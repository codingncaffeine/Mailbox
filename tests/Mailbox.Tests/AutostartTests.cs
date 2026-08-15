using Mailbox.Core.Platform;

namespace Mailbox.Tests;

/// <summary>
/// The XDG autostart entry: written and removed under a directory of the test's choosing, and
/// read back for what it says rather than for whether it exists.
/// </summary>
public class AutostartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailbox-autostart-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void OffUntilEnabled()
    {
        var autostart = new Autostart(_dir);
        Assert.False(autostart.IsEnabled);
        Assert.False(autostart.StartsMinimized);
        Assert.False(File.Exists(autostart.EntryPath));
    }

    [Fact]
    public void EnableWritesAnEntryAndDisableRemovesIt()
    {
        var autostart = new Autostart(_dir);

        autostart.Enable(minimized: false, command: "mailbox");
        Assert.True(autostart.IsEnabled);
        Assert.False(autostart.StartsMinimized);

        var text = File.ReadAllText(autostart.EntryPath);
        Assert.Contains("[Desktop Entry]", text);
        Assert.Contains("Type=Application", text);
        Assert.Contains("Exec=mailbox\n", text);
        Assert.DoesNotContain("--minimized", text);

        autostart.Disable();
        Assert.False(autostart.IsEnabled);
        Assert.False(File.Exists(autostart.EntryPath));
    }

    [Fact]
    public void MinimisedGoesOnTheCommandLine()
    {
        var autostart = new Autostart(_dir);
        autostart.Enable(minimized: true, command: "mailbox");

        Assert.True(autostart.StartsMinimized);
        Assert.Contains("Exec=mailbox --minimized", File.ReadAllText(autostart.EntryPath));

        // Enabling again without it takes it off: one entry, replaced, not appended to.
        autostart.Enable(minimized: false, command: "mailbox");
        Assert.False(autostart.StartsMinimized);
    }

    [Fact]
    public void AnEntryTheDesktopSwitchedOffReadsAsOff()
    {
        // GNOME and KDE both disable an entry in place rather than deleting it.
        var autostart = new Autostart(_dir);
        Directory.CreateDirectory(_dir);

        File.WriteAllText(autostart.EntryPath, "[Desktop Entry]\nType=Application\nExec=mailbox\nHidden=true\n");
        Assert.False(autostart.IsEnabled);

        File.WriteAllText(autostart.EntryPath, "[Desktop Entry]\nType=Application\nExec=mailbox\nX-GNOME-Autostart-enabled=false\n");
        Assert.False(autostart.IsEnabled);

        File.WriteAllText(autostart.EntryPath, "[Desktop Entry]\nType=Application\nExec=mailbox\nX-GNOME-Autostart-enabled=true\n");
        Assert.True(autostart.IsEnabled);
    }

    [Fact]
    public void ExecQuotingFollowsTheDesktopEntryRules()
    {
        Assert.Equal("/usr/bin/mailbox", Autostart.QuoteExec("/usr/bin/mailbox"));
        Assert.Equal("\"/home/a person/bin/mailbox\"", Autostart.QuoteExec("/home/a person/bin/mailbox"));
        Assert.Equal("\"/opt/it\\\"s/mailbox\"", Autostart.QuoteExec("/opt/it\"s/mailbox"));
        Assert.Equal("\"/opt/\\$HOME/mailbox\"", Autostart.QuoteExec("/opt/$HOME/mailbox"));
    }

    [Fact]
    public void RenderRefusesAnEmptyCommand()
        => Assert.ThrowsAny<ArgumentException>(() => Autostart.Render(" ", false));
}
