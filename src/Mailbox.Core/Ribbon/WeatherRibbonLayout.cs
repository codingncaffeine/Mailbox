using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The Weather module's ribbon.
/// </summary>
/// <remarks>
/// Authored rather than transcribed: the reference has no Weather module, only a strip along the
/// top of its calendar. The groups are in the order a reader reaches for them — add a place, put
/// the list in order, bring it up to date, choose the units — and the Send/Receive and View tabs
/// are the shell's own, so a reader who has learnt them elsewhere finds them here.
/// </remarks>
public static class WeatherRibbonLayout
{
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    public static RibbonLayout Build() => new()
    {
        Module = MailboxModule.Weather,

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
                        CollapsePriority = 5,
                        Items = [RibbonItem.Large(WeatherCommands.AddPlace.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "location",
                        Label = "Location",
                        KeyTip = "ZL",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(WeatherCommands.MakeHome.Id),
                            RibbonItem.Large(WeatherCommands.MoveUp.Id),
                            RibbonItem.Large(WeatherCommands.MoveDown.Id),
                            RibbonItem.Large(WeatherCommands.RemovePlace.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "update",
                        Label = "Update",
                        KeyTip = "ZU",
                        CollapsePriority = 3,
                        Items = [RibbonItem.Large(WeatherCommands.Update.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "units",
                        Label = "Units",
                        KeyTip = "ZT",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(WeatherCommands.Celsius.Id)],
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
                            RibbonItem.Large(WeatherCommands.Update.Id),
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
            WeatherCommands.Update.Id,
            MailCommands.Undo.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(WeatherCommands.AddPlace.Id)),

                Cluster("location", "Location",
                    RibbonItem.Small(WeatherCommands.MakeHome.Id),
                    RibbonItem.Sheddable(WeatherCommands.MoveUp.Id),
                    RibbonItem.Sheddable(WeatherCommands.MoveDown.Id),
                    RibbonItem.Sheddable(WeatherCommands.RemovePlace.Id)),

                Cluster("update", "Update",
                    RibbonItem.Small(WeatherCommands.Update.Id)),

                Cluster("units", "Units",
                    RibbonItem.Small(WeatherCommands.Celsius.Id)),

                Cluster("find", "Find",
                    RibbonItem.Small(MailCommands.Search.Id))),

            ["sendreceive"] = Bar(
                Cluster("sendreceive", "Send & Receive",
                    RibbonItem.Small(MailCommands.SendReceiveAll.Id),
                    RibbonItem.Small(WeatherCommands.Update.Id),
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
