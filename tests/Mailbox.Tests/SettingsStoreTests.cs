using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// Preferences are a file, not a database: a hundred-odd small values read once at startup and
/// written on change. What has to hold is that a value survives a restart, that a half-written
/// file cannot replace a good one, and that a settings file from a newer build is not quietly
/// stripped of everything the older build does not recognise.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mailbox-tests", Guid.NewGuid().ToString("n"));

    private string Path_ => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ValuesSurviveAReopen()
    {
        var first = new SettingsStore(Path_);
        first.Set("mail.spellcheck", true);
        first.Set("mail.signature", "Regards");
        first.Set("mail.autosave", 3d);

        var second = new SettingsStore(Path_);

        Assert.True(second.GetBool("mail.spellcheck"));
        Assert.Equal("Regards", second.GetString("mail.signature"));
        Assert.Equal(3d, second.GetNumber("mail.autosave"));
    }

    [Fact]
    public void UnsetKeysFallBackToTheCallersDefault()
    {
        var store = new SettingsStore(Path_);

        Assert.True(store.GetBool("never.written", fallback: true));
        Assert.Equal("fallback", store.GetString("never.written", "fallback"));
        Assert.Equal(7d, store.GetNumber("never.written", 7));
        Assert.False(store.Has("never.written"));
    }

    /// <summary>
    /// A setting written by a newer build must still be there after an older build has opened
    /// the file and written something of its own, or downgrading silently resets preferences.
    /// </summary>
    [Fact]
    public void KeysItDoesNotRecogniseAreKept()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, """{ "from.a.newer.build": "keep me" }""");

        var store = new SettingsStore(Path_);
        store.Set("something.else", true);

        Assert.Equal("keep me", new SettingsStore(Path_).GetString("from.a.newer.build"));
    }

    [Fact]
    public void ACorruptFileStartsFromDefaultsAndIsNotDestroyed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, "{ this is not json");

        var store = new SettingsStore(Path_);

        Assert.False(store.Has("anything"));
        Assert.Equal("{ this is not json", File.ReadAllText(Path_));
    }

    [Fact]
    public void ChangingAValueRaisesChangedWithItsKey()
    {
        var store = new SettingsStore(Path_);
        var seen = new List<string>();
        store.Changed += (_, key) => seen.Add(key);

        store.Set("a", true);
        store.Set("a", true);       // same value, no event
        store.Set("a", false);

        Assert.Equal(["a", "a"], seen);
    }

    [Fact]
    public void ATransientStoreWritesNothingToDisk()
    {
        var store = SettingsStore.Transient();
        store.Set("a", true);

        Assert.True(store.GetBool("a"));
        Assert.False(File.Exists(Path_));
    }
}
