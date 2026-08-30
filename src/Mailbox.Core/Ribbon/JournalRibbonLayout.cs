using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The Journal module's ribbon: its own tab collection, as every module has.
/// </summary>
/// <remarks>
/// <b>No capture of this module exists</b> — the reference hides it behind Ctrl+8 — so this bar is
/// authored from its own Home tab in the order it lists the groups: New, Delete, Actions, Tags,
/// Current View, Find. The Go To group and the timeline's three scales are on the View tab, where
/// the calendar's equivalents are, rather than on the toolbar the drawn view could have grown.
/// </remarks>
public static class JournalRibbonLayout
{
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    public static RibbonLayout Build() => new()
    {
        Module = MailboxModule.Journal,

        QuickAccess =
        [
            MailCommands.SendReceiveAll.Id,
            MailCommands.Undo.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(JournalCommands.NewEntry.Id),
                    RibbonItem.Small(JournalCommands.NewItems.Id, RibbonItemKind.DropDown)),

                Cluster("delete", "Delete",
                    RibbonItem.Sheddable(JournalCommands.Delete.Id)),

                Cluster("view", "Current View",
                    RibbonItem.Sheddable(JournalCommands.TimelineView.Id),
                    RibbonItem.Sheddable(JournalCommands.ByContactView.Id),
                    RibbonItem.Sheddable(JournalCommands.ByCategoryView.Id),
                    RibbonItem.Sheddable(JournalCommands.EntryListView.Id),
                    RibbonItem.Sheddable(JournalCommands.PhoneCallsView.Id),
                    RibbonItem.Sheddable(JournalCommands.LastSevenDaysView.Id)),

                Cluster("actions", "Actions",
                    RibbonItem.Sheddable(JournalCommands.Forward.Id)),

                Cluster("tags", "Tags",
                    RibbonItem.Sheddable(JournalCommands.Categorize.Id, RibbonItemKind.DropDown)),

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
                Cluster("goto", "Go To",
                    RibbonItem.Sheddable(JournalCommands.Today.Id),
                    RibbonItem.Small(JournalCommands.Back.Id),
                    RibbonItem.Small(JournalCommands.Forwards.Id)),

                Cluster("scale", "Arrangement",
                    RibbonItem.Sheddable(JournalCommands.DayScale.Id),
                    RibbonItem.Sheddable(JournalCommands.WeekScale.Id),
                    RibbonItem.Sheddable(JournalCommands.MonthScale.Id)),

                Cluster("currentview", "Current View",
                    RibbonItem.Small(ViewCommands.ChangeView.Id, RibbonItemKind.DropDown),
                    RibbonItem.Small(ViewCommands.ViewSettings.Id, RibbonItemKind.DropDown)),

                Cluster("layout", "Layout",
                    RibbonItem.Small(ViewCommands.LayoutMenu.Id, RibbonItemKind.DropDown))),

            ["help"] = DefaultRibbonLayouts.HelpBar,
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
                            RibbonItem.Large(JournalCommands.NewEntry.Id),
                            RibbonItem.Large(JournalCommands.NewItems.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "delete",
                        Label = "Delete",
                        KeyTip = "ZD",
                        CollapsePriority = 5,
                        Items = [RibbonItem.Large(JournalCommands.Delete.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "view",
                        Label = "Current View",
                        KeyTip = "ZV",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(JournalCommands.TimelineView.Id),
                            RibbonItem.Large(JournalCommands.ByContactView.Id),
                            RibbonItem.Large(JournalCommands.ByCategoryView.Id),
                            RibbonItem.Large(JournalCommands.EntryListView.Id),
                            RibbonItem.Large(JournalCommands.PhoneCallsView.Id),
                            RibbonItem.Large(JournalCommands.LastSevenDaysView.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "actions",
                        Label = "Actions",
                        KeyTip = "ZA",
                        CollapsePriority = 3,
                        Items = [RibbonItem.Large(JournalCommands.Forward.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        KeyTip = "ZT",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(JournalCommands.Categorize.Id, RibbonItemKind.DropDown)],
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
                        Id = "goto",
                        Label = "Go To",
                        KeyTip = "ZG",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(JournalCommands.Today.Id),
                            RibbonItem.Large(JournalCommands.Back.Id),
                            RibbonItem.Large(JournalCommands.Forwards.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "scale",
                        Label = "Arrangement",
                        KeyTip = "ZR",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(JournalCommands.DayScale.Id),
                            RibbonItem.Large(JournalCommands.WeekScale.Id),
                            RibbonItem.Large(JournalCommands.MonthScale.Id),
                        ],
                    },

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
                        Id = "layout",
                        Label = "Layout",
                        KeyTip = "ZL",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(ViewCommands.LayoutMenu.Id, RibbonItemKind.DropDown)],
                    },
                ],
            },

            // Help is the application's tab rather than the module's — the same tab in every
            // module, as the reference draws it — so both halves come from the shell's copy
            // rather than being declared empty here and never filled.
            new RibbonTab
            {
                Id = "help",
                Label = "Help",
                KeyTip = "Y",
                Groups = DefaultRibbonLayouts.HelpGroups,
            },
        ],
    };
}
