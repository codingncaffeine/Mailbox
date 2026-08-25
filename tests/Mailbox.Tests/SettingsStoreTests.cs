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
    public void ACorruptFileStartsFromDefaultsAndIsKept()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, "{ this is not json");

        var store = new SettingsStore(Path_);

        Assert.False(store.Has("anything"));

        // Moved aside rather than left where it was. This test used to assert the file was
        // still at its own path, which was true for exactly as long as it took anything to
        // write a setting: the shell writes several while it starts, and each one rewrote the
        // file from an empty object, taking the ribbon customization, the Quick Steps and every
        // Options choice with it. What has to survive is the content, not the path.
        Assert.Equal("{ this is not json", File.ReadAllText(Path_ + ".corrupt"));
    }

    [Fact]
    public void TheKeptCopySurvivesTheNextSettingChange()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, "{ this is not json");

        var store = new SettingsStore(Path_);
        store.Set("view.zoom", 120);

        Assert.Equal("{ this is not json", File.ReadAllText(Path_ + ".corrupt"));
        Assert.Equal(120, new SettingsStore(Path_).GetNumber("view.zoom", 0));
    }

    [Fact]
    public void ASecondUnreadableFileDoesNotOverwriteTheFirstKeptCopy()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, "{ the original");
        _ = new SettingsStore(Path_);

        // A later run that meets a second bad file keeps the first copy, which is the one from
        // before anything went wrong.
        File.WriteAllText(Path_, "{ the second");
        _ = new SettingsStore(Path_);

        Assert.Equal("{ the original", File.ReadAllText(Path_ + ".corrupt"));
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

    /// <summary>A key can be forgotten, after which the caller's default applies again.</summary>
    [Fact]
    public void RemovingAKeyRestoresTheDefault()
    {
        var store = new SettingsStore(Path_);
        store.Set("account.you@example.com.delivery.folder", 12d);
        Assert.True(store.Has("account.you@example.com.delivery.folder"));

        var changed = new List<string>();
        store.Changed += (_, key) => changed.Add(key);
        store.Remove("account.you@example.com.delivery.folder");
        store.Remove("never.written");

        Assert.False(store.Has("account.you@example.com.delivery.folder"));
        Assert.Equal(["account.you@example.com.delivery.folder"], changed);
        Assert.False(new SettingsStore(Path_).Has("account.you@example.com.delivery.folder"));
    }
}
