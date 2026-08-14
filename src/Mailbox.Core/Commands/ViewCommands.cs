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
        Category = "Send &amp; Receive",
        KeyTip = "SA",
    };

    public static readonly MailboxCommand UpdateFolder = new()
    {
        Id = new("app.updatefolder"),
        Label = "Update Folder",
        Description = "Check the current folder for new messages.",
        Icon = "update-folder",
        Category = "Send &amp; Receive",
        KeyTip = "U",
        DefaultGesture = "Shift+F9",
    };

    public static readonly MailboxCommand SendReceiveGroups = new()
    {
        Id = new("app.sendreceive.groups"),
        Label = "Send/Receive Groups",
        Description = "Choose which accounts and folders are included in each send and receive.",
        Icon = "sr-groups",
        Category = "Send &amp; Receive",
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

    // ---- Shell -------------------------------------------------------------------------
    public static readonly MailboxCommand Redo = new()
    {
        Id = new("app.redo"),
        Label = "Redo",
        Description = "Repeat the action that was just undone.",
        Icon = "redo",
        Category = "Actions",
        DefaultGesture = "Ctrl+Y",
    };

    public static readonly MailboxCommand Apps = new()
    {
        Id = new("app.apps"),
        Label = "All Apps",
        Description = "Open the apps list — Folders, Notes, Shortcuts and anything installed.",
        Icon = "apps",
        Category = "Add-ins",
        KeyTip = "AP",
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
        KeyTip = "RA",
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
        ImmersiveReader,
        Redo, Apps, SearchPeople, ReadAloud,
        MoveToQuick, ToManager, TeamEmail,
    ];
}
