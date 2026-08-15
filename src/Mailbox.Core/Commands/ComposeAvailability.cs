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
/// someone deciding which of these it is; and the plan, so the progress record is derived from
/// the code rather than maintained beside it and left to drift.
/// <para>
/// A blocked button is still a real button — it is placed, it has its screentip and its KeyTip,
/// and it is reachable from the catalogue. What it does not do is pretend.
/// </para>
/// </remarks>
public static class ComposeAvailability
{
    private const string EditorGap =
        "The editor does not offer it. Phase 5 took a document model rather than building one, " +
        "so what is missing here is what that model does not carry — see the survey in §7.3.";

    private const string Drawing =
        "Not planned for the editor. A drawing surface is a different program inside this one, " +
        "and mail that needs one is mail with an attachment.";

    private const string Stationery =
        "Phase 6 — stationery and themes, and Phase 5 for anywhere to apply them.";

    private const string People =
        "Phase 12 — People. Names resolve against contacts, and there are none yet.";

    private const string Crypto =
        "Phase 15 — S/MIME and OpenPGP, which ship disabled until a key exists.";

    private const string I18n = "Phase 16 — internationalization and the accessibility pass.";

    private static readonly ComposeCommandStatus[] Table =
    [
        // ---- The window ---------------------------------------------------------------
        new(ComposeCommands.Send.Id, ComposeCommandState.Working,
            "Builds the message and queues it in the outbox, which the next send/receive drains."),
        new(ComposeCommands.SaveDraft.Id, ComposeCommandState.Working, "Saves to Drafts."),
        new(ComposeCommands.Discard.Id, ComposeCommandState.Working,
            "Closes the window, asking first if anything was typed."),
        new(ComposeCommands.PreviousItem.Id, ComposeCommandState.Blocked,
            "Phase 4 — opening an existing message in its own window."),
        new(ComposeCommands.NextItem.Id, ComposeCommandState.Blocked,
            "Phase 4 — opening an existing message in its own window."),
        new(MailCommands.Undo.Id, ComposeCommandState.Working, "Undoes the last edit to the body."),
        new(ViewCommands.Redo.Id, ComposeCommandState.Working, "Redoes it."),

        // ---- Clipboard ----------------------------------------------------------------
        new(ComposeCommands.Paste.Id, ComposeCommandState.Working,
            "Pastes, keeping the formatting where the clipboard carries any."),
        new(ComposeCommands.Cut.Id, ComposeCommandState.Working,
            "On Ctrl+X. The editor handles it and exposes no method for the button to call."),
        new(ComposeCommands.Copy.Id, ComposeCommandState.Working,
            "On Ctrl+C. The editor handles it and exposes no method for the button to call."),
        new(ComposeCommands.FormatPainter.Id, ComposeCommandState.Working,
            "Picks up the formatting at the caret, then paints the next selection with it."),

        // ---- Font ---------------------------------------------------------------------
        new(ComposeCommands.Font.Id, ComposeCommandState.Working,
            "Picks a family, listing the Microsoft names and saying what each will actually " +
            "look like here and to a recipient (§6)."),
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
        new(ComposeCommands.FormatPlainText.Id, ComposeCommandState.Blocked,
            "Phase 6 — composing as plain text only. Every message already carries a plain text " +
            "alternative beside its HTML, so a recipient who wants text gets text."),
        new(ComposeCommands.FormatHtml.Id, ComposeCommandState.Working,
            "The format this window composes in, and says so."),
        new(ComposeCommands.FormatRichText.Id, ComposeCommandState.Blocked,
            "Phase 6 — the reference's RTF mode. The editor serializes RTF; what is missing is " +
            "the decision to send it, and TNEF around it."),

        // ---- Editing ------------------------------------------------------------------
        new(ComposeCommands.Find.Id, ComposeCommandState.Working, "Finds text in the body."),
        new(ComposeCommands.Replace.Id, ComposeCommandState.Working,
            "Finds text in the body and replaces it."),
        new(ComposeCommands.SelectAll.Id, ComposeCommandState.Working, "Selects the whole body."),
        new(ComposeCommands.Zoom.Id, ComposeCommandState.Working, "Scales the body text."),

        // ---- Names --------------------------------------------------------------------
        new(MailCommands.AddressBook.Id, ComposeCommandState.Blocked, People),
        new(ComposeCommands.CheckNames.Id, ComposeCommandState.Working,
            "Checks that every address in To, Cc and Bcc parses, and says which do not. " +
            "Resolving a bare name against contacts is Phase 12."),

        // ---- Include ------------------------------------------------------------------
        new(ComposeCommands.AttachFile.Id, ComposeCommandState.Working,
            "Picks files and attaches them to the sent message."),
        new(ComposeCommands.AttachItem.Id, ComposeCommandState.Blocked,
            "Phase 4 — attaching another stored message needs the message picker."),
        new(ComposeCommands.Signature.Id, ComposeCommandState.Blocked,
            "Phase 6 — signatures, which have no settings surface yet."),
        new(ComposeCommands.Link.Id, ComposeCommandState.Working, "Inserts a real hyperlink."),

        // ---- Tags ---------------------------------------------------------------------
        new(ComposeCommands.HighImportance.Id, ComposeCommandState.Working,
            "Sets the message's priority headers."),
        new(ComposeCommands.LowImportance.Id, ComposeCommandState.Working,
            "Sets the message's priority headers."),
        new(MailCommands.FollowUp.Id, ComposeCommandState.Blocked,
            "Phase 8 — follow-up flags with due dates and reminders."),
        new(ComposeCommands.Properties.Id, ComposeCommandState.Blocked,
            "Phase 8 — the message properties dialog."),

        // ---- Voice, apps, editor ------------------------------------------------------
        new(ComposeCommands.Dictate.Id, ComposeCommandState.Blocked,
            "No decision on record. Rule 1 says build it; no speech engine has been chosen, " +
            "and doing it without one sending audio off the machine is the open question."),
        new(ViewCommands.Apps.Id, ComposeCommandState.Blocked,
            "Phase 15 — the plugin host. There are no add-ins to list."),
        new(ComposeCommands.Editor.Id, ComposeCommandState.Blocked,
            "Phase 5's remaining piece — Hunspell against the editor's document."),
        new(ViewCommands.ImmersiveReader.Id, ComposeCommandState.Blocked, I18n),

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
        new(ComposeCommands.Encrypt.Id, ComposeCommandState.Blocked, Crypto),
        new(ComposeCommands.Sign.Id, ComposeCommandState.Blocked, Crypto),
        new(ComposeCommands.VotingButtons.Id, ComposeCommandState.Blocked,
            "Phase 8 — voting needs the reading side to collect the replies."),
        new(ComposeCommands.DeliveryReceipt.Id, ComposeCommandState.Working,
            "Adds the Return-Receipt-To header."),
        new(ComposeCommands.ReadReceipt.Id, ComposeCommandState.Working,
            "Adds the Disposition-Notification-To header."),
        new(ComposeCommands.SaveSentItemTo.Id, ComposeCommandState.Blocked,
            "Phase 3 — the folder picker, which the message list's Move To also waits on."),
        new(ComposeCommands.DelayDelivery.Id, ComposeCommandState.Working,
            "Holds the message in the outbox until the time chosen."),
        new(ComposeCommands.DirectRepliesTo.Id, ComposeCommandState.Working,
            "Sets the Reply-To header."),

        // ---- Review -------------------------------------------------------------------
        new(ComposeCommands.Spelling.Id, ComposeCommandState.Blocked,
            "Phase 5's remaining piece — Hunspell against the editor's document."),
        new(ComposeCommands.Thesaurus.Id, ComposeCommandState.Blocked,
            "Not planned. No free thesaurus with usable licensing has been chosen."),
        new(ComposeCommands.WordCount.Id, ComposeCommandState.Working,
            "Counts words, characters and paragraphs in the body."),
        new(ViewCommands.ReadAloud.Id, ComposeCommandState.Blocked, I18n),
        new(ComposeCommands.SmartLookup.Id, ComposeCommandState.Blocked,
            "No decision on record. Looking the selection up means sending it to a search " +
            "engine, and whether Mailbox should do that quietly is the owner's call."),
        new(ComposeCommands.Language.Id, ComposeCommandState.Blocked, I18n),
        new(ComposeCommands.CheckAccessibility.Id, ComposeCommandState.Blocked, I18n),
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
