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

    public static readonly MailboxCommand SaveToBoard = new()
    {
        Id = new("feeds.board.save"),
        Label = "Save to Board",
        Description = "Keep this article in a named collection. It stays in its feed as well.",
        Icon = "bookmark",
        Category = "Boards",
        Scope = ModuleScope.Feeds,
        KeyTip = "SB",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand RemoveFromBoard = new()
    {
        Id = new("feeds.board.remove"),
        Label = "Take Off Board",
        Description = "Take this article off the board you are reading. The article itself stays.",
        Icon = "remove-feed",
        Category = "Boards",
        Scope = ModuleScope.Feeds,
        KeyTip = "TB",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand SaveLink = new()
    {
        Id = new("feeds.board.link"),
        Label = "Save a Link",
        Description = "Put any web address on a board, whether or not you subscribe to the site.",
        Icon = "link",
        Category = "Boards",

        // Every module: a reader comes across an address while they are reading their mail at
        // least as often as while they are reading their feeds, and this is the one command in
        // the module that needs nothing selected.
        Scope = ModuleScope.Any,
        KeyTip = "SL",
    };

    public static readonly MailboxCommand Boards = new()
    {
        Id = new("feeds.boards"),
        Label = "Boards",
        Description = "Make, rename and remove the collections articles are saved into.",
        Icon = "bookmark",
        Category = "Boards",
        Scope = ModuleScope.Feeds,
        KeyTip = "BD",
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

    public static readonly MailboxCommand Newsletters = new()
    {
        Id = new("feeds.newsletters"),
        Label = "Newsletters",
        Description = "Read the newsletters already arriving in your mail as articles.",
        Icon = "mail",
        Category = "New",

        // Every module: this is about mail, and a reader thinking about it is as likely to be
        // looking at their inbox as at their feeds.
        Scope = ModuleScope.Any,
        KeyTip = "NL",
    };

    public static readonly MailboxCommand Mute = new()
    {
        Id = new("feeds.mute"),
        Label = "Mute Filters",
        Description = "Words and phrases whose articles are not delivered.",
        Icon = "ignore",
        Category = "Manage",
        Scope = ModuleScope.Feeds,
        KeyTip = "MF",
    };

    public static readonly MailboxCommand MuteThis = new()
    {
        Id = new("feeds.mute.this"),
        Label = "Mute This",
        Description = "Stop delivering articles whose headline carries the selected article's subject.",
        Icon = "ignore",
        Category = "Manage",
        Scope = ModuleScope.Feeds,
        KeyTip = "MT",
        RequiresSelection = true,
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
        Subscribe, Newsletters, Update, UpdateThis, MarkAllRead,
        ReadLater, SaveToBoard, RemoveFromBoard, SaveLink, Boards,
        OpenOriginal, Delete, Categorize,
        FeedSettings, Unsubscribe, Mute, MuteThis, Import, Export,
    ];
}
