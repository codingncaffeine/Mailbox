using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The Feeds module's ribbon.
/// </summary>
/// <remarks>
/// Authored rather than transcribed: the reference has no Feeds module, so there is nothing to
/// match. The groups are in the order a reader reaches for them — subscribe, read what came in,
/// keep one for later, and manage the list — and the Send/Receive and View tabs are the shell's
/// own, so a reader who has learnt them elsewhere finds them here.
/// </remarks>
public static class FeedsRibbonLayout
{
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    public static RibbonLayout Build() => new()
    {
        Module = MailboxModule.Feeds,


        Tabs =
        [
            new RibbonTab
            {
                Id = "file",
                Label = "File",
                KeyTip = "F",
                IsBackstage = true,
                Groups = [],
            },

            new RibbonTab
            {
                Id = "home",
                Label = "Home",
                KeyTip = "H",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "new",
                        Label = "New",
                        KeyTip = "ZN",
                        CollapsePriority = 7,
                        Items = [RibbonItem.Large(FeedCommands.Subscribe.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "update",
                        Label = "Update",
                        KeyTip = "ZU",
                        CollapsePriority = 6,
                        Items =
                        [
                            RibbonItem.Large(FeedCommands.Update.Id),
                            RibbonItem.Large(FeedCommands.UpdateThis.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "delete",
                        Label = "Delete",
                        KeyTip = "ZD",
                        CollapsePriority = 5,
                        Items = [RibbonItem.Large(FeedCommands.Delete.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        KeyTip = "ZT",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(FeedCommands.ReadLater.Id),
                            RibbonItem.Large(FeedCommands.MarkAllRead.Id),
                            RibbonItem.Large(FeedCommands.Categorize.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "actions",
                        Label = "Actions",
                        KeyTip = "ZA",
                        CollapsePriority = 3,
                        Items = [RibbonItem.Large(FeedCommands.OpenOriginal.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "manage",
                        Label = "Manage",
                        KeyTip = "ZM",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Large(FeedCommands.FeedSettings.Id),
                            RibbonItem.Large(FeedCommands.Unsubscribe.Id),
                            RibbonItem.Large(FeedCommands.Import.Id),
                            RibbonItem.Large(FeedCommands.Export.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "find",
                        Label = "Find",
                        KeyTip = "ZF",
                        CollapsePriority = 1,
                        Items = [RibbonItem.Large(MailCommands.Search.Id)],
                    },
                ],
            },

            new RibbonTab
            {
                Id = "sendreceive",
                Label = "Send / Receive",
                KeyTip = "S",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "sendreceive",
                        Label = "Send & Receive",
                        KeyTip = "ZS",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.SendReceiveAll.Id),
                            RibbonItem.Large(FeedCommands.Update.Id),
                            RibbonItem.Large(FeedCommands.UpdateThis.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "download",
                        Label = "Download",
                        KeyTip = "ZW",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Large(ViewCommands.ShowProgress.Id),
                            RibbonItem.Large(ViewCommands.CancelAll.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "preferences",
                        Label = "Preferences",
                        KeyTip = "ZP",
                        CollapsePriority = 1,
                        Items = [RibbonItem.Large(MailCommands.WorkOffline.Id)],
                    },
                ],
            },

            new RibbonTab
            {
                Id = "view",
                Label = "View",
                KeyTip = "V",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "layout",
                        Label = "Layout",
                        KeyTip = "ZL",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(ViewCommands.LayoutMenu.Id, RibbonItemKind.DropDown)],
                    },

                    new RibbonGroup
                    {
                        Id = "window",
                        Label = "Window",
                        KeyTip = "ZO",
                        CollapsePriority = 1,
                        Items = [RibbonItem.Large(ViewCommands.Refresh.Id)],
                    },
                ],
            },
        ],

        QuickAccess =
        [
            FeedCommands.Update.Id,
            MailCommands.Undo.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(FeedCommands.Subscribe.Id)),

                Cluster("update", "Update",
                    RibbonItem.Small(FeedCommands.Update.Id),
                    RibbonItem.Sheddable(FeedCommands.UpdateThis.Id)),

                Cluster("delete", "Delete",
                    RibbonItem.Sheddable(FeedCommands.Delete.Id)),

                Cluster("tags", "Tags",
                    RibbonItem.Sheddable(FeedCommands.ReadLater.Id),
                    RibbonItem.Sheddable(FeedCommands.MarkAllRead.Id),
                    RibbonItem.Sheddable(FeedCommands.Categorize.Id, RibbonItemKind.DropDown)),

                Cluster("actions", "Actions",
                    RibbonItem.Sheddable(FeedCommands.OpenOriginal.Id)),

                Cluster("manage", "Manage",
                    RibbonItem.Sheddable(FeedCommands.FeedSettings.Id),
                    RibbonItem.Sheddable(FeedCommands.Unsubscribe.Id),
                    RibbonItem.Sheddable(FeedCommands.Import.Id),
                    RibbonItem.Sheddable(FeedCommands.Export.Id)),

                Cluster("find", "Find",
                    RibbonItem.Small(MailCommands.Search.Id))),

            ["sendreceive"] = Bar(
                Cluster("sendreceive", "Send & Receive",
                    RibbonItem.Small(MailCommands.SendReceiveAll.Id),
                    RibbonItem.Small(FeedCommands.Update.Id),
                    RibbonItem.Small(FeedCommands.UpdateThis.Id),
                    RibbonItem.Small(ViewCommands.ShowProgress.Id),
                    RibbonItem.Small(ViewCommands.CancelAll.Id)),

                Cluster("preferences", "Preferences",
                    RibbonItem.Small(MailCommands.WorkOffline.Id))),

            ["view"] = Bar(
                Cluster("layout", "Layout",
                    RibbonItem.Small(ViewCommands.LayoutMenu.Id, RibbonItemKind.DropDown)),

                Cluster("refresh", "Window",
                    RibbonItem.Small(ViewCommands.Refresh.Id))),
        },
    };
}
