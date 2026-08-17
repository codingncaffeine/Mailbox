using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The Notes module's ribbon: its own tab collection, as every module has.
/// </summary>
/// <remarks>
/// <b>No capture of this module exists</b>, so this bar is authored from the reference's own Home
/// tab in the order it lists the groups — New, Delete, Current View, Actions, Tags, Find — rather
/// than transcribed from a picture. It is the shortest bar of the six because a note has one
/// field: what is written on it.
/// </remarks>
public static class NotesRibbonLayout
{
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    public static RibbonLayout Build() => new()
    {
        Module = MailboxModule.Notes,

        QuickAccess =
        [
            MailCommands.SendReceiveAll.Id,
            MailCommands.Undo.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(NoteCommands.NewNote.Id),
                    RibbonItem.Small(NoteCommands.NewItems.Id, RibbonItemKind.DropDown)),

                Cluster("delete", "Delete",
                    RibbonItem.Sheddable(NoteCommands.Delete.Id)),

                Cluster("view", "Current View",
                    RibbonItem.Sheddable(NoteCommands.IconsView.Id),
                    RibbonItem.Sheddable(NoteCommands.NotesListView.Id),
                    RibbonItem.Sheddable(NoteCommands.LastSevenDaysView.Id)),

                Cluster("actions", "Actions",
                    RibbonItem.Sheddable(NoteCommands.Forward.Id),
                    RibbonItem.Small(NoteCommands.MoveTo.Id, RibbonItemKind.DropDown)),

                Cluster("tags", "Tags",
                    RibbonItem.Sheddable(NoteCommands.Categorize.Id, RibbonItemKind.DropDown)),

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
                    RibbonItem.Sheddable(NoteCommands.IconsView.Id),
                    RibbonItem.Sheddable(NoteCommands.NotesListView.Id),
                    RibbonItem.Sheddable(NoteCommands.LastSevenDaysView.Id),
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
                        CollapsePriority = 6,
                        Items =
                        [
                            RibbonItem.Large(NoteCommands.NewNote.Id),
                            RibbonItem.Large(NoteCommands.NewItems.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "delete",
                        Label = "Delete",
                        KeyTip = "ZD",
                        CollapsePriority = 5,
                        Items = [RibbonItem.Large(NoteCommands.Delete.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "view",
                        Label = "Current View",
                        KeyTip = "ZV",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(NoteCommands.IconsView.Id),
                            RibbonItem.Large(NoteCommands.NotesListView.Id),
                            RibbonItem.Large(NoteCommands.LastSevenDaysView.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "actions",
                        Label = "Actions",
                        KeyTip = "ZA",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(NoteCommands.Forward.Id),
                            RibbonItem.Large(NoteCommands.MoveTo.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        KeyTip = "ZT",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(NoteCommands.Categorize.Id, RibbonItemKind.DropDown)],
                    },

                    new RibbonGroup
                    {
                        Id = "find",
                        Label = "Find",
                        KeyTip = "ZF",
                        CollapsePriority = 4,
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
                        CollapsePriority = 3,
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
                            RibbonItem.Large(NoteCommands.IconsView.Id),
                            RibbonItem.Large(NoteCommands.NotesListView.Id),
                            RibbonItem.Large(NoteCommands.LastSevenDaysView.Id),
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
