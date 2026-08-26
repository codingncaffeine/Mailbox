namespace Mailbox.Core.Commands;

/// <summary>
/// The Send/Receive and View tabs, transcribed from the reference application running the Simplified
/// ribbon. Order here is the order on screen.
/// </summary>
public static class ViewCommands
{
    // ---- Send / Receive ----------------------------------------------------------------
    public static readonly MailboxCommand SendAll = new()
    {
        Id = new("app.sendall"),
        Label = "Send All",
        Description = "Send everything waiting in the Outbox without checking for new mail.",
        Icon = "send",
        Category = "Send & Receive",
        KeyTip = "SA",
    };

    public static readonly MailboxCommand UpdateFolder = new()
    {
        Id = new("app.updatefolder"),
        Label = "Update Folder",
        Description = "Check the current folder for new messages.",
        Icon = "update-folder",
        Category = "Send & Receive",
        KeyTip = "U",
        DefaultGesture = "Shift+F9",
    };

    public static readonly MailboxCommand SendReceiveGroups = new()
    {
        Id = new("app.sendreceive.groups"),
        Label = "Send/Receive Groups",
        Description = "Choose which accounts and folders are included in each send and receive.",
        Icon = "sr-groups",
        Category = "Send & Receive",
        KeyTip = "G",
    };

    public static readonly MailboxCommand ShowProgress = new()
    {
        Id = new("app.showprogress"),
        Label = "Show Progress",
        Description = "Open the send and receive progress window.",
        Icon = "show-progress",
        Category = "Download",
        KeyTip = "SP",
    };

    public static readonly MailboxCommand CancelAll = new()
    {
        Id = new("app.cancelall"),
        Label = "Cancel All",
        Description = "Stop every send and receive currently running.",
        Icon = "cancel",
        Category = "Download",
        KeyTip = "CA",
    };

    // ---- View --------------------------------------------------------------------------
    public static readonly MailboxCommand ChangeView = new()
    {
        Id = new("view.change"),
        Label = "Change View",
        Description = "Switch between Compact, Single and Preview layouts.",
        Icon = "change-view",
        IconTint = "ribbon.icon.blue",
        Category = "Current View",
        KeyTip = "CV",
    };

    public static readonly MailboxCommand ViewSettings = new()
    {
        Id = new("view.settings"),
        Label = "Current View",
        Description = "Open Advanced View Settings: columns, grouping, sorting, filtering and " +
                      "conditional formatting.",
        Icon = "view-settings",
        Category = "Current View",
        KeyTip = "VS",
    };

    // The Change View gallery's three, the Current View menu's two, and the gallery's three
    // dialogs, as commands: keyboard, harness and the menus all run the same thing.
    public static readonly MailboxCommand ChangeViewCompact = new()
    {
        Id = new("view.change.compact"),
        Label = "Compact",
        Description = "The Compact view: a card in a narrow list, a line with a preview in a wide one.",
        Icon = "change-view",
        IconTint = "ribbon.icon.blue",
        Category = "Current View",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand ChangeViewSingle = new()
    {
        Id = new("view.change.single"),
        Label = "Single",
        Description = "The Single view: one line per message, in columns.",
        Icon = "change-view",
        IconTint = "ribbon.icon.blue",
        Category = "Current View",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand ChangeViewPreview = new()
    {
        Id = new("view.change.preview"),
        Label = "Preview",
        Description = "The Preview view: one line per message with its preview beneath.",
        Icon = "change-view",
        IconTint = "ribbon.icon.blue",
        Category = "Current View",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand ManageViews = new()
    {
        Id = new("view.manage"),
        Label = "Manage Views…",
        Description = "Create, copy, modify, rename and delete the views this folder can use.",
        Icon = "view-settings",
        Category = "Current View",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand SaveViewAs = new()
    {
        Id = new("view.saveas"),
        Label = "Save Current View As a New View…",
        Description = "Keep the current view under a name of your own.",
        Icon = "view-settings",
        Category = "Current View",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand ApplyViewToFolders = new()
    {
        Id = new("view.applyto"),
        Label = "Apply Current View to Other Mail Folders…",
        Description = "Put this folder's view on other folders of the account.",
        Icon = "view-settings",
        Category = "Current View",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand OpenViewSettings = new()
    {
        Id = new("view.viewsettings"),
        Label = "View Settings",
        Description = "Advanced View Settings: columns, grouping, sorting, filtering and conditional formatting.",
        Icon = "view-settings",
        Category = "Current View",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand ResetView = new()
    {
        Id = new("view.reset"),
        Label = "Reset View",
        Description = "Put this folder's view back the way it came.",
        Icon = "view-settings",
        Category = "Current View",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand Refresh = new()
    {
        Id = new("view.refresh"),
        Label = "Refresh",
        Description = "Reload the folder pane and the list from the store.",
        Icon = "sync",
        Category = "Window",
        DefaultGesture = "F5",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand ArrangeBy = new()
    {
        Id = new("view.arrangeby"),
        Label = "Arrange By",
        Description = "Group the message list by date, sender, subject, size, category or flag.",
        Icon = "arrange",
        Category = "Arrangement",
        KeyTip = "AB",
    };

    public static readonly MailboxCommand ReverseSort = new()
    {
        Id = new("view.reversesort"),
        Label = "Reverse Sort",
        Description = "Reverse the order the current arrangement sorts in.",
        Icon = "reverse-sort",
        Category = "Arrangement",
        KeyTip = "RS",
    };

    public static readonly MailboxCommand TighterSpacing = new()
    {
        Id = new("view.tighterspacing"),
        Label = "Use Tighter Spacing",
        Description = "Reduce the space around list rows so more messages fit on screen.",
        Icon = "spacing",
        Category = "Arrangement",
        KeyTip = "TS",
    };

    public static readonly MailboxCommand LayoutMenu = new()
    {
        Id = new("view.layout"),
        Label = "Layout",
        Description = "Show or hide the folder pane, reading pane and To-Do Bar.",
        Icon = "layout",
        Category = "Layout",
        KeyTip = "L",
    };

    public static readonly MailboxCommand ImmersiveReader = new()
    {
        Id = new("view.reader"),
        Label = "Immersive Reader",
        Description = "Open the selected message in a distraction-free reading view.",
        Icon = "reader",
        Category = "Immersive",
        KeyTip = "IR",
        RequiresSingleSelection = true,
    };

    /// <summary>
    /// The reference's View tab has it in a Focused Inbox group; the Simplified bar keeps it
    /// behind the "…", which is where this is too — off the default row, in the overflow.
    /// </summary>
    public static readonly MailboxCommand ShowFocusedInbox = new()
    {
        Id = new("view.focusedinbox"),
        Label = "Show Focused Inbox",
        Description = "Split the Inbox into Focused and Other, sorted as mail arrives.",
        Icon = "focus-time",
        Category = "Focused Inbox",
        Scope = ModuleScope.Mail,
        InDefaultLayout = false,
    };

    // ---- Shell -------------------------------------------------------------------------
    // No shell chord: in the reference's main window Ctrl+Y is Go to Folder, and Redo's Ctrl+Y
    // is the editor's own, inside a message.
    public static readonly MailboxCommand Redo = new()
    {
        Id = new("app.redo"),
        Label = "Redo",
        Description = "Repeat the action that was just undone.",
        Icon = "redo",
        Category = "Actions",
    };

    public static readonly MailboxCommand Apps = new()
    {
        Id = new("app.apps"),
        Label = "All Apps",
        Description = "Open the apps list — Folders, Notes, Shortcuts and anything installed.",
        Icon = "apps",
        IconTint = "ribbon.icon.blue",
        Category = "Add-ins",
        KeyTip = "T",
    };

    /// <summary>
    /// Reads the selected message aloud. the reference application greys this out with nothing selected, which is
    /// the state the reference capture shows.
    /// </summary>
    public static readonly MailboxCommand ReadAloud = new()
    {
        Id = new("mail.readaloud"),
        Label = "Read Aloud",
        Description = "Read the selected message out loud.",
        Icon = "read-aloud",
        Category = "Speech",
        Scope = ModuleScope.Mail,
        KeyTip = "C",
        RequiresSingleSelection = true,
    };

    /// <summary>
    /// The Zoom dialog over an open message's body — the message window's own button. The shell
    /// reads at the status bar's zoom; a window carries a zoom of its own, as the reference's do.
    /// </summary>
    public static readonly MailboxCommand Zoom = new()
    {
        Id = new("view.zoom"),
        Label = "Zoom",
        Description = "Choose how large the message's text is drawn.",
        Icon = "zoom",
        Category = "Zoom",
        Scope = ModuleScope.Mail,
    };

    /// <summary>Waits for Phase 16's language work, and says so when pressed (§20).</summary>
    public static readonly MailboxCommand Translate = new()
    {
        Id = new("view.translate"),
        Label = "Translate",
        Description = "Translate the selected text.",
        Icon = "language",
        Category = "Language",
        Scope = ModuleScope.Mail,
    };

    /// <summary>Find inside the open message. Waits for Phase 16's polish, and says so (§20).</summary>
    public static readonly MailboxCommand FindInMessage = new()
    {
        Id = new("mail.find"),
        Label = "Find",
        Description = "Find words inside this message.",
        Icon = "search",
        Category = "Editing",
        Scope = ModuleScope.Mail,
    };

    /// <summary>
    /// the reference's third Move entry is Send to OneNote. Deliberately absent: that is Microsoft
    /// integration, which the project excludes, so the group ships with two entries.
    /// </summary>
    public static readonly MailboxCommand MoveToQuick = new()
    {
        Id = new("mail.moveto.quick"),
        Label = "Move to: ?",
        Description = "Move the selected message to the folder you last chose.",
        Icon = "move",
        Category = "Quick Steps",
        KeyTip = "MQ",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
    };

    public static readonly MailboxCommand ToManager = new()
    {
        Id = new("mail.quickstep.tomanager"),
        Label = "To Manager",
        Description = "Forward the selected message to your manager.",
        Icon = "forward",
        Category = "Quick Steps",
        KeyTip = "MG",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
    };

    public static readonly MailboxCommand TeamEmail = new()
    {
        Id = new("mail.quickstep.teamemail"),
        Label = "Team Email",
        Description = "Start a message to your team.",
        Icon = "mail",
        Category = "Quick Steps",
        KeyTip = "MT",
        Scope = ModuleScope.Mail,
    };

    public static readonly MailboxCommand SearchPeople = new()
    {
        Id = new("app.searchpeople"),
        Label = "Search People",
        Description = "Look someone up in your contacts.",
        Icon = "people",
        Category = "Find",
        KeyTip = "SP",
    };

    /// <summary>
    /// The shortcut list itself: "?" shows every command and the key that runs it.
    /// </summary>
    /// <remarks>
    /// Customize Keyboard already lists exactly that, so "?" opens it rather than a second window
    /// that would have to be kept in step with the first. Both spellings of the key are bound —
    /// the "?" of a US keyboard is Shift and the key left of the right Shift, and a layout that
    /// puts "?" there unshifted sends it alone.
    /// </remarks>
    public static readonly MailboxCommand KeyboardShortcuts = new()
    {
        Id = new("app.keyboard"),
        Label = "Keyboard Shortcuts",
        Description = "Show every command and the key that runs it.",
        Icon = "keyboard",
        Category = "Help",
        DefaultGesture = "Shift+OemQuestion",
        AlsoGestures = ["OemQuestion"],
        InDefaultLayout = false,
    };

    /// <summary>
    /// The rail's modules as commands, one per accelerator.
    /// </summary>
    /// <remarks>
    /// Ctrl+1 to Ctrl+8 are the reference's own module switches (§6), and the enum's values are
    /// those numbers. They are commands rather than a special case in the key handler so they can
    /// be rebound, searched for and placed like everything else — and unplaced on the default
    /// ribbon, because the reference switches modules from the rail and not from a tab.
    /// </remarks>
    private static MailboxCommand Module(MailboxModule module, string icon) => new()
    {
        Id = new($"app.module.{module.ToString().ToLowerInvariant()}"),
        Label = module.ToString(),
        Description = $"Switch to {module}.",
        Icon = icon,
        Category = "Go To",
        DefaultGesture = $"Ctrl+{(int)module}",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand GoToMail = Module(MailboxModule.Mail, "mail");
    public static readonly MailboxCommand GoToCalendar = Module(MailboxModule.Calendar, "calendar");
    public static readonly MailboxCommand GoToPeople = Module(MailboxModule.People, "people");
    public static readonly MailboxCommand GoToTasks = Module(MailboxModule.Tasks, "tasks");
    public static readonly MailboxCommand GoToNotes = Module(MailboxModule.Notes, "notes");
    public static readonly MailboxCommand GoToJournal = Module(MailboxModule.Journal, "journal");

    /// <summary>The module a switch command names, or null when it is not one.</summary>
    public static MailboxModule? ModuleOf(CommandId id)
    {
        foreach (var (command, module) in ((MailboxCommand, MailboxModule)[])
                 [
                     (GoToMail, MailboxModule.Mail), (GoToCalendar, MailboxModule.Calendar),
                     (GoToPeople, MailboxModule.People), (GoToTasks, MailboxModule.Tasks),
                     (GoToNotes, MailboxModule.Notes), (GoToJournal, MailboxModule.Journal),
                 ])
        {
            if (command.Id == id) return module;
        }

        return null;
    }

    // ---- The View tab's Messages group ------------------------------------------------------

    /// <summary>
    /// Show as Conversations. A tick on the ribbon rather than a button, which is how the
    /// reference draws the one switch on that tab that has a state rather than an action.
    /// </summary>
    public static readonly MailboxCommand ShowAsConversations = new()
    {
        Id = new("view.conversations"),
        Label = "Show as Conversations",
        Description = "Fold replies under the message they answer, so a thread is one row.",
        Icon = "conversation",
        Category = "Messages",
        Scope = ModuleScope.Mail,
        KeyTip = "SC",
    };

    public static readonly MailboxCommand ConversationSettings = new()
    {
        Id = new("view.conversations.settings"),
        Label = "Conversation Settings",
        Description = "Choose what a conversation shows: messages from other folders, senders above the subject, and the rest.",
        Icon = "conversation-settings",
        Category = "Messages",
        Scope = ModuleScope.Mail,
        KeyTip = "CS",
    };

    // ---- Arrangement ------------------------------------------------------------------------

    /// <summary>
    /// The number of lines of the message's own text the list shows under its subject.
    /// </summary>
    public static readonly MailboxCommand MessagePreview = new()
    {
        Id = new("view.messagepreview"),
        Label = "Message Preview",
        Description = "Show one, two or three lines of each message under its subject, or none at all.",
        Icon = "message-preview",
        IconTint = "ribbon.icon.blue",
        Category = "Arrangement",
        Scope = ModuleScope.Mail,
        KeyTip = "PV",
    };

    /// <summary>
    /// One entry of the Arrangement gallery. The reference's gallery is a grid of the fields a
    /// folder can be arranged by, with the current one boxed.
    /// </summary>
    private static MailboxCommand Arrange(string id, string label, string icon, string keyTip) => new()
    {
        Id = new($"view.arrange.{id}"),
        Label = label,
        Description = $"Arrange the message list by {label.ToLowerInvariant()}.",
        Icon = icon,
        IconTint = "ribbon.icon.blue",
        Category = "Arrangement",
        Scope = ModuleScope.Mail,
        KeyTip = keyTip,
    };

    public static readonly MailboxCommand ArrangeByDate = Arrange("date", "Date", "arrange-date", "AD");
    public static readonly MailboxCommand ArrangeByFrom = Arrange("from", "From", "arrange-from", "AF");
    public static readonly MailboxCommand ArrangeByTo = Arrange("to", "To", "arrange-to", "AT");
    public static readonly MailboxCommand ArrangeBySize = Arrange("size", "Size", "arrange-size", "AZ");

    /// <summary>Categorize's four swatches, which no monochrome glyph can carry.</summary>
    public static readonly MailboxCommand ArrangeByCategories = Arrange("categories", "Categories", "categorize", "AC")
        with { IconArtwork = "categorize", IconTint = null };

    public static readonly MailboxCommand ArrangeByFlagStatus = Arrange("flagstatus", "Flag Status", "flag", "AL")
        with { IconArtwork = "followup", IconTint = null };

    public static readonly MailboxCommand ArrangeByFlagStart = Arrange("flagstart", "Flag: Start Date", "flag", "AS")
        with { IconArtwork = "followup", IconTint = null };

    public static readonly MailboxCommand ArrangeByFlagDue = Arrange("flagdue", "Flag: Due Date", "flag", "AU")
        with { IconArtwork = "followup", IconTint = null };

    /// <summary>The gallery in the order the reference lists it, the boxed one first.</summary>
    public static IReadOnlyList<MailboxCommand> Arrangements =>
    [
        ArrangeByDate, ArrangeByFrom, ArrangeByTo, ArrangeByCategories,
        ArrangeByFlagStatus, ArrangeByFlagStart, ArrangeByFlagDue, ArrangeBySize,
    ];

    public static readonly MailboxCommand AddColumns = new()
    {
        Id = new("view.addcolumns"),
        Label = "Add Columns",
        Description = "Choose which columns the message list shows, and in what order.",
        Icon = "add-columns",
        IconTint = "ribbon.icon.green",
        Category = "Arrangement",
        Scope = ModuleScope.Mail,
        KeyTip = "AA",
    };

    public static readonly MailboxCommand ExpandCollapse = new()
    {
        Id = new("view.expandcollapse"),
        Label = "Expand/Collapse",
        Description = "Open or close the groups the current arrangement divides the list into.",
        Icon = "expand-collapse",
        IconTint = "ribbon.icon.green",
        Category = "Arrangement",
        Scope = ModuleScope.Mail,
        KeyTip = "EC",
    };

    // ---- Layout -----------------------------------------------------------------------------

    public static readonly MailboxCommand FolderPane = new()
    {
        Id = new("view.folderpane"),
        Label = "Folder Pane",
        Description = "Show the folder pane in full, minimised to its icons, or not at all.",
        Icon = "folder-pane",
        IconTint = "ribbon.icon.blue",
        Category = "Layout",
        KeyTip = "FP",
    };

    public static readonly MailboxCommand ReadingPane = new()
    {
        Id = new("view.readingpane"),
        Label = "Reading Pane",
        Description = "Put the reading pane to the right of the list, under it, or turn it off.",
        Icon = "reading-pane",
        IconTint = "ribbon.icon.blue",
        Category = "Layout",
        KeyTip = "PN",
    };

    public static readonly MailboxCommand ToDoBar = new()
    {
        Id = new("view.todobar"),
        Label = "To-Do Bar",
        Description = "Dock the calendar, tasks or people down the right-hand side.",
        Icon = "todo-bar",
        IconTint = "ribbon.icon.blue",
        Category = "Layout",
        KeyTip = "TB",
    };

    // ---- Window -----------------------------------------------------------------------------

    public static readonly MailboxCommand RemindersWindow = new()
    {
        Id = new("view.reminders"),
        Label = "Reminders Window",
        Description = "Show what is due: the reminders waiting across mail, the calendar and tasks.",
        Icon = "reminders-window",
        Category = "Window",
        KeyTip = "RW",
    };

    public static readonly MailboxCommand OpenInNewWindow = new()
    {
        Id = new("view.newwindow"),
        Label = "Open in New Window",
        Description = "Open the current folder in a second window of its own.",
        Icon = "new-window",
        IconTint = "ribbon.icon.green",
        Category = "Window",
        KeyTip = "NW",
    };

    public static readonly MailboxCommand CloseAllItems = new()
    {
        Id = new("view.closeall"),
        Label = "Close All Items",
        Description = "Close every message, appointment and contact window this one has opened.",
        Icon = "close-all",
        IconTint = "ribbon.icon.delete",
        Category = "Window",
        KeyTip = "CL",
    };

    // ---- The Send/Receive tab's Server group -------------------------------------------------

    /// <summary>
    /// Headers without their messages, for a folder the reader would rather choose from than
    /// download whole. The three that follow act on what this brings back.
    /// </summary>
    public static readonly MailboxCommand DownloadHeaders = new()
    {
        Id = new("app.downloadheaders"),
        Label = "Download Headers",
        Description = "Fetch the sender, subject and size of everything in this folder, without the messages themselves.",
        Icon = "download-headers",
        IconTint = "ribbon.icon.blue",
        Category = "Server",
        Scope = ModuleScope.Mail,
        KeyTip = "DH",
    };

    public static readonly MailboxCommand MarkToDownload = new()
    {
        Id = new("app.markdownload"),
        Label = "Mark to Download",
        Description = "Mark the selected headers so the next send/receive fetches their messages.",
        Icon = "mark-download",
        IconTint = "ribbon.icon.blue",
        Category = "Server",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,
        KeyTip = "MD",
    };

    public static readonly MailboxCommand UnmarkToDownload = new()
    {
        Id = new("app.unmarkdownload"),
        Label = "Unmark to Download",
        Description = "Take the download mark off the selected headers.",
        Icon = "unmark-download",
        IconTint = "ribbon.icon.delete",
        Category = "Server",
        Scope = ModuleScope.Mail,
        RequiresSelection = true,

        // Not "UD": Update Folder is "U" in the same tab, and a single letter fires before a
        // second one can be typed.
        KeyTip = "MU",
    };

    public static readonly MailboxCommand ProcessMarkedHeaders = new()
    {
        Id = new("app.processheaders"),
        Label = "Process Marked Headers",
        Description = "Fetch the messages behind every header marked for download.",
        Icon = "process-headers",
        IconTint = "ribbon.icon.green",
        Category = "Server",
        Scope = ModuleScope.Mail,
        KeyTip = "PH",
    };

    // ---- The Help tab ------------------------------------------------------------------------
    //
    // Eight buttons, transcribed from the capture. Four of them lead somewhere this project has:
    // the manual, the issues page twice, and the release notes. The other four name services the
    // reference's publisher runs — a support desk, training videos, a repair tool and a
    // diagnostics collector — and they are drawn rather than dropped, because the tab is what
    // the reference's tab is. What each of those four says when it is pressed is written in the
    // application's own voice: a screentip is interface, and no interface string here names the
    // reference or anybody's publisher.

    /// <summary>Where the project lives, and the pages the Help tab sends a reader to.</summary>
    public static class Project
    {
        public const string Repository = "https://github.com/codingncaffeine/Mailbox";

        /// <summary>The manual. The README until there is a longer one, which is where it will go.</summary>
        public const string Manual = Repository + "#readme";

        /// <summary>Where a fault or an idea goes. Both Feedback and Suggest a Feature open it.</summary>
        public const string Issues = Repository + "/issues/new";

        /// <summary>Every release carries what is new and what is fixed; the newest is at the top.</summary>
        public const string Releases = Repository + "/releases";
    }

    public static readonly MailboxCommand Help = new()
    {
        Id = new("help.manual"),
        Label = "Help",
        Description = "Open the manual.",
        Icon = "help",
        IconTint = "ribbon.icon.blue",
        Category = "Help",
        KeyTip = "H",
        DefaultGesture = "F1",
    };

    public static readonly MailboxCommand ContactSupport = new()
    {
        Id = new("help.support"),
        Label = "Contact Support",
        Description = "There is no support desk behind this application; the issues page is where a problem gets looked at.",
        Icon = "contact-support",
        Category = "Help",
        KeyTip = "CS",
    };

    public static readonly MailboxCommand Feedback = new()
    {
        Id = new("help.feedback"),
        Label = "Feedback",
        Description = "Say what is wrong, or what worked — the issues page.",
        Icon = "feedback",
        Category = "Help",
        KeyTip = "FB",
    };

    public static readonly MailboxCommand SuggestFeature = new()
    {
        Id = new("help.suggest"),
        Label = "Suggest a Feature",
        Description = "Ask for something the application does not do yet — the issues page.",
        Icon = "suggest-feature",
        Category = "Help",
        KeyTip = "SF",
    };

    public static readonly MailboxCommand ShowTraining = new()
    {
        Id = new("help.training"),
        Label = "Show Training",
        Description = "There are no training videos for this application. The manual is what there is, and Help opens it.",
        Icon = "show-training",
        Category = "Help",
        KeyTip = "ST",
    };

    public static readonly MailboxCommand WhatsNew = new()
    {
        Id = new("help.whatsnew"),
        Label = "What's New",
        Description = "What the newest release added and fixed.",
        Icon = "whats-new",
        IconTint = "ribbon.icon.blue",
        Category = "Help",
        KeyTip = "WN",
    };

    public static readonly MailboxCommand SupportTool = new()
    {
        Id = new("help.supporttool"),
        Label = "Support Tool",
        Description = "Nothing here diagnoses or repairs an installation; a fault worth looking at goes to the issues page.",
        Icon = "support-tool",
        Category = "Help",
        KeyTip = "SU",
    };

    public static readonly MailboxCommand GetDiagnostics = new()
    {
        Id = new("help.diagnostics"),
        Label = "Get Diagnostics",
        Description = "Collecting a report to send to a support desk needs a support desk. The logs are written to disk either way.",
        Icon = "get-diagnostics",
        Category = "Tools",
        KeyTip = "GD",
    };

    public static IEnumerable<MailboxCommand> All =>
    [
        GoToMail, GoToCalendar, GoToPeople, GoToTasks, GoToNotes, GoToJournal,
        SendAll, UpdateFolder, SendReceiveGroups, ShowProgress, CancelAll,
        ChangeView, ViewSettings, ArrangeBy, ReverseSort, TighterSpacing, LayoutMenu,
        ChangeViewCompact, ChangeViewSingle, ChangeViewPreview, ManageViews, SaveViewAs, ApplyViewToFolders,
        OpenViewSettings, ResetView, Refresh,
        ImmersiveReader, ShowFocusedInbox,
        Redo, Apps, SearchPeople, ReadAloud, KeyboardShortcuts,
        MoveToQuick, ToManager, TeamEmail,
        Zoom, Translate, FindInMessage,
        ShowAsConversations, ConversationSettings, MessagePreview,
        .. Arrangements,
        AddColumns, ExpandCollapse,
        FolderPane, ReadingPane, ToDoBar,
        RemindersWindow, OpenInNewWindow, CloseAllItems,
        DownloadHeaders, MarkToDownload, UnmarkToDownload, ProcessMarkedHeaders,
        Help, ContactSupport, Feedback, SuggestFeature, ShowTraining, WhatsNew, SupportTool, GetDiagnostics,
    ];
}
