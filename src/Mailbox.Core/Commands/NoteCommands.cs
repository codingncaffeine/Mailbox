namespace Mailbox.Core.Commands;

/// <summary>
/// The Notes module's commands (§9): making a note, and what the reference's own bar offers
/// beside it.
/// </summary>
/// <remarks>
/// Its own class for the reason the Tasks one is: the ids say which module a command belongs to,
/// the key map picks between two of the same name by which module is open, and Customize Ribbon
/// groups by the same thing. Delete and Categorize appear here as well as on the mail bar because
/// pressing one with a note picked has to act on a note.
/// <para>
/// The reference's Notes bar is short — a module whose items have one field does not need many
/// buttons — and its Current View group offers exactly three arrangements, which are the three
/// this module draws.
/// </para>
/// </remarks>
public static class NoteCommands
{
    // ---- New ---------------------------------------------------------------------------------

    public static readonly MailboxCommand NewNote = new()
    {
        Id = new("notes.new"),
        Label = "New Note",
        Description = "Create a new note.",
        Icon = "new-note",
        Category = "New",

        // Every module, as the reference's creation chords are: Ctrl+Shift+N makes a note from
        // wherever the reader is.
        Scope = ModuleScope.Any,
        KeyTip = "NN",
        DefaultGesture = "Ctrl+Shift+N",

        // Ctrl+N makes the open module's new thing, which here is a note. GestureHome is what
        // says so — see CalendarCommands.NewAppointment for why Scope cannot.
        AlsoGestures = ["Ctrl+N"],
        GestureHome = ModuleScope.Notes,
    };

    public static readonly MailboxCommand NewItems = new()
    {
        Id = new("notes.new.items"),
        Label = "New Items",
        Description = "Create a new item of any type.",
        Icon = "new-items",
        Category = "New",
        Scope = ModuleScope.Notes,
        KeyTip = "NI",
    };

    // ---- The note itself -----------------------------------------------------------------------

    public static readonly MailboxCommand Delete = new()
    {
        Id = new("notes.delete"),
        Label = "Delete",
        Description = "Delete the selected note.",
        Icon = "delete",
        Category = "Delete",
        Scope = ModuleScope.Notes,
        KeyTip = "D",
        DefaultGesture = "Delete",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Open = new()
    {
        Id = new("notes.open"),
        Label = "Open",
        Description = "Open the selected note.",
        Icon = "open-item",
        Category = "Actions",
        Scope = ModuleScope.Notes,
        KeyTip = "O",
        DefaultGesture = "Ctrl+O",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Forward = new()
    {
        Id = new("notes.forward"),
        Label = "Forward",
        Description = "Send the note to somebody as a message.",
        Icon = "forward",
        Category = "Actions",
        Scope = ModuleScope.Notes,
        KeyTip = "FW",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand MoveTo = new()
    {
        Id = new("notes.moveto"),
        Label = "Move",
        Description = "Move the note to another folder.",
        Icon = "move-to",
        Category = "Actions",
        Scope = ModuleScope.Notes,
        KeyTip = "MV",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("notes.categorize"),
        Label = "Categorize",
        Description = "Tag the note with a colour category, which is what colours it.",
        Icon = "categorize",
        Category = "Tags",
        Scope = ModuleScope.Notes,
        KeyTip = "G",
        RequiresSelection = true,
    };

    // ---- The views the Current View group offers ---------------------------------------------

    public static readonly MailboxCommand IconsView = new()
    {
        Id = new("notes.view.icons"),
        Label = "Icon",
        Description = "The notes as squares, newest first.",
        Icon = "notes-icons",
        Category = "Current View",
        Scope = ModuleScope.Notes,
        KeyTip = "VI",
    };

    public static readonly MailboxCommand NotesListView = new()
    {
        Id = new("notes.view.list"),
        Label = "Notes List",
        Description = "One row a note, with what it says beside it.",
        Icon = "note-list",
        Category = "Current View",
        Scope = ModuleScope.Notes,
        KeyTip = "VL",
    };

    public static readonly MailboxCommand LastSevenDaysView = new()
    {
        Id = new("notes.view.week"),
        Label = "Last 7 Days",
        Description = "The same rows, kept to the week just gone.",
        Icon = "last-seven-days",
        Category = "Current View",
        Scope = ModuleScope.Notes,
        KeyTip = "VW",
    };

    /// <summary>Every command this module owns, which is what the catalogue registers.</summary>
    public static IReadOnlyList<MailboxCommand> All { get; } =
    [
        NewNote, NewItems,
        Delete, Open, Forward, MoveTo, Categorize,
        IconsView, NotesListView, LastSevenDaysView,
    ];
}
