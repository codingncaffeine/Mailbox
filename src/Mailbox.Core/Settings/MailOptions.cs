namespace Mailbox.Core.Settings;

/// <summary>What a new message is composed as.</summary>
public enum ComposeFormat
{
    Html,
    PlainText,
    RichText,
}

/// <summary>
/// The Mail page's settings, read by the code that acts on them.
/// </summary>
/// <remarks>
/// The Options pages persist every row under a key, and until something reads that key the
/// setting is a checkbox that remembers itself and does nothing else. This is the reading half:
/// one typed accessor per setting that has a feature behind it, so the compose window, the
/// sender and the shell ask here rather than each remembering a string.
/// <para>
/// The keys are named here and declared on the rows, so a page can be reworded without a
/// choice silently resetting — the plan's own rule for a setting the code refers to. A setting
/// with no accessor here is one nothing reads yet, and §20 says so per row.
/// </para>
/// </remarks>
public sealed class MailOptions(SettingsStore settings)
{
    private readonly SettingsStore _settings =
        settings ?? throw new ArgumentNullException(nameof(settings));

    // ---- Keys, declared on the rows in OptionsPages ---------------------------------------

    public const string ComposeFormatKey = "mail.compose.format";
    public const string CheckSpellingBeforeSendKey = "mail.compose.spellcheck.beforesend";
    public const string AutosaveMinutesKey = "mail.compose.autosave.minutes";
    public const string SaveCopiesInSentKey = "mail.send.savecopies";
    public const string DefaultImportanceKey = "mail.send.importance";
    public const string DefaultSensitivityKey = "mail.send.sensitivity";
    public const string AlwaysUseDefaultAccountKey = "mail.send.defaultaccount";
    public const string CommasSeparateRecipientsKey = "mail.send.commas";
    public const string AutomaticNameCheckingKey = "mail.send.checknames";
    public const string CtrlEnterSendsKey = "mail.send.ctrlenter";
    public const string UseAutoCompleteListKey = "mail.send.autocomplete";
    public const string OpenRepliesInNewWindowKey = "mail.reply.newwindow";
    public const string CloseOriginalOnReplyKey = "mail.reply.closeoriginal";
    public const string RequestDeliveryReceiptKey = "mail.tracking.delivery";
    public const string RequestReadReceiptKey = "mail.tracking.read";
    public const string EmptyDeletedOnExitKey = "mail.exit.emptydeleted";
    public const string SendImmediatelyKey = "mail.send.immediately";
    public const string ScheduleMinutesKey = "mail.sendreceive.minutes";
    public const string ReplyStyleKey = "mail.reply.style";
    public const string ForwardStyleKey = "mail.forward.style";
    public const string ReplyPrefixKey = "mail.reply.prefix";
    public const string PrefaceCommentsKey = "mail.reply.preface";
    public const string IgnoreOriginalSpellingKey = "mail.compose.spellcheck.ignoreoriginal";

    // ---- Composing ------------------------------------------------------------------------

    /// <summary>
    /// The format a new message opens in. HTML unless asked otherwise.
    /// </summary>
    /// <remarks>
    /// The row is a combo holding an index — 0 HTML, 1 Rich Text, 2 Plain Text, in the
    /// reference's order — so this reads the index and names it.
    /// </remarks>
    public ComposeFormat ComposeFormat => (int)_settings.GetNumber(ComposeFormatKey, 0) switch
    {
        1 => ComposeFormat.RichText,
        2 => ComposeFormat.PlainText,
        _ => ComposeFormat.Html,
    };

    public bool CheckSpellingBeforeSend => _settings.GetBool(CheckSpellingBeforeSendKey, false);

    /// <summary>How often a message being written is saved to Drafts. Zero is never.</summary>
    public int AutosaveMinutes => Math.Clamp((int)_settings.GetNumber(AutosaveMinutesKey, 3), 0, 99);

    // ---- Sending --------------------------------------------------------------------------

    /// <summary>Whether a message that went files a copy in Sent Items. On, as it should be.</summary>
    public bool SaveCopiesInSent => _settings.GetBool(SaveCopiesInSentKey, true);

    /// <summary>0 Normal, 1 Low, 2 High — the combo's order.</summary>
    public int DefaultImportanceIndex => (int)_settings.GetNumber(DefaultImportanceKey, 0);

    /// <summary>0 Normal, 1 Personal, 2 Private, 3 Confidential — the combo's order.</summary>
    public int DefaultSensitivityIndex => (int)_settings.GetNumber(DefaultSensitivityKey, 0);

    /// <summary>
    /// The <c>Sensitivity</c> header value for the chosen default, or null for Normal, which is
    /// what the header's absence means.
    /// </summary>
    public string? DefaultSensitivityHeader => DefaultSensitivityIndex switch
    {
        1 => "Personal",
        2 => "Private",
        3 => "Company-Confidential",
        _ => null,
    };

    /// <summary>
    /// Whether a new message always comes from the default account, or from the account whose
    /// folder is open. Off in the reference, and off here: writing from the mailbox you are
    /// looking at is what people mean.
    /// </summary>
    public bool AlwaysUseDefaultAccount => _settings.GetBool(AlwaysUseDefaultAccountKey, false);

    public bool CommasSeparateRecipients => _settings.GetBool(CommasSeparateRecipientsKey, true);

    public bool AutomaticNameChecking => _settings.GetBool(AutomaticNameCheckingKey, true);

    public bool CtrlEnterSends => _settings.GetBool(CtrlEnterSendsKey, true);

    /// <summary>
    /// Whether the To, Cc and Bcc lines offer names from the Auto-Complete List as they are
    /// typed. Off, the list is still fed — turning it back on has something to offer.
    /// </summary>
    public bool UseAutoCompleteList => _settings.GetBool(UseAutoCompleteListKey, true);

    /// <summary>
    /// Whether Reply, Reply All and Forward open a separate window rather than an inline strip
    /// in the reading pane. Off, as the reference has it: the reply grows where the message is.
    /// </summary>
    public bool OpenRepliesInNewWindow => _settings.GetBool(OpenRepliesInNewWindowKey, false);

    /// <summary>Whether replying to or forwarding a message in its own window closes that window.</summary>
    public bool CloseOriginalOnReply => _settings.GetBool(CloseOriginalOnReplyKey, false);

    public bool RequestDeliveryReceipt => _settings.GetBool(RequestDeliveryReceiptKey, false);

    public bool RequestReadReceipt => _settings.GetBool(RequestReadReceiptKey, false);

    /// <summary>
    /// Whether a message goes as soon as it can, rather than on the next send/receive. On, as
    /// everywhere: a message that sits in the outbox until F9 is a message people think went.
    /// </summary>
    public bool SendImmediately => _settings.GetBool(SendImmediatelyKey, true);

    // ---- Leaving --------------------------------------------------------------------------

    public bool EmptyDeletedItemsOnExit => _settings.GetBool(EmptyDeletedOnExitKey, false);

    // ---- Replying, for when there is a reply ----------------------------------------------

    /// <summary>The prefix on each quoted line of a plain-text reply.</summary>
    public string ReplyPrefix => _settings.GetString(ReplyPrefixKey, ">");

    /// <summary>What a reply does with the original: an index into the reference's own list.</summary>
    public int ReplyStyleIndex => (int)_settings.GetNumber(ReplyStyleKey, 0);

    public int ForwardStyleIndex => (int)_settings.GetNumber(ForwardStyleKey, 0);
}
