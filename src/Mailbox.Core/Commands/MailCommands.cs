namespace Mailbox.Core.Commands;

/// <summary>
/// The Mail module's command set, mirroring the reference's Home, Send/Receive, Folder
/// and View tabs.
/// </summary>
/// <remarks>
/// Commands with <see cref="MailboxCommand.InDefaultLayout"/> set to false are the additions
/// beyond the reference application parity. They are ordinary catalogue entries — searchable, bindable,
/// placeable — and are simply not positioned by the default ribbon layout, so first run is
/// an exact clone. See the plan, rule 5.
/// </remarks>
public static class MailCommands
{
    // ---- New -------------------------------------------------------------------------
    public static readonly MailboxCommand NewEmail = new()
    {
        Id = new("mail.new"),
        Label = "New Email",
        Description = "Create a new email message.",
        Icon = "mail-new",
        Category = "New",
        Scope = ModuleScope.Mail,
        KeyTip = "N",
        DefaultGesture = "Ctrl+N",
    };

    public static readonly MailboxCommand NewItems = new()
    {
        Id = new("mail.new.items"),
        Label = "New Items",
        Description = "Create a new item of any type.",
        Icon = "new-items",
        Category = "New",
        KeyTip = "I",
    };

    // ---- Delete ----------------------------------------------------------------------
    public static readonly MailboxCommand Ignore = new()
    {
        Id = new("mail.ignore"),
        Label = "Ignore",
        Description = "Move this conversation and all future messages in it to Deleted Items.",
        Icon = "ignore",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        KeyTip = "X",
        DefaultGesture = "Ctrl+Delete",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand CleanUp = new()
    {
        Id = new("mail.cleanup"),
        Label = "Clean Up",
        Description = "Delete redundant messages whose text is already quoted in a later reply.",
        Icon = "cleanup",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        KeyTip = "PN",
    };

    public static readonly MailboxCommand Junk = new()
    {
        Id = new("mail.junk"),
        Label = "Junk",
        Description = "Mark the selected messages as junk and manage the sender lists.",
        Icon = "junk",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        KeyTip = "J",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Delete = new()
    {
        Id = new("mail.delete"),
        Label = "Delete",
        Description = "Move the selected items to the Deleted Items folder.",
        Icon = "delete",
        Category = "Delete",
        KeyTip = "D",
        DefaultGesture = "Delete",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Archive = new()
    {
        Id = new("mail.archive"),
        Label = "Archive",
        Description = "Move the selected items to the Archive folder.",
        Icon = "archive",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        KeyTip = "1",
        DefaultGesture = "Back",
        RequiresSelection = true,
    };

    // ---- Respond ---------------------------------------------------------------------
    public static readonly MailboxCommand Reply = new()
    {
        Id = new("mail.reply"),
        Label = "Reply",
        Description = "Reply to the sender.",
        Icon = "reply",
        Category = "Respond",
        Scope = ModuleScope.Mail,
        KeyTip = "R",
        DefaultGesture = "Ctrl+R",
        RequiresSingleSelection = true,
    };

    public static readonly MailboxCommand ReplyAll = new()
    {
        Id = new("mail.reply.all"),
        Label = "Reply All",
        Description = "Reply to the sender and all other recipients.",
        Icon = "reply-all",
        Category = "Respond",
        Scope = ModuleScope.Mail,
        KeyTip = "A",
        DefaultGesture = "Ctrl+Shift+R",
        RequiresSingleSelection = true,
    };

    public static readonly MailboxCommand Forward = new()
    {
        Id = new("mail.forward"),
        Label = "Forward",
        Description = "Forward the selected message to someone else.",
        Icon = "forward",
        Category = "Respond",
        Scope = ModuleScope.Mail,
        KeyTip = "FW",
        DefaultGesture = "Ctrl+F",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Meeting = new()
    {
        Id = new("mail.meeting.reply"),
        Label = "Meeting",
        Description = "Reply with a meeting request to everyone on this message.",
        Icon = "meeting",
        Category = "Respond",
        Scope = ModuleScope.Mail,
        KeyTip = "H",
        DefaultGesture = "Ctrl+Alt+R",
        RequiresSingleSelection = true,
    };

    public static readonly MailboxCommand MoreRespond = new()
    {
        Id = new("mail.respond.more"),
        Label = "More",
        Description = "Reply with an attachment, forward as attachment, and other responses.",
        Icon = "more",
        Category = "Respond",
        Scope = ModuleScope.Mail,
        KeyTip = "V",
        RequiresSelection = true,
    };

    // ---- Move ------------------------------------------------------------------------
    public static readonly MailboxCommand MoveTo = new()
    {
        Id = new("mail.move"),
        Label = "Move",
        Description = "Move the selected items to a folder.",
        Icon = "move",
        Category = "Move",
        KeyTip = "MV",
        DefaultGesture = "Ctrl+Shift+V",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Rules = new()
    {
        Id = new("mail.rules"),
        Label = "Rules",
        Description = "Create a rule from the selected message, or manage all rules.",
        Icon = "rules",
        Category = "Move",
        Scope = ModuleScope.Mail,
        KeyTip = "E",
    };

    public static readonly MailboxCommand QuickSteps = new()
    {
        Id = new("mail.quicksteps"),
        Label = "Quick Steps",
        Description = "Apply a saved sequence of actions to the selected items.",
        Icon = "quicksteps",
        Category = "Quick Steps",
        Scope = ModuleScope.Mail,
        KeyTip = "Q",
    };

    // ---- Tags ------------------------------------------------------------------------
    public static readonly MailboxCommand Unread = new()
    {
        Id = new("mail.markunread"),
        Label = "Unread/Read",
        Description = "Toggle whether the selected messages are marked as read.",
        Icon = "unread",
        Category = "Tags",
        Scope = ModuleScope.Mail,
        KeyTip = "W",
        DefaultGesture = "Ctrl+U",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("item.categorize"),
        Label = "Categorize",
        Description = "Assign a colour category to the selected items.",
        Icon = "categorize",
        Category = "Tags",
        KeyTip = "G",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand FollowUp = new()
    {
        Id = new("item.followup"),
        Label = "Follow Up",
        Description = "Flag the selected items for follow up, with an optional reminder.",
        Icon = "flag",
        Category = "Tags",
        KeyTip = "U",
        DefaultGesture = "Ctrl+Shift+G",
        RequiresSelection = true,
    };

    // ---- Find ------------------------------------------------------------------------
    public static readonly MailboxCommand Search = new()
    {
        Id = new("app.search"),
        Label = "Search",
        Description = "Search the current folder, subfolders, or all items.",
        Icon = "search",
        Category = "Find",
        KeyTip = "SE",
        DefaultGesture = "Ctrl+E",
    };

    public static readonly MailboxCommand AddressBook = new()
    {
        Id = new("app.addressbook"),
        Label = "Address Book",
        Description = "Open the address book to look up a contact.",
        Icon = "address-book",
        Category = "Find",
        KeyTip = "B",
        DefaultGesture = "Ctrl+Shift+B",
    };

    public static readonly MailboxCommand FilterEmail = new()
    {
        Id = new("mail.filter"),
        Label = "Filter Email",
        Description = "Show only messages matching a condition, such as unread or flagged.",
        Icon = "filter",
        Category = "Find",
        Scope = ModuleScope.Mail,
        KeyTip = "L",
    };

    // ---- Send/Receive ----------------------------------------------------------------
    public static readonly MailboxCommand SendReceiveAll = new()
    {
        Id = new("app.sendreceive.all"),
        Label = "Send/Receive All Folders",
        Description = "Check every account for new mail and send anything waiting in the Outbox.",
        Icon = "send-receive",
        Category = "Send & Receive",
        KeyTip = "O",
        DefaultGesture = "F9",
    };

    public static readonly MailboxCommand WorkOffline = new()
    {
        Id = new("app.workoffline"),
        Label = "Work Offline",
        Description = "Stop connecting to servers. Queued changes are sent when you go back online.",
        Icon = "work-offline",
        Category = "Preferences",
        KeyTip = "JW",
    };

    /// <summary>On the Quick Access Toolbar by default, exactly as in the reference application.</summary>
    public static readonly MailboxCommand Undo = new()
    {
        Id = new("app.undo"),
        Label = "Undo",
        Description = "Reverse the last action.",
        Icon = "undo",
        Category = "Actions",
        DefaultGesture = "Ctrl+Z",
    };

    // ---- Beyond the reference application: present, catalogued, not on the default ribbon ---------------
    public static readonly MailboxCommand Snooze = new()
    {
        Id = new("mail.snooze"),
        Label = "Snooze",
        Description = "Hide this message and bring it back to the top of the Inbox later.",
        Icon = "snooze",
        Category = "Tags",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand ViewSource = new()
    {
        Id = new("mail.viewsource"),
        Label = "View Source",
        Description = "Show the raw message exactly as it was received, headers and all.",
        Icon = "source",
        Category = "Actions",
        Scope = ModuleScope.Mail,
        RequiresSingleSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand TrackerReport = new()
    {
        Id = new("mail.trackers"),
        Label = "Tracker Report",
        Description = "List the remote hosts this message tried to contact when it was opened.",
        Icon = "tracker",
        Category = "Actions",
        Scope = ModuleScope.Mail,
        RequiresSingleSelection = true,
        InDefaultLayout = false,
    };

    /// <summary>
    /// In the reference, on the File menu rather than the bar — so it is parity, and simply not
    /// somewhere the shipped layout places it.
    /// </summary>
    public static readonly MailboxCommand Print = new()
    {
        Id = new("mail.print"),
        Label = "Print",
        Description = "Print the selected message.",
        Icon = "print",
        Category = "Actions",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Ctrl+P",
        RequiresSingleSelection = true,
    };

    /// <summary>
    /// Not in the reference, which prints to whatever the system offers. On Linux the engine
    /// writes a PDF directly, so the step through a print dialog is one nobody needs.
    /// </summary>
    public static readonly MailboxCommand PrintToPdf = new()
    {
        Id = new("mail.print.pdf"),
        Label = "Print to PDF",
        Description = "Write the selected message to a PDF file.",
        Icon = "print",
        Category = "Actions",
        Scope = ModuleScope.Mail,
        RequiresSingleSelection = true,
        InDefaultLayout = false,
    };

    /// <summary>
    /// The reference's other print style: the folder as a list rather than one message.
    /// </summary>
    public static readonly MailboxCommand PrintList = new()
    {
        Id = new("mail.print.list"),
        Label = "Print List",
        Description = "Print the messages in this folder as a list.",
        Icon = "print",
        Category = "Actions",
        Scope = ModuleScope.Mail,
    };

    public static readonly MailboxCommand AuthenticationDetails = new()
    {
        Id = new("mail.authresults"),
        Label = "Authentication",
        Description = "Show DKIM, SPF and DMARC results for the selected message.",
        Icon = "shield",
        Category = "Actions",
        Scope = ModuleScope.Mail,
        RequiresSingleSelection = true,
        InDefaultLayout = false,
    };

    public static IEnumerable<MailboxCommand> All =>
    [
        NewEmail, NewItems,
        Ignore, CleanUp, Junk, Delete, Archive,
        Reply, ReplyAll, Forward, Meeting, MoreRespond,
        MoveTo, Rules, QuickSteps,
        Unread, Categorize, FollowUp,
        Search, AddressBook, FilterEmail,
        SendReceiveAll, WorkOffline, Undo,
        Snooze, ViewSource, TrackerReport, AuthenticationDetails, Print, PrintToPdf, PrintList,
    ];
}
