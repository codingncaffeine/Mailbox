using Mailbox.Core.Commands;
using Mailbox.Core.Keyboard;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// The shortcuts the owner's list names, over the whole catalogue rather than one module's — so
/// the keys that mean different things in different modules are asked in each of them.
/// </summary>
public class ShortcutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailbox-shortcuts-" + Guid.NewGuid().ToString("n"));

    public ShortcutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The catalogue the application itself registers, in that order.</summary>
    private KeyMap Fresh()
    {
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        catalog.RegisterRange(ViewCommands.All);
        catalog.RegisterRange(ComposeCommands.All);
        catalog.RegisterRange(CalendarCommands.All);
        catalog.RegisterRange(AppointmentCommands.All);
        return new KeyMap(new SettingsStore(Path.Combine(_root, "settings.json")), catalog);
    }

    // ---- The owner's list, module by module ------------------------------------------

    /// <summary>
    /// The mail keys of the owner's list, each asked with the mail module open.
    /// </summary>
    [Fact]
    public void TheMailKeysRunTheMailCommands()
    {
        var keys = Fresh();

        (string Chord, CommandId Command)[] wanted =
        [
            ("Ctrl+N", MailCommands.NewEmail.Id),
            ("Ctrl+Shift+M", MailCommands.NewEmail.Id),
            ("Shift+Enter", MailCommands.OpenItem.Id),
            ("Enter", MailCommands.OpenItem.Id),
            ("Delete", MailCommands.Delete.Id),
            ("Shift+Delete", MailCommands.PermanentDelete.Id),
            ("Ctrl+F", MailCommands.Forward.Id),
            ("Ctrl+R", MailCommands.Reply.Id),
            ("Ctrl+Shift+R", MailCommands.ReplyAll.Id),
            ("Ctrl+Q", MailCommands.MarkAsRead.Id),
            ("Ctrl+U", MailCommands.MarkAsUnread.Id),
            ("Insert", MailCommands.ToggleFlag.Id),
            ("Ctrl+OemPeriod", MailCommands.NextMessage.Id),
            ("Ctrl+OemComma", MailCommands.PreviousMessage.Id),
            ("Ctrl+Y", MailCommands.GoToFolder.Id),
            ("Ctrl+E", MailCommands.Search.Id),
            ("Ctrl+1", ViewCommands.GoToMail.Id),
            ("Ctrl+2", ViewCommands.GoToCalendar.Id),
        ];

        foreach (var (chord, command) in wanted)
        {
            Assert.Equal(command, keys.CommandFor(Chord.Parse(chord)!, MailboxModule.Mail));
        }
    }

    /// <summary>The calendar keys of the owner's list, each asked with the calendar open.</summary>
    [Fact]
    public void TheCalendarKeysRunTheCalendarCommands()
    {
        var keys = Fresh();

        (string Chord, CommandId Command)[] wanted =
        [
            ("Ctrl+N", CalendarCommands.NewAppointment.Id),
            ("Ctrl+Shift+A", CalendarCommands.NewAppointment.Id),
            ("Ctrl+Shift+Q", CalendarCommands.NewMeeting.Id),
            ("Enter", CalendarCommands.OpenItem.Id),
            ("Delete", CalendarCommands.DeleteItem.Id),
            ("Ctrl+Alt+1", CalendarCommands.DayView.Id),
            ("Ctrl+Alt+2", CalendarCommands.WorkWeekView.Id),
            ("Ctrl+Alt+3", CalendarCommands.WeekView.Id),
            ("Ctrl+Alt+4", CalendarCommands.MonthView.Id),
            ("Alt+-", CalendarCommands.WeekView.Id),
            ("Alt+=", CalendarCommands.MonthView.Id),
            ("Ctrl+Alt+Left", CalendarCommands.Back.Id),
            ("Alt+Down", CalendarCommands.Back.Id),
            ("Alt+PageDown", CalendarCommands.Back.Id),
            ("Ctrl+Alt+Right", CalendarCommands.Forward.Id),
            ("Alt+Up", CalendarCommands.Forward.Id),
            ("Alt+PageUp", CalendarCommands.Forward.Id),
            ("Alt+Home", CalendarCommands.Today.Id),
            ("Alt+Shift+Y", CalendarCommands.Today.Id),
            ("Ctrl+G", CalendarCommands.GoToDate.Id),
        ];

        foreach (var (chord, command) in wanted)
        {
            Assert.Equal(command, keys.CommandFor(Chord.Parse(chord)!, MailboxModule.Calendar));
        }
    }

    /// <summary>
    /// Ctrl+N makes the open module's new thing, and Delete throws away the open module's item —
    /// the same key, a different command, decided by which module is in front of the reader.
    /// </summary>
    [Fact]
    public void OneKeyMeansTheOpenModulesThing()
    {
        var keys = Fresh();

        Assert.Equal(MailCommands.NewEmail.Id, keys.CommandFor(Chord.Parse("Ctrl+N")!, MailboxModule.Mail));
        Assert.Equal(CalendarCommands.NewAppointment.Id, keys.CommandFor(Chord.Parse("Ctrl+N")!, MailboxModule.Calendar));

        Assert.Equal(MailCommands.Delete.Id, keys.CommandFor(Chord.Parse("Delete")!, MailboxModule.Mail));
        Assert.Equal(CalendarCommands.DeleteItem.Id, keys.CommandFor(Chord.Parse("Delete")!, MailboxModule.Calendar));

        Assert.Equal(MailCommands.OpenItem.Id, keys.CommandFor(Chord.Parse("Enter")!, MailboxModule.Mail));
        Assert.Equal(CalendarCommands.OpenItem.Id, keys.CommandFor(Chord.Parse("Enter")!, MailboxModule.Calendar));
    }

    /// <summary>
    /// A command scoped to one module keeps out of another's way — Ctrl+R reaches for a message
    /// that the calendar has not got — while the creation chords still work from anywhere.
    /// </summary>
    [Fact]
    public void AModulesOwnKeysDoNotReachIntoAnother()
    {
        var keys = Fresh();

        Assert.Null(keys.CommandFor(Chord.Parse("Ctrl+R")!, MailboxModule.Calendar));
        Assert.Null(keys.CommandFor(Chord.Parse("Ctrl+Alt+1")!, MailboxModule.Mail));

        // Held by no module in particular, so it runs wherever it is pressed.
        Assert.Equal(MailCommands.SendReceiveAll.Id, keys.CommandFor(Chord.Parse("F9")!, MailboxModule.Calendar));
        Assert.Equal(MailCommands.Search.Id, keys.CommandFor(Chord.Parse("Ctrl+E")!, MailboxModule.Calendar));

        // Held by the calendar alone and by nothing the mail module has: it books an appointment
        // from either, as the reference's creation chords do.
        Assert.Equal(CalendarCommands.NewAppointment.Id, keys.CommandFor(Chord.Parse("Ctrl+Shift+A")!, MailboxModule.Mail));
        Assert.Equal(CalendarCommands.NewMeeting.Id, keys.CommandFor(Chord.Parse("Ctrl+Shift+Q")!, MailboxModule.Mail));
    }

    /// <summary>
    /// A window of its own answers for its own commands: Ctrl+U underlines in a compose window
    /// and marks a message unread in the shell, and neither can reach the other.
    /// </summary>
    [Fact]
    public void AWindowOfItsOwnKeepsItsOwnKeys()
    {
        var keys = Fresh();

        (string Chord, CommandId Command)[] compose =
        [
            ("Ctrl+B", ComposeCommands.Bold.Id),
            ("Ctrl+I", ComposeCommands.Italic.Id),
            ("Ctrl+U", ComposeCommands.Underline.Id),
            ("Ctrl+K", ComposeCommands.Link.Id),
            ("Ctrl+A", ComposeCommands.SelectAll.Id),
            ("Alt+K", ComposeCommands.CheckNames.Id),
            ("Ctrl+Enter", ComposeCommands.Send.Id),
            ("Ctrl+S", ComposeCommands.SaveDraft.Id),
            ("F7", ComposeCommands.Spelling.Id),
        ];

        foreach (var (chord, command) in compose)
        {
            Assert.Equal(command, keys.CommandFor(Chord.Parse(chord)!, CommandSurface.Compose));
        }

        Assert.Equal(AppointmentCommands.SaveAndClose.Id, keys.CommandFor(Chord.Parse("Alt+S")!, CommandSurface.Appointment));
        Assert.Equal(AppointmentCommands.Send.Id, keys.CommandFor(Chord.Parse("Ctrl+Enter")!, CommandSurface.Appointment));

        // The shell cannot run either window's commands, whatever the hash order of the catalogue.
        Assert.Equal(MailCommands.MarkAsUnread.Id, keys.CommandFor(Chord.Parse("Ctrl+U")!, MailboxModule.Mail));
        Assert.Null(keys.CommandFor(Chord.Parse("Ctrl+B")!, MailboxModule.Mail));
        Assert.Null(keys.CommandFor(Chord.Parse("Alt+S")!, MailboxModule.Calendar));
    }

    /// <summary>Asking without a module is the shortcut editor's question: every chord, any module.</summary>
    [Fact]
    public void WithoutAModuleEveryChordStillAnswers()
    {
        var keys = Fresh();
        Assert.Equal(MailCommands.NewEmail.Id, keys.CommandFor(Chord.Parse("Ctrl+N")!));
        Assert.Equal(CalendarCommands.DayView.Id, keys.CommandFor(Chord.Parse("Ctrl+Alt+1")!));
        Assert.Equal(MailCommands.Reply.Id, keys.CommandFor(Chord.Parse("Ctrl+R")!));
    }

    /// <summary>"?" opens the list of shortcuts, shifted as a US keyboard sends it or not.</summary>
    [Fact]
    public void TheQuestionMarkOpensTheShortcutList()
    {
        var keys = Fresh();
        Assert.Equal(ViewCommands.KeyboardShortcuts.Id, keys.CommandFor(Chord.Parse("Shift+OemQuestion")!, MailboxModule.Mail));
        Assert.Equal(ViewCommands.KeyboardShortcuts.Id, keys.CommandFor(Chord.Parse("OemQuestion")!, MailboxModule.Mail));
        Assert.Equal("Shift+?", keys.GestureFor(ViewCommands.KeyboardShortcuts.Id)!.Display);
    }

    /// <summary>The punctuation keys read back as the names the windowing layer gives them.</summary>
    [Theory]
    [InlineData("Ctrl+.", "Ctrl+OemPeriod", "Ctrl+.")]
    [InlineData("Ctrl+,", "Ctrl+OemComma", "Ctrl+,")]
    [InlineData("Alt+-", "Alt+OemMinus", "Alt+-")]
    [InlineData("Alt+=", "Alt+OemPlus", "Alt+=")]
    [InlineData("?", "OemQuestion", "?")]
    public void PunctuationChordsParseAsWritten(string text, string canonical, string display)
    {
        var chord = Chord.Parse(text)!;
        Assert.Equal(canonical, chord.ToString());
        Assert.Equal(display, chord.Display);
    }

    /// <summary>
    /// The owner's list writes the message steps as Ctrl+&gt; and Ctrl+&lt;, which is the same key
    /// with Shift held — so both spellings run them.
    /// </summary>
    [Fact]
    public void TheShiftedMessageStepsRunToo()
    {
        var keys = Fresh();
        Assert.Equal(MailCommands.NextMessage.Id, keys.CommandFor(Chord.Parse("Ctrl+Shift+OemPeriod")!, MailboxModule.Mail));
        Assert.Equal(MailCommands.PreviousMessage.Id, keys.CommandFor(Chord.Parse("Ctrl+Shift+OemComma")!, MailboxModule.Mail));
    }

    /// <summary>
    /// Every shipped shortcut reaches the command it was given to, asked where that command
    /// lives.
    /// </summary>
    /// <remarks>
    /// The catalogue is a dictionary, so two commands holding one chord are resolved by a hash
    /// order that nobody chose and that changes with the next command added. This is the sweep
    /// that catches such a pair while it is still a decision rather than a bug report.
    /// </remarks>
    [Fact]
    public void EveryShippedShortcutReachesItsOwnCommand()
    {
        var keys = Fresh();
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        catalog.RegisterRange(ViewCommands.All);
        catalog.RegisterRange(ComposeCommands.All);
        catalog.RegisterRange(CalendarCommands.All);
        catalog.RegisterRange(AppointmentCommands.All);

        var clashes = new List<string>();
        foreach (var command in catalog.All)
        {
            if (Chord.Parse(command.DefaultGesture) is not { } chord) continue;

            // Asked where the command lives: a window of its own for the two that have one, and
            // otherwise the module the command is scoped to.
            var winner = command.Surface != CommandSurface.Shell
                ? keys.CommandFor(chord, command.Surface)
                : keys.CommandFor(chord, command.Scope == ModuleScope.Calendar ? MailboxModule.Calendar : MailboxModule.Mail);

            if (winner != command.Id) clashes.Add($"{command.Id} ({chord}) is answered by {winner}");
        }

        Assert.Empty(clashes);
    }
}
