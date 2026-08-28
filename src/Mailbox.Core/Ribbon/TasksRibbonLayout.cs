using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The Tasks module's ribbon: its own tab collection, as every module has.
/// </summary>
/// <remarks>
/// <b>Only the right-hand two-thirds of this bar has a capture.</b> The Tasks screenshot is taken
/// with the peek open over the left of the window, and what it shows, in order, is Delete, Reply,
/// Reply All, Forward, Mark Complete, Remove from List, Flag Task, Categorize, Private, High
/// Importance, Low Importance and the overflow — so Delete, Respond, Manage Task and Tags are
/// transcribed from it, and the New and Current View clusters are authored from the reference's
/// own Home tab in the order it lists them.
/// <para>
/// Reply, Reply All and Forward are on it because the To-Do List holds flagged mail beside the
/// tasks, exactly as the reference's does. The list carries that mail today; the three commands
/// are declared, placed and not yet wired to the mail module's respond path, and they say so
/// when pressed rather than a bar that does not look like the reference's.
/// </para>
/// </remarks>
public static class TasksRibbonLayout
{
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    public static RibbonLayout Build() => new()
    {
        Module = MailboxModule.Tasks,

        QuickAccess =
        [
            MailCommands.SendReceiveAll.Id,
            MailCommands.Undo.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(TaskCommands.NewTask.Id),
                    RibbonItem.Small(TaskCommands.NewEmail.Id),
                    RibbonItem.Small(TaskCommands.NewItems.Id, RibbonItemKind.DropDown)),

                Cluster("delete", "Delete",
                    RibbonItem.Sheddable(TaskCommands.Delete.Id)),

                // Transcribed: the capture's own order, and the three that keep their words
                // longest are the ones the reference still shows as words at that width.
                Cluster("respond", "Respond",
                    RibbonItem.Sheddable(TaskCommands.Reply.Id),
                    RibbonItem.Sheddable(TaskCommands.ReplyAll.Id),
                    RibbonItem.Sheddable(TaskCommands.Forward.Id)),

                Cluster("manage", "Manage Task",
                    RibbonItem.Sheddable(TaskCommands.MarkComplete.Id),
                    RibbonItem.Sheddable(TaskCommands.RemoveFromList.Id)),

                Cluster("tags", "Tags",
                    RibbonItem.Sheddable(TaskCommands.FollowUp.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(TaskCommands.Categorize.Id, RibbonItemKind.DropDown),
                    RibbonItem.Small(TaskCommands.Private.Id),
                    RibbonItem.Small(TaskCommands.HighImportance.Id),
                    RibbonItem.Small(TaskCommands.LowImportance.Id)),

                Cluster("find", "Find",
                    RibbonItem.Small(MailCommands.Search.Id))),

            ["sendreceive"] = Bar(
                Cluster("sendreceive", "Send & Receive",
                    RibbonItem.Small(MailCommands.SendReceiveAll.Id),
                    RibbonItem.Small(ViewCommands.SendAll.Id),
                    RibbonItem.Small(ViewCommands.UpdateFolder.Id),
                    RibbonItem.Small(ViewCommands.SendReceiveGroups.Id, RibbonItemKind.DropDown)),

                Cluster("download", "Download",
                    RibbonItem.Small(ViewCommands.ShowProgress.Id),
                    RibbonItem.Small(ViewCommands.CancelAll.Id)),

                Cluster("preferences", "Preferences",
                    RibbonItem.Small(MailCommands.WorkOffline.Id))),

            ["view"] = Bar(
                Cluster("currentview", "Current View",
                    RibbonItem.Small(ViewCommands.ChangeView.Id, RibbonItemKind.DropDown),
                    RibbonItem.Small(ViewCommands.ViewSettings.Id, RibbonItemKind.DropDown)),

                Cluster("arrangement", "Arrangement",
                    RibbonItem.Sheddable(TaskCommands.TodoListView.Id),
                    RibbonItem.Sheddable(TaskCommands.SimpleListView.Id),
                    RibbonItem.Sheddable(TaskCommands.DetailedView.Id),
                    RibbonItem.Small(ViewCommands.ReverseSort.Id)),

                Cluster("layout", "Layout",
                    RibbonItem.Small(ViewCommands.LayoutMenu.Id, RibbonItemKind.DropDown))),

            ["help"] = new SimplifiedBar { Groups = [] },
        },

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
                        Items =
                        [
                            RibbonItem.Large(TaskCommands.NewTask.Id),
                            RibbonItem.Large(TaskCommands.NewEmail.Id),
                            RibbonItem.Large(TaskCommands.NewItems.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "delete",
                        Label = "Delete",
                        KeyTip = "ZD",
                        CollapsePriority = 6,
                        Items = [RibbonItem.Large(TaskCommands.Delete.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "respond",
                        Label = "Respond",
                        KeyTip = "ZR",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(TaskCommands.Reply.Id),
                            RibbonItem.Large(TaskCommands.ReplyAll.Id),
                            RibbonItem.Large(TaskCommands.Forward.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "manage",
                        Label = "Manage Task",
                        KeyTip = "ZM",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Large(TaskCommands.MarkComplete.Id),
                            RibbonItem.Large(TaskCommands.RemoveFromList.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "view",
                        Label = "Current View",
                        KeyTip = "ZV",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(TaskCommands.TodoListView.Id),
                            RibbonItem.Large(TaskCommands.SimpleListView.Id),
                            RibbonItem.Large(TaskCommands.DetailedView.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        KeyTip = "ZT",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(TaskCommands.FollowUp.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(TaskCommands.Categorize.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(TaskCommands.Private.Id),
                            RibbonItem.Large(TaskCommands.HighImportance.Id),
                            RibbonItem.Large(TaskCommands.LowImportance.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "find",
                        Label = "Find",
                        KeyTip = "ZF",
                        CollapsePriority = 5,
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
                        CollapsePriority = 5,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.SendReceiveAll.Id),
                            RibbonItem.Large(ViewCommands.SendAll.Id),
                            RibbonItem.Large(ViewCommands.UpdateFolder.Id),
                            RibbonItem.Large(ViewCommands.SendReceiveGroups.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "download",
                        Label = "Download",
                        KeyTip = "ZD",
                        CollapsePriority = 3,
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
                        Id = "currentview",
                        Label = "Current View",
                        KeyTip = "ZC",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(ViewCommands.ChangeView.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ViewCommands.ViewSettings.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "arrangement",
                        Label = "Arrangement",
                        KeyTip = "ZR",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(TaskCommands.TodoListView.Id),
                            RibbonItem.Large(TaskCommands.SimpleListView.Id),
                            RibbonItem.Large(TaskCommands.DetailedView.Id),
                            RibbonItem.Large(ViewCommands.ReverseSort.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "layout",
                        Label = "Layout",
                        KeyTip = "ZL",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(ViewCommands.LayoutMenu.Id, RibbonItemKind.DropDown)],
                    },
                ],
            },

            new RibbonTab
            {
                Id = "help",
                Label = "Help",
                KeyTip = "Y",
                Groups = [],
            },
        ],
    };
}
