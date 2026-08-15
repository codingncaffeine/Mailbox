namespace Mailbox.Core.Commands;

/// <summary>
/// The compose window's command set — the Message, Insert, Options, Format Text and Review
/// tabs of a new message, transcribed from captures of the reference application.
/// </summary>
/// <remarks>
/// A separate window with its own tab set, which is why these are their own class rather than
/// more of <see cref="MailCommands"/>: the ribbon model swaps the entire tab collection per
/// host, and the two sets are never on screen together.
/// <para>
/// Where the reference reuses a command the main window already has — Address Book, Follow Up,
/// All Apps, Read Aloud, Immersive Reader, Undo, Redo — the compose ribbon places the existing
/// catalogue entry rather than declaring a second one. Two ids for one action is how a
/// customization file ends up meaning different things in different windows.
/// </para>
/// </remarks>
public static class ComposeCommands
{
    // ---- The window itself ------------------------------------------------------------
    // Not on the ribbon: Send sits beside the address fields, and the rest are on the
    // window's Quick Access Toolbar, exactly as the reference has them.

    public static readonly MailboxCommand Send = new()
    {
        Id = new("compose.send"),
        Label = "Send",
        Description = "Send this message.",
        Icon = "send",
        Category = "Compose",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Ctrl+Enter",
    };

    public static readonly MailboxCommand SaveDraft = new()
    {
        Id = new("compose.save"),
        Label = "Save",
        Description = "Save this message to the Drafts folder.",
        Icon = "save",
        Category = "Compose",
        Scope = ModuleScope.Mail,
        DefaultGesture = "Ctrl+S",
    };

    public static readonly MailboxCommand Discard = new()
    {
        Id = new("compose.discard"),
        Label = "Discard",
        Description = "Close this message without saving it.",
        Icon = "delete",
        Category = "Compose",
        Scope = ModuleScope.Mail,
    };

    public static readonly MailboxCommand PreviousItem = new()
    {
        Id = new("compose.previous"),
        Label = "Previous Item",
        Description = "Open the previous item.",
        Icon = "chevron-up",
        Category = "Compose",
        Scope = ModuleScope.Mail,
    };

    public static readonly MailboxCommand NextItem = new()
    {
        Id = new("compose.next"),
        Label = "Next Item",
        Description = "Open the next item.",
        Icon = "chevron-down",
        Category = "Compose",
        Scope = ModuleScope.Mail,
    };

    // ---- Message tab · Clipboard ------------------------------------------------------

    public static readonly MailboxCommand Paste = new()
    {
        Id = new("compose.paste"),
        Label = "Paste",
        Description = "Paste the contents of the clipboard.",
        Icon = "paste",
        Category = "Clipboard",
        Scope = ModuleScope.Mail,
        KeyTip = "V",
        DefaultGesture = "Ctrl+V",
    };

    public static readonly MailboxCommand Cut = new()
    {
        Id = new("compose.cut"),
        Label = "Cut",
        Description = "Remove the selection and put it on the clipboard.",
        Icon = "cut",
        Category = "Clipboard",
        Scope = ModuleScope.Mail,
        KeyTip = "X",
        DefaultGesture = "Ctrl+X",
    };

    public static readonly MailboxCommand Copy = new()
    {
        Id = new("compose.copy"),
        Label = "Copy",
        Description = "Put a copy of the selection on the clipboard.",
        Icon = "copy",
        Category = "Clipboard",
        Scope = ModuleScope.Mail,
        KeyTip = "C",
        DefaultGesture = "Ctrl+C",
    };

    public static readonly MailboxCommand FormatPainter = new()
    {
        Id = new("compose.formatpainter"),
        Label = "Format Painter",
        Description = "Copy formatting from one place and apply it to another.",
        Icon = "format-painter",
        Category = "Clipboard",
        Scope = ModuleScope.Mail,
        KeyTip = "FP",
    };

    // ---- Message and Format Text · Font ----------------------------------------------

    public static readonly MailboxCommand Font = new()
    {
        Id = new("format.font"),
        Label = "Font",
        Description = "Change the typeface of the selected text.",
        Icon = "font",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "FF",
    };

    public static readonly MailboxCommand FontSize = new()
    {
        Id = new("format.fontsize"),
        Label = "Font Size",
        Description = "Change the size of the selected text.",
        Icon = "font-size",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "FS",
    };

    public static readonly MailboxCommand GrowFont = new()
    {
        Id = new("format.growfont"),
        Label = "Grow Font",
        Description = "Make the selected text one size larger.",
        Icon = "grow-font",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "FG",
    };

    public static readonly MailboxCommand ShrinkFont = new()
    {
        Id = new("format.shrinkfont"),
        Label = "Shrink Font",
        Description = "Make the selected text one size smaller.",
        Icon = "shrink-font",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "FK",
    };

    public static readonly MailboxCommand Bold = new()
    {
        Id = new("format.bold"),
        Label = "Bold",
        Description = "Make the selected text bold.",
        Icon = "bold",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "1",
        DefaultGesture = "Ctrl+B",
    };

    public static readonly MailboxCommand Italic = new()
    {
        Id = new("format.italic"),
        Label = "Italic",
        Description = "Italicize the selected text.",
        Icon = "italic",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "2",
        DefaultGesture = "Ctrl+I",
    };

    public static readonly MailboxCommand Underline = new()
    {
        Id = new("format.underline"),
        Label = "Underline",
        Description = "Underline the selected text.",
        Icon = "underline",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "3",
        DefaultGesture = "Ctrl+U",
    };

    public static readonly MailboxCommand Strikethrough = new()
    {
        Id = new("format.strikethrough"),
        Label = "Strikethrough",
        Description = "Draw a line through the selected text.",
        Icon = "strikethrough",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "4",
    };

    public static readonly MailboxCommand Subscript = new()
    {
        Id = new("format.subscript"),
        Label = "Subscript",
        Description = "Put the selected text below the baseline.",
        Icon = "subscript",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "5",
    };

    public static readonly MailboxCommand Superscript = new()
    {
        Id = new("format.superscript"),
        Label = "Superscript",
        Description = "Put the selected text above the baseline.",
        Icon = "superscript",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "6",
    };

    public static readonly MailboxCommand Highlight = new()
    {
        Id = new("format.highlight"),
        Label = "Text Highlight Color",
        Description = "Make the selected text stand out by marking it as with a highlighter.",
        Icon = "highlight",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "IH",
    };

    public static readonly MailboxCommand FontColor = new()
    {
        Id = new("format.fontcolor"),
        Label = "Font Color",
        Description = "Change the colour of the selected text.",
        Icon = "font-color",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "FC",
    };

    public static readonly MailboxCommand ClearFormatting = new()
    {
        Id = new("format.clear"),
        Label = "Clear All Formatting",
        Description = "Remove all formatting from the selection, leaving plain text.",
        Icon = "clear-formatting",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "E",
    };

    public static readonly MailboxCommand FontDialog = new()
    {
        Id = new("format.font.dialog"),
        Label = "Font…",
        Description = "Open the Font dialog for the full set of character formatting.",
        Icon = "font",
        Category = "Font",
        Scope = ModuleScope.Mail,
        KeyTip = "FN",
    };

    // ---- Message and Format Text · Paragraph -----------------------------------------

    public static readonly MailboxCommand Bullets = new()
    {
        Id = new("format.bullets"),
        Label = "Bullets",
        Description = "Start a bulleted list.",
        Icon = "bullets",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "UL",
    };

    public static readonly MailboxCommand Numbering = new()
    {
        Id = new("format.numbering"),
        Label = "Numbering",
        Description = "Start a numbered list.",
        Icon = "numbering",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "NU",
    };

    public static readonly MailboxCommand MultilevelList = new()
    {
        Id = new("format.multilevel"),
        Label = "Multilevel List",
        Description = "Start a list with more than one level.",
        Icon = "multilevel-list",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "ML",
    };

    public static readonly MailboxCommand DecreaseIndent = new()
    {
        Id = new("format.indent.decrease"),
        Label = "Decrease Indent",
        Description = "Move the paragraph closer to the margin.",
        Icon = "indent-decrease",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "AO",
    };

    public static readonly MailboxCommand IncreaseIndent = new()
    {
        Id = new("format.indent.increase"),
        Label = "Increase Indent",
        Description = "Move the paragraph further from the margin.",
        Icon = "indent-increase",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "AI",
    };

    public static readonly MailboxCommand Align = new()
    {
        Id = new("format.align"),
        Label = "Align",
        Description = "Align the paragraph left, centre, right or justified.",
        Icon = "align",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "AL",
    };

    public static readonly MailboxCommand LineSpacing = new()
    {
        Id = new("format.linespacing"),
        Label = "Line and Paragraph Spacing",
        Description = "Change the space between lines and around paragraphs.",
        Icon = "line-spacing",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "K",
    };

    public static readonly MailboxCommand ShowParagraphMarks = new()
    {
        Id = new("format.showmarks"),
        Label = "Show/Hide ¶",
        Description = "Show paragraph marks and other hidden formatting symbols.",
        Icon = "clear-formatting",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "8",
    };

    public static readonly MailboxCommand Borders = new()
    {
        Id = new("format.borders"),
        Label = "Borders",
        Description = "Put a border around the selected paragraphs or table cells.",
        Icon = "table",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "B",
    };

    public static readonly MailboxCommand Shading = new()
    {
        Id = new("format.shading"),
        Label = "Shading",
        Description = "Colour the background behind the selection.",
        Icon = "page-color",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "H",
    };

    public static readonly MailboxCommand Sort = new()
    {
        Id = new("format.sort"),
        Label = "Sort",
        Description = "Put the selected paragraphs or table rows in order.",
        Icon = "arrange",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "SO",
    };

    public static readonly MailboxCommand ParagraphDialog = new()
    {
        Id = new("format.paragraph.dialog"),
        Label = "Paragraph…",
        Description = "Open the Paragraph dialog for indents, spacing and pagination.",
        Icon = "line-spacing",
        Category = "Paragraph",
        Scope = ModuleScope.Mail,
        KeyTip = "PG",
    };

    // ---- Format Text · Styles ---------------------------------------------------------

    public static readonly MailboxCommand Styles = new()
    {
        Id = new("format.styles"),
        Label = "Styles",
        Description = "Apply a named paragraph or character style.",
        Icon = "styles",
        Category = "Styles",
        Scope = ModuleScope.Mail,
        KeyTip = "L",
    };

    public static readonly MailboxCommand ChangeStyles = new()
    {
        Id = new("format.changestyles"),
        Label = "Change Styles",
        Description = "Change the style set, colours and fonts used by this message.",
        Icon = "change-styles",
        Category = "Styles",
        Scope = ModuleScope.Mail,
        KeyTip = "G",
    };

    public static readonly MailboxCommand StylesDialog = new()
    {
        Id = new("format.styles.dialog"),
        Label = "Styles…",
        Description = "Open the Styles pane.",
        Icon = "styles",
        Category = "Styles",
        Scope = ModuleScope.Mail,
        KeyTip = "SD",
    };

    // ---- Format Text · Format ---------------------------------------------------------
    // The message's wire format, which is a mail concept rather than a word-processing one.

    public static readonly MailboxCommand FormatHtml = new()
    {
        Id = new("format.html"),
        Label = "HTML",
        Description = "Compose this message as HTML, which keeps formatting for most recipients.",
        Icon = "source",
        Category = "Format",
        Scope = ModuleScope.Mail,
        KeyTip = "TH",
    };

    public static readonly MailboxCommand FormatPlainText = new()
    {
        Id = new("format.plaintext"),
        Label = "Plain Text",
        Description = "Compose this message as plain text, dropping all formatting.",
        Icon = "font",
        Category = "Format",
        Scope = ModuleScope.Mail,
        KeyTip = "TP",
    };

    public static readonly MailboxCommand FormatRichText = new()
    {
        Id = new("format.richtext"),
        Label = "Rich Text",
        Description = "Compose this message as Rich Text, which only some mail clients read.",
        Icon = "styles",
        Category = "Format",
        Scope = ModuleScope.Mail,
        KeyTip = "TR",
    };

    // ---- Format Text · Editing --------------------------------------------------------

    public static readonly MailboxCommand Find = new()
    {
        Id = new("format.find"),
        Label = "Find",
        Description = "Find text in this message.",
        Icon = "search",
        Category = "Editing",
        Scope = ModuleScope.Mail,
        KeyTip = "FD",
    };

    public static readonly MailboxCommand Replace = new()
    {
        Id = new("format.replace"),
        Label = "Replace",
        Description = "Find text and replace it with something else.",
        Icon = "search",
        Category = "Editing",
        Scope = ModuleScope.Mail,
        KeyTip = "R",
    };

    public static readonly MailboxCommand SelectAll = new()
    {
        Id = new("format.selectall"),
        Label = "Select",
        Description = "Select all of the message body.",
        Icon = "mark-complete",
        Category = "Editing",
        Scope = ModuleScope.Mail,
        KeyTip = "SL",
        DefaultGesture = "Ctrl+A",
    };

    public static readonly MailboxCommand Zoom = new()
    {
        Id = new("format.zoom"),
        Label = "Zoom",
        Description = "Change how large the message body is drawn.",
        Icon = "zoom",
        Category = "Editing",
        Scope = ModuleScope.Mail,
        KeyTip = "Q",
    };

    // ---- Message tab · Names ----------------------------------------------------------

    public static readonly MailboxCommand CheckNames = new()
    {
        Id = new("compose.checknames"),
        Label = "Check Names",
        Description = "Resolve the names in the address fields against your contacts.",
        Icon = "check-names",
        Category = "Names",
        Scope = ModuleScope.Mail,
        KeyTip = "K2",
        DefaultGesture = "Ctrl+K",
    };

    // ---- Message and Insert · Include -------------------------------------------------

    public static readonly MailboxCommand AttachFile = new()
    {
        Id = new("compose.attach.file"),
        Label = "Attach File",
        Description = "Attach a file to this message.",
        Icon = "attach",
        Category = "Include",
        Scope = ModuleScope.Mail,
        KeyTip = "AF",
    };

    public static readonly MailboxCommand AttachItem = new()
    {
        Id = new("compose.attach.item"),
        Label = "Attach Item",
        Description = "Attach another message, contact or appointment to this one.",
        Icon = "mail",
        Category = "Include",
        Scope = ModuleScope.Mail,
        KeyTip = "AT",
    };

    public static readonly MailboxCommand Signature = new()
    {
        Id = new("compose.signature"),
        Label = "Signature",
        Description = "Insert one of your signatures.",
        Icon = "signature",
        Category = "Include",
        Scope = ModuleScope.Mail,
        KeyTip = "SI",
    };

    public static readonly MailboxCommand Link = new()
    {
        Id = new("insert.link"),
        Label = "Link",
        Description = "Insert a hyperlink.",
        Icon = "link",
        Category = "Links",
        Scope = ModuleScope.Mail,
        KeyTip = "LI",
    };

    // ---- Message tab · Tags -----------------------------------------------------------

    public static readonly MailboxCommand HighImportance = new()
    {
        Id = new("compose.importance.high"),
        Label = "High Importance",
        Description = "Mark this message as high importance.",
        Icon = "importance",
        Category = "Tags",
        Scope = ModuleScope.Mail,
        KeyTip = "HI",
    };

    public static readonly MailboxCommand LowImportance = new()
    {
        Id = new("compose.importance.low"),
        Label = "Low Importance",
        Description = "Mark this message as low importance.",
        Icon = "chevron-down",
        Category = "Tags",
        Scope = ModuleScope.Mail,
        KeyTip = "LO",
    };

    public static readonly MailboxCommand Properties = new()
    {
        Id = new("compose.properties"),
        Label = "Properties…",
        Description = "Open this message's properties: importance, sensitivity and tracking.",
        Icon = "settings",
        Category = "Tags",
        Scope = ModuleScope.Mail,
        KeyTip = "PR",
    };

    // ---- Message tab · Voice and Editor -----------------------------------------------

    public static readonly MailboxCommand Dictate = new()
    {
        Id = new("compose.dictate"),
        Label = "Dictate",
        Description = "Write the message by speaking it.",
        Icon = "dictate",
        Category = "Voice",
        Scope = ModuleScope.Mail,
        KeyTip = "D",
    };

    public static readonly MailboxCommand Editor = new()
    {
        Id = new("review.editor"),
        Label = "Editor",
        Description = "Check spelling, grammar and writing style.",
        Icon = "editor",
        Category = "Proofing",
        Scope = ModuleScope.Mail,
        KeyTip = "ED",
    };

    // ---- Insert tab -------------------------------------------------------------------

    public static readonly MailboxCommand Table = new()
    {
        Id = new("insert.table"),
        Label = "Table",
        Description = "Insert a table.",
        Icon = "table",
        Category = "Tables",
        Scope = ModuleScope.Mail,
        KeyTip = "T",
    };

    public static readonly MailboxCommand Pictures = new()
    {
        Id = new("insert.pictures"),
        Label = "Pictures",
        Description = "Insert a picture from this computer.",
        Icon = "picture",
        Category = "Illustrations",
        Scope = ModuleScope.Mail,
        KeyTip = "P",
    };

    public static readonly MailboxCommand StockImages = new()
    {
        Id = new("insert.stockimages"),
        Label = "Stock Images",
        Description = "Insert a picture from a stock image library.",
        Icon = "stock-images",
        Category = "Illustrations",
        Scope = ModuleScope.Mail,
        KeyTip = "F",
    };

    public static readonly MailboxCommand OnlinePictures = new()
    {
        Id = new("insert.onlinepictures"),
        Label = "Online Pictures",
        Description = "Insert a picture from the web.",
        Icon = "online-pictures",
        Category = "Illustrations",
        Scope = ModuleScope.Mail,
        KeyTip = "O",
    };

    public static readonly MailboxCommand Shapes = new()
    {
        Id = new("insert.shapes"),
        Label = "Shapes",
        Description = "Insert a ready-made shape.",
        Icon = "shapes",
        Category = "Illustrations",
        Scope = ModuleScope.Mail,
        KeyTip = "SH",
    };

    public static readonly MailboxCommand Icons = new()
    {
        Id = new("insert.icons"),
        Label = "Icons",
        Description = "Insert an icon.",
        Icon = "icons",
        Category = "Illustrations",
        Scope = ModuleScope.Mail,
        KeyTip = "IC",
    };

    public static readonly MailboxCommand Models3D = new()
    {
        Id = new("insert.models3d"),
        Label = "3D Models",
        Description = "Insert a three-dimensional model.",
        Icon = "3d-models",
        Category = "Illustrations",
        Scope = ModuleScope.Mail,
        KeyTip = "M3",
    };

    public static readonly MailboxCommand SmartArt = new()
    {
        Id = new("insert.smartart"),
        Label = "SmartArt",
        Description = "Insert a diagram.",
        Icon = "smartart",
        Category = "Illustrations",
        Scope = ModuleScope.Mail,
        KeyTip = "M",
    };

    public static readonly MailboxCommand Chart = new()
    {
        Id = new("insert.chart"),
        Label = "Chart",
        Description = "Insert a chart.",
        Icon = "chart",
        Category = "Illustrations",
        Scope = ModuleScope.Mail,
        KeyTip = "G2",
    };

    public static readonly MailboxCommand Equation = new()
    {
        Id = new("insert.equation"),
        Label = "Equation",
        Description = "Insert a mathematical equation.",
        Icon = "equation",
        Category = "Symbols",
        Scope = ModuleScope.Mail,
        KeyTip = "E2",
    };

    public static readonly MailboxCommand Symbol = new()
    {
        Id = new("insert.symbol"),
        Label = "Symbol",
        Description = "Insert a character that is not on the keyboard.",
        Icon = "symbol",
        Category = "Symbols",
        Scope = ModuleScope.Mail,
        KeyTip = "U2",
    };

    // ---- Options tab ------------------------------------------------------------------

    public static readonly MailboxCommand Themes = new()
    {
        Id = new("options.themes"),
        Label = "Themes",
        Description = "Change the overall look of this message.",
        Icon = "themes",
        Category = "Themes",
        Scope = ModuleScope.Mail,
        KeyTip = "TT",
    };

    public static readonly MailboxCommand ThemeColors = new()
    {
        Id = new("options.colors"),
        Label = "Colors",
        Description = "Change the theme's colour set.",
        Icon = "theme-colors",
        Category = "Themes",
        Scope = ModuleScope.Mail,
        KeyTip = "TC",
    };

    public static readonly MailboxCommand ThemeFonts = new()
    {
        Id = new("options.fonts"),
        Label = "Fonts",
        Description = "Change the theme's heading and body fonts.",
        Icon = "font",
        Category = "Themes",
        Scope = ModuleScope.Mail,
        KeyTip = "TF",
    };

    public static readonly MailboxCommand ThemeEffects = new()
    {
        Id = new("options.effects"),
        Label = "Effects",
        Description = "Change the theme's effects.",
        Icon = "theme-effects",
        Category = "Themes",
        Scope = ModuleScope.Mail,
        KeyTip = "TE",
    };

    public static readonly MailboxCommand PageColor = new()
    {
        Id = new("options.pagecolor"),
        Label = "Page Color",
        Description = "Colour the background of the message body.",
        Icon = "page-color",
        Category = "Themes",
        Scope = ModuleScope.Mail,
        KeyTip = "PC",
    };

    public static readonly MailboxCommand ShowBcc = new()
    {
        Id = new("options.bcc"),
        Label = "Bcc",
        Description = "Show the Bcc field, whose recipients no other recipient can see.",
        Icon = "bcc",
        Category = "Show Fields",
        Scope = ModuleScope.Mail,
        KeyTip = "B2",
    };

    public static readonly MailboxCommand ShowFrom = new()
    {
        Id = new("options.from"),
        Label = "From",
        Description = "Show the From field, to send from a different account.",
        Icon = "from-field",
        Category = "Show Fields",
        Scope = ModuleScope.Mail,
        KeyTip = "F2",
    };

    public static readonly MailboxCommand Permission = new()
    {
        Id = new("options.permission"),
        Label = "Permission",
        Description = "Restrict what recipients may do with this message.",
        Icon = "permission",
        Category = "Permission",
        Scope = ModuleScope.Mail,
        KeyTip = "PM",
    };

    public static readonly MailboxCommand Encrypt = new()
    {
        Id = new("options.encrypt"),
        Label = "Encrypt",
        Description = "Encrypt this message so only the recipients can read it.",
        Icon = "encrypt",
        Category = "Permission",
        Scope = ModuleScope.Mail,
        KeyTip = "EN",
    };

    public static readonly MailboxCommand Sign = new()
    {
        Id = new("options.sign"),
        Label = "Sign",
        Description = "Add a digital signature so recipients can verify the sender.",
        Icon = "sign",
        Category = "Permission",
        Scope = ModuleScope.Mail,
        KeyTip = "SG",
    };

    public static readonly MailboxCommand VotingButtons = new()
    {
        Id = new("options.voting"),
        Label = "Use Voting Buttons",
        Description = "Add buttons recipients can use to reply with one of a set of answers.",
        Icon = "voting",
        Category = "Tracking",
        Scope = ModuleScope.Mail,
        KeyTip = "VO",
    };

    public static readonly MailboxCommand DeliveryReceipt = new()
    {
        Id = new("options.receipt.delivery"),
        Label = "Request a Delivery Receipt",
        Description = "Ask to be told when this message reaches the recipient's server.",
        Icon = "receipt",
        Category = "Tracking",
        Scope = ModuleScope.Mail,
        KeyTip = "RD",
    };

    public static readonly MailboxCommand ReadReceipt = new()
    {
        Id = new("options.receipt.read"),
        Label = "Request a Read Receipt",
        Description = "Ask to be told when the recipient opens this message.",
        Icon = "receipt",
        Category = "Tracking",
        Scope = ModuleScope.Mail,
        KeyTip = "RR",
    };

    public static readonly MailboxCommand SaveSentItemTo = new()
    {
        Id = new("options.savesentto"),
        Label = "Save Sent Item To",
        Description = "Choose which folder keeps the copy of this message.",
        Icon = "folder",
        Category = "More Options",
        Scope = ModuleScope.Mail,
        KeyTip = "SS",
    };

    public static readonly MailboxCommand DelayDelivery = new()
    {
        Id = new("options.delay"),
        Label = "Delay Delivery",
        Description = "Hold this message in the Outbox until a time you choose.",
        Icon = "delay-delivery",
        Category = "More Options",
        Scope = ModuleScope.Mail,
        KeyTip = "DD",
    };

    public static readonly MailboxCommand DirectRepliesTo = new()
    {
        Id = new("options.directreplies"),
        Label = "Direct Replies To",
        Description = "Send replies to a different address than the one this is sent from.",
        Icon = "direct-replies",
        Category = "More Options",
        Scope = ModuleScope.Mail,
        KeyTip = "DR",
    };

    // ---- Review tab -------------------------------------------------------------------

    public static readonly MailboxCommand Spelling = new()
    {
        Id = new("review.spelling"),
        Label = "Spelling and Grammar",
        Description = "Check the message for spelling and grammar mistakes.",
        Icon = "spelling",
        Category = "Proofing",
        Scope = ModuleScope.Mail,
        KeyTip = "S",
        DefaultGesture = "F7",
    };

    public static readonly MailboxCommand Thesaurus = new()
    {
        Id = new("review.thesaurus"),
        Label = "Thesaurus",
        Description = "Look up other words with the same meaning.",
        Icon = "thesaurus",
        Category = "Proofing",
        Scope = ModuleScope.Mail,
        KeyTip = "TS",
    };

    public static readonly MailboxCommand WordCount = new()
    {
        Id = new("review.wordcount"),
        Label = "Word Count",
        Description = "Count the words, characters and paragraphs in this message.",
        Icon = "word-count",
        Category = "Proofing",
        Scope = ModuleScope.Mail,
        KeyTip = "W",
    };

    public static readonly MailboxCommand SmartLookup = new()
    {
        Id = new("review.smartlookup"),
        Label = "Smart Lookup",
        Description = "Look the selected text up on the web.",
        Icon = "smart-lookup",
        Category = "Insights",
        Scope = ModuleScope.Mail,
        KeyTip = "SM",
    };

    public static readonly MailboxCommand Language = new()
    {
        Id = new("review.language"),
        Label = "Language",
        Description = "Set the proofing language for the selected text.",
        Icon = "language",
        Category = "Language",
        Scope = ModuleScope.Mail,
        KeyTip = "LA",
    };

    public static readonly MailboxCommand CheckAccessibility = new()
    {
        Id = new("review.accessibility"),
        Label = "Check Accessibility",
        Description = "Find things in this message that are hard for some people to read.",
        Icon = "accessibility",
        Category = "Accessibility",
        Scope = ModuleScope.Mail,
        KeyTip = "A1",
    };

    /// <summary>Every command this class declares, for registration.</summary>
    public static IReadOnlyList<MailboxCommand> All { get; } =
    [
        Send, SaveDraft, Discard, PreviousItem, NextItem,
        Paste, Cut, Copy, FormatPainter,
        Font, FontSize, GrowFont, ShrinkFont, Bold, Italic, Underline, Strikethrough,
        Subscript, Superscript, Highlight, FontColor, ClearFormatting, FontDialog,
        Bullets, Numbering, MultilevelList, DecreaseIndent, IncreaseIndent, Align,
        LineSpacing, ShowParagraphMarks, Borders, Shading, Sort, ParagraphDialog,
        Styles, ChangeStyles, StylesDialog,
        FormatHtml, FormatPlainText, FormatRichText,
        Find, Replace, SelectAll, Zoom,
        CheckNames,
        AttachFile, AttachItem, Signature, Link,
        HighImportance, LowImportance, Properties,
        Dictate, Editor,
        Table, Pictures, StockImages, OnlinePictures, Shapes, Icons, Models3D, SmartArt, Chart,
        Equation, Symbol,
        Themes, ThemeColors, ThemeFonts, ThemeEffects, PageColor,
        ShowBcc, ShowFrom,
        Permission, Encrypt, Sign,
        VotingButtons, DeliveryReceipt, ReadReceipt,
        SaveSentItemTo, DelayDelivery, DirectRepliesTo,
        Spelling, Thesaurus, WordCount, SmartLookup, Language, CheckAccessibility,
    ];
}
