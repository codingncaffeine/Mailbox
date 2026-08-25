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

    /// <summary>
    /// The unified mailbox: an "All Accounts" root at the top of the folder pane (§12, §14).
    /// </summary>
    /// <remarks>
    /// Off by default and stays off until somebody says otherwise, because it restructures the
    /// nav tree rather than adding a command — §14's rule that turning one of these on is a
    /// decision rather than a discovery.
    /// </remarks>
    public const string UnifiedMailboxKey = "mail.unified.enabled";
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
    public const string ReminderSoundFileKey = "reminders.sound.file";
    public const string DismissPastRemindersKey = "reminders.dismisspast";
    public const string RemindersOnTopKey = "reminders.ontop";
    public const string FocusedInboxKey = "view.focusedinbox";
    public const string CleanUpKeepUnreadKey = "mail.cleanup.keepunread";
    public const string CleanUpKeepCategorizedKey = "mail.cleanup.keepcategorized";
    public const string CleanUpKeepFlaggedKey = "mail.cleanup.keepflagged";
    public const string CleanUpKeepSignedKey = "mail.cleanup.keepsigned";
    public const string CleanUpKeepModifiedKey = "mail.cleanup.keepmodified";
    public const string CleanUpFolderKey = "mail.cleanup.folder";
    public const string ReadingPaneMarkOnViewKey = "mail.readingpane.markonview";
    public const string ReadingPaneMarkSecondsKey = "mail.readingpane.markseconds";
    public const string ReadingPaneMarkOnChangeKey = "mail.readingpane.markonchange";
    public const string IgnoreConfirmKey = "mail.ignore.confirm";
    public const string DesktopAlertKey = "mail.arrival.alert";
    public const string ArrivalSoundKey = "mail.arrival.sound";
    public const string ArrivalSoundFileKey = "mail.arrival.sound.file";
    public const string RulesOnFeedsKey = "rules.rss";
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

    // Editor Options › Proofing: the switches the checker applies. All on by default, as the
    // reference ships them and as the checker behaved before it could be told otherwise.
    public const string IgnoreUppercaseKey = "mail.spelling.ignoreuppercase";
    public const string IgnoreNumbersKey = "mail.spelling.ignorenumbers";
    public const string IgnoreAddressesKey = "mail.spelling.ignoreaddresses";
    public const string FlagRepeatedKey = "mail.spelling.flagrepeated";

    public bool SpellingIgnoresUppercase => _settings.GetBool(IgnoreUppercaseKey, true);
    public bool SpellingIgnoresNumbers => _settings.GetBool(IgnoreNumbersKey, true);
    public bool SpellingIgnoresAddresses => _settings.GetBool(IgnoreAddressesKey, true);
    public bool SpellingFlagsRepeated => _settings.GetBool(FlagRepeatedKey, true);

    // ---- AutoCorrect ------------------------------------------------------------------------
    //
    // The reference's AutoCorrect dialog, which is two tabs: what it does to words, and what it
    // does to marks and paragraphs. Declared here as keys and read as bools, as the Proofing
    // switches above are, because the rules themselves live in the editor and Core cannot see
    // them — the compose surface is where the two meet.
    //
    // On by default, every one of them, which is how the reference ships and what somebody who
    // has never opened the dialog expects. The two exceptions are the reference's own: the maths
    // table applies inside equations there and there are none here, and hyperlinking is the
    // editor's switch rather than a rule of ours.

    public const string AutocorrectReplaceKey = "mail.autocorrect.replace";
    public const string AutocorrectTwoInitialsKey = "mail.autocorrect.twoinitials";
    public const string AutocorrectSentencesKey = "mail.autocorrect.sentences";
    public const string AutocorrectTableCellsKey = "mail.autocorrect.tablecells";
    public const string AutocorrectDaysKey = "mail.autocorrect.days";
    public const string AutocorrectCapsLockKey = "mail.autocorrect.capslock";
    public const string AutocorrectSuggestionsKey = "mail.autocorrect.suggestions";
    public const string AutocorrectMathKey = "mail.autocorrect.math";
    public const string AutoformatQuotesKey = "mail.autoformat.quotes";
    public const string AutoformatFractionsKey = "mail.autoformat.fractions";
    public const string AutoformatDashesKey = "mail.autoformat.dashes";
    public const string AutoformatEmphasisKey = "mail.autoformat.emphasis";
    public const string AutoformatHyperlinksKey = "mail.autoformat.hyperlinks";
    public const string AutoformatBulletsKey = "mail.autoformat.bullets";
    public const string AutoformatNumberingKey = "mail.autoformat.numbering";
    public const string AutoformatBordersKey = "mail.autoformat.borders";

    /// <summary>The reader's own Replace/With rows, as the difference from the shipped table.</summary>
    public const string AutocorrectTableKey = "mail.autocorrect.table";

    /// <summary>The words the two capital rules leave alone, as one JSON object of two lists.</summary>
    public const string AutocorrectExceptionsKey = "mail.autocorrect.exceptions";

    public bool AutocorrectReplaces => _settings.GetBool(AutocorrectReplaceKey, true);
    public bool AutocorrectTwoInitials => _settings.GetBool(AutocorrectTwoInitialsKey, true);
    public bool AutocorrectSentences => _settings.GetBool(AutocorrectSentencesKey, true);
    public bool AutocorrectTableCells => _settings.GetBool(AutocorrectTableCellsKey, true);
    public bool AutocorrectDays => _settings.GetBool(AutocorrectDaysKey, true);
    public bool AutocorrectCapsLock => _settings.GetBool(AutocorrectCapsLockKey, true);
    public bool AutocorrectSuggestions => _settings.GetBool(AutocorrectSuggestionsKey, true);
    public bool AutocorrectMath => _settings.GetBool(AutocorrectMathKey, false);
    public bool AutoformatQuotes => _settings.GetBool(AutoformatQuotesKey, true);
    public bool AutoformatFractions => _settings.GetBool(AutoformatFractionsKey, true);
    public bool AutoformatDashes => _settings.GetBool(AutoformatDashesKey, true);
    public bool AutoformatEmphasis => _settings.GetBool(AutoformatEmphasisKey, true);
    public bool AutoformatHyperlinks => _settings.GetBool(AutoformatHyperlinksKey, true);
    public bool AutoformatBullets => _settings.GetBool(AutoformatBulletsKey, true);
    public bool AutoformatNumbering => _settings.GetBool(AutoformatNumberingKey, true);
    public bool AutoformatBorders => _settings.GetBool(AutoformatBordersKey, true);

    /// <summary>The Replace/With table's stored difference, for the editor to read back.</summary>
    public string AutocorrectTable => _settings.GetString(AutocorrectTableKey, string.Empty);

    /// <summary>The exception lists as stored, or empty for this build's own.</summary>
    public string AutocorrectExceptions => _settings.GetString(AutocorrectExceptionsKey, string.Empty);

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

    /// <summary>Whether the folder pane opens with an "All Accounts" root. Off unless asked for.</summary>
    public bool UnifiedMailbox => _settings.GetBool(UnifiedMailboxKey, false);

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

    /// <summary>Whether mail from somebody in the address book is never junk. On by default.</summary>
    public bool TrustContacts
    {
        get => _settings.GetBool(JunkTrustContactsKey, true);
        set => _settings.Set(JunkTrustContactsKey, value);
    }

    /// <summary>Whether a send/receive that brought new mail shows a desktop notification.</summary>
    public bool DisplayDesktopAlert => _settings.GetBool(DesktopAlertKey, true);

    /// <summary>Whether mail arriving plays a sound. On, as the reference has it.</summary>
    public bool PlayArrivalSound => _settings.GetBool(ArrivalSoundKey, true);

    /// <summary>
    /// Whether rules run over items downloaded from RSS feeds. Off, as the reference has it.
    /// </summary>
    /// <remarks>
    /// The tick at the foot of Rules and Alerts. Off out of the box for a good reason: a feed
    /// already files itself into a folder of its own, and a rule written for mail — "move
    /// anything from this week into Reading" — would sweep up a hundred articles the first time
    /// it saw them.
    /// </remarks>
    public bool RulesOnFeeds
    {
        get => _settings.GetBool(RulesOnFeedsKey, false);
        set => _settings.Set(RulesOnFeedsKey, value);
    }

    /// <summary>
    /// A sound file to play instead of the desktop's own new-mail sound, or empty for the
    /// desktop's.
    /// </summary>
    /// <remarks>
    /// <b>A stated divergence.</b> The reference's Message-arrival group has no such row: its
    /// "Play a sound" is a switch over whatever the system's own sound scheme names for new
    /// mail, and the sound itself is chosen in the desktop's control panel. Linux has the first
    /// half of that — the freedesktop sound theme's <c>message-new-email</c>, which an empty
    /// value here asks for — and not the second: no desktop offers a per-application new-mail
    /// sound to set. So the choice lives where the switch is, under rule 2.
    /// </remarks>
    public string ArrivalSoundFile
    {
        get => _settings.GetString(ArrivalSoundFileKey);
        set => _settings.Set(ArrivalSoundFileKey, value);
    }

    /// <summary>
    /// Which sound to play: the file the settings name, else the one the build ships, else null
    /// for the desktop's own sound for the occasion.
    /// </summary>
    /// <remarks>
    /// One rule for both sounds this application makes — mail arriving and a reminder coming
    /// due. In that order because each is more specific than the last, and each falls through
    /// when it is not there rather than failing: a chosen file that has since been deleted or
    /// sits on an unmounted disk quietly becomes the shipped sound again, which is what somebody
    /// who moved a file wants, and the alternative is silence they cannot explain.
    /// </remarks>
    public static string? SoundFor(string? chosen, string? bundled)
        => File.Exists(chosen) ? chosen : File.Exists(bundled) ? bundled : null;

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

    /// <summary>The Options page's Conversation Clean Up switches, as the clean-up reads them.</summary>
    /// <summary>
    /// "Cleaned-up items will go to this folder": a folder name — Deleted Items when empty —
    /// resolved per account by name, since each account has its own folders.
    /// </summary>
    public string CleanUpFolder
    {
        get => _settings.GetString(CleanUpFolderKey);
        set => _settings.Set(CleanUpFolderKey, value ?? string.Empty);
    }

    // ---- Reading Pane… ---------------------------------------------------------------------------
    // The reference's defaults: read after a while in the pane is off, read when the selection
    // moves on is on. Nothing was ever marked read by looking until these existed.

    /// <summary>"Mark items as read when viewed in the Reading Pane".</summary>
    public bool ReadingPaneMarkOnView
    {
        get => _settings.GetBool(ReadingPaneMarkOnViewKey, false);
        set => _settings.Set(ReadingPaneMarkOnViewKey, value);
    }

    /// <summary>"Wait N seconds before marking item as read".</summary>
    public int ReadingPaneMarkSeconds
    {
        get => Math.Clamp((int)_settings.GetNumber(ReadingPaneMarkSecondsKey, 5), 0, 999);
        set => _settings.Set(ReadingPaneMarkSecondsKey, Math.Clamp(value, 0, 999));
    }

    /// <summary>"Mark item as read when selection changes".</summary>
    public bool ReadingPaneMarkOnChange
    {
        get => _settings.GetBool(ReadingPaneMarkOnChangeKey, true);
        set => _settings.Set(ReadingPaneMarkOnChangeKey, value);
    }

    public Mailbox.Core.Conversations.CleanUpPolicy CleanUpPolicy => new()
    {
        KeepUnread = _settings.GetBool(CleanUpKeepUnreadKey, false),
        KeepCategorized = _settings.GetBool(CleanUpKeepCategorizedKey, true),
        KeepFlagged = _settings.GetBool(CleanUpKeepFlaggedKey, true),
        KeepSigned = _settings.GetBool(CleanUpKeepSignedKey, true),
        KeepIfModified = _settings.GetBool(CleanUpKeepModifiedKey, true),
    };

    /// <summary>Whether Ignore Conversation asks first. On until the reader ticks "don't show again".</summary>
    public bool ConfirmIgnore
    {
        get => _settings.GetBool(IgnoreConfirmKey, true);
        set => _settings.Set(IgnoreConfirmKey, value);
    }

    // ---- Reminders (Options › Advanced) ---------------------------------------------------

    /// <summary>Whether the Reminders window opens when a flag's reminder time comes. On.</summary>
    public bool ShowReminders => _settings.GetBool(ShowRemindersKey, true);

    /// <summary>Whether a reminder coming due plays a sound. On.</summary>
    public bool PlayReminderSound => _settings.GetBool(ReminderSoundKey, true);

    /// <summary>
    /// The sound a reminder plays, or empty for the one the build ships and the desktop's
    /// <c>alarm-clock-elapsed</c> behind it.
    /// </summary>
    /// <remarks>
    /// The reference draws this one: its Reminders group reads "Play reminder sound:" with a
    /// field and a Browse… beside it, so unlike the arrival sound this is fidelity rather than a
    /// divergence. Both go through <see cref="SoundFor"/> all the same.
    /// </remarks>
    public string ReminderSoundFile
    {
        get => _settings.GetString(ReminderSoundFileKey);
        set => _settings.Set(ReminderSoundFileKey, value);
    }

    /// <summary>
    /// Whether a reminder for a calendar event that has already finished is dismissed instead of
    /// shown. Off, as the reference has it.
    /// </summary>
    /// <remarks>
    /// The reference's fifth Reminders row. It is about calendar events alone: a task's reminder
    /// stays whether or not its due date has gone, because an overdue task is precisely the one
    /// worth being told about, where a meeting that ended on Tuesday is not.
    /// </remarks>
    public bool DismissPastReminders => _settings.GetBool(DismissPastRemindersKey, false);

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
