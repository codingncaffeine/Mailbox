using Mailbox.Core.Platform;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

public class DisplaySettingsTests
{
    private static (DisplaySettings Display, Dictionary<string, string> Env) Fresh(SettingsStore? settings = null)
        => (new DisplaySettings(settings ?? SettingsStore.Transient()), new Dictionary<string, string>(StringComparer.Ordinal));

    [Fact]
    public void FreshIsAutomaticAndAppliesNothing()
    {
        var (display, env) = Fresh();
        Assert.Equal(DisplayBackend.Auto, display.Backend);
        Assert.Null(display.Scale);
        Assert.Null(display.ApplyToEnvironment(k => env.GetValueOrDefault(k), (k, v) => env[k] = v));
        Assert.Empty(env);
    }

    [Fact]
    public void APinnedScaleBecomesTheTwoScaleVariables()
    {
        var (display, env) = Fresh();
        display.Scale = 1.25;

        var line = display.ApplyToEnvironment(k => env.GetValueOrDefault(k), (k, v) => env[k] = v);

        Assert.Equal("1.25", env["AVALONIA_GLOBAL_SCALE_FACTOR"]);
        Assert.Equal("mailbox-setting=1", env["AVALONIA_SCREEN_SCALE_FACTORS"]);
        Assert.Contains("scale 1.25", line);
        Assert.Equal(1.25, display.Scale);
    }

    [Fact]
    public void TheEnvironmentAlreadySetIsLeftAlone()
    {
        var (display, env) = Fresh();
        display.Scale = 1.5;
        display.Backend = DisplayBackend.Wayland;
        env["AVALONIA_GLOBAL_SCALE_FACTOR"] = "1";
        env["MAILBOX_WAYLAND"] = "0";

        Assert.Null(display.ApplyToEnvironment(k => env.GetValueOrDefault(k), (k, v) => env[k] = v));
        Assert.Equal("1", env["AVALONIA_GLOBAL_SCALE_FACTOR"]);
        Assert.False(env.ContainsKey("AVALONIA_SCREEN_SCALE_FACTORS"));
        Assert.Equal("0", env["MAILBOX_WAYLAND"]);
    }

    [Fact]
    public void TheWaylandBackendBecomesTheFlagTheHarnessUses()
    {
        var (display, env) = Fresh();
        display.Backend = DisplayBackend.Wayland;

        var line = display.ApplyToEnvironment(k => env.GetValueOrDefault(k), (k, v) => env[k] = v);

        Assert.Equal("1", env["MAILBOX_WAYLAND"]);
        Assert.Contains("Wayland", line);
    }

    [Fact]
    public void TheWordsRoundTripAndAHandEditThatIsNotOneReadsAsAutomatic()
    {
        var settings = SettingsStore.Transient();
        var display = new DisplaySettings(settings);
        display.Backend = DisplayBackend.X11;
        display.Scale = 2;
        Assert.Equal("x11", settings.GetString(DisplaySettings.BackendKey));
        Assert.Equal("2", settings.GetString(DisplaySettings.ScaleKey));

        settings.Set(DisplaySettings.BackendKey, "cocoa");
        settings.Set(DisplaySettings.ScaleKey, "huge");
        Assert.Equal(DisplayBackend.Auto, new DisplaySettings(settings).Backend);
        Assert.Null(new DisplaySettings(settings).Scale);

        settings.Set(DisplaySettings.ScaleKey, "12");
        Assert.Null(new DisplaySettings(settings).Scale);
    }
}
