namespace Mailbox.Core.Commands;

/// <summary>
/// The Journal module's commands (§9): recording something that took time, and moving the
/// timeline through it.
/// </summary>
/// <remarks>
/// The reference hides this module behind Ctrl+8 and has long since stopped developing it, so
/// its bar is the shortest of the six: make an entry, delete one, and choose which of the four
/// arrangements to look at them in. The timeline's own three scales are commands rather than a
/// control on the view, so a reader who has bound a key to Week gets a week in this module too.
/// </remarks>
public static class JournalCommands
{
    public static readonly MailboxCommand NewEntry = new()
    {
        Id = new("journal.new"),
        Label = "Journal Entry",
        Description = "Record something that happened.",
        Icon = "journal-entry",
        Category = "New",

        // Every module, as the reference's creation chords are: Ctrl+Shift+J records one from
        // wherever the reader is.
        Scope = ModuleScope.Any,
        KeyTip = "NJ",
        DefaultGesture = "Ctrl+Shift+J",

        // Ctrl+N makes the open module's new thing, which here is an entry.
        AlsoGestures = ["Ctrl+N"],
    };

    public static readonly MailboxCommand NewItems = new()
    {
        Id = new("journal.new.items"),
        Label = "New Items",
        Description = "Create a new item of any type.",
        Icon = "new-items",
        Category = "New",
        Scope = ModuleScope.Journal,
        KeyTip = "NI",
    };

    public static readonly MailboxCommand Delete = new()
    {
        Id = new("journal.delete"),
        Label = "Delete",
        Description = "Delete the selected entry.",
        Icon = "delete",
        Category = "Delete",
        Scope = ModuleScope.Journal,
        KeyTip = "D",
        DefaultGesture = "Delete",
    };

    public static readonly MailboxCommand Open = new()
    {
        Id = new("journal.open"),
        Label = "Open",
        Description = "Open the selected entry.",
        Icon = "open-item",
        Category = "Actions",
        Scope = ModuleScope.Journal,
        KeyTip = "O",
        DefaultGesture = "Ctrl+O",
    };

    public static readonly MailboxCommand Forward = new()
    {
        Id = new("journal.forward"),
        Label = "Forward",
        Description = "Send the entry to somebody as a message.",
        Icon = "forward",
        Category = "Actions",
        Scope = ModuleScope.Journal,
        KeyTip = "FW",
    };

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("journal.categorize"),
        Label = "Categorize",
        Description = "Tag the entry with a colour category.",
        Icon = "categorize",
        Category = "Tags",
        Scope = ModuleScope.Journal,
        KeyTip = "G",
    };

    // ---- Moving through time -------------------------------------------------------------------

    public static readonly MailboxCommand Today = new()
    {
        Id = new("journal.today"),
        Label = "Today",
        Description = "Bring the timeline back to today.",
        Icon = "today",
        Category = "Go To",
        Scope = ModuleScope.Journal,
        KeyTip = "T",
    };

    public static readonly MailboxCommand Back = new()
    {
        Id = new("journal.back"),
        Label = "Back",
        Description = "The span before this one.",
        Icon = "chevron-left",
        Category = "Go To",
        Scope = ModuleScope.Journal,
        KeyTip = "BK",
    };

    public static readonly MailboxCommand Forwards = new()
    {
        Id = new("journal.next"),
        Label = "Forward",
        Description = "The span after this one.",
        Icon = "chevron-right",
        Category = "Go To",
        Scope = ModuleScope.Journal,
        KeyTip = "FD",
    };

    public static readonly MailboxCommand DayScale = new()
    {
        Id = new("journal.scale.day"),
        Label = "Day",
        Description = "A day of the timeline, hour by hour.",
        Icon = "day-view",
        Category = "Arrangement",
        Scope = ModuleScope.Journal,
        KeyTip = "SD",
    };

    public static readonly MailboxCommand WeekScale = new()
    {
        Id = new("journal.scale.week"),
        Label = "Week",
        Description = "A week of the timeline.",
        Icon = "week-view",
        Category = "Arrangement",
        Scope = ModuleScope.Journal,
        KeyTip = "SW",
    };

    public static readonly MailboxCommand MonthScale = new()
    {
        Id = new("journal.scale.month"),
        Label = "Month",
        Description = "A month of the timeline.",
        Icon = "month-view",
        Category = "Arrangement",
        Scope = ModuleScope.Journal,
        KeyTip = "SM",
    };

    // ---- The views the Current View group offers ---------------------------------------------

    public static readonly MailboxCommand TimelineView = new()
    {
        Id = new("journal.view.timeline"),
        Label = "Timeline",
        Description = "The entries hung under when they happened.",
        Icon = "journal-timeline",
        Category = "Current View",
        Scope = ModuleScope.Journal,
        KeyTip = "VT",
    };

    public static readonly MailboxCommand EntryListView = new()
    {
        Id = new("journal.view.entries"),
        Label = "Entry List",
        Description = "One row an entry, grouped by what kind of thing it was.",
        Icon = "note-list",
        Category = "Current View",
        Scope = ModuleScope.Journal,
        KeyTip = "VE",
    };

    public static readonly MailboxCommand PhoneCallsView = new()
    {
        Id = new("journal.view.calls"),
        Label = "Phone Calls",
        Description = "The calls alone.",
        Icon = "phone",
        Category = "Current View",
        Scope = ModuleScope.Journal,
        KeyTip = "VP",
    };

    public static readonly MailboxCommand LastSevenDaysView = new()
    {
        Id = new("journal.view.week"),
        Label = "Last 7 Days",
        Description = "The same rows, kept to the week just gone.",
        Icon = "last-seven-days",
        Category = "Current View",
        Scope = ModuleScope.Journal,
        KeyTip = "VW",
    };

    /// <summary>Every command this module owns, which is what the catalogue registers.</summary>
    public static IReadOnlyList<MailboxCommand> All { get; } =
    [
        NewEntry, NewItems,
        Delete, Open, Forward, Categorize,
        Today, Back, Forwards,
        DayScale, WeekScale, MonthScale,
        TimelineView, EntryListView, PhoneCallsView, LastSevenDaysView,
    ];
}
