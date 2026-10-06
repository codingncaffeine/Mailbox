namespace Mailbox.HeadlessTests;

/// <summary>
/// The suite brings up the application's startup, which opens whatever profile the XDG
/// directories name. These hold the line that it is never a real one.
/// </summary>
public class TestProfileTests
{
    [Fact]
    public void EveryDirectoryTheApplicationReadsIsThisRunsOwn()
    {
        foreach (var variable in new[] { "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "MAILBOX_STORE" })
        {
            Assert.StartsWith(TestProfile.Root, Environment.GetEnvironmentVariable(variable) ?? string.Empty);
        }
    }

    /// <summary>
    /// Through the application's own resolution, after startup has run: the stores it opens are
    /// under this run's profile, not a home directory's.
    /// </summary>
    [Fact]
    public void TheStoresStartupOpensAreUnderThisRunsProfile()
    {
        HeadlessApp.OnUiThread(() => { });

        Assert.StartsWith(TestProfile.Root, Mailbox.Store.Pim.PimStore.DefaultPath());
        Assert.StartsWith(TestProfile.Root, Mailbox.Store.AccountStores.DefaultDirectory());
    }
}
