using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The Calendar module's ribbon: its own tab collection, as every module has.
/// </summary>
/// <remarks>
/// The Simplified bar is transcribed from the calendar captures, whose four rules fall at
/// x = 493, 712, 1190 and 1365 — so four clusters and a rule closing the run, exactly as the
/// mail bar ends. The classic tab is the reference's own groups for this module: New, Go To,
/// Arrange, Manage Calendars, Share, Find.
/// <para>
/// Send/Receive is the mail module's tab unchanged — it is about accounts, not about what is on
/// screen — while View is this module's own, because a calendar has arrangements a message list
/// does not.
/// </para>
/// </remarks>
public static class CalendarRibbonLayout
{
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    /// <summary>The Search People box on the Find cluster, the width the mail bar gives it.</summary>
    private const double SearchPeopleWidth = 110;

    public static RibbonLayout Build() => new()
    {
        Module = MailboxModule.Calendar,

        // The shipped Quick Access Toolbar is the shell's, not the module's: the reference keeps
        // Send/Receive All Folders and Undo there whichever module is open.
        QuickAccess =
        [
            MailCommands.SendReceiveAll.Id,
            MailCommands.Undo.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(CalendarCommands.NewAppointment.Id),
                    RibbonItem.Small(CalendarCommands.NewMeeting.Id, RibbonItemKind.SplitButton),
                    RibbonItem.Sheddable(CalendarCommands.AddFocusTime.Id)),

                Cluster("goto", "Go To",
                    RibbonItem.Small(CalendarCommands.Today.Id),
                    RibbonItem.Sheddable(CalendarCommands.Next7Days.Id),
                    RibbonItem.Launcher(CalendarCommands.GoToDate.Id)),

                Cluster("arrange", "Arrange",
                    RibbonItem.Sheddable(CalendarCommands.DayView.Id),
                    RibbonItem.Sheddable(CalendarCommands.WorkWeekView.Id),
                    RibbonItem.Sheddable(CalendarCommands.WeekView.Id),
                    RibbonItem.Sheddable(CalendarCommands.MonthView.Id),
                    RibbonItem.Sheddable(CalendarCommands.ScheduleView.Id),
                    RibbonItem.Launcher(CalendarCommands.CalendarOptions.Id)),

                Cluster("manage", "Manage Calendars",
                    RibbonItem.Small(CalendarCommands.OpenCalendar.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(CalendarCommands.Share.Id, RibbonItemKind.DropDown))),

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
                    RibbonItem.Sheddable(CalendarCommands.DayView.Id),
                    RibbonItem.Sheddable(CalendarCommands.WorkWeekView.Id),
                    RibbonItem.Sheddable(CalendarCommands.WeekView.Id),
                    RibbonItem.Sheddable(CalendarCommands.MonthView.Id),
                    RibbonItem.Small(CalendarCommands.TimeScale.Id, RibbonItemKind.DropDown)),

                Cluster("colour", "Colour",
                    RibbonItem.Small(CalendarCommands.CalendarColour.Id, RibbonItemKind.DropDown),
                    RibbonItem.Small(CalendarCommands.Overlay.Id)),

                Cluster("layout", "Layout",
                    RibbonItem.Small(CalendarCommands.DailyTaskList.Id, RibbonItemKind.DropDown),
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
                            RibbonItem.Large(CalendarCommands.NewAppointment.Id),
                            RibbonItem.Large(CalendarCommands.NewMeeting.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Large(CalendarCommands.NewItems.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "goto",
                        Label = "Go To",
                        KeyTip = "ZG",
                        CollapsePriority = 5,
                        DialogLauncher = CalendarCommands.GoToDate.Id,
                        Items =
                        [
                            RibbonItem.Large(CalendarCommands.Today.Id),
                            RibbonItem.Large(CalendarCommands.Next7Days.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "arrange",
                        Label = "Arrange",
                        KeyTip = "ZA",
                        CollapsePriority = 1,
                        DialogLauncher = CalendarCommands.CalendarOptions.Id,
                        Items =
                        [
                            RibbonItem.Large(CalendarCommands.DayView.Id),
                            RibbonItem.Large(CalendarCommands.WorkWeekView.Id),
                            RibbonItem.Large(CalendarCommands.WeekView.Id),
                            RibbonItem.Large(CalendarCommands.MonthView.Id),
                            RibbonItem.Large(CalendarCommands.ScheduleView.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "manage",
                        Label = "Manage Calendars",
                        KeyTip = "ZM",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(CalendarCommands.OpenCalendar.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(CalendarCommands.CalendarGroups.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "share",
                        Label = "Share",
                        KeyTip = "ZS",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Small(CalendarCommands.EmailCalendar.Id),
                            RibbonItem.Small(CalendarCommands.Share.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(CalendarCommands.PublishCalendar.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "find",
                        Label = "Find",
                        KeyTip = "ZF",
                        CollapsePriority = 6,
                        Items =
                        [
                            RibbonItem.Field(ViewCommands.SearchPeople.Id, SearchPeopleWidth, "Search People"),
                            RibbonItem.Small(MailCommands.AddressBook.Id),
                        ],
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
                        KeyTip = "ZR",
                        CollapsePriority = 1,
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
                        CollapsePriority = 3,
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
                            RibbonItem.Large(ViewCommands.ResetView.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "arrangement",
                        Label = "Arrangement",
                        KeyTip = "ZA",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(CalendarCommands.DayView.Id),
                            RibbonItem.Large(CalendarCommands.WorkWeekView.Id),
                            RibbonItem.Large(CalendarCommands.WeekView.Id),
                            RibbonItem.Large(CalendarCommands.MonthView.Id),
                            RibbonItem.Large(CalendarCommands.TimeScale.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "colour",
                        Label = "Colour",
                        KeyTip = "ZO",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(CalendarCommands.CalendarColour.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(CalendarCommands.Overlay.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "layout",
                        Label = "Layout",
                        KeyTip = "ZL",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Large(CalendarCommands.DailyTaskList.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ViewCommands.LayoutMenu.Id, RibbonItemKind.DropDown),
                        ],
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
