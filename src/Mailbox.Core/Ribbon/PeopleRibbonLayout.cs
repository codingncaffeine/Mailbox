using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The People module's ribbon: its own tab collection, as every module has.
/// </summary>
/// <remarks>
/// <b>Only the right-hand half of this bar has a capture.</b> The People screenshot is taken with
/// the peek pane open over the left of the window, and what it shows is Move, Send to OneNote,
/// Share Contacts, Categorize, Follow Up, Private, the Search People box and the address-book
/// button — so the Tags and Find clusters and the tail of Actions are transcribed, and New,
/// Delete, Communicate and Current View are authored from the reference's own Home tab in the
/// order it lists them. Where a number here is a decision rather than a measurement it says so.
/// <para>
/// <b>Send to OneNote is not here.</b> It reaches a service that is out of scope, and
/// a button that cannot do what it says is worse than one that is absent — the same call the
/// mail bar made.
/// </para>
/// </remarks>
public static class PeopleRibbonLayout
{
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    /// <summary>The Search People box, the width the mail bar gives its own search.</summary>
    private const double SearchPeopleWidth = 110;

    public static RibbonLayout Build() => new()
    {
        Module = MailboxModule.People,

        // The shipped Quick Access Toolbar is the shell's, not the module's.
        QuickAccess =
        [
            MailCommands.SendReceiveAll.Id,
            MailCommands.Undo.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            // Read off the reference's own bar at 1679 wide, which shows New Contact ⌄, Delete,
            // Move ⌄, Share Contacts ⌄, Categorize ⌄, Follow Up ⌄, Private, the Search People box
            // and the address book — all of them labelled — and nothing else. Communicate,
            // Current View and Forward are not on its Simplified row at all: they are in the
            // Classic tab and so in the "…", which is where its own bar keeps them. Putting them
            // here instead cost every other label, the bar shedding words to make room for icons
            // the reference never shows.
            //
            // Send to OneNote sits between Move and Share Contacts there. It reaches a service
            // that is out of scope, and a button that cannot do what it says is worse than one
            // that is absent — the same call the mail bar made.
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(PeopleCommands.NewContact.Id, RibbonItemKind.SplitButton)
                        with { ChevronCommand = PeopleCommands.NewItems.Id }),

                Cluster("delete", "Delete",
                    RibbonItem.Small(PeopleCommands.Delete.Id)),

                Cluster("actions", "Actions",
                    RibbonItem.Sheddable(PeopleCommands.MoveTo.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(PeopleCommands.ShareContacts.Id, RibbonItemKind.DropDown)),

                Cluster("tags", "Tags",
                    RibbonItem.Sheddable(PeopleCommands.Categorize.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(PeopleCommands.FollowUp.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(PeopleCommands.Private.Id)),

                // The address book is an icon with no label beside the box, as the reference
                // draws it — the box says what the pair is for.
                Cluster("find", "Find",
                    RibbonItem.Field(ViewCommands.SearchPeople.Id, SearchPeopleWidth, "Search People"),
                    RibbonItem.Glyph(MailCommands.AddressBook.Id))),

            // Send/Receive is the mail module's tab unchanged: it is about accounts, not about
            // what is on screen.
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
                    RibbonItem.Sheddable(PeopleCommands.PeopleView.Id),
                    RibbonItem.Sheddable(PeopleCommands.CardView.Id),
                    RibbonItem.Sheddable(PeopleCommands.PhoneView.Id),
                    RibbonItem.Sheddable(PeopleCommands.ListView.Id),
                    RibbonItem.Small(ViewCommands.ReverseSort.Id)),

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
                        CollapsePriority = 7,
                        Items =
                        [
                            RibbonItem.Large(PeopleCommands.NewContact.Id),
                            RibbonItem.Large(PeopleCommands.NewContactGroup.Id),
                            RibbonItem.Large(PeopleCommands.NewItems.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "delete",
                        Label = "Delete",
                        KeyTip = "ZD",
                        CollapsePriority = 6,
                        Items = [RibbonItem.Large(PeopleCommands.Delete.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "communicate",
                        Label = "Communicate",
                        KeyTip = "ZC",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(PeopleCommands.EmailContact.Id),
                            RibbonItem.Large(PeopleCommands.MeetContact.Id),
                            RibbonItem.Large(PeopleCommands.MoreCommunicate.Id, RibbonItemKind.DropDown),
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
                            RibbonItem.Large(PeopleCommands.PeopleView.Id),
                            RibbonItem.Large(PeopleCommands.BusinessCardView.Id),
                            RibbonItem.Large(PeopleCommands.CardView.Id),
                            RibbonItem.Large(PeopleCommands.PhoneView.Id),
                            RibbonItem.Large(PeopleCommands.ListView.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "actions",
                        Label = "Actions",
                        KeyTip = "ZA",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Large(PeopleCommands.MoveTo.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(PeopleCommands.MailMerge.Id),
                            RibbonItem.Large(PeopleCommands.ShareContacts.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(PeopleCommands.ForwardContact.Id),
                            RibbonItem.Large(PeopleCommands.OpenSharedContacts.Id),
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
                            RibbonItem.Large(PeopleCommands.Categorize.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(PeopleCommands.FollowUp.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(PeopleCommands.Private.Id),
                            RibbonItem.Large(PeopleCommands.Favourite.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "find",
                        Label = "Find",
                        KeyTip = "ZF",
                        CollapsePriority = 5,
                        Items =
                        [
                            RibbonItem.Field(ViewCommands.SearchPeople.Id, SearchPeopleWidth, "Search People"),
                            RibbonItem.Large(MailCommands.AddressBook.Id),
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
                            RibbonItem.Large(PeopleCommands.PeopleView.Id),
                            RibbonItem.Large(PeopleCommands.CardView.Id),
                            RibbonItem.Large(PeopleCommands.PhoneView.Id),
                            RibbonItem.Large(PeopleCommands.ListView.Id),
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
