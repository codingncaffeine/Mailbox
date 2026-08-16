using Mailbox.Core.Commands;
using Mailbox.Core.Keyboard;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>Chords and the key map: defaults, the reader's changes, conflicts, persistence.</summary>
public class KeyMapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailbox-keys-" + Guid.NewGuid().ToString("n"));

    public KeyMapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (SettingsStore Settings, CommandCatalog Catalog, KeyMap Keys) Fresh()
    {
        var settings = new SettingsStore(Path.Combine(_root, "settings.json"));
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        catalog.RegisterRange(ViewCommands.All);
        return (settings, catalog, new KeyMap(settings, catalog));
    }

    [Theory]
    [InlineData("Ctrl+Shift+R", ChordModifiers.Control | ChordModifiers.Shift, "R", "Ctrl+Shift+R")]
    [InlineData("shift+ctrl+r", ChordModifiers.Control | ChordModifiers.Shift, "R", "Ctrl+Shift+R")]
    [InlineData("Delete", ChordModifiers.None, "Delete", "Delete")]
    [InlineData("Back", ChordModifiers.None, "Back", "Backspace")]
    [InlineData("F9", ChordModifiers.None, "F9", "F9")]
    [InlineData("Ctrl+1", ChordModifiers.Control, "D1", "Ctrl+1")]
    [InlineData("Alt+Delete", ChordModifiers.Alt, "Delete", "Alt+Delete")]
    public void ChordsParseNormaliseAndDisplay(string text, ChordModifiers modifiers, string key, string display)
    {
        var chord = Chord.Parse(text)!;
        Assert.Equal(modifiers, chord.Modifiers);
        Assert.Equal(key, chord.Key);
        Assert.Equal(display, chord.Display);
        Assert.Equal(chord, Chord.Parse(chord.ToString()));
        Assert.Null(Chord.Parse(""));
        Assert.Null(Chord.Parse("Ctrl+"));
    }

    [Fact]
    public void TheDefaultsAreTheCommandsOwn()
    {
        var (_, _, keys) = Fresh();
        Assert.Equal("Ctrl+N", keys.GestureFor(MailCommands.NewEmail.Id)!.ToString());
        Assert.Equal("Delete", keys.GestureFor(MailCommands.Delete.Id)!.ToString());
        Assert.Equal(MailCommands.MarkAsRead.Id, keys.CommandFor(Chord.Parse("Ctrl+Q")!));
        Assert.Equal(MailCommands.SendReceiveAll.Id, keys.CommandFor(Chord.Parse("F9")!));
        Assert.Null(keys.CommandFor(Chord.Parse("Ctrl+Alt+Shift+F12")!));
        Assert.Null(keys.GestureFor(MailCommands.Categorize.Id));
        Assert.False(keys.IsCustomised(MailCommands.NewEmail.Id));
    }

    [Fact]
    public void AssigningTakesTheChordFromWhoeverHadItAndPersists()
    {
        var (settings, catalog, keys) = Fresh();

        // Ctrl+Q moves from Mark as Read to Categorize; Mark as Read is left with nothing.
        var lost = keys.Assign(MailCommands.Categorize.Id, Chord.Parse("Ctrl+Q")!);
        Assert.Equal(MailCommands.MarkAsRead.Id, lost);
        Assert.Equal(MailCommands.Categorize.Id, keys.CommandFor(Chord.Parse("Ctrl+Q")!));
        Assert.Null(keys.GestureFor(MailCommands.MarkAsRead.Id));
        Assert.True(keys.IsCustomised(MailCommands.Categorize.Id));
        Assert.True(keys.IsCustomised(MailCommands.MarkAsRead.Id));

        // Assigning a free chord loses nothing.
        Assert.Null(keys.Assign(MailCommands.Archive.Id, Chord.Parse("Ctrl+Shift+E")!));

        // Written to the settings, read back by a new map over them.
        var again = new KeyMap(new SettingsStore(Path.Combine(_root, "settings.json")), catalog);
        Assert.Equal(MailCommands.Categorize.Id, again.CommandFor(Chord.Parse("Ctrl+Q")!));
        Assert.Null(again.GestureFor(MailCommands.MarkAsRead.Id));
        Assert.Equal("Ctrl+Shift+E", again.GestureFor(MailCommands.Archive.Id)!.ToString());

        // Reset one, then all.
        again.Reset(MailCommands.MarkAsRead.Id);
        Assert.Equal("Ctrl+Q", again.GestureFor(MailCommands.MarkAsRead.Id)!.ToString());
        again.ResetAll();
        Assert.Null(again.GestureFor(MailCommands.Categorize.Id));
        Assert.False(again.IsCustomised(MailCommands.Archive.Id));
        _ = settings;
    }

    [Fact]
    public void RemovingLeavesACommandWithNoKeyAndAssigningTheDefaultBackIsNoOverride()
    {
        var (_, _, keys) = Fresh();
        var raised = 0;
        keys.Changed += (_, _) => raised++;

        keys.Remove(MailCommands.NewEmail.Id);
        Assert.Null(keys.GestureFor(MailCommands.NewEmail.Id));
        Assert.Null(keys.CommandFor(Chord.Parse("Ctrl+N")!));

        keys.Assign(MailCommands.NewEmail.Id, Chord.Parse("Ctrl+N")!);
        Assert.False(keys.IsCustomised(MailCommands.NewEmail.Id));
        Assert.Equal(2, raised);
    }
}
