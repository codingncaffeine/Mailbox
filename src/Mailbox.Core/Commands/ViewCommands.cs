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
        Label = "View Settings…",
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

    public static IEnumerable<MailboxCommand> All =>
    [
        SendAll, UpdateFolder, SendReceiveGroups, ShowProgress, CancelAll,
        ChangeView, ViewSettings, ArrangeBy, ReverseSort, TighterSpacing, LayoutMenu,
        ChangeViewCompact, ChangeViewSingle, ChangeViewPreview, ManageViews, SaveViewAs, ApplyViewToFolders,
        OpenViewSettings, ResetView, Refresh,
        ImmersiveReader, ShowFocusedInbox,
        Redo, Apps, SearchPeople, ReadAloud,
        MoveToQuick, ToManager, TeamEmail,
    ];
}
