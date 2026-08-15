using Mailbox.Theming.Themes;

using Mailbox.Core.Settings;

namespace Mailbox.App.Options;

/// <summary>
/// Every Options page, transcribed from reference captures.
/// </summary>
/// <remarks>
/// vendor-cloud options are deliberately absent rather than stubbed: cloud settings
/// storage, Office Background, OneDrive and SharePoint attachment handling, LinkedIn, Rights
/// Protected message previews, Send to OneNote and the Developer tab. Everything that survives
/// is rewritten from "Office" and "the reference application" to "Mailbox".
/// </remarks>
public static class OptionsPages
{
    /// <summary>
    /// Keys for the settings something else reads. Everything else keys off its own label,
    /// which is fine while nothing depends on it; these are named because code refers to them,
    /// and a reworded label must not silently reset a user's choice.
    /// </summary>
    public static class Keys
    {
        public const string UserName = "general.username";
        public const string Initials = "general.initials";
        public const string FirstDayOfWeek = "calendar.firstdayofweek";
        public const string ShowWeekNumbers = "calendar.showweeknumbers";
        public const string ShowReadingPane = "panes.showreadingpane";
    }

    private static IReadOnlyList<OptionsPage>? _all;

    /// <summary>
    /// Built on first access, not in a field initializer.
    /// </summary>
    /// <remarks>
    /// Static fields initialize in declaration order, so an initializer here would run before
    /// the shared option arrays further down the file were assigned, and every page that reads
    /// one would be handed a null. Deferring construction removes the ordering dependency
    /// entirely rather than relying on the declarations staying in the right sequence.
    /// </remarks>
    public static IReadOnlyList<OptionsPage> All => _all ??=
    [
        General(), Mail(), Calendar(), People(), Tasks(), Search(),
        Language(), Accessibility(), Advanced(),
        CustomizeRibbon(), QuickAccessToolbar(),
        AddIns(), TrustCenter(),
    ];

    public static OptionsPage? Find(string id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>Pages that come after a rule in the rail, matching the reference's grouping.</summary>
    public static IReadOnlySet<string> RuleAfter { get; } =
        new HashSet<string> { "advanced", "qat" };

    // ------------------------------------------------------------------------------------

    private static OptionsPage General() => new(
        "general", "General", "settings",
        "General options for working with Mailbox.",
        [
            new OptionSection("User Interface options",
            [
                new ComboRow("When using multiple displays:",
                    ["Optimize for best appearance", "Optimize for compatibility (restart required)"],
                    LabelWidth: 200) { HasInfo = true },
                new CheckRow("Show Mini Toolbar on selection", true) { HasInfo = true },
                new CheckRow("Enable Live Preview", true) { HasInfo = true },
                new ComboRow("ScreenTip style:",
                [
                    "Show feature descriptions in ScreenTips",
                    "Don't show feature descriptions in ScreenTips",
                    "Don't show ScreenTips",
                ], LabelWidth: 200),
            ]),

            new OptionSection("Personalize your copy of Mailbox",
            [
                new TextRow("User name:", "A. Person") { Key = Keys.UserName },
                new TextRow("Initials:", "AP", Width: 74) { Key = Keys.Initials },
                // Filled with a live control by the window; see OptionsWindow.
                new SlotRow("theme"),
                new SlotRow("density"),
            ]),

            new OptionSection("Start up options",
            [
                new ComboRow("When Mailbox opens:",
                [
                    "Ask me if I want to reopen previous items",
                    "Always reopen previous items",
                    "Never reopen previous items",
                ], LabelWidth: 200),
            ]),
        ]);

    private static OptionsPage Mail() => new(
        "mail", "Mail", "mail",
        "Change the settings for messages you create and receive.",
        [
            new OptionSection("Compose messages",
            [
                new ActionRow("source", "Change the editing settings for messages.", "Editor Options...",
                [
                    new ComboRow("Compose messages in this format:",
                        ["HTML", "Rich Text", "Plain Text"], 0, 130, 210) { Key = MailOptions.ComposeFormatKey },
                    // Text prediction is an AI feature, which the plan rules out on its first
                    // page. Greyed rather than removed: the row is where the reference puts it.
                    new CheckRow("Show text predictions while typing") { HasInfo = true, IsDisabled = true },
                ]),
                new ActionRow("reader", string.Empty, "Spelling and Autocorrect...",
                [
                    new CheckRow("Always check spelling before sending") { Key = MailOptions.CheckSpellingBeforeSendKey },
                    new CheckRow("Ignore original message text in reply or forward", true) { Key = MailOptions.IgnoreOriginalSpellingKey },
                ]),
                new ActionRow("source", "Create or modify signatures for messages.", "Signatures..."),
                new ActionRow("categorize",
                    "Use stationery to change default fonts and styles, colors, and backgrounds.",
                    "Stationery and Fonts..."),
            ]),

            new OptionSection("Mailbox panes",
            [
                new ActionRow("reading-pane",
                    "Customize how items are marked as read when using the Reading Pane.",
                    "Reading Pane..."),
            ]),

            new OptionSection("Message arrival",
            [
                new SubHeadingRow("When new messages arrive:"),
                new CheckRow("Play a sound", true) { Indent = 1 },
                new CheckRow("Briefly change the mouse pointer") { Indent = 1 },
                new CheckRow("Show an envelope icon in the taskbar", true) { Indent = 1 },
                new CheckRow("Display a Desktop Alert", true) { Indent = 1, Key = MailOptions.DesktopAlertKey },
            ]),

            new OptionSection("Conversation Clean Up",
            [
                new BrowseRow("Cleaned-up items will go to this folder:"),
                new NoteRow("Messages moved by Clean Up will go to their account's Deleted Items."),
                new CheckRow("When cleaning sub-folders, recreate the folder hierarchy in the destination folder"),
                new CheckRow("Don't move unread messages"),
                new CheckRow("Don't move categorized messages", true),
                new CheckRow("Don't move flagged messages", true),
                new CheckRow("Don't move digitally-signed messages", true),
                new CheckRow("When a reply modifies a message, don't move the original", true),
            ]),

            new OptionSection("Replies and forwards",
            [
                new CheckRow("Open replies and forwards in a new window") { Key = MailOptions.OpenRepliesInNewWindowKey },
                new CheckRow("Close original message window when replying or forwarding") { Key = MailOptions.CloseOriginalOnReplyKey },
                new TextRow("Preface comments with:", "you@example.com", 240, 200) { Key = MailOptions.PrefaceCommentsKey },
                new ComboRow("When replying to a message:", ReplyStyles, 0, 300, 200) { Key = MailOptions.ReplyStyleKey },
                new ComboRow("When forwarding a message:", ReplyStyles, 0, 300, 200) { Key = MailOptions.ForwardStyleKey },
                new TextRow("Preface each line in a plain-text message with:", ">", 60, 300) { Key = MailOptions.ReplyPrefixKey },
            ]),

            new OptionSection("Save messages",
            [
                new SpinnerRow("Automatically save items that have not been sent after this many minutes:", 3) { Key = MailOptions.AutosaveMinutesKey },
                new ComboRow("Save to this folder:", ["Drafts", "Inbox", "Sent Items"], 0, 150, 200),
                new CheckRow("When replying to a message that is not in the Inbox, save the reply in the same folder"),
                new CheckRow("Save forwarded messages", true),
                new CheckRow("Save copies of messages in the Sent Items folder", true) { Key = MailOptions.SaveCopiesInSentKey },
                // Everything is written UTF-8. There is no other format to choose, so the box
                // is greyed rather than a choice that means nothing.
                new CheckRow("Use Unicode format", true) { IsDisabled = true },
            ]),

            new OptionSection("Send messages",
            [
                // §12's Undo Send, beside delayed delivery because it is the same mechanism
                // with a smaller number in it — the outbox holding a message back.
                new SlotRow("undosend"),
                new ComboRow("Default importance level:", ["Normal", "Low", "High"], 0, 150, 240) { Key = MailOptions.DefaultImportanceKey },
                new ComboRow("Default sensitivity level:",
                    ["Normal", "Personal", "Private", "Confidential"], 0, 150, 240) { Key = MailOptions.DefaultSensitivityKey },
                new CheckRow("Mark messages as expired after this many days:"),
                new CheckRow("Always use the default account when composing new messages") { Key = MailOptions.AlwaysUseDefaultAccountKey },
                new CheckRow("Commas can be used to separate multiple message recipients", true) { Key = MailOptions.CommasSeparateRecipientsKey },
                new CheckRow("Automatic name checking", true) { Key = MailOptions.AutomaticNameCheckingKey },
                new CheckRow("Delete meeting request from Inbox when responding", true),
                new CheckRow("CTRL+ENTER sends a message", true) { Key = MailOptions.CtrlEnterSendsKey },
                // Live: the switch and the Empty button share the row, as the reference has it.
                new SlotRow("autocomplete"),
            ]),

            new OptionSection("Tracking",
            [
                new CheckRow("Delivery receipt confirming the message was delivered to the recipient's email server") { Key = MailOptions.RequestDeliveryReceiptKey },
                new CheckRow("Read receipt confirming the recipient viewed the message") { Key = MailOptions.RequestReadReceiptKey },
                new SubHeadingRow("For any message received that includes a read receipt request:"),
                new RadioRow("readreceipt", "Always send a read receipt") { Indent = 1 },
                new RadioRow("readreceipt", "Never send a read receipt") { Indent = 1 },
                new RadioRow("readreceipt", "Ask each time whether to send a read receipt", true) { Indent = 1 },
            ]),

            new OptionSection("Message format",
            [
                new CheckRow("Reduce message size by removing format information not necessary to display the message"),
                new ComboRow("When sending messages in Rich Text format to Internet recipients:",
                    ["Convert to HTML format", "Convert to Plain Text format", "Send using the reference application Rich Text format"],
                    0, 300, 380),
            ]),

            new OptionSection("Other",
            [
                new CheckRow("Show Paste Options buttons", true),
                new CheckRow("Use single-key reading with the Reading Pane", true),
            ]),
        ]);

    private static readonly string[] ReplyStyles =
    [
        "Include original message text",
        "Do not include original message",
        "Attach original message",
        "Include and indent original message text",
        "Prefix each line of the original message",
    ];

    private static OptionsPage Calendar() => new(
        "calendar", "Calendar", "calendar",
        "Change the settings for calendars, meetings, and time zones.",
        [
            new OptionSection("Work time",
            [
                new ComboRow("Start time:", Times, 16, 130, 200),
                new ComboRow("End time:", Times, 34, 130, 200),
                new SubHeadingRow("Work week:"),
                new CheckRow("Sun") { Indent = 1 },
                new CheckRow("Mon", true) { Indent = 1 },
                new CheckRow("Tue", true) { Indent = 1 },
                new CheckRow("Wed", true) { Indent = 1 },
                new CheckRow("Thu", true) { Indent = 1 },
                new CheckRow("Fri", true) { Indent = 1 },
                new CheckRow("Sat") { Indent = 1 },
                new ComboRow("First day of week:", Weekdays, 0, 150, 200) { Key = Keys.FirstDayOfWeek },
                new ComboRow("First week of year:",
                    ["Starts on Jan 1", "First 4-day week", "First full week"], 0, 180, 200),
            ]),

            new OptionSection("Calendar options",
            [
                new ComboRow("Default reminders:", Reminders, 3, 150, 240),
                new CheckRow("Allow attendees to propose new times for meetings", true),
                new ComboRow("Use this response when proposing a new meeting time:",
                    ["Tentative", "Accept", "Decline"], 0, 150, 340),
                new CheckRow("Add holidays to the Calendar:"),
                new CheckRow("Enable an alternate calendar"),
                new CheckRow("When sending meeting requests outside of your organization, use the iCalendar format", true),
                new CheckRow("Show bell icon on the calendar for appointments and meetings with reminders", true),
            ]),

            new OptionSection("Display options",
            [
                new ComboRow("Default calendar colour:", Colours, 0, 150, 240),
                new CheckRow("Use this colour on all calendars"),
                new CheckRow("Show week numbers in the month view and in the Date Navigator")
                    { Key = Keys.ShowWeekNumbers },
                new CheckRow("Show a Weather bar on the calendar"),
            ]),

            new OptionSection("Time zones",
            [
                new TextRow("Label:", "", 180, 200),
                new ComboRow("Time zone:", ["System time zone"], 0, 300, 200),
                new CheckRow("Show a second time zone"),
            ]),
        ]);

    private static OptionsPage People() => new(
        "people", "People", "people",
        "Change the settings for people and how they are stored and displayed.",
        [
            new OptionSection("Names and filing",
            [
                new ComboRow("Default \"Full Name\" order:",
                    ["First (Middle) Last", "Last First", "First Last1 Last2"], 0, 220, 240),
                new ComboRow("Default \"File As\" order:",
                    ["Last, First", "First Last", "Company", "Last, First (Company)"], 0, 220, 240),
                new CheckRow("Check for duplicates when saving new contacts", true),
            ]),

            new OptionSection("Contacts index",
            [
                new CheckRow("Show an additional index", true),
            ]),

            new OptionSection("Online status and photographs",
            [
                new CheckRow("Show user photographs when available", true),
            ]),
        ]);

    private static OptionsPage Tasks() => new(
        "tasks", "Tasks", "tasks",
        "Change the settings that track your tasks and to-do items.",
        [
            new OptionSection("Task options",
            [
                new CheckRow("Set reminders on tasks with due dates", true),
                new ComboRow("Default reminder time:", Times, 18, 130, 240),
                new CheckRow("Keep my task list updated with copies of tasks I assign to other people", true),
                new CheckRow("Send status report when I complete an assigned task", true),
                new ComboRow("Overdue task colour:", Colours, 4, 150, 240),
                new ComboRow("Completed task colour:", Colours, 5, 150, 240),
                new CheckRow("Set Quick Click flag to:", true),
            ]),

            new OptionSection("Work hours",
            [
                new SpinnerRow("Task working hours per day:", 8, 1, 24, 240),
                new SpinnerRow("Task working hours per week:", 40, 1, 168, 240),
            ]),
        ]);

    private static OptionsPage Search() => new(
        "search", "Search", "search",
        "Change how items are searched and indexed.",
        [
            new OptionSection("Sources",
            [
                new SubHeadingRow("Include results only from:"),
                new RadioRow("scope", "Current folder") { Indent = 1 },
                new RadioRow("scope", "Current folder. Current mailbox when searching from the Inbox", true) { Indent = 1 },
                new RadioRow("scope", "Current mailbox") { Indent = 1 },
                new RadioRow("scope", "All mailboxes") { Indent = 1 },
                new CheckRow("Include messages from the Deleted Items folder in each data file when searching in All Items"),
            ]),

            new OptionSection("Results",
            [
                new CheckRow("Improve search speed by limiting the number of results shown", true),
                new CheckRow("Highlight the words in the search results", true),
                new ComboRow("Highlight colour:", Colours, 2, 150, 200),
            ]),
        ]);

    private static OptionsPage Language() => new(
        "language", "Language", "info",
        "Set the language preferences for Mailbox.",
        [
            new OptionSection("Display language",
            [
                new NoteRow("Choose the language used for buttons, tabs and Help."),
                new ComboRow("Interface language:", ["Match system language", "English"], 0, 260, 200),
            ]),

            new OptionSection("Editing languages",
            [
                new NoteRow("Add the languages you write in. Spelling and grammar follow this list."),
                new ComboRow("Primary editing language:", ["English (United Kingdom)", "English (United States)"], 0, 260, 200),
            ]),
        ]);

    private static OptionsPage Accessibility() => new(
        "accessibility", "Accessibility", "info",
        "Make Mailbox easier to use.",
        [
            new OptionSection("Feedback options",
            [
                new CheckRow("Provide feedback with sound"),
                new ComboRow("Sound scheme:", ["Modern", "Classic"], 0, 180, 240),
                new CheckRow("Provide feedback with animation", true),
            ]),

            new OptionSection("Application display options",
            [
                new CheckRow("Show shortcut keys in ScreenTips", true),
                new CheckRow("Always use high-contrast-safe colours"),
                new CheckRow("Always expand alt text for pictures in messages"),
            ]),

            new OptionSection("Automatic alt text",
            [
                new NoteRow("Alt text is read aloud by screen readers and shown when a picture cannot load."),
                new CheckRow("Warn me when a picture I send has no alt text", true),
            ]),
        ]);

    private static OptionsPage Advanced() => new(
        "advanced", "Advanced", "settings",
        "Advanced options for working with Mailbox.",
        [
            new OptionSection("Mailbox panes",
            [
                new ActionRow("layout", "Customize Mailbox panes.", "Navigation...",
                [
                    new CheckRow("Show the To-Do Bar", true),
                    new CheckRow("Show the Reading Pane", true) { Key = Keys.ShowReadingPane },
                ]),
            ]),

            new OptionSection("Start and exit",
            [
                new ComboRow("Start Mailbox in this folder:", ["Inbox", "Calendar", "Tasks"], 0, 200, 240),
                // Live: two rows over the XDG autostart entry, the Linux-native form of the
                // reference's run-at-login (§10). Written as they go, like every other row.
                new SlotRow("autostart"),
                new CheckRow("Empty Deleted Items folders when exiting Mailbox") { Key = MailOptions.EmptyDeletedOnExitKey },
            ]),

            new OptionSection("AutoArchive",
            [
                new ActionRow("archive",
                    "Reduce mailbox size by deleting or moving old items to an archive data file.",
                    "AutoArchive Settings..."),
            ]),

            new OptionSection("Reminders",
            [
                new CheckRow("Show reminders", true),
                new CheckRow("Play reminder sound", true),
                new CheckRow("Show reminders on top of other windows", true),
            ]),

            new OptionSection("Export",
            [
                new ActionRow("archive", "Export Mailbox information to a file.", "Export..."),
            ]),

            new OptionSection("Send and receive",
            [
                new SubHeadingRow("Set send and receive schedules and choose what is included."),
                new CheckRow("Send immediately when connected", true) { Key = MailOptions.SendImmediatelyKey },
                // The schedule itself lives on the send/receive groups, where the reference keeps
                // it too; this spinner is that dialog's number for the All Accounts group and is
                // wired to the same state, not a second copy of it.
                new SlotRow("schedule"),
            ]),

            new OptionSection("Developers",
            [
                new CheckRow("Show plugin user interface errors"),
                new CheckRow("Enable the plugin developer tools"),
            ]),

            new OptionSection("Other",
            [
                new CheckRow("Prompt for confirmation before permanently deleting items", true),
                new CheckRow("Show paste options when content is pasted", true),
                new CheckRow("Use animations when expanding conversations and groups", true),
            ]),
        ]);

    // Customize Ribbon and Quick Access Toolbar are editors over the ribbon layout document
    // rather than lists of options, so they are built by their own view rather than described
    // here. See OptionsWindow.
    private static OptionsPage CustomizeRibbon() => new(
        "ribbon", "Customize Ribbon", "settings", "Customize the Ribbon.", []);

    private static OptionsPage QuickAccessToolbar() => new(
        "qat", "Quick Access Toolbar", "settings", "Customize the Quick Access Toolbar.", []);

    private static OptionsPage AddIns() => new(
        "addins", "Add-ins", "apps",
        "View and manage plugins.",
        [
            new OptionSection("Plugins",
            [
                new NoteRow("Plugins are .NET assemblies discovered from the plugins directory. " +
                            "Each declares the permissions it needs, and can be enabled or " +
                            "disabled without restarting Mailbox."),
                new CheckRow("Load plugins at startup", true),
                new CheckRow("Warn me when a plugin requests a permission it did not declare", true),
            ]),
        ]);

    private static OptionsPage TrustCenter() => new(
        "trust", "Trust Center", "shield",
        "Keep your messages and your machine secure.",
        [
            new OptionSection("Email Security",
            [
                new SubHeadingRow("Encryption is built in and starts switched off, because each " +
                                  "needs key material before it can do anything."),
                new CheckRow("Enable S/MIME"),
                new CheckRow("Enable OpenPGP"),
                new CheckRow("Encrypt the local store with a master password"),
            ]),

            new OptionSection("Automatic download",
            [
                new CheckRow("Don't download pictures automatically in messages", true),
                new CheckRow("Warn me before downloading content when editing, forwarding or replying", true),
                new CheckRow("Report the hosts a message tried to contact", true),
            ]),

            new OptionSection("Message authentication",
            [
                new CheckRow("Show DKIM, SPF and DMARC results in the reading pane", true),
                new CheckRow("Warn me about lookalike sender domains", true),
                new CheckRow("Warn me when a display name disagrees with the sending address", true),
            ]),
        ]);

    // ---- Shared option lists -------------------------------------------------------------

    private static readonly string[] Weekdays =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    private static readonly string[] Reminders =
        ["0 minutes", "5 minutes", "10 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours"];

    private static readonly string[] Colours =
        ["Blue", "Green", "Orange", "Purple", "Red", "Grey", "Yellow", "Teal"];

    private static readonly string[] Times = BuildTimes();

    private static string[] BuildTimes()
    {
        var times = new List<string>();
        for (var half = 0; half < 48; half++)
        {
            times.Add(DateTime.Today.AddMinutes(half * 30).ToString("h:mm tt"));
        }
        return [.. times];
    }

    /// <summary>Theme names for the General page's Mailbox Theme picker.</summary>
    public static IReadOnlyList<string> ThemeNames =>
        OfficeThemes.All.Select(OfficeThemes.DisplayName).ToList();
}
