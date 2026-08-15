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
            // 596, 831, 1049, 1093 and 1289. Only New Email, Unread/Read and Send/Receive All
            // Folders carry text; everything else is icon-only, which is why the row fits.
            // Quick Steps is boxed and Search People is a real input, not a button.
            ["home"] = Bar(
                Cluster("new", "New",
                    RibbonItem.Small(MailCommands.NewEmail.Id, RibbonItemKind.SplitButton)),

                Cluster("movedelete", "Move & Delete",
                    RibbonItem.Glyph(MailCommands.Delete.Id, RibbonItemKind.SplitButton),
                    RibbonItem.Glyph(MailCommands.Archive.Id),
                    RibbonItem.Glyph(MailCommands.MoveTo.Id, RibbonItemKind.SplitButton)),

                Cluster("respond", "Respond",
                    RibbonItem.Glyph(MailCommands.Reply.Id),
                    RibbonItem.Glyph(MailCommands.ReplyAll.Id),
                    RibbonItem.Glyph(MailCommands.Forward.Id)),

                Cluster("quicksteps", "Quick Steps",
                    RibbonItem.Boxed(ViewCommands.MoveToQuick.Id, QuickStepBoxWidth)),

                Cluster("tags", "Tags",
                    RibbonItem.Small(MailCommands.Unread.Id),
                    RibbonItem.Glyph(MailCommands.Categorize.Id, RibbonItemKind.DropDown),
                    RibbonItem.Glyph(MailCommands.FollowUp.Id, RibbonItemKind.DropDown)),

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

            ["help"] = new SimplifiedBar { Groups = [] },
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
                        CollapsePriority = 9,
                        Items = [RibbonItem.Large(ViewCommands.Apps.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "sendreceivegroup",
                        Label = "Send/Receive",
                        CollapsePriority = 8,
                        Items = [RibbonItem.Large(MailCommands.SendReceiveAll.Id)],
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
                        CollapsePriority = 1,
                        Items = [RibbonItem.Large(MailCommands.SendReceiveAll.Id)],
                    },
                    new RibbonGroup
                    {
                        Id = "preferences",
                        Label = "Preferences",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(MailCommands.WorkOffline.Id)],
                    },
                ],
            },

            // No Folder tab: current the reference application builds ship File, Home, Send/Receive,
            // View and Help, with the folder commands folded elsewhere.
            new RibbonTab
            {
                Id = "view",
                Label = "View",
                KeyTip = "V",
                Groups = [],
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

    public static RibbonLayout For(MailboxModule module) => module switch
    {
        MailboxModule.Mail => Mail,
        _ => new RibbonLayout { Module = module, Tabs = [] },
    };
}
