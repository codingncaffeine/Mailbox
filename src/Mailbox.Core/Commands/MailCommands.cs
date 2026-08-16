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
        AlsoGestures = ["Ctrl+Shift+M"],
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

    // The Junk menu's entries, as commands of their own: unplaced on the default ribbon — the
    // reference shows them only under Junk — but searchable, placeable and pressable like
    // everything else. Blocked and Safe are the lists in Junk Email Options.
    public static readonly MailboxCommand BlockSender = new()
    {
        Id = new("mail.junk.block"),
        Label = "Block Sender",
        Description = "Add the sender to the Blocked Senders list and move the message to Junk Email.",
        Icon = "junk",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand NeverBlockSender = new()
    {
        Id = new("mail.junk.neverblock"),
        Label = "Never Block Sender",
        Description = "Add the sender to the Safe Senders list.",
        Icon = "shield",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand NeverBlockDomain = new()
    {
        Id = new("mail.junk.neverblockdomain"),
        Label = "Never Block Sender's Domain",
        Description = "Add the sender's whole domain to the Safe Senders list.",
        Icon = "shield",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    /// <summary>The Clean Up drop-down's three entries, as commands: they can be run by keyboard, Quick Step and harness alike.</summary>
    public static readonly MailboxCommand CleanUpConversation = new()
    {
        Id = new("mail.cleanup.conversation"),
        Label = "Clean Up Conversation",
        Description = "Move redundant messages in the selected conversation to Deleted Items.",
        Icon = "cleanup",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand CleanUpFolder = new()
    {
        Id = new("mail.cleanup.folder"),
        Label = "Clean Up Folder",
        Description = "Move redundant messages in every conversation of the current folder to Deleted Items.",
        Icon = "cleanup",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand CleanUpFolderAndSubfolders = new()
    {
        Id = new("mail.cleanup.subfolders"),
        Label = "Clean Up Folder & Subfolders",
        Description = "Move redundant messages in every conversation of the current folder and its subfolders to Deleted Items.",
        Icon = "cleanup",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand NeverBlockGroup = new()
    {
        Id = new("mail.junk.neverblockgroup"),
        Label = "Never Block this Group or Mailing List",
        Description = "Add the addresses the message was sent to — a list you belong to — to the Safe Recipients list.",
        Icon = "people",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand NotJunk = new()
    {
        Id = new("mail.junk.notjunk"),
        Label = "Not Junk",
        Description = "Move the message back to the Inbox and teach the filter it is not junk.",
        Icon = "mail",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand JunkOptions = new()
    {
        Id = new("mail.junk.options"),
        Label = "Junk Email Options",
        Description = "Set the junk filter's level and manage the safe and blocked lists.",
        Icon = "settings",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        InDefaultLayout = false,
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
        IconTint = "ribbon.icon.magenta",
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
        IconTint = "ribbon.icon.magenta",
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
        IconTint = "ribbon.icon.blue",
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
        RequiresSelection = true,
    };

    // The keyboard's own two, as the reference has them: Ctrl+Q reads, Ctrl+U unreads.
    public static readonly MailboxCommand MarkAsRead = new()
    {
        Id = new("mail.read"),
        Label = "Mark as Read",
        Description = "Mark the selected messages as read.",
        Icon = "unread",
        Category = "Tags",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Ctrl+Q",
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand MarkAsUnread = new()
    {
        Id = new("mail.unread"),
        Label = "Mark as Unread",
        Description = "Mark the selected messages as unread.",
        Icon = "unread",
        Category = "Tags",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Ctrl+U",
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand PermanentDelete = new()
    {
        Id = new("mail.delete.permanent"),
        Label = "Delete Permanently",
        Description = "Delete the selected messages for good, after asking — Deleted Items is skipped.",
        Icon = "delete",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Shift+Delete",
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    /// <summary>Ctrl+Y in the reference's main window: the Go to Folder dialog over every account's tree.</summary>
    public static readonly MailboxCommand GoToFolder = new()
    {
        Id = new("nav.folder"),
        Label = "Go to Folder…",
        Description = "Choose a folder to open, from any account.",
        Icon = "folder",
        Category = "Go To",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Ctrl+Y",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand GoToInbox = new()
    {
        Id = new("nav.inbox"),
        Label = "Go to Inbox",
        Description = "Open the Inbox.",
        Icon = "mail",
        Category = "Go To",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Ctrl+Shift+I",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand GoToOutbox = new()
    {
        Id = new("nav.outbox"),
        Label = "Go to Outbox",
        Description = "Open the Outbox.",
        Icon = "mail",
        Category = "Go To",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Ctrl+Shift+O",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("item.categorize"),
        Label = "Categorize",
        Description = "Assign a colour category to the selected items.",
        Icon = "categorize",
        IconArtwork = "categorize",
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
        IconArtwork = "followup",
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
        AlsoGestures = ["F3"],
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
        IconTint = "ribbon.icon.green",
        Category = "Send & Receive",
        KeyTip = "O",
        DefaultGesture = "F9",
        AlsoGestures = ["Ctrl+M"],
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

    /// <summary>
    /// The reference has it on the Folder tab, which the current build's ribbon does not ship;
    /// here it is under File › Tools and in the catalogue.
    /// </summary>
    public static readonly MailboxCommand RecoverDeleted = new()
    {
        Id = new("mail.recoverdeleted"),
        Label = "Recover Deleted Items",
        Description = "Bring back mail that was permanently deleted recently.",
        Icon = "undo",
        Category = "Delete",
        Scope = ModuleScope.Mail,
        InDefaultLayout = false,
    };

    // Focused Inbox's four: on the row's menu when the view is on, and in the catalogue always.
    public static readonly MailboxCommand MoveToOther = new()
    {
        Id = new("mail.focus.other"),
        Label = "Move to Other",
        Description = "Move the selected messages to the Other side of the Focused Inbox.",
        Icon = "move",
        Category = "Move",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand MoveToFocused = new()
    {
        Id = new("mail.focus.focused"),
        Label = "Move to Focused",
        Description = "Move the selected messages to the Focused side of the Inbox.",
        Icon = "move",
        Category = "Move",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand AlwaysMoveToOther = new()
    {
        Id = new("mail.focus.other.always"),
        Label = "Always Move to Other",
        Description = "Move the selected messages to Other, and every future message from their senders.",
        Icon = "move",
        Category = "Move",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand AlwaysMoveToFocused = new()
    {
        Id = new("mail.focus.focused.always"),
        Label = "Always Move to Focused",
        Description = "Move the selected messages to Focused, and every future message from their senders.",
        Icon = "move",
        Category = "Move",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    /// <summary>The reference's Folder tab has it; here it is on the folder pane's menu and in the catalogue.</summary>
    public static readonly MailboxCommand NewSearchFolder = new()
    {
        Id = new("mail.searchfolder.new"),
        Label = "New Search Folder",
        Description = "Create a folder that shows the mail matching a saved search.",
        Icon = "search",
        Category = "Find",
        Scope = ModuleScope.Mail,
        InDefaultLayout = false,
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
        BlockSender, NeverBlockSender, NeverBlockDomain, NeverBlockGroup, NotJunk, JunkOptions,
        CleanUpConversation, CleanUpFolder, CleanUpFolderAndSubfolders,
        MarkAsRead, MarkAsUnread, PermanentDelete, GoToFolder, GoToInbox, GoToOutbox,
        Reply, ReplyAll, Forward, Meeting, MoreRespond,
        MoveTo, Rules, QuickSteps,
        Unread, Categorize, FollowUp,
        Search, AddressBook, FilterEmail,
        SendReceiveAll, WorkOffline, Undo,
        Snooze, ViewSource, TrackerReport, AuthenticationDetails, Print, PrintToPdf, PrintList,
        RecoverDeleted, NewSearchFolder,
        MoveToOther, MoveToFocused, AlwaysMoveToOther, AlwaysMoveToFocused,
    ];
}
