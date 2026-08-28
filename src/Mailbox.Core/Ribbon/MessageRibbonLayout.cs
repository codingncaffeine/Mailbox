using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The read ribbon: what a message opened in its own window carries — the third ribbon host,
/// after the shell's and the compose window's.
/// </summary>
/// <remarks>
/// Transcribed from the reference's own message window (<c>reply/double click email.png</c>,
/// 1660 wide, Simplified): Delete · Archive · Move | Reply · Reply All · Forward | All Apps |
/// the Quick Steps box | Mark Unread · Categorize | Find · Read Aloud · Immersive Reader ·
/// Translate | Zoom, then the bar's own "…". Categorize and Translate are icon-only with their
/// chevrons at a width where everything around them keeps its words, so they are glyphs here
/// rather than sheddables — the capture is the spec, and it sheds those two first of all.
/// <para>
/// The Quick Steps cluster carries the gallery id, so <see cref="QuickStepsRibbon.Inject"/>
/// fills this window's box exactly as it fills the shell's.
/// </para>
/// </remarks>
public static class MessageRibbonLayout
{
    /// <summary>The reference's Quick Steps box, same width the shell's bar measures.</summary>
    private const double QuickStepBoxWidth = 106;

    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    public static RibbonLayout Layout { get; } = new()
    {
        Module = MailboxModule.Mail,

        // The window's own Quick Access Toolbar: undo and redo, then the two arrows that step
        // to the previous and next message — the reference's own set, less the Save this
        // window has no honest meaning for yet.
        QuickAccess =
        [
            MailCommands.Undo.Id,
            ViewCommands.Redo.Id,
            MailCommands.PreviousMessage.Id,
            MailCommands.NextMessage.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            ["message"] = Bar(
                Cluster("movedelete", "Move & Delete",
                    RibbonItem.Small(MailCommands.Delete.Id, RibbonItemKind.SplitButton),
                    RibbonItem.Small(MailCommands.Archive.Id),
                    RibbonItem.Small(MailCommands.MoveTo.Id, RibbonItemKind.SplitButton)),

                Cluster("respond", "Respond",
                    RibbonItem.Sheddable(MailCommands.Reply.Id),
                    RibbonItem.Sheddable(MailCommands.ReplyAll.Id),
                    RibbonItem.Sheddable(MailCommands.Forward.Id)),

                Cluster("apps", "Apps",
                    RibbonItem.Small(ViewCommands.Apps.Id)),

                Cluster("quicksteps", "Quick Steps",
                    RibbonItem.Boxed(ViewCommands.MoveToQuick.Id, QuickStepBoxWidth)),

                Cluster("tags", "Tags",
                    RibbonItem.Small(MailCommands.MarkAsUnread.Id),
                    RibbonItem.Glyph(MailCommands.Categorize.Id, RibbonItemKind.DropDown)),

                Cluster("editing", "Editing",
                    RibbonItem.Sheddable(ViewCommands.FindInMessage.Id),
                    RibbonItem.Sheddable(ViewCommands.ReadAloud.Id),
                    RibbonItem.Sheddable(ViewCommands.ImmersiveReader.Id),
                    RibbonItem.Glyph(ViewCommands.Translate.Id, RibbonItemKind.DropDown)),

                Cluster("zoom", "Zoom",
                    RibbonItem.Sheddable(ViewCommands.Zoom.Id))),

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
                Id = "message",
                Label = "Message",
                KeyTip = "H",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "delete",
                        Label = "Delete",
                        CollapsePriority = 4,
                        KeyTip = "ZD",
                        Items =
                        [
                            RibbonItem.Large(MailCommands.Delete.Id),
                            RibbonItem.Large(MailCommands.Archive.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "respond",
                        Label = "Respond",
                        CollapsePriority = 1,
                        KeyTip = "ZR",
                        Items =
                        [
                            RibbonItem.Large(MailCommands.Reply.Id),
                            RibbonItem.Large(MailCommands.ReplyAll.Id),
                            RibbonItem.Large(MailCommands.Forward.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "quicksteps",
                        Label = "Quick Steps",
                        IsGallery = true,
                        CollapsePriority = 5,
                        KeyTip = "ZQ",
                        Items =
                        [
                            RibbonItem.Small(ViewCommands.MoveToQuick.Id),
                            RibbonItem.Small(ViewCommands.ToManager.Id),
                            RibbonItem.Small(ViewCommands.TeamEmail.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "move",
                        Label = "Move",
                        CollapsePriority = 3,
                        KeyTip = "ZM",
                        Items =
                        [
                            RibbonItem.Large(MailCommands.MoveTo.Id, RibbonItemKind.SplitButton),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        CollapsePriority = 2,
                        KeyTip = "ZT",
                        Items =
                        [
                            RibbonItem.Small(MailCommands.MarkAsUnread.Id),
                            RibbonItem.Small(MailCommands.Categorize.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(MailCommands.FollowUp.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "editing",
                        Label = "Editing",
                        CollapsePriority = 6,
                        KeyTip = "ZE",
                        Items =
                        [
                            RibbonItem.Small(ViewCommands.FindInMessage.Id),
                            RibbonItem.Small(ViewCommands.ReadAloud.Id),
                            RibbonItem.Small(ViewCommands.ImmersiveReader.Id),
                            RibbonItem.Small(ViewCommands.Translate.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "zoom",
                        Label = "Zoom",
                        CollapsePriority = 7,
                        KeyTip = "ZO",
                        Items =
                        [
                            RibbonItem.Large(ViewCommands.Zoom.Id),
                        ],
                    },
                ],
            },

            new RibbonTab
            {
                Id = "help",
                Label = "Help",
                KeyTip = "E",
                Groups = DefaultRibbonLayouts.HelpGroups,
            },
        ],

        TellMe = "Tell me what you want to do",
    };
}
