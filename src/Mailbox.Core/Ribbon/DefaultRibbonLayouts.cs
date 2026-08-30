using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The shipped ribbon layouts, authored to the reference application parity.
/// </summary>
/// <remarks>
/// This is the load-bearing half of rule 5. Everything Mailbox can do is in the command
/// catalogue; what makes first run an exact clone is that <em>this document</em> places only
/// the commands the reference application places. Snooze, View Source and the rest are absent here and present
/// everywhere else — searchable, bindable, and one drag away in Customize Ribbon.
/// <para>
/// Group order, item order and sizes follow the reference's Home tab: New | Delete | Respond |
/// Quick Steps | Move | Tags | Find.
/// </para>
/// </remarks>
public static class DefaultRibbonLayouts
{
    /// <summary>
    /// The compose window's ribbon — its own host with its own tab collection.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Mail"/> rather than another tab on it: the reference opens a
    /// message in its own window with File, Message, Insert, Options, Format Text, Review and
    /// Help, and none of the main window's tabs. Transcription notes are on
    /// <see cref="ComposeRibbonLayout"/>.
    /// </remarks>
    public static RibbonLayout Compose { get; } = ComposeRibbonLayout.Build();

    /// <summary>
    /// What the Quick Access Toolbar's customize flyout offers, ticked when placed.
    /// </summary>
    /// <remarks>
    /// A short curated list, as the reference has, rather than the whole catalogue — the flyout
    /// is the quick way to place a common command, and Customize Ribbon is the way to reach
    /// everything else. Order is the reference's own, not alphabetical.
    /// </remarks>
    public static IReadOnlyList<CommandId> QuickAccessCandidates { get; } =
    [
        MailCommands.NewEmail.Id,
        MailCommands.SendReceiveAll.Id,
        MailCommands.Undo.Id,
        ViewCommands.Redo.Id,
        MailCommands.Delete.Id,
        MailCommands.Reply.Id,
        MailCommands.ReplyAll.Id,
        MailCommands.Forward.Id,
        MailCommands.MoveTo.Id,
        MailCommands.AddressBook.Id,
        MailCommands.WorkOffline.Id,
    ];

    /// <summary>
    /// The boxed Quick Steps entry on the Home row, measured from x=470 to x=576.
    /// </summary>
    private const double QuickStepBoxWidth = 106;

    /// <summary>The Search People input beside it, measured from x=840 to x=950.</summary>
    private const double SearchPeopleWidth = 110;

    /// <summary>A cluster of the Simplified bar, named as Customize Ribbon names it.</summary>
    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    public static RibbonLayout Mail { get; } = new()
    {
        Module = MailboxModule.Mail,
        // the reference's shipped QAT: Send/Receive All Folders, then Undo.
        QuickAccess =
        [
            MailCommands.SendReceiveAll.Id,
            MailCommands.Undo.Id,
        ],

        // The Simplified ribbon, which is what the reference application ships by default.
        // Transcribed left to right from a running copy — the reference curates a shorter,
        // reordered set here rather than flattening the classic groups, and groups it
        // differently too: Delete and Move are one cluster called "Move & Delete". The group
        // names are the ones Customize Ribbon lists.
        Simplified = new Dictionary<string, SimplifiedBar>
        {
            // Transcribed from home.png, with the cluster rules measured at x = 191, 341, 457,
            // 596, 831, 1049, 1093 and 1289. Quick Steps is boxed and Search People is a real
            // input, not a button.
            //
            // Which entries carry text is a decision the bar makes, not the layout: the
            // reference shows a label wherever there is room and sheds them right to left when
            // there is not — the Respond and Tags labels are on show in a wide window and gone
            // by home.png's 1447, which is why this row was first transcribed as icon-only.
            // SimplifiedRowPanel does the shedding, so what is asked for here is the full state.
            // ShowLabel = false is reserved for entries the reference never labels at any
            // width: a formatting run, and the glyph-only Delete and Move stacks.
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(MailCommands.NewEmail.Id, RibbonItemKind.SplitButton)
                        with { ChevronCommand = MailCommands.NewItems.Id }),

                Cluster("movedelete", "Move & Delete",
                    RibbonItem.Glyph(MailCommands.Delete.Id, RibbonItemKind.SplitButton),
                    RibbonItem.Glyph(MailCommands.Archive.Id),
                    RibbonItem.Glyph(MailCommands.MoveTo.Id, RibbonItemKind.SplitButton)),

                Cluster("respond", "Respond",
                    RibbonItem.Sheddable(MailCommands.Reply.Id),
                    RibbonItem.Sheddable(MailCommands.ReplyAll.Id),
                    RibbonItem.Sheddable(MailCommands.Forward.Id)),

                Cluster("quicksteps", "Quick Steps",
                    RibbonItem.Boxed(ViewCommands.MoveToQuick.Id, QuickStepBoxWidth)),

                Cluster("tags", "Tags",
                    RibbonItem.Small(MailCommands.Unread.Id),
                    RibbonItem.Sheddable(MailCommands.Categorize.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(MailCommands.FollowUp.Id, RibbonItemKind.DropDown)),

                Cluster("find", "Find",
                    RibbonItem.Field(ViewCommands.SearchPeople.Id, SearchPeopleWidth, "Search People"),
                    RibbonItem.Glyph(MailCommands.AddressBook.Id),
                    RibbonItem.Glyph(MailCommands.FilterEmail.Id, RibbonItemKind.DropDown)),

                Cluster("apps", "Apps",
                    RibbonItem.Glyph(ViewCommands.Apps.Id)),

                Cluster("sendreceivegroup", "Send/Receive",
                    RibbonItem.Small(MailCommands.SendReceiveAll.Id))),

            // Rules measured at x = 655, 896 and 1019: Work Offline is its own cluster, not the
            // tail of the download one.
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

            // Rules measured at x = 324, 568 and 977. Use Tighter Spacing opens the Layout
            // cluster rather than closing the Arrangement one, which the flat transcription
            // had the wrong side of.
            ["view"] = Bar(
                Cluster("currentview", "Current View",
                    RibbonItem.Small(ViewCommands.ChangeView.Id, RibbonItemKind.DropDown),
                    RibbonItem.Small(ViewCommands.ViewSettings.Id, RibbonItemKind.DropDown)),

                Cluster("arrangement", "Arrangement",
                    RibbonItem.Small(ViewCommands.ArrangeBy.Id, RibbonItemKind.DropDown),
                    RibbonItem.Small(ViewCommands.ReverseSort.Id)),

                Cluster("layout", "Layout",
                    RibbonItem.Small(ViewCommands.TighterSpacing.Id),
                    RibbonItem.Small(ViewCommands.LayoutMenu.Id, RibbonItemKind.DropDown),
                    RibbonItem.Small(ViewCommands.ImmersiveReader.Id))),

            // No Folder row: the Simplified capture's tab strip does not carry that tab at all
            // (see RibbonTab.ClassicOnly), so there is nothing for a row to fill.

            // Transcribed from the Simplified capture: seven entries, then the cluster's own
            // "…", and Get Diagnostics is not on the row — the overflow is where it is.
            ["help"] = new SimplifiedBar
            {
                // No rule closing the row: the bar draws its own "…" behind one after the last
                // cluster, and that is the single "…" the capture shows after Support Tool.
                TrailingRule = false,
                Groups =
                [
                    Cluster("help", "Help",
                        RibbonItem.Small(ViewCommands.Help.Id),
                        RibbonItem.Small(ViewCommands.ContactSupport.Id),
                        RibbonItem.Small(ViewCommands.Feedback.Id),
                        RibbonItem.Small(ViewCommands.SuggestFeature.Id),
                        RibbonItem.Small(ViewCommands.ShowTraining.Id),
                        RibbonItem.Small(ViewCommands.WhatsNew.Id),
                        RibbonItem.Small(ViewCommands.SupportTool.Id)),
                ],
            },
        },
        Tabs =
        [
            // Leftmost, ahead of Home, and it opens the Backstage rather than a ribbon.
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
                            RibbonItem.Large(MailCommands.NewEmail.Id),
                            RibbonItem.Large(MailCommands.NewItems.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "delete",
                        Label = "Delete",
                        KeyTip = "ZD",
                        CollapsePriority = 4,
                        // The stack is icon-only; only Delete and Archive carry labels.
                        Items =
                        [
                            RibbonItem.Glyph(MailCommands.Ignore.Id),
                            RibbonItem.Glyph(MailCommands.CleanUp.Id, RibbonItemKind.DropDown),
                            RibbonItem.Glyph(MailCommands.Junk.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(MailCommands.Delete.Id),
                            RibbonItem.Large(MailCommands.Archive.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "respond",
                        Label = "Respond",
                        KeyTip = "ZR",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.Reply.Id),
                            RibbonItem.Large(MailCommands.ReplyAll.Id),
                            RibbonItem.Large(MailCommands.Forward.Id),
                            RibbonItem.Glyph(MailCommands.Meeting.Id),
                            RibbonItem.Glyph(MailCommands.MoreRespond.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    // the reference's Quick Steps is a gallery: a bordered box listing the saved
                    // steps, not a button. Its default entries are Move to, To Manager and
                    // Team Email.
                    new RibbonGroup
                    {
                        Id = "quicksteps",
                        Label = "Quick Steps",
                        KeyTip = "ZQ",
                        CollapsePriority = 6,
                        IsGallery = true,
                        DialogLauncher = MailCommands.QuickSteps.Id,
                        Items =
                        [
                            RibbonItem.Small(ViewCommands.MoveToQuick.Id),
                            RibbonItem.Small(ViewCommands.ToManager.Id),
                            RibbonItem.Small(ViewCommands.TeamEmail.Id),
                        ],
                    },

                    // the reference application stacks Move as small buttons, not large ones. Its third entry is
                    // OneNote, which is vendor cloud integration and therefore out of scope.
                    new RibbonGroup
                    {
                        Id = "move",
                        Label = "Move",
                        KeyTip = "ZM",
                        CollapsePriority = 5,
                        Items =
                        [
                            RibbonItem.Small(MailCommands.MoveTo.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(MailCommands.Rules.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        KeyTip = "ZT",
                        CollapsePriority = 3,
                        DialogLauncher = MailCommands.FollowUp.Id,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.Unread.Id),
                            RibbonItem.Small(MailCommands.Categorize.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(MailCommands.FollowUp.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "find",
                        Label = "Find",
                        KeyTip = "ZF",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Small(ViewCommands.SearchPeople.Id, RibbonItemKind.TextBox),
                            RibbonItem.Small(MailCommands.AddressBook.Id),
                            RibbonItem.Small(MailCommands.FilterEmail.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    // The three single-button groups that close out the Home tab.
                    new RibbonGroup
                    {
                        Id = "speech",
                        Label = "Speech",
                        KeyTip = "ZS",
                        CollapsePriority = 10,
                        Items =
                        [
                            new RibbonItem
                            {
                                Command = ViewCommands.ReadAloud.Id,
                                Size = RibbonItemSize.Large,
                                IsDisabled = true,
                            },
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "apps",
                        Label = "Apps",
                        KeyTip = "ZA",
                        CollapsePriority = 9,
                        Items = [RibbonItem.Large(ViewCommands.Apps.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "sendreceivegroup",
                        Label = "Send/Receive",
                        KeyTip = "ZY",
                        CollapsePriority = 8,
                        Items = [RibbonItem.Large(MailCommands.SendReceiveAll.Id)],
                    },
                ],
            },

            // Transcribed from the reference's own Send / Receive tab: four groups, and the
            // large button of each leads a stack of three or stands alone.
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
                        KeyTip = "ZE",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.SendReceiveAll.Id),
                            RibbonItem.Small(ViewCommands.UpdateFolder.Id),
                            RibbonItem.Small(ViewCommands.SendAll.Id),
                            RibbonItem.Small(ViewCommands.SendReceiveGroups.Id, RibbonItemKind.DropDown),
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

                    // Headers without their messages, and the three commands that act on what
                    // came back. Each of the small three carries its own chevron in the
                    // reference, because each asks whether it means the selection or the folder.
                    new RibbonGroup
                    {
                        Id = "server",
                        Label = "Server",
                        KeyTip = "ZV",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(ViewCommands.DownloadHeaders.Id),
                            RibbonItem.Small(ViewCommands.MarkToDownload.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ViewCommands.UnmarkToDownload.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ViewCommands.ProcessMarkedHeaders.Id, RibbonItemKind.DropDown),
                        ],
                    },
                    new RibbonGroup
                    {
                        Id = "preferences",
                        Label = "Preferences",
                        KeyTip = "ZP",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(MailCommands.WorkOffline.Id)],
                    },
                ],
            },

            // The Folder tab, transcribed from the reference: five groups, and every command in
            // it has long been reachable from the folder pane's own menu.
            new RibbonTab
            {
                Id = "folder",
                Label = "Folder",
                KeyTip = "O",

                // The Simplified bar has no Folder tab: both captures were taken minutes apart
                // and only the classic one shows it.
                ClassicOnly = true,
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "foldernew",
                        Label = "New",
                        KeyTip = "ZN",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.NewFolder.Id),
                            RibbonItem.Large(MailCommands.NewSearchFolder.Id),
                        ],
                    },
                    new RibbonGroup
                    {
                        Id = "folderactions",
                        Label = "Actions",
                        KeyTip = "ZA",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.RenameFolder.Id),
                            RibbonItem.Small(MailCommands.CopyFolder.Id),
                            RibbonItem.Small(MailCommands.MoveFolder.Id),
                            RibbonItem.Small(MailCommands.DeleteFolder.Id),
                        ],
                    },
                    new RibbonGroup
                    {
                        Id = "foldercleanup",
                        Label = "Clean Up",
                        KeyTip = "ZC",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.MarkAllAsRead.Id),
                            RibbonItem.Large(MailCommands.RunRulesNow.Id),
                            RibbonItem.Large(MailCommands.ShowAllFoldersAtoZ.Id),
                            RibbonItem.Small(MailCommands.CleanUpFolder.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(MailCommands.DeleteAll.Id),
                            RibbonItem.Small(MailCommands.RecoverDeleted.Id),
                        ],
                    },
                    new RibbonGroup
                    {
                        Id = "folderfavorites",
                        Label = "Favorites",
                        KeyTip = "ZF",
                        CollapsePriority = 5,
                        Items = [RibbonItem.Large(MailCommands.AddToFavorites.Id)],
                    },
                    new RibbonGroup
                    {
                        Id = "folderproperties",
                        Label = "Properties",
                        KeyTip = "ZP",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.AutoArchiveSettings.Id),
                            new RibbonItem
                            {
                                Command = MailCommands.FolderPermissions.Id,
                                Size = RibbonItemSize.Large,
                                IsDisabled = true,
                            },
                            RibbonItem.Large(MailCommands.FolderProperties.Id),
                        ],
                    },
                ],
            },

            // The View tab, transcribed from the reference: Current View, Messages, Arrangement
            // — whose middle is a boxed grid rather than buttons — Layout, Window, and the one
            // greyed group at the end.
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
                            RibbonItem.Large(ViewCommands.OpenViewSettings.Id),
                            RibbonItem.Large(ViewCommands.ResetView.Id),
                        ],
                    },
                    new RibbonGroup
                    {
                        Id = "messages",
                        Label = "Messages",
                        KeyTip = "ZM",
                        CollapsePriority = 5,
                        Items =
                        [
                            RibbonItem.Check(ViewCommands.ShowAsConversations.Id),
                            new RibbonItem
                            {
                                Command = ViewCommands.ConversationSettings.Id,
                                Size = RibbonItemSize.Small,
                                Kind = RibbonItemKind.DropDown,
                                IsDisabled = true,
                            },
                        ],
                    },

                    // The gallery is the middle of this group, not the whole of it: Message
                    // Preview stands to its left and three small buttons to its right.
                    new RibbonGroup
                    {
                        Id = "arrangement",
                        Label = "Arrangement",
                        KeyTip = "ZA",
                        CollapsePriority = 1,
                        GalleryColumns = 4,
                        GalleryMore = ViewCommands.ArrangeBy.Id,
                        Items =
                        [
                            RibbonItem.Large(ViewCommands.MessagePreview.Id, RibbonItemKind.DropDown),
                            .. ViewCommands.Arrangements.Select(c => RibbonItem.Gallery(c.Id)),
                            RibbonItem.Small(ViewCommands.ReverseSort.Id),
                            RibbonItem.Small(ViewCommands.AddColumns.Id),
                            RibbonItem.Small(ViewCommands.ExpandCollapse.Id, RibbonItemKind.DropDown),
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
                            RibbonItem.Large(ViewCommands.TighterSpacing.Id),
                            RibbonItem.Large(ViewCommands.FolderPane.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ViewCommands.ReadingPane.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ViewCommands.ToDoBar.Id, RibbonItemKind.DropDown),
                        ],
                    },
                    new RibbonGroup
                    {
                        Id = "window",
                        Label = "Window",
                        KeyTip = "ZW",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(ViewCommands.RemindersWindow.Id),
                            RibbonItem.Large(ViewCommands.OpenInNewWindow.Id),
                            RibbonItem.Large(ViewCommands.CloseAllItems.Id),
                        ],
                    },
                    new RibbonGroup
                    {
                        Id = "immersivereader",
                        Label = "Immersive Reader",
                        KeyTip = "ZI",
                        CollapsePriority = 6,
                        Items =
                        [
                            new RibbonItem
                            {
                                Command = ViewCommands.ImmersiveReader.Id,
                                Size = RibbonItemSize.Large,
                                IsDisabled = true,
                            },
                        ],
                    },
                ],
            },

            // Help, transcribed from the capture: seven large buttons in one group and one in a
            // group of its own. Four lead somewhere this project has; the other four name
            // services the reference's publisher runs, and each says so when it is pressed.
            new RibbonTab
            {
                Id = "help",
                Label = "Help",
                KeyTip = "Y",
                Groups = HelpGroups,
            },
        ],
    };

    /// <summary>
    /// The Calendar module's own tab collection. Transcription notes are on
    /// <see cref="CalendarRibbonLayout"/>.
    /// </summary>
    public static RibbonLayout Calendar { get; } = CalendarRibbonLayout.Build();

    /// <summary>
    /// The People module's own tab collection. Transcription notes are on
    /// <see cref="PeopleRibbonLayout"/> — including which half of it has a capture.
    /// </summary>
    public static RibbonLayout People { get; } = PeopleRibbonLayout.Build();

    public static RibbonLayout For(MailboxModule module) => module switch
    {
        MailboxModule.Mail => Mail,
        MailboxModule.Calendar => Calendar,
        MailboxModule.People => People,
        _ => new RibbonLayout { Module = module, Tabs = [] },
    };

    /// <summary>
    /// The Help tab's two groups, which every window that carries the tab carries.
    /// </summary>
    /// <remarks>
    /// The reference's Help tab is the same tab wherever it appears — the shell, a message, a
    /// contact — so it is written once. See the transcription note on the shell's own tab.
    /// </remarks>
    /// <remarks>
    /// Built on each read rather than initialised once: the layouts above are static properties
    /// too, and one initialised before this would have read a null.
    /// </remarks>
    internal static IReadOnlyList<RibbonGroup> HelpGroups =>
    [
        new RibbonGroup
        {
            Id = "help",
            Label = "Help",
            KeyTip = "ZH",
            CollapsePriority = 1,
            Items =
            [
                RibbonItem.Large(ViewCommands.Help.Id),
                RibbonItem.Large(ViewCommands.ContactSupport.Id),
                RibbonItem.Large(ViewCommands.Feedback.Id),
                RibbonItem.Large(ViewCommands.SuggestFeature.Id),
                RibbonItem.Large(ViewCommands.ShowTraining.Id),
                RibbonItem.Large(ViewCommands.WhatsNew.Id),
                RibbonItem.Large(ViewCommands.SupportTool.Id),
            ],
        },
        new RibbonGroup
        {
            Id = "helptools",
            Label = "Tools",
            KeyTip = "ZT",
            CollapsePriority = 2,
            Items = [RibbonItem.Large(ViewCommands.GetDiagnostics.Id)],
        },
    ];

    /// <summary>
    /// The Help tab's Simplified row, which every module that carries the tab carries.
    /// </summary>
    /// <remarks>
    /// The same seven entries as the classic tab's first group, in the same order, with Get
    /// Diagnostics left to the bar's own "…" — transcribed from the Simplified capture. Written
    /// once for the same reason <see cref="HelpGroups"/> is: Help is the application's tab
    /// rather than a module's, and five modules drew it empty in both layouts because each had
    /// declared its own and filled neither.
    /// </remarks>
    internal static SimplifiedBar HelpBar => new()
    {
        // No rule closing the row: the bar draws its own "…" behind one after the last cluster,
        // and that is the single "…" the capture shows after Support Tool.
        TrailingRule = false,
        Groups =
        [
            Cluster("help", "Help",
                RibbonItem.Small(ViewCommands.Help.Id),
                RibbonItem.Small(ViewCommands.ContactSupport.Id),
                RibbonItem.Small(ViewCommands.Feedback.Id),
                RibbonItem.Small(ViewCommands.SuggestFeature.Id),
                RibbonItem.Small(ViewCommands.ShowTraining.Id),
                RibbonItem.Small(ViewCommands.WhatsNew.Id),
                RibbonItem.Small(ViewCommands.SupportTool.Id)),
        ],
    };
}
