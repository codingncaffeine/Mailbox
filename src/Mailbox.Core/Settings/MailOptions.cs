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
    public const string JunkLevelKey = "mail.junk.level";
    public const string JunkDeleteKey = "mail.junk.delete";
    public const string JunkDisableLinksKey = "mail.junk.disablelinks";
    public const string JunkWarnDomainsKey = "mail.junk.warndomains";
    public const string JunkTrustContactsKey = "mail.junk.trustcontacts";
    public const string JunkSafeAutoAddKey = "mail.junk.autoaddrecipients";
    public const string RecoverDaysKey = "mail.recover.days";
    public const string ShowRemindersKey = "reminders.show";
    public const string ReminderSoundKey = "reminders.sound";
    public const string RemindersOnTopKey = "reminders.ontop";
    public const string FocusedInboxKey = "view.focusedinbox";
    public const string DesktopAlertKey = "mail.arrival.alert";
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

    /// <summary>
    /// How hard the junk filter works, 0..3 = Off / Low / High / Safe Lists Only. Low is the
    /// reference's default: only the most obvious junk, few wanted messages caught by mistake.
    /// </summary>
    public int JunkLevelIndex
    {
        get => (int)_settings.GetNumber(JunkLevelKey, 1);
        set => _settings.Set(JunkLevelKey, Math.Clamp(value, 0, 3));
    }

    /// <summary>
    /// Whether suspected junk is deleted outright rather than filed in Junk. Off, and the
    /// dialog says why it is a bad idea: a message the filter got wrong is gone.
    /// </summary>
    public bool DeleteSuspectedJunk
    {
        get => _settings.GetBool(JunkDeleteKey, false);
        set => _settings.Set(JunkDeleteKey, value);
    }

    /// <summary>Whether links in a message in the Junk folder are drawn inert. On.</summary>
    public bool DisableLinksInJunk
    {
        get => _settings.GetBool(JunkDisableLinksKey, true);
        set => _settings.Set(JunkDisableLinksKey, value);
    }

    /// <summary>Whether the reading pane warns about lookalike sender domains. On.</summary>
    public bool WarnAboutSuspiciousDomains
    {
        get => _settings.GetBool(JunkWarnDomainsKey, true);
        set => _settings.Set(JunkWarnDomainsKey, value);
    }

    /// <summary>
    /// Whether everyone a message is sent to joins the safe-senders list. Off, as the reference
    /// has it: it makes the list large and the Auto-Complete List already remembers them.
    /// </summary>
    public bool AutoAddRecipientsToSafeSenders
    {
        get => _settings.GetBool(JunkSafeAutoAddKey, false);
        set => _settings.Set(JunkSafeAutoAddKey, value);
    }

    /// <summary>Whether mail from a contact is never junk. On; the contacts arrive with Phase 12.</summary>
    public bool TrustContacts
    {
        get => _settings.GetBool(JunkTrustContactsKey, true);
        set => _settings.Set(JunkTrustContactsKey, value);
    }

    /// <summary>Whether a send/receive that brought new mail shows a desktop notification.</summary>
    public bool DisplayDesktopAlert => _settings.GetBool(DesktopAlertKey, true);

    public bool RequestDeliveryReceipt => _settings.GetBool(RequestDeliveryReceiptKey, false);

    public bool RequestReadReceipt => _settings.GetBool(RequestReadReceiptKey, false);

    /// <summary>
    /// Whether a message goes as soon as it can, rather than on the next send/receive. On, as
    /// everywhere: a message that sits in the outbox until F9 is a message people think went.
    /// </summary>
    public bool SendImmediately => _settings.GetBool(SendImmediatelyKey, true);

    /// <summary>
    /// How long a permanently deleted message can still be recovered, in days (§11). Thirty, as
    /// the reference's servers keep them; 0 keeps nothing.
    /// </summary>
    public int RecoverDays => Math.Clamp((int)_settings.GetNumber(RecoverDaysKey, 30), 0, 365);

    /// <summary>Focused Inbox (§12): whether the Inbox is split into Focused and Other. Off until asked.</summary>
    public bool ShowFocusedInbox
    {
        get => _settings.GetBool(FocusedInboxKey, false);
        set => _settings.Set(FocusedInboxKey, value);
    }

    // ---- Reminders (Options › Advanced) ---------------------------------------------------

    /// <summary>Whether the Reminders window opens when a flag's reminder time comes. On.</summary>
    public bool ShowReminders => _settings.GetBool(ShowRemindersKey, true);

    /// <summary>Whether a reminder plays the desktop's alarm sound. On.</summary>
    public bool PlayReminderSound => _settings.GetBool(ReminderSoundKey, true);

    /// <summary>Whether the Reminders window stays above other windows. On.</summary>
    public bool RemindersOnTop => _settings.GetBool(RemindersOnTopKey, true);

    // ---- Leaving --------------------------------------------------------------------------

    public bool EmptyDeletedItemsOnExit => _settings.GetBool(EmptyDeletedOnExitKey, false);

    // ---- Replying, for when there is a reply ----------------------------------------------

    /// <summary>The prefix on each quoted line of a plain-text reply.</summary>
    public string ReplyPrefix => _settings.GetString(ReplyPrefixKey, ">");

    /// <summary>What a reply does with the original: an index into the reference's own list.</summary>
    public int ReplyStyleIndex => (int)_settings.GetNumber(ReplyStyleKey, 0);

    public int ForwardStyleIndex => (int)_settings.GetNumber(ForwardStyleKey, 0);
}
