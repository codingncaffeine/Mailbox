namespace Mailbox.Core.Commands;

public enum ComposeCommandState
{
    /// <summary>Does what its label says, today.</summary>
    Working,

    /// <summary>Present on the ribbon, and says what it is waiting for when pressed.</summary>
    Blocked,
}

/// <summary>One compose command, whether it works, and if not, what it is waiting for.</summary>
public sealed record ComposeCommandStatus(
    CommandId Command, ComposeCommandState State, string Note);

/// <summary>
/// What every button in the compose window actually does today.
/// </summary>
/// <remarks>
/// One table, read by three things: the window, so a blocked button says what it is waiting for
/// rather than "not wired yet"; a test, so a command cannot be added to the ribbon without
/// someone deciding which of these it is; and the progress record, so it is derived from the
/// code rather than maintained beside it and left to drift.
/// <para>
/// A blocked button is still a real button — it is placed, it has its screentip and its KeyTip,
/// and it is reachable from the catalogue. What it does not do is pretend.
/// </para>
/// </remarks>
public static class ComposeAvailability
{
    private const string EditorGap =
        "The editor does not offer it. The editor here is a library rather than ours, so what " +
        "is missing is what its document model does not carry.";

    private const string Drawing =
        "Not planned for the editor. A drawing surface is a different program inside this one, " +
        "and mail that needs one is mail with an attachment.";

    private const string Stationery =
        "Not planned. These pick one of the reference's own stationery themes, which are its " +
        "artwork — rule 4. Stationery and Fonts… is built and sets the faces new mail, replies " +
        "and plain text are written in, which is the part that travels.";

    private const string Speech =
        "No decision on record. It needs a speech engine, and choosing one that does not send " +
        "what is on screen off the machine is the open question — the same one Dictate names.";

    private static readonly ComposeCommandStatus[] Table =
    [
        // ---- The window ---------------------------------------------------------------
        new(ComposeCommands.Send.Id, ComposeCommandState.Working,
            "Builds the message and queues it in the outbox, which the next send/receive drains."),
        new(ComposeCommands.SaveDraft.Id, ComposeCommandState.Working, "Saves to Drafts."),
        new(ComposeCommands.Discard.Id, ComposeCommandState.Working,
            "Closes the window, asking first if anything was typed."),
        // Not Phase 4, which is done and opens messages in their own windows. What these want
        // is for the window to know which list it came from, so there is a next item to go to.
        new(ComposeCommands.PreviousItem.Id, ComposeCommandState.Blocked,
            "A compose window does not know which list it was opened from, so there is no "
            + "previous item to step to. The message window does step, because it is opened from "
            + "a row and keeps it (OpenedMessageContext); a draft opened from Drafts would want "
            + "the same, and a new message has no list at all."),
        new(ComposeCommands.NextItem.Id, ComposeCommandState.Blocked,
            "A compose window does not know which list it was opened from, so there is no next "
            + "item to step to. See Previous Item."),
        new(MailCommands.Undo.Id, ComposeCommandState.Working, "Undoes the last edit to the body."),
        new(ViewCommands.Redo.Id, ComposeCommandState.Working, "Redoes it."),

        // ---- Clipboard ----------------------------------------------------------------
        new(ComposeCommands.Paste.Id, ComposeCommandState.Working,
            "Pastes, keeping the formatting where the clipboard carries any."),
        new(ComposeCommands.Cut.Id, ComposeCommandState.Working,
            "Cuts the selection. The editor answers this as a key, so the button presses it."),
        new(ComposeCommands.Copy.Id, ComposeCommandState.Working,
            "Copies the selection, the same way."),
        new(ComposeCommands.FormatPainter.Id, ComposeCommandState.Working,
            "Picks up the formatting at the caret, then paints the next selection with it."),

        // ---- Font ---------------------------------------------------------------------
        new(ComposeCommands.Font.Id, ComposeCommandState.Working,
            "Picks a family, listing the Microsoft names and saying what each will actually " +
            "look like here and to a recipient."),
        new(ComposeCommands.FontSize.Id, ComposeCommandState.Working, "Sets the size in points."),
        new(ComposeCommands.GrowFont.Id, ComposeCommandState.Working, "One step larger."),
        new(ComposeCommands.ShrinkFont.Id, ComposeCommandState.Working, "One step smaller."),
        new(ComposeCommands.Bold.Id, ComposeCommandState.Working, "Bolds the selection."),
        new(ComposeCommands.Italic.Id, ComposeCommandState.Working, "Italicises the selection."),
        new(ComposeCommands.Underline.Id, ComposeCommandState.Working, "Underlines the selection."),
        new(ComposeCommands.Strikethrough.Id, ComposeCommandState.Working, "Strikes the selection."),
        new(ComposeCommands.Subscript.Id, ComposeCommandState.Blocked, EditorGap),
        new(ComposeCommands.Superscript.Id, ComposeCommandState.Blocked, EditorGap),
        new(ComposeCommands.Highlight.Id, ComposeCommandState.Working,
            "Highlights the selection in one of the reference's own colours."),
        new(ComposeCommands.FontColor.Id, ComposeCommandState.Working,
            "Colours the selection. Automatic writes no colour and lets the reader's client decide."),
        new(ComposeCommands.ClearFormatting.Id, ComposeCommandState.Blocked, EditorGap),
        new(ComposeCommands.FontDialog.Id, ComposeCommandState.Blocked,
            "Not planned as a dialog. Family, size, colour and highlight each have their own " +
            "button on this tab, and all four work."),

        // ---- Paragraph ----------------------------------------------------------------
        new(ComposeCommands.Bullets.Id, ComposeCommandState.Working, "Makes the paragraphs a bulleted list."),
        new(ComposeCommands.Numbering.Id, ComposeCommandState.Working, "Makes them a numbered list."),
        new(ComposeCommands.MultilevelList.Id, ComposeCommandState.Working,
            "Sets the marker — disc, circle, square, dash, numbers, letters or roman."),
        new(ComposeCommands.DecreaseIndent.Id, ComposeCommandState.Working, "Outdents the paragraph."),
        new(ComposeCommands.IncreaseIndent.Id, ComposeCommandState.Working, "Indents it."),
        new(ComposeCommands.Align.Id, ComposeCommandState.Working,
            "Left, centre, right or justified."),
        new(ComposeCommands.LineSpacing.Id, ComposeCommandState.Working,
            "Single, 1.15, 1.5 or double."),
        new(ComposeCommands.ShowParagraphMarks.Id, ComposeCommandState.Blocked, EditorGap),
        new(ComposeCommands.Borders.Id, ComposeCommandState.Blocked, EditorGap),
        new(ComposeCommands.Shading.Id, ComposeCommandState.Blocked, EditorGap),
        new(ComposeCommands.Sort.Id, ComposeCommandState.Blocked, EditorGap),
        new(ComposeCommands.ParagraphDialog.Id, ComposeCommandState.Blocked,
            "Not planned as a dialog. Alignment, indent, spacing and the list markers each have " +
            "their own button on this tab, and all four work."),

        // ---- Styles and format --------------------------------------------------------
        new(ComposeCommands.Styles.Id, ComposeCommandState.Blocked,
            "The editor does not offer named paragraph styles — it carries headings and quotes " +
            "and no style sheet."),
        new(ComposeCommands.ChangeStyles.Id, ComposeCommandState.Blocked,
            "The editor does not offer named paragraph styles — it carries headings and quotes " +
            "and no style sheet."),
        new(ComposeCommands.StylesDialog.Id, ComposeCommandState.Blocked,
            "The editor does not offer named paragraph styles — it carries headings and quotes " +
            "and no style sheet."),
        new(ComposeCommands.FormatPlainText.Id, ComposeCommandState.Working,
            "Sends this message as plain text alone. The document keeps its formatting on "
            + "screen — what changes is what leaves — and the window says so."),
        new(ComposeCommands.FormatHtml.Id, ComposeCommandState.Working,
            "The format this window composes in, and says so."),
        new(ComposeCommands.FormatRichText.Id, ComposeCommandState.Blocked,
            "No decision on record, and it is the owner's: whether Mailbox should send RTF at " +
            "all. The mode is persisted and composes as HTML; the editor serializes RTF but has " +
            "not been held to the standard the HTML was, and TNEF is read here and never " +
            "written."),

        // ---- Editing ------------------------------------------------------------------
        new(ComposeCommands.Find.Id, ComposeCommandState.Working, "Finds text in the body."),
        new(ComposeCommands.Replace.Id, ComposeCommandState.Working,
            "Finds text in the body and replaces it."),
        new(ComposeCommands.SelectAll.Id, ComposeCommandState.Working, "Selects the whole body."),
        new(ComposeCommands.Zoom.Id, ComposeCommandState.Working, "Scales the body text."),

        // ---- Names --------------------------------------------------------------------
        new(MailCommands.AddressBook.Id, ComposeCommandState.Working,
            "Opens Select Names: the address book, and the three lines to put people on."),
        new(ComposeCommands.CheckNames.Id, ComposeCommandState.Working,
            "Resolves a bare name against the address book and writes the address in when only " +
            "one contact matches, names it as ambiguous when several do, and says which of what " +
            "is left does not parse."),

        // ---- Include ------------------------------------------------------------------
        new(ComposeCommands.AttachFile.Id, ComposeCommandState.Working,
            "Picks files and attaches them to the sent message."),
        new(ComposeCommands.AttachItem.Id, ComposeCommandState.Blocked,
            "Attaching another stored message needs something to pick it with. Search finds "
            + "messages and the folder picker picks folders; what is missing is a message "
            + "picker — a folder tree with a list beside it — and nothing else wants one yet."),
        new(ComposeCommands.Signature.Id, ComposeCommandState.Working,
            "Inserts a signature at the caret, and is where they are written and removed. An "
            + "account can sign new messages automatically; none does unless asked to."),
        new(ComposeCommands.Link.Id, ComposeCommandState.Working, "Inserts a real hyperlink."),

        // ---- Tags ---------------------------------------------------------------------
        new(ComposeCommands.HighImportance.Id, ComposeCommandState.Working,
            "Sets the message's priority headers."),
        new(ComposeCommands.LowImportance.Id, ComposeCommandState.Working,
            "Sets the message's priority headers."),
        new(MailCommands.FollowUp.Id, ComposeCommandState.Blocked,
            "Flagging on the way out is a different thing from flagging a stored row, which "
            + "works: it asks the recipient to follow up, and travels as an X-Message-Flag "
            + "header nothing here writes or reads. A message being composed has no row of its "
            + "own to carry a flag either, until it is saved."),
        new(ComposeCommands.Properties.Id, ComposeCommandState.Blocked,
            "The message properties dialog is not built. Everything on it that has a home here "
            + "already has a button on this tab — sensitivity aside, which has nowhere to go: "
            + "the header exists, and no client on the receiving end is obliged to honour it."),

        // ---- Voice, apps, editor ------------------------------------------------------
        new(ComposeCommands.Dictate.Id, ComposeCommandState.Blocked,
            "No decision on record. Rule 1 says build it; no speech engine has been chosen, " +
            "and doing it without one sending audio off the machine is the open question."),
        new(ViewCommands.Apps.Id, ComposeCommandState.Working,
            "Opens the installed plugins' commands, grouped by plugin, through the same "
            + "dispatcher as everywhere else."),
        new(ComposeCommands.Editor.Id, ComposeCommandState.Working,
            "Runs the spelling check over the message."),
        new(ViewCommands.ImmersiveReader.Id, ComposeCommandState.Blocked,
            "The editor does not offer it. Line focus, syllables and a column width are a second "
            + "way of laying out the same document, and the editor lays out one way."),

        // ---- Insert -------------------------------------------------------------------
        new(ComposeCommands.Table.Id, ComposeCommandState.Working,
            "Inserts a table, up to 50 rows by 20 columns."),
        new(ComposeCommands.Pictures.Id, ComposeCommandState.Working,
            "Inserts a picture from a file. It travels with the message as a related part."),
        new(ComposeCommands.StockImages.Id, ComposeCommandState.Blocked,
            "Not planned. A stock image library is a hosted service, and Mailbox operates none."),
        new(ComposeCommands.OnlinePictures.Id, ComposeCommandState.Blocked,
            "Not planned. Fetching a picture from the web on the sender's behalf is the same " +
            "privacy problem the reading pane refuses; a local file is the supported path."),
        new(ComposeCommands.Shapes.Id, ComposeCommandState.Blocked, Drawing),
        new(ComposeCommands.Icons.Id, ComposeCommandState.Blocked, Drawing),
        new(ComposeCommands.Models3D.Id, ComposeCommandState.Blocked,
            "Not planned. A 3D model in an email renders nowhere the recipient is likely to be."),
        new(ComposeCommands.SmartArt.Id, ComposeCommandState.Blocked, Drawing),
        new(ComposeCommands.Chart.Id, ComposeCommandState.Blocked, Drawing),
        new(ComposeCommands.Equation.Id, ComposeCommandState.Blocked,
            "Not planned for now. An equation editor is its own project, and mail that needs " +
            "one is better served by a picture of one."),
        new(ComposeCommands.Symbol.Id, ComposeCommandState.Working,
            "Inserts a character into the body from a picker."),

        // ---- Options ------------------------------------------------------------------
        new(ComposeCommands.Themes.Id, ComposeCommandState.Blocked, Stationery),
        new(ComposeCommands.ThemeColors.Id, ComposeCommandState.Blocked, Stationery),
        new(ComposeCommands.ThemeFonts.Id, ComposeCommandState.Blocked, Stationery),
        new(ComposeCommands.ThemeEffects.Id, ComposeCommandState.Blocked, Stationery),
        new(ComposeCommands.PageColor.Id, ComposeCommandState.Blocked, Stationery),
        new(ComposeCommands.ShowBcc.Id, ComposeCommandState.Working, "Shows and hides the Bcc field."),
        new(ComposeCommands.ShowFrom.Id, ComposeCommandState.Working,
            "Shows the From field, which picks which account sends."),
        new(ComposeCommands.Permission.Id, ComposeCommandState.Blocked,
            "Not planned. Restricting what a recipient may do with a message needs a rights " +
            "management server, which is exactly the tenant infrastructure that is out of scope."),
        new(ComposeCommands.Encrypt.Id, ComposeCommandState.Working,
            "Encrypts the message to every recipient and to its author, in whichever of S/MIME and " +
            "OpenPGP holds keys for all of them. A recipient with no key stops the send and is named."),
        new(ComposeCommands.Sign.Id, ComposeCommandState.Working,
            "Signs the message with this account's own key, immediately before it goes and never " +
            "on a draft. Needs S/MIME or OpenPGP switched on in the Trust Center first."),
        new(ComposeCommands.VotingButtons.Id, ComposeCommandState.Blocked,
            "Voting rides TNEF, which is read here and never written — the same gap Rich Text "
            + "names. A tally would then need the responses collected against the sent message, "
            + "which is a second half nothing has asked for yet."),
        new(ComposeCommands.DeliveryReceipt.Id, ComposeCommandState.Working,
            "Adds the Return-Receipt-To header."),
        new(ComposeCommands.ReadReceipt.Id, ComposeCommandState.Working,
            "Adds the Disposition-Notification-To header."),
        new(ComposeCommands.SaveSentItemTo.Id, ComposeCommandState.Blocked,
            "Not the folder picker, which exists and which Move To uses: what is missing is a "
            + "per-message override to put in it. The send path files the copy in the account's "
            + "own Sent Items, and nothing on a message says otherwise."),
        new(ComposeCommands.DelayDelivery.Id, ComposeCommandState.Working,
            "Holds the message in the outbox until the time chosen."),
        new(ComposeCommands.DirectRepliesTo.Id, ComposeCommandState.Working,
            "Sets the Reply-To header."),

        // ---- Review -------------------------------------------------------------------
        new(ComposeCommands.Spelling.Id, ComposeCommandState.Working,
            "Checks the message against the desktop's own Hunspell dictionaries, offers "
            + "corrections, and can be taught a word. Squiggles as you type need the editor to "
            + "draw on its own text run, which it does not offer."),
        new(ComposeCommands.Thesaurus.Id, ComposeCommandState.Blocked,
            "Not planned. No free thesaurus with usable licensing has been chosen."),
        new(ComposeCommands.WordCount.Id, ComposeCommandState.Working,
            "Counts words, characters and paragraphs in the body."),
        new(ViewCommands.ReadAloud.Id, ComposeCommandState.Blocked, Speech),
        new(ComposeCommands.SmartLookup.Id, ComposeCommandState.Blocked,
            "No decision on record. Looking the selection up means sending it to a search " +
            "engine, and whether Mailbox should do that quietly is the owner's call."),
        new(ComposeCommands.Language.Id, ComposeCommandState.Blocked,
            "The scaffolding is in (Localizer, one flat JSON per culture) and the surfaces have "
            + "not adopted it, so there is nothing for a language choice to change yet. Proofing "
            + "language is the other half: the spelling check reads the desktop's dictionaries, "
            + "and picking one per selection wants the editor to carry it on a run."),
        new(ComposeCommands.CheckAccessibility.Id, ComposeCommandState.Blocked,
            "Not the application's own accessibility, which is done: this inspects the message "
            + "being written — alt text on its pictures, contrast, a table's header row — and "
            + "nothing reads the document for that."),
    ];

    public static IReadOnlyList<ComposeCommandStatus> All => Table;

    private static readonly Dictionary<CommandId, ComposeCommandStatus> ById =
        Table.ToDictionary(s => s.Command);

    public static ComposeCommandStatus? For(CommandId id)
        => ById.TryGetValue(id, out var status) ? status : null;

    public static bool Works(CommandId id)
        => For(id) is { State: ComposeCommandState.Working };

    public static int WorkingCount => Table.Count(s => s.State == ComposeCommandState.Working);

    public static int BlockedCount => Table.Count(s => s.State == ComposeCommandState.Blocked);
}
