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
    private const string Editor =
        "Phase 5 — the rich text editor. The body is plain text until it exists.";

    private const string EditorImage =
        "Phase 5 — the document model, which is what an image or shape would be placed into.";

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
        new(MailCommands.Undo.Id, ComposeCommandState.Blocked,
            "Phase 8 — the undo stack. The body's own text box undoes itself in the meantime."),
        new(ViewCommands.Redo.Id, ComposeCommandState.Blocked,
            "Phase 8 — the undo stack. The body's own text box redoes itself in the meantime."),

        // ---- Clipboard ----------------------------------------------------------------
        new(ComposeCommands.Paste.Id, ComposeCommandState.Working, "Plain text into the body."),
        new(ComposeCommands.Cut.Id, ComposeCommandState.Working, "Plain text out of the body."),
        new(ComposeCommands.Copy.Id, ComposeCommandState.Working, "Plain text out of the body."),
        new(ComposeCommands.FormatPainter.Id, ComposeCommandState.Blocked, Editor),

        // ---- Font ---------------------------------------------------------------------
        new(ComposeCommands.Font.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.FontSize.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.GrowFont.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.ShrinkFont.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Bold.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Italic.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Underline.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Strikethrough.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Subscript.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Superscript.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Highlight.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.FontColor.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.ClearFormatting.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.FontDialog.Id, ComposeCommandState.Blocked, Editor),

        // ---- Paragraph ----------------------------------------------------------------
        new(ComposeCommands.Bullets.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Numbering.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.MultilevelList.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.DecreaseIndent.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.IncreaseIndent.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Align.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.LineSpacing.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.ShowParagraphMarks.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Borders.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Shading.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.Sort.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.ParagraphDialog.Id, ComposeCommandState.Blocked, Editor),

        // ---- Styles and format --------------------------------------------------------
        new(ComposeCommands.Styles.Id, ComposeCommandState.Blocked,
            "Phase 5, stage 4 — paragraph styles are the last stage of the editor."),
        new(ComposeCommands.ChangeStyles.Id, ComposeCommandState.Blocked,
            "Phase 5, stage 4 — paragraph styles are the last stage of the editor."),
        new(ComposeCommands.StylesDialog.Id, ComposeCommandState.Blocked,
            "Phase 5, stage 4 — paragraph styles are the last stage of the editor."),
        new(ComposeCommands.FormatPlainText.Id, ComposeCommandState.Working,
            "The only format the body can produce today, and the one it is already in."),
        new(ComposeCommands.FormatHtml.Id, ComposeCommandState.Blocked, Editor),
        new(ComposeCommands.FormatRichText.Id, ComposeCommandState.Blocked,
            "Phase 5, stage 3 — RTF serialization."),

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
        new(ComposeCommands.Link.Id, ComposeCommandState.Working,
            "Inserts the address as text, which is what a plain-text body can carry. " +
            "A real hyperlink is Phase 5."),

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
            "Phase 5 — spelling and grammar run against the editor's document."),
        new(ViewCommands.ImmersiveReader.Id, ComposeCommandState.Blocked, I18n),

        // ---- Insert -------------------------------------------------------------------
        new(ComposeCommands.Table.Id, ComposeCommandState.Blocked,
            "Phase 5, stage 3 — table layout is the hardest single piece of the editor."),
        new(ComposeCommands.Pictures.Id, ComposeCommandState.Blocked, EditorImage),
        new(ComposeCommands.StockImages.Id, ComposeCommandState.Blocked,
            "Not planned. A stock image library is a hosted service, and Mailbox operates none."),
        new(ComposeCommands.OnlinePictures.Id, ComposeCommandState.Blocked,
            "Not planned. Fetching a picture from the web on the sender's behalf is the same " +
            "privacy problem the reading pane refuses; a local file is the supported path."),
        new(ComposeCommands.Shapes.Id, ComposeCommandState.Blocked, EditorImage),
        new(ComposeCommands.Icons.Id, ComposeCommandState.Blocked, EditorImage),
        new(ComposeCommands.Models3D.Id, ComposeCommandState.Blocked,
            "Not planned. A 3D model in an email renders nowhere the recipient is likely to be."),
        new(ComposeCommands.SmartArt.Id, ComposeCommandState.Blocked, EditorImage),
        new(ComposeCommands.Chart.Id, ComposeCommandState.Blocked, EditorImage),
        new(ComposeCommands.Equation.Id, ComposeCommandState.Blocked,
            "Phase 5 — an equation needs a document model to sit in."),
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
            "Phase 5, stage 3 — Hunspell runs against the editor's document."),
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
