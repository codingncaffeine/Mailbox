using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The compose window's ribbon, transcribed from captures of the reference application's new
/// message window: File, Message, Insert, Options, Format Text, Review, Help.
/// </summary>
/// <remarks>
/// A separate host with its own tab collection, which the ribbon model supports by design —
/// <see cref="RibbonView"/>'s layout is swapped rather than its tabs edited.
/// <para>
/// <b>Simplified rows are measured; classic groups are not.</b> The captures are of the
/// Simplified ribbon, so every row below is transcribed left to right from one. The classic
/// group arrangement is authored from the same command set and the reference's usual grouping,
/// and has no capture behind it — treat the group boundaries and item sizes as provisional and
/// measure them when a classic capture exists.
/// </para>
/// </remarks>
internal static class ComposeRibbonLayout
{
    /// <summary>
    /// The Font box, measured off the capture: x=128–234 on the Message row and x=124–230 on
    /// Format Text, so 107px either way.
    /// </summary>
    private const double FontFieldWidth = 107;

    /// <summary>The Font Size box beside it: x=251–301 and x=247–297, so 51px.</summary>
    private const double FontSizeFieldWidth = 51;

    /// <summary>
    /// What the two boxes read. The reference shows whatever is under the caret — "12" on the
    /// Message capture, "Aptos (Body)" on Format Text — so ours shows the body's default until
    /// the editor in Phase 5 can report a selection. Calibri rather than Aptos, because §6
    /// records that Aptos is the one common font with no metric-compatible substitute.
    /// </summary>
    private const string DefaultBodyFont = "Calibri";

    private const string DefaultBodySize = "12";

    private static RibbonItem Sep => new()
    {
        Command = new CommandId("app.separator"),
        Kind = RibbonItemKind.Separator,
        Size = RibbonItemSize.Small,
    };

    internal static RibbonLayout Build() => new()
    {
        Module = MailboxModule.Mail,

        // The reference's compose QAT: Save, Undo, Redo, then the item navigators.
        QuickAccess =
        [
            ComposeCommands.SaveDraft.Id,
            MailCommands.Undo.Id,
            ViewCommands.Redo.Id,
            ComposeCommands.PreviousItem.Id,
            ComposeCommands.NextItem.Id,
        ],

        SimplifiedRows = new Dictionary<string, IReadOnlyList<RibbonItem>>
        {
            // Cluster boundaries are the separator positions measured off the capture: x = 115,
            // 819, 899, 1214, 1344, 1388, 1489. Only Attach File, Link, Signature, All Apps and
            // Editor carry text; the formatting run is icon-only, which is what keeps it to a
            // third of the bar.
            ["message"] =
            [
                RibbonItem.Glyph(ComposeCommands.Paste.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.FormatPainter.Id),
                Sep,
                RibbonItem.Combo(ComposeCommands.Font.Id, FontFieldWidth, DefaultBodyFont),
                RibbonItem.Combo(ComposeCommands.FontSize.Id, FontSizeFieldWidth, DefaultBodySize),
                RibbonItem.Glyph(ComposeCommands.Bold.Id),
                RibbonItem.Glyph(ComposeCommands.Italic.Id),
                RibbonItem.Glyph(ComposeCommands.Underline.Id),
                RibbonItem.Glyph(ComposeCommands.Highlight.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.FontColor.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.Bullets.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.Numbering.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.Align.Id, RibbonItemKind.DropDown),
                RibbonItem.Glyph(ComposeCommands.DecreaseIndent.Id),
                RibbonItem.Glyph(ComposeCommands.IncreaseIndent.Id),
                RibbonItem.Overflow(),
                RibbonItem.Launcher(ComposeCommands.FontDialog.Id),
                Sep,
                RibbonItem.Glyph(MailCommands.AddressBook.Id),
                RibbonItem.Glyph(ComposeCommands.CheckNames.Id),
                Sep,
                RibbonItem.Small(ComposeCommands.AttachFile.Id, RibbonItemKind.SplitButton),
                RibbonItem.Small(ComposeCommands.Link.Id, RibbonItemKind.SplitButton),
                RibbonItem.Small(ComposeCommands.Signature.Id, RibbonItemKind.DropDown),
                Sep,
                RibbonItem.Glyph(ComposeCommands.HighImportance.Id),
                RibbonItem.Glyph(ComposeCommands.LowImportance.Id),
                RibbonItem.Glyph(MailCommands.FollowUp.Id, RibbonItemKind.DropDown),
                Sep,
                RibbonItem.Glyph(ComposeCommands.Dictate.Id),
                Sep,
                RibbonItem.Small(ViewCommands.Apps.Id),
                Sep,
                RibbonItem.Small(ComposeCommands.Editor.Id),
                Sep,
            ],

            // Separators measured at x = 243, 337, 1175, 1266. Every item on this tab is
            // labelled, which is why it is the widest of the five.
            ["insert"] =
            [
                RibbonItem.Small(ComposeCommands.AttachFile.Id, RibbonItemKind.SplitButton),
                RibbonItem.Small(ComposeCommands.Signature.Id, RibbonItemKind.DropDown),
                Sep,
                RibbonItem.Small(ComposeCommands.Table.Id, RibbonItemKind.DropDown),
                Sep,
                RibbonItem.Small(ComposeCommands.Pictures.Id),
                RibbonItem.Small(ComposeCommands.StockImages.Id),
                RibbonItem.Small(ComposeCommands.OnlinePictures.Id),
                RibbonItem.Small(ComposeCommands.Shapes.Id, RibbonItemKind.DropDown),
                RibbonItem.Small(ComposeCommands.Icons.Id),
                RibbonItem.Small(ComposeCommands.Models3D.Id, RibbonItemKind.DropDown),
                RibbonItem.Small(ComposeCommands.SmartArt.Id),
                RibbonItem.Small(ComposeCommands.Chart.Id),
                Sep,
                RibbonItem.Small(ComposeCommands.Link.Id, RibbonItemKind.SplitButton),
                Sep,
                RibbonItem.Small(ComposeCommands.Equation.Id, RibbonItemKind.SplitButton),
                RibbonItem.Small(ComposeCommands.Symbol.Id, RibbonItemKind.DropDown),
                Sep,
            ],

            ["options"] =
            [
                RibbonItem.Small(ComposeCommands.Themes.Id, RibbonItemKind.DropDown),
                RibbonItem.Small(ComposeCommands.ThemeColors.Id, RibbonItemKind.DropDown),
                RibbonItem.Small(ComposeCommands.ThemeFonts.Id, RibbonItemKind.DropDown),
                RibbonItem.Small(ComposeCommands.ThemeEffects.Id, RibbonItemKind.DropDown),
                RibbonItem.Small(ComposeCommands.PageColor.Id, RibbonItemKind.DropDown),
                Sep,
                RibbonItem.Small(ComposeCommands.VotingButtons.Id, RibbonItemKind.DropDown),
                RibbonItem.Launcher(ComposeCommands.Properties.Id),
                Sep,
            ],

            // Separators measured at x = 111, 761, 1119, 1367, 1454. The font and paragraph runs
            // are icon-only; only Styles, Change Styles, Find and Zoom carry text.
            ["formattext"] =
            [
                RibbonItem.Glyph(ComposeCommands.Paste.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.FormatPainter.Id),
                Sep,
                RibbonItem.Combo(ComposeCommands.Font.Id, FontFieldWidth, DefaultBodyFont),
                RibbonItem.Combo(ComposeCommands.FontSize.Id, FontSizeFieldWidth, DefaultBodySize),
                RibbonItem.Glyph(ComposeCommands.GrowFont.Id),
                RibbonItem.Glyph(ComposeCommands.ShrinkFont.Id),
                RibbonItem.Glyph(ComposeCommands.Bold.Id),
                RibbonItem.Glyph(ComposeCommands.Italic.Id),
                RibbonItem.Glyph(ComposeCommands.Underline.Id),
                RibbonItem.Glyph(ComposeCommands.Strikethrough.Id),
                RibbonItem.Glyph(ComposeCommands.Subscript.Id),
                RibbonItem.Glyph(ComposeCommands.Superscript.Id),
                RibbonItem.Glyph(ComposeCommands.Highlight.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.FontColor.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.ClearFormatting.Id),
                RibbonItem.Launcher(ComposeCommands.FontDialog.Id),
                Sep,
                RibbonItem.Glyph(ComposeCommands.Bullets.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.Numbering.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.MultilevelList.Id, RibbonItemKind.SplitButton),
                RibbonItem.Glyph(ComposeCommands.DecreaseIndent.Id),
                RibbonItem.Glyph(ComposeCommands.IncreaseIndent.Id),
                RibbonItem.Glyph(ComposeCommands.Align.Id, RibbonItemKind.DropDown),
                RibbonItem.Glyph(ComposeCommands.LineSpacing.Id, RibbonItemKind.DropDown),
                RibbonItem.Launcher(ComposeCommands.ParagraphDialog.Id),
                Sep,
                RibbonItem.Small(ComposeCommands.Styles.Id, RibbonItemKind.DropDown),
                RibbonItem.Small(ComposeCommands.ChangeStyles.Id, RibbonItemKind.DropDown),
                RibbonItem.Launcher(ComposeCommands.StylesDialog.Id),
                Sep,
                RibbonItem.Small(ComposeCommands.Find.Id, RibbonItemKind.SplitButton),
                Sep,
                RibbonItem.Small(ComposeCommands.Zoom.Id),
                Sep,
            ],

            // Separators measured at x = 512, 633, 767, 887 — each of Speech, Insights and
            // Language is its own cluster of one, which is why the row has so many rules in it.
            ["review"] =
            [
                RibbonItem.Small(ComposeCommands.Spelling.Id, RibbonItemKind.SplitButton),
                RibbonItem.Small(ComposeCommands.Editor.Id),
                RibbonItem.Small(ComposeCommands.Thesaurus.Id),
                RibbonItem.Small(ComposeCommands.WordCount.Id),
                Sep,
                RibbonItem.Small(ViewCommands.ReadAloud.Id),
                Sep,
                RibbonItem.Small(ComposeCommands.SmartLookup.Id),
                Sep,
                RibbonItem.Small(ComposeCommands.Language.Id, RibbonItemKind.DropDown),
                Sep,
                RibbonItem.Small(ComposeCommands.CheckAccessibility.Id),
                Sep,
            ],
        },

        Tabs =
        [
            new RibbonTab { Id = "file", Label = "File", KeyTip = "F", IsBackstage = true, Groups = [] },

            new RibbonTab
            {
                Id = "message",
                Label = "Message",
                KeyTip = "H",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "clipboard",
                        Label = "Clipboard",
                        CollapsePriority = 8,
                        DialogLauncher = ComposeCommands.Paste.Id,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Paste.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Small(ComposeCommands.Cut.Id),
                            RibbonItem.Small(ComposeCommands.Copy.Id),
                            RibbonItem.Small(ComposeCommands.FormatPainter.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "basictext",
                        Label = "Basic Text",
                        CollapsePriority = 2,
                        DialogLauncher = ComposeCommands.FontDialog.Id,
                        Items =
                        [
                            RibbonItem.Small(ComposeCommands.Font.Id, RibbonItemKind.TextBox),
                            RibbonItem.Small(ComposeCommands.FontSize.Id, RibbonItemKind.TextBox),
                            RibbonItem.Glyph(ComposeCommands.GrowFont.Id),
                            RibbonItem.Glyph(ComposeCommands.ShrinkFont.Id),
                            RibbonItem.Glyph(ComposeCommands.Bold.Id),
                            RibbonItem.Glyph(ComposeCommands.Italic.Id),
                            RibbonItem.Glyph(ComposeCommands.Underline.Id),
                            RibbonItem.Glyph(ComposeCommands.Highlight.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.FontColor.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.Bullets.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.Numbering.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.DecreaseIndent.Id),
                            RibbonItem.Glyph(ComposeCommands.IncreaseIndent.Id),
                            RibbonItem.Glyph(ComposeCommands.Align.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "names",
                        Label = "Names",
                        CollapsePriority = 6,
                        Items =
                        [
                            RibbonItem.Large(MailCommands.AddressBook.Id),
                            RibbonItem.Large(ComposeCommands.CheckNames.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "include",
                        Label = "Include",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.AttachFile.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Large(ComposeCommands.AttachItem.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.Signature.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.Link.Id, RibbonItemKind.SplitButton),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        CollapsePriority = 3,
                        DialogLauncher = ComposeCommands.Properties.Id,
                        Items =
                        [
                            RibbonItem.Small(ComposeCommands.HighImportance.Id),
                            RibbonItem.Small(ComposeCommands.LowImportance.Id),
                            RibbonItem.Small(MailCommands.FollowUp.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "voice",
                        Label = "Voice",
                        CollapsePriority = 9,
                        Items = [RibbonItem.Large(ComposeCommands.Dictate.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "apps",
                        Label = "Apps",
                        CollapsePriority = 10,
                        Items = [RibbonItem.Large(ViewCommands.Apps.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "editor",
                        Label = "Editor",
                        CollapsePriority = 7,
                        Items = [RibbonItem.Large(ComposeCommands.Editor.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "immersive",
                        Label = "Immersive",
                        CollapsePriority = 11,
                        Items = [RibbonItem.Large(ViewCommands.ImmersiveReader.Id)],
                    },
                ],
            },

            new RibbonTab
            {
                Id = "insert",
                Label = "Insert",
                KeyTip = "N",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "include",
                        Label = "Include",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.AttachFile.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Large(ComposeCommands.AttachItem.Id, RibbonItemKind.DropDown),
                            RibbonItem.Large(ComposeCommands.Signature.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tables",
                        Label = "Tables",
                        CollapsePriority = 4,
                        Items = [RibbonItem.Large(ComposeCommands.Table.Id, RibbonItemKind.DropDown)],
                    },

                    new RibbonGroup
                    {
                        Id = "illustrations",
                        Label = "Illustrations",
                        CollapsePriority = 5,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Pictures.Id),
                            RibbonItem.Small(ComposeCommands.StockImages.Id),
                            RibbonItem.Small(ComposeCommands.OnlinePictures.Id),
                            RibbonItem.Small(ComposeCommands.Icons.Id),
                            RibbonItem.Small(ComposeCommands.Shapes.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.Models3D.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.SmartArt.Id),
                            RibbonItem.Small(ComposeCommands.Chart.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "links",
                        Label = "Links",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(ComposeCommands.Link.Id, RibbonItemKind.SplitButton)],
                    },

                    new RibbonGroup
                    {
                        Id = "symbols",
                        Label = "Symbols",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Equation.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Large(ComposeCommands.Symbol.Id, RibbonItemKind.DropDown),
                        ],
                    },
                ],
            },

            new RibbonTab
            {
                Id = "options",
                Label = "Options",
                KeyTip = "O",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "themes",
                        Label = "Themes",
                        CollapsePriority = 4,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Themes.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.ThemeColors.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.ThemeFonts.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.ThemeEffects.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.PageColor.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "showfields",
                        Label = "Show Fields",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Small(ComposeCommands.ShowBcc.Id),
                            RibbonItem.Small(ComposeCommands.ShowFrom.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "permission",
                        Label = "Permission",
                        CollapsePriority = 5,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Permission.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.Encrypt.Id),
                            RibbonItem.Small(ComposeCommands.Sign.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tracking",
                        Label = "Tracking",
                        CollapsePriority = 1,
                        DialogLauncher = ComposeCommands.Properties.Id,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.VotingButtons.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.DeliveryReceipt.Id),
                            RibbonItem.Small(ComposeCommands.ReadReceipt.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "moreoptions",
                        Label = "More Options",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Small(ComposeCommands.SaveSentItemTo.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.DelayDelivery.Id),
                            RibbonItem.Small(ComposeCommands.DirectRepliesTo.Id),
                        ],
                    },
                ],
            },

            new RibbonTab
            {
                Id = "formattext",
                Label = "Format Text",
                KeyTip = "X",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "clipboard",
                        Label = "Clipboard",
                        CollapsePriority = 6,
                        DialogLauncher = ComposeCommands.Paste.Id,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Paste.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Small(ComposeCommands.Cut.Id),
                            RibbonItem.Small(ComposeCommands.Copy.Id),
                            RibbonItem.Small(ComposeCommands.FormatPainter.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "font",
                        Label = "Font",
                        CollapsePriority = 1,
                        DialogLauncher = ComposeCommands.FontDialog.Id,
                        Items =
                        [
                            RibbonItem.Small(ComposeCommands.Font.Id, RibbonItemKind.TextBox),
                            RibbonItem.Small(ComposeCommands.FontSize.Id, RibbonItemKind.TextBox),
                            RibbonItem.Glyph(ComposeCommands.GrowFont.Id),
                            RibbonItem.Glyph(ComposeCommands.ShrinkFont.Id),
                            RibbonItem.Glyph(ComposeCommands.Bold.Id),
                            RibbonItem.Glyph(ComposeCommands.Italic.Id),
                            RibbonItem.Glyph(ComposeCommands.Underline.Id),
                            RibbonItem.Glyph(ComposeCommands.Strikethrough.Id),
                            RibbonItem.Glyph(ComposeCommands.Subscript.Id),
                            RibbonItem.Glyph(ComposeCommands.Superscript.Id),
                            RibbonItem.Glyph(ComposeCommands.Highlight.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.FontColor.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.ClearFormatting.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "paragraph",
                        Label = "Paragraph",
                        CollapsePriority = 2,
                        DialogLauncher = ComposeCommands.ParagraphDialog.Id,
                        Items =
                        [
                            RibbonItem.Glyph(ComposeCommands.Bullets.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.Numbering.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.MultilevelList.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Glyph(ComposeCommands.DecreaseIndent.Id),
                            RibbonItem.Glyph(ComposeCommands.IncreaseIndent.Id),
                            RibbonItem.Glyph(ComposeCommands.Sort.Id),
                            RibbonItem.Glyph(ComposeCommands.ShowParagraphMarks.Id),
                            RibbonItem.Glyph(ComposeCommands.Align.Id, RibbonItemKind.DropDown),
                            RibbonItem.Glyph(ComposeCommands.LineSpacing.Id, RibbonItemKind.DropDown),
                            RibbonItem.Glyph(ComposeCommands.Shading.Id, RibbonItemKind.DropDown),
                            RibbonItem.Glyph(ComposeCommands.Borders.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "styles",
                        Label = "Styles",
                        CollapsePriority = 4,
                        DialogLauncher = ComposeCommands.StylesDialog.Id,
                        IsGallery = true,
                        Items =
                        [
                            RibbonItem.Small(ComposeCommands.Styles.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.ChangeStyles.Id, RibbonItemKind.DropDown),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "format",
                        Label = "Format",
                        CollapsePriority = 3,
                        Items =
                        [
                            RibbonItem.Small(ComposeCommands.FormatHtml.Id),
                            RibbonItem.Small(ComposeCommands.FormatPlainText.Id),
                            RibbonItem.Small(ComposeCommands.FormatRichText.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "editing",
                        Label = "Editing",
                        CollapsePriority = 5,
                        Items =
                        [
                            RibbonItem.Small(ComposeCommands.Find.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Small(ComposeCommands.Replace.Id),
                            RibbonItem.Small(ComposeCommands.SelectAll.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(ComposeCommands.Zoom.Id),
                        ],
                    },
                ],
            },

            new RibbonTab
            {
                Id = "review",
                Label = "Review",
                KeyTip = "R",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "proofing",
                        Label = "Proofing",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.Large(ComposeCommands.Spelling.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Large(ComposeCommands.Editor.Id),
                            RibbonItem.Large(ComposeCommands.Thesaurus.Id),
                            RibbonItem.Large(ComposeCommands.WordCount.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "speech",
                        Label = "Speech",
                        CollapsePriority = 4,
                        Items = [RibbonItem.Large(ViewCommands.ReadAloud.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "insights",
                        Label = "Insights",
                        CollapsePriority = 5,
                        Items = [RibbonItem.Large(ComposeCommands.SmartLookup.Id)],
                    },

                    new RibbonGroup
                    {
                        Id = "language",
                        Label = "Language",
                        CollapsePriority = 3,
                        Items = [RibbonItem.Large(ComposeCommands.Language.Id, RibbonItemKind.DropDown)],
                    },

                    new RibbonGroup
                    {
                        Id = "accessibility",
                        Label = "Accessibility",
                        CollapsePriority = 2,
                        Items = [RibbonItem.Large(ComposeCommands.CheckAccessibility.Id, RibbonItemKind.SplitButton)],
                    },
                ],
            },

            new RibbonTab { Id = "help", Label = "Help", KeyTip = "Y", Groups = [] },
        ],
    };
}
