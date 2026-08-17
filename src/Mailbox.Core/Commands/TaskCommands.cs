namespace Mailbox.Core.Commands;

/// <summary>
/// The Tasks module's commands (§9): making a task, marking one done, and what the reference's
/// own bar offers beside those.
/// </summary>
/// <remarks>
/// Its own class for the reason the calendar's and People's are: the ids say which module a
/// command belongs to, the key map picks between two of the same name by which module is open,
/// and Customize Ribbon groups by the same thing. Delete, Categorize, Follow Up and Private
/// appear here as well as on the mail bar because pressing them with a task picked has to act on
/// a task, and a shared id would reach the mail module's handler.
/// <para>
/// Reply, Reply All and Forward are on the reference's Tasks bar because the To-Do List holds
/// flagged mail as well as tasks — which is a Phase 14 join here, so they are declared and say so.
/// </para>
/// </remarks>
public static class TaskCommands
{
    // ---- New ---------------------------------------------------------------------------------

    public static readonly MailboxCommand NewTask = new()
    {
        Id = new("tasks.new"),
        Label = "New Task",
        Description = "Create a new task.",
        Icon = "new-task",
        Category = "New",

        // Every module, as the reference's creation chords are: Ctrl+Shift+K makes a task from
        // wherever the reader is.
        Scope = ModuleScope.Any,
        KeyTip = "NT",
        DefaultGesture = "Ctrl+Shift+K",

        // Ctrl+N makes the open module's new thing, which here is a task.
        AlsoGestures = ["Ctrl+N"],
    };

    public static readonly MailboxCommand NewEmail = new()
    {
        Id = new("tasks.new.email"),
        Label = "New Email",
        Description = "Create a message.",
        Icon = "mail-new",
        Category = "New",
        Scope = ModuleScope.Tasks,
        KeyTip = "NE",
    };

    public static readonly MailboxCommand NewItems = new()
    {
        Id = new("tasks.new.items"),
        Label = "New Items",
        Description = "Create a new item of any type.",
        Icon = "new-items",
        Category = "New",
        Scope = ModuleScope.Tasks,
        KeyTip = "NI",
    };

    // ---- The task itself ---------------------------------------------------------------------

    public static readonly MailboxCommand Delete = new()
    {
        Id = new("tasks.delete"),
        Label = "Delete",
        Description = "Delete the selected task.",
        Icon = "delete",
        Category = "Delete",
        Scope = ModuleScope.Tasks,
        KeyTip = "D",
        DefaultGesture = "Delete",
    };

    public static readonly MailboxCommand Open = new()
    {
        Id = new("tasks.open"),
        Label = "Open",
        Description = "Open the selected task.",
        Icon = "open-item",
        Category = "Actions",
        Scope = ModuleScope.Tasks,
        KeyTip = "O",
        DefaultGesture = "Ctrl+O",
    };

    public static readonly MailboxCommand MarkComplete = new()
    {
        Id = new("tasks.complete"),
        Label = "Mark Complete",
        Description = "Mark the selected task as finished.",
        Icon = "mark-complete",
        Category = "Manage Task",
        Scope = ModuleScope.Tasks,
        KeyTip = "MC",
    };

    public static readonly MailboxCommand RemoveFromList = new()
    {
        Id = new("tasks.remove"),
        Label = "Remove from List",
        Description = "Take it off the to-do list without deleting it.",
        Icon = "remove-from-list",
        Category = "Manage Task",
        Scope = ModuleScope.Tasks,
        KeyTip = "RL",
    };

    public static readonly MailboxCommand FollowUp = new()
    {
        Id = new("tasks.followup"),
        Label = "Flag Task",
        Description = "Set or clear the follow-up flag and its due date.",
        Icon = "follow-up",
        Category = "Tags",
        Scope = ModuleScope.Tasks,
        KeyTip = "U",
    };

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("tasks.categorize"),
        Label = "Categorize",
        Description = "Tag the task with a colour category.",
        Icon = "categorize",
        Category = "Tags",
        Scope = ModuleScope.Tasks,
        KeyTip = "G",
    };

    public static readonly MailboxCommand Private = new()
    {
        Id = new("tasks.private"),
        Label = "Private",
        Description = "Keep the task's details to yourself when the list is shared.",
        Icon = "private",
        Category = "Tags",
        Scope = ModuleScope.Tasks,
        KeyTip = "PV",
    };

    public static readonly MailboxCommand HighImportance = new()
    {
        Id = new("tasks.importance.high"),
        Label = "High Importance",
        Description = "Mark the task as urgent.",
        Icon = "importance",
        IconTint = "status.danger",
        Category = "Tags",
        Scope = ModuleScope.Tasks,
        KeyTip = "IH",
    };

    public static readonly MailboxCommand LowImportance = new()
    {
        Id = new("tasks.importance.low"),
        Label = "Low Importance",
        Description = "Mark the task as low priority.",
        Icon = "importance-low",
        IconTint = "ribbon.icon.blue",
        Category = "Tags",
        Scope = ModuleScope.Tasks,
        KeyTip = "IL",
    };

    // ---- What a task can be done with --------------------------------------------------------

    public static readonly MailboxCommand Reply = new()
    {
        Id = new("tasks.reply"),
        Label = "Reply",
        Description = "Reply to whoever sent the flagged message.",
        Icon = "reply",
        Category = "Respond",
        Scope = ModuleScope.Tasks,
        KeyTip = "R",
    };

    public static readonly MailboxCommand ReplyAll = new()
    {
        Id = new("tasks.replyall"),
        Label = "Reply All",
        Description = "Reply to everyone on the flagged message.",
        Icon = "reply-all",
        Category = "Respond",
        Scope = ModuleScope.Tasks,
        KeyTip = "RA",
    };

    public static readonly MailboxCommand Forward = new()
    {
        Id = new("tasks.forward"),
        Label = "Forward",
        Description = "Forward the flagged message.",
        Icon = "forward",
        Category = "Respond",
        Scope = ModuleScope.Tasks,
        KeyTip = "FW",
    };

    public static readonly MailboxCommand MoveTo = new()
    {
        Id = new("tasks.moveto"),
        Label = "Move",
        Description = "Move the task to another list.",
        Icon = "move-to",
        Category = "Actions",
        Scope = ModuleScope.Tasks,
        KeyTip = "MV",
    };

    // ---- The views the Current View group offers ---------------------------------------------

    public static readonly MailboxCommand TodoListView = new()
    {
        Id = new("tasks.view.todo"),
        Label = "To-Do List",
        Description = "Everything outstanding, arranged by when it is due.",
        Icon = "todo-list",
        Category = "Current View",
        Scope = ModuleScope.Tasks,
        KeyTip = "VT",
    };

    public static readonly MailboxCommand SimpleListView = new()
    {
        Id = new("tasks.view.simple"),
        Label = "Simple List",
        Description = "Every task, finished ones included.",
        Icon = "task-simple-list",
        Category = "Current View",
        Scope = ModuleScope.Tasks,
        KeyTip = "VS",
    };

    public static readonly MailboxCommand DetailedView = new()
    {
        Id = new("tasks.view.detailed"),
        Label = "Detailed",
        Description = "Every column a task has.",
        Icon = "task-detailed",
        Category = "Current View",
        Scope = ModuleScope.Tasks,
        KeyTip = "VD",
    };

    /// <summary>Every command this module owns, which is what the catalogue registers.</summary>
    public static IReadOnlyList<MailboxCommand> All { get; } =
    [
        NewTask, NewEmail, NewItems,
        Delete, Open, MarkComplete, RemoveFromList,
        FollowUp, Categorize, Private, HighImportance, LowImportance,
        Reply, ReplyAll, Forward, MoveTo,
        TodoListView, SimpleListView, DetailedView,
    ];
}
