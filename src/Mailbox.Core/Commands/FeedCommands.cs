namespace Mailbox.Core.Commands;

/// <summary>
/// The Feeds module's commands: subscribing, reading, and moving a subscription list in and out.
/// </summary>
/// <remarks>
/// The reference has no such module and therefore no such bar — it keeps feeds as folders under
/// Mail, with the subscription list buried in Account Settings. These are authored, in the order
/// a reader reaches for them: add one, read what arrived, keep one for later, and get the whole
/// list out again.
/// <para>
/// Every one of them is in the catalogue, so each can be searched for, rebound and placed like
/// any other — which is also what makes them reachable from the keyboard without the ribbon.
/// </para>
/// </remarks>
public static class FeedCommands
{
    public static readonly MailboxCommand Subscribe = new()
    {
        Id = new("feeds.subscribe"),
        Label = "Add a Feed",
        Description = "Subscribe to a website or an RSS address.",
        Icon = "rss",
        Category = "New",

        // Every module: subscribing is something a reader does when they come across a site,
        // which is not usually while they are looking at their feeds.
        Scope = ModuleScope.Any,
        KeyTip = "AF",
        DefaultGesture = "Ctrl+Shift+U",
        AlsoGestures = ["Ctrl+N"],
    };

    public static readonly MailboxCommand Update = new()
    {
        Id = new("feeds.update"),
        Label = "Update Feeds",
        Description = "Read every subscription that is due.",
        Icon = "send-receive",
        Category = "Send & Receive",
        Scope = ModuleScope.Feeds,
        KeyTip = "U",
    };

    public static readonly MailboxCommand UpdateThis = new()
    {
        Id = new("feeds.update.one"),
        Label = "Update This Feed",
        Description = "Read this subscription now, whether or not it is due.",
        Icon = "refresh",
        Category = "Send & Receive",
        Scope = ModuleScope.Feeds,
        KeyTip = "UT",
    };

    public static readonly MailboxCommand MarkAllRead = new()
    {
        Id = new("feeds.markallread"),
        Label = "Mark All as Read",
        Description = "Mark everything showing as read.",
        Icon = "unread",
        Category = "Tags",
        Scope = ModuleScope.Feeds,
        KeyTip = "MR",
    };

    public static readonly MailboxCommand ReadLater = new()
    {
        Id = new("feeds.readlater"),
        Label = "Read Later",
        Description = "Keep this article to come back to.",
        Icon = "flag",
        Category = "Tags",
        Scope = ModuleScope.Feeds,
        KeyTip = "RL",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand OpenOriginal = new()
    {
        Id = new("feeds.open.original"),
        Label = "Open Original",
        Description = "Open the article on the publisher's own site.",
        Icon = "link",
        Category = "Actions",
        Scope = ModuleScope.Feeds,
        KeyTip = "OO",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Delete = new()
    {
        Id = new("feeds.delete"),
        Label = "Delete",
        Description = "Delete the selected article.",
        Icon = "delete",
        Category = "Delete",
        Scope = ModuleScope.Feeds,
        KeyTip = "D",
        DefaultGesture = "Delete",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("feeds.categorize"),
        Label = "Categorize",
        Description = "Put a colour category on the selected article.",
        Icon = "categorize",
        Category = "Tags",
        Scope = ModuleScope.Feeds,
        KeyTip = "C",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand FeedSettings = new()
    {
        Id = new("feeds.settings"),
        Label = "Feed Settings",
        Description = "Rename this feed, file it under a heading, and choose what is downloaded with it.",
        Icon = "settings",
        Category = "Manage",
        Scope = ModuleScope.Feeds,
        KeyTip = "FS",
    };

    public static readonly MailboxCommand Unsubscribe = new()
    {
        Id = new("feeds.unsubscribe"),
        Label = "Unsubscribe",
        Description = "Stop reading this feed. The articles already filed stay where they are.",
        Icon = "remove-feed",
        Category = "Manage",
        Scope = ModuleScope.Feeds,
        KeyTip = "UN",
    };

    public static readonly MailboxCommand Import = new()
    {
        Id = new("feeds.import.opml"),
        Label = "Import Feeds",
        Description = "Bring in a subscription list from another reader, as OPML.",
        Icon = "import",
        Category = "Manage",
        Scope = ModuleScope.Any,
        KeyTip = "IM",
    };

    public static readonly MailboxCommand Export = new()
    {
        Id = new("feeds.export.opml"),
        Label = "Export Feeds",
        Description = "Write the subscription list out as OPML, for another reader to read.",
        Icon = "export",
        Category = "Manage",
        Scope = ModuleScope.Any,
        KeyTip = "EX",
    };

    /// <summary>Every command this module owns, which is what the catalogue registers.</summary>
    public static IReadOnlyList<MailboxCommand> All { get; } =
    [
        Subscribe, Update, UpdateThis, MarkAllRead,
        ReadLater, OpenOriginal, Delete, Categorize,
        FeedSettings, Unsubscribe, Import, Export,
    ];
}
