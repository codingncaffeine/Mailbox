using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Mailbox.App.Options;
using Mailbox.App.Views;
using Mailbox.Core;
using Mailbox.Core.Accounts;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Store;
using Mailbox.Store.Lists;
using Mailbox.Theming;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Themes;

namespace Mailbox.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>What a row in the folder pane stands for, beyond a folder of mail.</summary>
public enum FolderNodeKind
{
    /// <summary>An account's heading, or a folder of mail.</summary>
    Folder,

    /// <summary>The "Search Folders" heading under an account.</summary>
    SearchFolders,

    /// <summary>One search folder — a saved query, listed under the heading.</summary>
    SearchFolder,

    /// <summary>The "Favourites" heading at the top of the pane.</summary>
    FavouritesHeading,

    /// <summary>A folder listed under Favourites — the same folder as its row in the tree below.</summary>
    Favourite,
}

public sealed class FolderNode(string name, int depth, int unread, bool bold = false, FolderNodeKind kind = FolderNodeKind.Folder)
{
    public string Name { get; } = name;
    public int Unread { get; } = unread;
    public FolderNodeKind Kind { get; } = kind;
    public Thickness IndentMargin { get; } = new(depth * 14, 0, 0, 0);
    public FontWeight Weight { get; } = bold || unread > 0 ? FontWeight.SemiBold : FontWeight.Normal;
    public string UnreadDisplay { get; } = unread > 0 ? unread.ToString() : string.Empty;
    public override string ToString() => Name;
}

/// <summary>
/// One module entry. Classic renders these as a horizontal strip at the foot of the folder
/// pane; Modern renders the same collection vertically in the left app rail.
/// </summary>
public sealed class ModuleTab(MailboxModule module, string icon, bool isActive) : ObservableObject
{
    public MailboxModule Module { get; } = module;

    /// <summary>Invoked when the rail button is pressed. Set by the shell.</summary>
    public System.Windows.Input.ICommand? Activate { get; set; }
    public string Label { get; } = module.ToString();
    public string Glyph { get; } = IconGlyphs.GetOrEmpty(icon, 20);
    public string RailGlyph { get; } = IconGlyphs.GetOrEmpty(icon, 24);
    public FontFamily IconFamily { get; } = IconFont.Family;
    public string Tip { get; } = $"{module} (Ctrl+{(int)module})";

    /// <summary>
    /// Drives a style selector rather than a brush property. Colour stays in XAML so it
    /// resolves from theme tokens — a view model naming a colour is exactly what the coverage
    /// audit is meant to catch.
    /// </summary>
    /// <remarks>
    /// Settable and observed since Phase 11 gave the rail a second module to switch to: the mark
    /// against the rail's edge has to move when it does, and a value fixed at construction was
    /// only ever right while Mail was the only module there was.
    /// </remarks>
    public bool IsActive
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(StyleClass));
        }
    } = isActive;

    public string StyleClass => IsActive ? "module active" : "module";
}

/// <summary>One command on the Quick Access Toolbar in the title bar.</summary>
public sealed class QuickAccessButton(MailboxCommand command)
{
    /// <summary>
    /// A rule rather than a command. The toolbar is the one bar a user can put one on, and it
    /// carries the same sentinel id the ribbon's own rules do.
    /// </summary>
    public static QuickAccessButton Separator { get; } = new(new MailboxCommand
    {
        Id = RibbonItem.SeparatorId,
        Label = string.Empty,
        Description = string.Empty,
        Icon = string.Empty,
        Category = string.Empty,
    })
    { IsSeparator = true };

    public bool IsSeparator { get; private init; }

    public string Label { get; } = command.Label;
    public string Glyph { get; } = IconGlyphs.GetOrEmpty(command.Icon, 16);
    public FontFamily IconFamily { get; } = IconFont.Family;

    /// <summary>
    /// The command this button stands for. It routes through the catalogue exactly as the
    /// ribbon does — the same command reached from the toolbar, the reading pane or the
    /// ribbon has to arrive at one place, or wiring it once will not wire it everywhere.
    /// </summary>
    public CommandId Command { get; } = command.Id;

    /// <summary>Set by the shell so a click reaches the same handler as a ribbon click.</summary>
    public System.Windows.Input.ICommand? Invoke { get; set; }

    public string Tip { get; } = command.DefaultGesture is { } gesture
        ? $"{command.Label} ({gesture})"
        : command.Label;
}

/// <summary>
/// One row in the message list.
/// </summary>
/// <remarks>
/// Implements <see cref="IArrangeable"/> directly so the arrangement engine works on the rows
/// the list already holds, rather than grouping store records and then converting — which would
/// mean two representations that can disagree about what is on screen.
/// </remarks>
public sealed class MessageRow(
    long id,
    string from,
    string subject,
    string preview,
    DateTimeOffset received,
    bool isUnread,
    string toLine,
    string body) : ObservableObject, IThreadable
{
    /// <summary>The store's id, so a command knows what it is acting on.</summary>
    public long Id { get; } = id;

    public string From { get; } = from;
    public string Subject { get; } = subject;
    public string Preview { get; } = preview;
    public string ToLine { get; } = toLine;
    public string Body { get; } = body;

    public DateTimeOffset Received { get; } = received;

    public long SizeBytes { get; init; }

    /// <summary>What threads this with its replies. The subject without its prefixes.</summary>
    public string ThreadKey { get; init; } = string.Empty;

    /// <summary>Which folder it is filed in, so a conversation can tell it spans two.</summary>
    public long FolderId { get; init; }

    /// <summary>
    /// The folder a search result was found in, shown on the row while searching across folders.
    /// Empty in an ordinary folder view, where every row is in the folder on screen.
    /// </summary>
    public string FolderLabel { get; init; } = string.Empty;

    public bool HasFolderLabel => FolderLabel.Length > 0;

    /// <summary>
    /// The colour tokens of the categories on this message, in order. Token names rather than
    /// colours, so the strip is repainted by a theme change like everything else.
    /// </summary>
    public IReadOnlyList<string> CategoryTokens
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(HasCategories));
        }
    } = [];

    public bool HasCategories => CategoryTokens.Count > 0;

    /// <summary>How far the row is indented under its conversation.</summary>
    public int Depth
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(Indent)); }
    }

    /// <summary>
    /// The row's own padding plus its indent under a conversation. One value because it is one
    /// margin — binding the indent alone would replace the padding rather than add to it.
    /// </summary>
    public Thickness Indent => new(6 + (Depth * 18), 3, 8, 3);

    public bool HasAttachment { get; init; }

    public string DisplayFrom => From;

    // ---- Facts for a view's filter and conditional formatting ---------------------------------

    public string FromAddress { get; init; } = string.Empty;
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public DateTimeOffset? Sent { get; init; }

    /// <summary>The names of the categories on this message, beside their colour tokens.</summary>
    public IReadOnlyList<string> CategoryNames { get; set; } = [];

    /// <summary>What a search-syntax condition sees of this row.</summary>
    public Mailbox.Core.Search.SearchFacts Facts() => new()
    {
        FromName = From,
        FromAddress = FromAddress,
        To = To,
        Cc = Cc,
        Subject = Subject,
        Body = Body.Length > 0 ? Body : Preview,
        Categories = CategoryNames,
        HasAttachment = HasAttachment,
        IsRead = !IsUnread,
        IsFlagged = IsFlagged,
        Importance = Importance,
        SizeBytes = SizeBytes,
        Received = Received,
        Sent = Sent,
        Due = FollowUpDue,
    };

    // ---- How the view draws this row ---------------------------------------------------------

    /// <summary>True while the list is in the compact card layout; the template selector reads it.</summary>
    public bool IsCard { get; set; }

    /// <summary>
    /// The conditional-formatting rule the row matched, or null for none. Set by the shell when
    /// the list is rebuilt; the row's weight, style and ink follow it, with the unread styling
    /// underneath.
    /// </summary>
    public Mailbox.Core.Views.ConditionalFormat? Format
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(SenderWeight));
            Raise(nameof(SubjectWeight));
            Raise(nameof(FormatStyle));
            Raise(nameof(FormatBrushToken));
            Raise(nameof(InkToken));
        }
    }

    public FontStyle FormatStyle => Format is { Italic: true } ? FontStyle.Italic : FontStyle.Normal;

    /// <summary>The token of the ink a rule asks for, or null to leave the theme's own.</summary>
    public string? FormatBrushToken => Format?.ColourToken;

    /// <summary>
    /// Whether the view's "Unread messages" rule is on. Off, an unread row draws like a read one
    /// — the bar at its edge stays, the bold and the blue go.
    /// </summary>
    public bool UnreadStyling
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(ShowAsUnread));
            Raise(nameof(SenderWeight));
            Raise(nameof(SubjectWeight));
            Raise(nameof(InkToken));
        }
    } = true;

    /// <summary>Unread, and drawn as unread.</summary>
    public bool ShowAsUnread => IsUnread && UnreadStyling;

    /// <summary>The ink the row's text takes: a rule's token, else the unread ink, else null for the theme's own.</summary>
    public string? InkToken => FormatBrushToken ?? (ShowAsUnread ? "list.row.unread.text" : null);

    /// <summary>The Sent column: the sent date written the way Received is, or as Format Columns says.</summary>
    public string SentLabel => Sent is { } sent ? DateLabel(sent, Mailbox.Core.Views.ViewFields.Sent) : string.Empty;

    /// <summary>The Size column: KB, as the reference writes it.</summary>
    public string SizeLabel => SizeBytes <= 0 ? string.Empty : $"{Math.Max(1, (SizeBytes + 512) / 1024)} KB";

    /// <summary>The Importance column: an exclamation for high, an arrow for low, nothing for normal.</summary>
    public string ImportanceGlyph => Importance switch { 2 => "!", 0 => "\u2193", _ => string.Empty };

    /// <summary>The Reminder column: a bell while a reminder is set.</summary>
    public string ReminderGlyph => HasReminder ? "\u23F0" : string.Empty;

    public bool HasReminder { get; init; }

    /// <summary>
    /// Mutable, because marking read is the one thing a row does to itself often enough that
    /// rebuilding the list for it would be visible.
    /// </summary>
    public bool IsUnread
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(SenderWeight));
            Raise(nameof(SubjectWeight));
            Raise(nameof(ShowAsUnread));
            Raise(nameof(InkToken));
        }
    } = isUnread;

    public bool IsFlagged
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(FlagGlyph)); }
    }

    /// <summary>Whether a follow-up on this message has been marked complete.</summary>
    public bool FollowUpComplete { get; init; }

    /// <summary>When a follow-up is due, for the tooltip and the flag menu's state.</summary>
    public DateTimeOffset? FollowUpDue { get; init; }

    /// <summary>When a snoozed message comes back, or null for one that is awake (§12).</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }

    public bool IsSnoozed => SnoozedUntil is not null;

    /// <summary>0 low, 1 normal, 2 high — the message's own importance, for the "!" column and Filter Email.</summary>
    public int Importance { get; init; } = 1;

    /// <summary>Format Columns' choice for the date columns of the view on screen, by field id.</summary>
    public static IReadOnlyDictionary<string, Mailbox.Core.Views.DateFormat> DateFormats { get; set; } = new Dictionary<string, Mailbox.Core.Views.DateFormat>();

    /// <summary>How the row writes its date: a time today, a weekday this week, else the date — or as Format Columns says.</summary>
    public string ReceivedLabel => DateLabel(Received, Mailbox.Core.Views.ViewFields.Received);

    /// <summary>A date the way the view's format for that column writes it.</summary>
    internal static string DateLabel(DateTimeOffset when, string field)
    {
        var format = DateFormats.TryGetValue(field, out var f) ? f : Mailbox.Core.Views.DateFormat.BestFit;
        var local = when.ToLocalTime();
        return format switch
        {
            Mailbox.Core.Views.DateFormat.Short => local.ToString("d"),
            Mailbox.Core.Views.DateFormat.Long => local.ToString("ddd d MMM yyyy h:mm tt"),
            Mailbox.Core.Views.DateFormat.TimeOnly => local.ToString("h:mm tt"),
            _ => ShellViewModel.Received(when),
        };
    }

    /// <summary>A flag while a follow-up is open, a check once it is complete, nothing otherwise.</summary>
    public string FlagGlyph => IsFlagged ? "\u2691" : FollowUpComplete ? "\u2713" : string.Empty;

    public FontWeight SenderWeight => ShowAsUnread || Format is { Bold: true } ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight SubjectWeight => ShowAsUnread || Format is { Bold: true } ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>
/// The collapsed head of a conversation: the newest message, with a count and a chevron.
/// </summary>
/// <remarks>
/// Only drawn for threads of two or more. A single message is not a conversation and showing it
/// with an expander that reveals itself would be nonsense.
/// </remarks>
public sealed class ConversationRow(MessageRow newest, int count, bool expanded, bool split)
{
    public MessageRow Newest { get; } = newest;
    public int Count { get; } = count;
    public bool IsExpanded { get; } = expanded;

    /// <summary>The thread's messages are in more than one folder.</summary>
    public bool IsSplit { get; } = split;

    public string Glyph => IsExpanded ? "\u2304" : "\u203A";
    public string CountLabel => Count.ToString();
    public string SplitGlyph => IsSplit ? "\u21C4" : string.Empty;

    /// <summary>True while the list is in the compact card layout.</summary>
    public bool IsCard => Newest.IsCard;

    public string From => Newest.From;
    public string Subject => Newest.Subject;
    public string Preview => Newest.Preview;
    public string ReceivedLabel => Newest.ReceivedLabel;
    public bool IsUnread => Newest.IsUnread;
    public FontWeight SenderWeight => IsUnread ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight SubjectWeight => IsUnread ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>
/// A group header in the list. Sits in the same flat sequence as the rows it heads, which is
/// what lets one virtualizing list draw both without nesting a panel per group.
/// </summary>
public sealed class GroupHeaderRow(string header, int count, bool collapsed)
{
    public string Header { get; } = header;
    public int Count { get; } = count;
    public bool IsCollapsed { get; } = collapsed;

    public string Glyph => IsCollapsed ? "\u203A" : "\u2304";
    public string CountLabel => $"({Count})";
}

/// <summary>
/// Phase 0 shell state. Backed by sample data — the point of this phase is that the chrome
/// passes a squint test against real the reference application, not that it moves mail. Phases 2 onward replace
/// each collection with the real store.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ThemeService _themes;
    private FolderNode? _selectedFolder;
    private MessageRow? _selectedMessage;
    private string _selectedTheme;
    private string _searchText = string.Empty;

    private readonly AccountStores? _accounts;
    private readonly CommandCatalog _catalog;

    public ShellViewModel(
        ThemeService themes,
        CommandCatalog catalog,
        RibbonLayout layout,
        ShellLayoutMode layoutMode,
        AccountStores? accounts = null,
        QuickAccessLayout? quickAccess = null)
    {
        _accounts = accounts;
        _catalog = catalog;
        _themes = themes;
        QuickAccessCustomization = quickAccess;
        _selectedTheme = themes.DisplayName(themes.ThemeId);
        LayoutMode = layoutMode;

        Themes = new ObservableCollection<string>(
            themes.Library.Ids.Select(themes.DisplayName));

        QuickAccess = new ObservableCollection<QuickAccessButton>(
            ToolbarButtons(catalog, quickAccess?.Commands ?? layout.QuickAccess));

        ReadingPaneActions = new ObservableCollection<QuickAccessButton>(
            new[] { MailCommands.Reply.Id, MailCommands.ReplyAll.Id, MailCommands.Forward.Id }
                .Where(id => catalog.TryGet(id, out _))
                .Select(id => new QuickAccessButton(catalog.Get(id))));

        // New the reference's command bar: New mail, then the actions that used to be the Delete,
        // Respond and Tags groups, flattened into one row.
        foreach (var id in new[]
                 {
                     MailCommands.NewEmail.Id, MailCommands.Delete.Id, MailCommands.Archive.Id,
                     MailCommands.MoveTo.Id, MailCommands.Reply.Id, MailCommands.ReplyAll.Id,
                     MailCommands.Forward.Id, MailCommands.Unread.Id, MailCommands.FollowUp.Id,
                 })
        {
            if (catalog.TryGet(id, out var command)) CommandBar.Add(new QuickAccessButton(command));
        }

        Folders =
        [
            new FolderNode("Favourites", 0, 0, bold: true),
            new FolderNode("Inbox", 1, 4),
            new FolderNode("Sent Items", 1, 0),
            new FolderNode("Deleted Items", 1, 0),
            new FolderNode("you@example.com", 0, 0, bold: true),
            new FolderNode("Inbox", 1, 4),
            new FolderNode("Drafts", 1, 1),
            new FolderNode("Sent Items", 1, 0),
            new FolderNode("Deleted Items", 1, 0),
            new FolderNode("Junk Email", 1, 0),
            new FolderNode("Archive", 1, 0),
            new FolderNode("Outbox", 1, 0),
            new FolderNode("RSS Feeds", 1, 0),
            new FolderNode("Search Folders", 1, 0),
        ];

        // Sample shown until an account exists. Dates are relative to now so the arrangement's
        // Today / Yesterday buckets are exercised rather than always reading "last year".
        var now = DateTimeOffset.Now;
        Messages =
        [
            new MessageRow(1, "Alice Chen", "Re: Q3 numbers",
                "Thanks for pulling those together — the variance on line 14 is the one I'd want to talk through before Thursday.",
                now.AddHours(-4), true, "To: you@example.com",
                "Thanks for pulling those together.\n\nThe variance on line 14 is the one I'd want to " +
                "talk through before Thursday. Everything else reconciles against what finance sent " +
                "over last week.\n\nAlice") { SizeBytes = 4_200, ThreadKey = "q3 numbers" },
            new MessageRow(2, "Build Notifications", "mailbox/main — build passed",
                "Commit 4f2a1c9 built successfully on linux-x64. 0 warnings, 0 errors.",
                now.AddHours(-5), true, "To: you@example.com",
                "Commit 4f2a1c9 built successfully on linux-x64.\n\n0 warnings, 0 errors.\nElapsed 00:00:04.62")
                { SizeBytes = 1_100, ThreadKey = "mailbox/main — build passed" },
            new MessageRow(3, "Dana Whitfield", "Lunch Thursday?",
                "There's a new place near the office that does a decent laksa. Around 12:30?",
                now.AddDays(-1), false, "To: you@example.com",
                "There's a new place near the office that does a decent laksa.\n\nAround 12:30?")
                { SizeBytes = 900, ThreadKey = "lunch thursday?" },
            new MessageRow(4, "Sam Reyes", "Draft agenda attached",
                "Rough cut for Monday. Shout if there's anything you want added before I send it round.",
                now.AddDays(-1).AddHours(-3), false, "To: you@example.com",
                "Rough cut for Monday.\n\nShout if there's anything you want added before I send it round.")
                { SizeBytes = 38_000, HasAttachment = true, ThreadKey = "draft agenda attached" },
            new MessageRow(5, "Fastmail", "Your account statement is ready",
                "Your monthly statement for August is available to download.",
                now.AddDays(-3), false, "To: you@example.com",
                "Your monthly statement for August is available to download.") { SizeBytes = 2_400, ThreadKey = "your account statement is ready" },
            new MessageRow(6, "Priya Raman", "Re: Font substitution question",
                "Confirmed — Carlito is metric-compatible with Calibri, so the layout holds either way.",
                now.AddDays(-9), false, "To: you@example.com",
                "Confirmed — Carlito is metric-compatible with Calibri, so the layout holds either way.")
                { SizeBytes = 1_800, ThreadKey = "font substitution question" },
        ];

        _selectedFolder = Folders[5];
        // With an account configured the shell shows that account. Without one it shows the
        // sample above, which is what makes an unconfigured Mailbox worth looking at — and is
        // replaced the moment there is real mail rather than mixed with it.
        if (LoadFromStore()) HasAccount = true;

        _selectedMessage = Messages.FirstOrDefault();

        RebuildColumns();

        ShowAll = new RelayCommand(() => UnreadOnly = false);
        ShowUnread = new RelayCommand(() => UnreadOnly = true);
        ToggleSort = new RelayCommand(() => SortDescending = !SortDescending);
        ToggleNav = new RelayCommand(() => NavCollapsed = !NavCollapsed);

        ClearSearchCommand = new RelayCommand(ClearSearch);
        ShowReadingPane = new RelayCommand(() => ReadingPaneVisible = true);
        HideReadingPane = new RelayCommand(() => ReadingPaneVisible = false);
        ZoomIn = new RelayCommand(() => ZoomPercent += 10);
        ZoomOut = new RelayCommand(() => ZoomPercent -= 10);

        // Nothing is on screen until the rows have been grouped, and grouping is what the list
        // binds to. Last, so it sees whichever source — store or sample — was loaded above.
        Rebuild();
    }

    public ObservableCollection<string> Themes { get; }
    public ObservableCollection<QuickAccessButton> QuickAccess { get; }
    public ObservableCollection<QuickAccessButton> ReadingPaneActions { get; }

    /// <summary>
    /// The toolbar's customization state, or null when the shell is running without settings —
    /// the fidelity harness and the tests both do.
    /// </summary>
    public QuickAccessLayout? QuickAccessCustomization { get; }

    /// <summary>
    /// The toolbar draws in one of two places, so each host binds to its own flag rather than
    /// to the placement — a view has no business knowing what an enum member means.
    /// </summary>
    public bool IsQuickAccessAbove =>
        QuickAccessCustomization is not { IsVisible: false }
        && QuickAccessCustomization?.Placement != QuickAccessPlacement.BelowRibbon;

    public bool IsQuickAccessBelow =>
        QuickAccessCustomization is { IsVisible: true, Placement: QuickAccessPlacement.BelowRibbon };

    /// <summary>
    /// Refills the toolbar from its customization state. The buttons are replaced rather than
    /// mutated, so whatever wired their commands has to run again — see
    /// <c>MainWindow.WireToolbarCommands</c>.
    /// </summary>
    public void RebuildQuickAccess()
    {
        if (QuickAccessCustomization is not { } customization) return;

        QuickAccess.Clear();
        foreach (var button in ToolbarButtons(_catalog, customization.Commands))
        {
            QuickAccess.Add(button);
        }

        RaiseQuickAccessPlacement();
    }

    /// <summary>
    /// Turns stored ids into buttons, keeping the rules and dropping what the catalogue does
    /// not know — a hand-edited settings file is allowed to name a command that no longer
    /// exists, and that costs one button rather than the toolbar.
    /// </summary>
    private static IEnumerable<QuickAccessButton> ToolbarButtons(
        CommandCatalog catalog, IEnumerable<CommandId> ids)
    {
        foreach (var id in ids)
        {
            if (id == RibbonItem.SeparatorId) yield return QuickAccessButton.Separator;
            else if (catalog.TryGet(id, out var command)) yield return new QuickAccessButton(command);
        }
    }

    public void RaiseQuickAccessPlacement()
    {
        Raise(nameof(IsQuickAccessAbove));
        Raise(nameof(IsQuickAccessBelow));
    }

    // ---- Shell layout -------------------------------------------------------------------
    // Classic and Modern differ structurally, not just in colour, so the shell shows and
    // hides whole regions rather than restyling them.

    public ShellLayoutMode LayoutMode { get; }

    public bool IsClassic => LayoutMode == ShellLayoutMode.Classic;
    public bool IsModern => LayoutMode == ShellLayoutMode.Modern;

    /// <summary>
    /// Search lives in the title bar in both layouts. Current the reference application builds moved it
    /// there in Classic too — it is not above the message list any more.
    /// </summary>
    public bool ShowHeaderSearch => true;
    public bool ShowListSearch => false;

    /// <summary>
    /// The vertical app rail is present in both. Classic gained it in the same update that
    /// moved the modules off the bottom of the folder pane.
    /// </summary>
    public bool ShowAppRail => true;

    /// <summary>Superseded by the app rail; kept for the pre-move the reference application look.</summary>
    public bool ShowBottomModuleStrip => false;

    // No "Try the new the reference application" toggle. That pill exists to move people onto the vendor's web
    // app; Mailbox is the thing it is nagging them away from. The space stays empty.

    /// <summary>Modern replaces the ribbon with a single-row command bar.</summary>
    public bool ShowRibbon => IsClassic;
    public bool ShowCommandBar => IsModern;

    /// <summary>Commands on the Modern command bar, in the reference's own order.</summary>
    public ObservableCollection<QuickAccessButton> CommandBar { get; } = [];

    /// <summary>
    /// True once the calendar peek has been docked to the right edge, where it is the To-Do
    /// Bar's first section and takes the reading pane's place until closed.
    /// </summary>
    public bool IsCalendarDocked
    {
        get;
        set { if (Set(ref field, value)) { Raise(); Raise(nameof(IsToDoBarVisible)); } }
    }

    /// <summary>True when the To-Do Bar is showing its tasks section.</summary>
    public bool AreTasksDocked
    {
        get;
        set { if (Set(ref field, value)) { Raise(); Raise(nameof(IsToDoBarVisible)); } }
    }

    /// <summary>True when the To-Do Bar is showing its People section — the favourite contacts.</summary>
    public bool ArePeopleDocked
    {
        get;
        set { if (Set(ref field, value)) { Raise(); Raise(nameof(IsToDoBarVisible)); } }
    }

    /// <summary>
    /// Whether the To-Do Bar is on at all, which is what the pane itself is bound to: it is on
    /// when any of its sections is, and off when the menu's Off turns them all off.
    /// </summary>
    public bool IsToDoBarVisible => IsCalendarDocked || AreTasksDocked || ArePeopleDocked;
    public ObservableCollection<FolderNode> Folders { get; }
    public ObservableCollection<MessageRow> Messages { get; }

    /// <summary>True once an account exists. Until then the shell is showing the sample.</summary>
    public bool HasAccount { get; private set; }

    public bool ShowSampleNotice => !HasAccount;

    /// <summary>
    /// Which account and folder each row stands for. Every account has its own store, so a
    /// folder id alone is not enough to find its mail.
    /// </summary>
    private readonly Dictionary<FolderNode, (OpenAccount Account, long FolderId, FolderRole Role)> _folderIds = [];

    /// <summary>The search-folder nodes, and the saved query each stands for.</summary>
    private readonly Dictionary<FolderNode, (OpenAccount Account, SearchFolder Folder)> _searchFolderIds = [];

    /// <summary>Each account's "Search Folders" heading, so a right-click on it knows the account.</summary>
    private readonly Dictionary<FolderNode, OpenAccount> _searchFolderRoots = [];

    /// <summary>
    /// Replaces the sample with what the store holds. Returns false when there is no account,
    /// which leaves the sample in place.
    /// </summary>
    private bool LoadFromStore(bool selectFirst = true)
    {
        if (_accounts is null) return false;

        var accounts = _accounts.All;
        if (accounts.Count == 0) return false;

        Folders.Clear();
        _folderIds.Clear();
        _searchFolderIds.Clear();
        _searchFolderRoots.Clear();

        var own = accounts.Select(a => a.Account.Address).ToList();
        var now = DateTimeOffset.UtcNow;

        // Favourites first, as the reference lists them: a heading, then each favourite folder
        // by name with its unread count — a second row for a folder that is also in its
        // account's tree below. Seeded once, on a fresh profile, with the default account's
        // Inbox, Sent Items and Deleted Items.
        var favourites = App.Favourites;
        if (!favourites.IsSeeded && (_accounts.Default ?? accounts[0]) is { } primary)
        {
            var seed = new[] { FolderRole.Inbox, FolderRole.Sent, FolderRole.Deleted }
                .Select(role => primary.Mail.FolderWithRole(primary.Account.Id, role))
                .Where(f => f is not null)
                .Select(f => FolderPath(primary.Mail.Folders(primary.Account.Id), f!))
                .ToList();
            favourites.SeedIfFresh(primary.Account.Address, seed);
        }

        Folders.Add(new FolderNode("Favourites", 0, 0, bold: true, kind: FolderNodeKind.FavouritesHeading));
        foreach (var favourite in favourites.All)
        {
            var account = accounts.FirstOrDefault(a => string.Equals(a.Account.Address, favourite.Address, StringComparison.OrdinalIgnoreCase));
            if (account is null) continue;
            var all = account.Mail.Folders(account.Account.Id);
            var folder = all.FirstOrDefault(f => FolderPath(all, f) == favourite.Path);
            if (folder is null) continue;

            var node = new FolderNode(folder.Name, 1, folder.Unread, kind: FolderNodeKind.Favourite);
            _folderIds[node] = (account, folder.Id, folder.Role);
            Folders.Add(node);
        }

        foreach (var account in accounts)
        {
            Folders.Add(new FolderNode(account.Account.Address, 0, 0, bold: true));

            // IMAP folders nest, so the pane does too: a child sits one step in from its
            // parent. The account is depth 0, so a top-level folder is depth 1 as before, and
            // an ordinary POP3 account — every folder parentless — is unchanged.
            var folders = account.Mail.Folders(account.Account.Id);
            var depths = new Dictionary<long, int>();

            foreach (var folder in OrderedForTree(folders))
            {
                var depth = folder.ParentId is { } parent && depths.TryGetValue(parent, out var parentDepth)
                    ? parentDepth + 1
                    : 1;
                depths[folder.Id] = depth;

                var node = new FolderNode(folder.Name, depth, folder.Unread);
                _folderIds[node] = (account, folder.Id, folder.Role);
                Folders.Add(node);
            }

            // Search Folders last, as the reference lists them: the heading, then each saved
            // query with its unread count.
            var root = new FolderNode("Search Folders", 1, 0, kind: FolderNodeKind.SearchFolders);
            _searchFolderRoots[root] = account;
            Folders.Add(root);

            foreach (var search in account.Mail.SearchFolders())
            {
                int unread;
                try
                {
                    unread = account.Mail.SearchFolderUnread(search.Query, own, now);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Search folder “{search.Name}” could not be counted.", ex);
                    unread = 0;
                }

                var node = new FolderNode(search.Name, 2, unread, kind: FolderNodeKind.SearchFolder);
                _searchFolderIds[node] = (account, search);
                Folders.Add(node);
            }
        }

        if (selectFirst) SelectedFolder = Folders.FirstOrDefault(f => _folderIds.ContainsKey(f));
        Raise(nameof(TotalUnread));
        return true;
    }

    /// <summary>
    /// Folders in tree order: each parent immediately before its children, so a single indent
    /// depth reads correctly down a flat list. A folder whose parent is missing is treated as a
    /// root, which is what a POP3 account's flat set already is.
    /// </summary>
    private static IEnumerable<Folder> OrderedForTree(IReadOnlyList<Folder> folders)
    {
        var byParent = folders.Where(f => f.ParentId is not null)
            .GroupBy(f => f.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Ordinal).ThenBy(f => f.Id).ToList());
        var ids = folders.Select(f => f.Id).ToHashSet();

        IEnumerable<Folder> Descend(long parent)
        {
            if (!byParent.TryGetValue(parent, out var children)) yield break;
            foreach (var child in children)
            {
                yield return child;
                foreach (var descendant in Descend(child.Id)) yield return descendant;
            }
        }

        // Roots are the parentless folders and any whose parent is not in this set.
        foreach (var folder in folders.Where(f => f.ParentId is null || !ids.Contains(f.ParentId.Value))
                     .OrderBy(f => f.Ordinal).ThenBy(f => f.Id))
        {
            yield return folder;
            foreach (var descendant in Descend(folder.Id)) yield return descendant;
        }
    }

    /// <summary>
    /// The Outbox, which holds queued mail rather than filed mail.
    /// </summary>
    /// <remarks>
    /// Its rows come from the send queue, not from the messages table — nothing is filed into
    /// this folder, which is why selecting it showed an empty list however much was waiting.
    /// <para>
    /// The row that matters is the one that failed permanently. It keeps its reason in the
    /// store and had nowhere to show it: the status bar said so once, as it happened, and then
    /// the message sat in a queue nobody could see.
    /// </para>
    /// </remarks>
    private void LoadOutbox(OpenAccount account)
    {
        foreach (var item in account.Mail.Outbox(account.Account.Id))
        {
            var raw = account.Mail.LoadBlob(item.BlobId);
            var message = Parse(raw);

            var state = item.State switch
            {
                OutboxState.Failed => $"Not sent — {item.LastError ?? "the server refused it"}",
                OutboxState.Held => "Held: Mailbox is working offline.",
                OutboxState.Sending => "Sending…",
                OutboxState.Sent => "Sent.",
                _ => item.Attempts > 0
                    ? $"Waiting to try again (attempt {item.Attempts + 1})."
                    : "Waiting to be sent.",
            };

            Messages.Add(new MessageRow(
                item.Id,
                message?.To.ToString() ?? account.Account.Address,
                message?.Subject is { Length: > 0 } subject ? subject : "(no subject)",
                state,
                item.Queued.ToLocalTime(),

                // A failure is what the reader is here for, so it is the thing the row bolds.
                isUnread: item.State == OutboxState.Failed,
                $"From: {account.Account.Address}",
                state)
            {
                SizeBytes = raw?.Length ?? 0,
            });
        }

    }

    /// <summary>
    /// Reads a queued message far enough to describe it, and shrugs if it will not parse.
    /// </summary>
    /// <remarks>
    /// A row that cannot be read is still a row: the message is in the queue whatever its bytes
    /// look like, and hiding it would be the same failure this view exists to fix.
    /// </remarks>
    private static MimeKit.MimeMessage? Parse(byte[]? raw)
    {
        if (raw is not { Length: > 0 }) return null;

        try
        {
            using var stream = new MemoryStream(raw);
            return MimeKit.MimeMessage.Load(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The list as it stands, for printing it.
    /// </summary>
    /// <remarks>
    /// Taken from the rows on screen rather than from the folder, so a printed list matches
    /// what was arranged, filtered and grouped. Printing the folder instead would produce a
    /// different list from the one the reader was looking at when they asked.
    /// </remarks>
    public IReadOnlyList<Mailbox.Rendering.TableRow> PrintableRows() =>
    [
        .. VisibleRows.Select(row => row switch
        {
            MessageRow message => new Mailbox.Rendering.TableRow(
                message.From,
                message.Subject,
                message.ReceivedLabel,
                Size(message.SizeBytes)) { IsUnread = message.IsUnread },

            _ => Mailbox.Rendering.TableRow.Group(row.ToString() ?? string.Empty),
        }),
    ];

    private static string Size(long bytes) => bytes switch
    {
        <= 0 => string.Empty,
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>Loads a folder's mail into the list. Called when the selection changes.</summary>
    private void LoadMessages(FolderNode? folder)
    {
        if (_accounts is null || folder is null) return;

        // A search folder: the saved query's results, each row saying which folder it is in.
        if (_searchFolderIds.TryGetValue(folder, out var search))
        {
            LoadSearchFolder(search.Account, search.Folder);
            return;
        }

        // The Search Folders heading itself holds nothing; the list empties.
        if (_searchFolderRoots.ContainsKey(folder))
        {
            Messages.Clear();
            Rebuild();
            SelectedMessage = null;
            return;
        }

        if (!_folderIds.TryGetValue(folder, out var where)) return;

        Messages.Clear();
        LoadFolderView(where.Account, where.FolderId);

        // The Outbox is not a folder of filed mail. What is in it is queued, and the row that
        // matters most is the one that failed permanently — which until now was visible for
        // exactly as long as the status bar took to say it.
        if (where.Role == FolderRole.Outbox)
        {
            LoadOutbox(where.Account);

            // Through the same arrangement and grouping as any other folder. Filling Messages
            // is not what puts rows on screen — Rebuild is — and returning early here left the
            // previous folder's rows displayed over an Outbox that had been loaded correctly.
            Rebuild();
            SelectedMessage = Messages.FirstOrDefault();
            return;
        }

        // The folder's awake mail, or — Filter Email › Snoozed — only what is snoozed, each row
        // saying when it comes back where the preview would be. With Focused Inbox on, the Inbox
        // lists one half at a time.
        var half = FocusedInboxOn && where.Role == FolderRole.Inbox ? (bool?)!ShowOther : null;
        var summaries = ShowSnoozed
            ? where.Account.Mail.Snoozed(where.FolderId)
            : where.Account.Mail.Messages(where.FolderId, half);

        foreach (var summary in summaries)
        {
            var preview = summary.SnoozedUntil is { } until
                ? $"Snoozed until {SnoozeLabel(until)} — {summary.Preview}"
                : summary.Preview;

            Messages.Add(new MessageRow(
                summary.Id,
                summary.DisplayFrom,
                summary.Subject,
                preview,
                summary.Received,
                !summary.IsRead,
                $"To: {where.Account.Account.Address}",
                summary.Preview)
            {
                SizeBytes = summary.SizeBytes,
                HasAttachment = summary.HasAttachment,
                IsFlagged = summary.IsFlagged,
                FollowUpComplete = summary.FollowUpComplete,
                FollowUpDue = summary.FollowUpDue,
                SnoozedUntil = summary.SnoozedUntil,
                Importance = summary.Importance,
                ThreadKey = Store.Lists.Arrangements.NormalisedSubject(summary.Subject),
                FolderId = summary.FolderId,
                FromAddress = summary.FromAddress,
                To = summary.To,
                Cc = summary.Cc,
                Sent = summary.Sent,
                HasReminder = summary.Reminder is not null,
            });
        }

        // One query for the page's categories rather than one per row.
        var categories = where.Account.Mail.CategoriesFor([.. Messages.Select(m => m.Id)]);
        foreach (var row in Messages)
        {
            if (categories.TryGetValue(row.Id, out var assigned))
            {
                row.CategoryTokens = [.. assigned.Select(c => c.ColourToken)];
                row.CategoryNames = [.. assigned.Select(c => c.Name)];
            }
        }

        Rebuild();
        SelectedMessage = Messages.FirstOrDefault();
    }

    /// <summary>How a snooze time is written: a time today, else the day and time.</summary>
    internal static string SnoozeLabel(DateTimeOffset until, DateTimeOffset? today = null)
    {
        var now = today ?? DateTimeOffset.Now;
        var local = until.ToLocalTime();
        return local.Date == now.Date ? local.ToString("h:mm tt") : local.ToString("ddd d MMM, h:mm tt");
    }

    /// <summary>
    /// How the list writes a date: a time for today, a weekday within the week, otherwise the
    /// date. Matches what the reference shows.
    /// </summary>
    internal static string Received(DateTimeOffset when, DateTimeOffset? today = null)
    {
        var now = today ?? DateTimeOffset.Now;
        var local = when.ToLocalTime();

        if (local.Date == now.Date) return local.ToString("h:mm tt");
        if (local.Date == now.Date.AddDays(-1)) return "Yesterday";
        if (now.Date - local.Date < TimeSpan.FromDays(7)) return local.ToString("ddd h:mm tt");
        return local.ToString("ddd dd/MM/yyyy");
    }

    /// <summary>Re-reads the current folder, after a send/receive has filed new mail.</summary>
    public void Refresh()
    {
        if (_accounts is null) return;

        var previous = Remember(SelectedFolder);
        LoadFromStore();
        SelectedFolder = previous is null
            ? Folders.FirstOrDefault(f => _folderIds.ContainsKey(f))
            : SameNodeAs(previous.Value) ?? SelectedFolder;
    }

    /// <summary>
    /// What a node stood for, remembered across a rebuild that throws every node away: which
    /// folder of which account, or which search folder, and whether it was the row under
    /// Favourites or the one in the tree — plus its name, for a heading that stands for nothing.
    /// </summary>
    private (string? Address, long? FolderId, long? SearchId, FolderNodeKind Kind, string Name)? Remember(FolderNode? node)
    {
        if (node is null) return null;
        var where = _folderIds.TryGetValue(node, out var f) ? f : default;
        var search = _searchFolderIds.TryGetValue(node, out var s) ? s.Folder.Id : (long?)null;
        return (where.Account?.Account.Address, where.Account is null ? null : where.FolderId, search, node.Kind, node.Name);
    }

    /// <summary>
    /// The node in the rebuilt pane that stands for what a remembered one did. By identity, not
    /// by name: two accounts each have an Inbox, and a favourite folder is listed twice — the
    /// same folder is looked for first with the same kind, then any kind, and a heading by name.
    /// </summary>
    private FolderNode? SameNodeAs((string? Address, long? FolderId, long? SearchId, FolderNodeKind Kind, string Name) previous)
    {
        if (previous.FolderId is { } folderId && previous.Address is { } address)
        {
            var candidates = _folderIds
                .Where(kv => kv.Value.FolderId == folderId && string.Equals(kv.Value.Account.Account.Address, address, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            return candidates.FirstOrDefault(n => n.Kind == previous.Kind) ?? candidates.FirstOrDefault();
        }

        if (previous.SearchId is { } searchId)
        {
            return _searchFolderIds.FirstOrDefault(kv => kv.Value.Folder.Id == searchId).Key;
        }

        return Folders.FirstOrDefault(f => f.Kind == previous.Kind && f.Name == previous.Name);
    }

    /// <summary>The reference application puts search above the message list, not in the title bar.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            Raise(nameof(ShowSearchPlaceholder));
            RunSearch();
        }
    }

    /// <summary>Where a search looks.</summary>
    public enum SearchScope
    {
        /// <summary>Only the folder on screen.</summary>
        ThisFolder,

        /// <summary>Every folder of the account whose folder is selected. The reference's default.</summary>
        CurrentMailbox,

        /// <summary>Every folder of every account.</summary>
        AllMailboxes,
    }

    private SearchScope _scope = SearchScope.CurrentMailbox;

    /// <summary>The scope the search box runs against, re-run when it changes.</summary>
    public SearchScope Scope
    {
        get => _scope;
        set
        {
            if (!Set(ref _scope, value)) return;
            Raise(nameof(ScopeLabel));
            Raise(nameof(ScopeIndex));
            if (IsSearching) RunSearch();
        }
    }

    /// <summary>The scope selector's options, in the reference's order.</summary>
    public IReadOnlyList<string> ScopeOptions { get; } =
        ["This Folder", "Current Mailbox", "All Mailboxes"];

    /// <summary>The scope as an index, so a ComboBox can bind to it.</summary>
    public int ScopeIndex
    {
        get => (int)_scope;
        set => Scope = (SearchScope)Math.Clamp(value, 0, 2);
    }

    public string ScopeLabel => ScopeOptions[(int)_scope];

    private bool _isSearching;

    /// <summary>True while search results are on screen rather than a folder.</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (Set(ref _isSearching, value)) Raise(nameof(SearchResultSummary));
        }
    }

    private int _searchResultCount;

    /// <summary>The line above the results: how many, and where they were looked for.</summary>
    public string SearchResultSummary => _searchResultCount switch
    {
        0 => $"No results in {ScopeLabel}",
        1 => $"1 result in {ScopeLabel}",
        _ => $"{_searchResultCount} results in {ScopeLabel}",
    };

    /// <summary>Clears the search and returns to the folder — the box's ✕ and Escape.</summary>
    public void ClearSearch()
    {
        if (string.IsNullOrEmpty(_searchText) && !IsSearching) return;
        _searchText = string.Empty;
        Raise(nameof(SearchText));
        Raise(nameof(ShowSearchPlaceholder));
        RunSearch();
    }

    /// <summary>
    /// Runs the search box against the store, or returns to the folder when it is empty.
    /// </summary>
    /// <remarks>
    /// Instant: it runs on every keystroke, because FTS5 over a mailbox this size answers in a
    /// millisecond and the reference filters as you type. Results carry the folder they were
    /// found in, since a search across folders is only legible if each row says where it is.
    /// </remarks>
    /// <summary>
    /// A search while a module other than Mail is open, for the shell to hand to that module.
    /// </summary>
    /// <remarks>
    /// The box searches whatever is on screen, which is what the reference's own Instant Search
    /// does: in the calendar it finds appointments, in People it finds people. What each module
    /// does with the words is the module's business, so this carries them and nothing else.
    /// </remarks>
    public event EventHandler<string>? ModuleSearchRequested;

    private void RunSearch()
    {
        if (_accounts is null) return;

        // Only the mail module's list is this class's to fill. Everything else is a module with
        // its own list, and its own idea of what a match is.
        if (Module != MailboxModule.Mail)
        {
            IsSearching = _searchText.Trim().Length > 0;
            ModuleSearchRequested?.Invoke(this, _searchText.Trim());
            return;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            if (!IsSearching) return;
            IsSearching = false;
            _searchResultCount = 0;
            LoadMessages(_selectedFolder);
            return;
        }

        Messages.Clear();

        var current = _selectedFolder is { } n && _folderIds.TryGetValue(n, out var where) ? where : default;

        // Which stores and which folder filter each search runs against, by scope.
        IEnumerable<(OpenAccount Account, long? FolderId)> targets = _scope switch
        {
            SearchScope.ThisFolder when current.Account is not null =>
                [(current.Account, current.FolderId)],
            SearchScope.CurrentMailbox when current.Account is not null =>
                [(current.Account, null)],
            SearchScope.AllMailboxes =>
                _accounts.All.Select(a => (a, (long?)null)),
            // No folder selected yet: fall back to every account rather than nothing.
            _ => _accounts.All.Select(a => (a, (long?)null)),
        };

        foreach (var (account, folderId) in targets)
        {
            var names = FolderNamesFor(account);
            foreach (var summary in account.Mail.Search(_searchText, folderId))
            {
                var label = _scope == SearchScope.ThisFolder
                    ? string.Empty
                    : names.GetValueOrDefault(summary.FolderId, string.Empty);

                Messages.Add(new MessageRow(
                    summary.Id,
                    summary.DisplayFrom,
                    summary.Subject,
                    summary.Preview,
                    summary.Received,
                    !summary.IsRead,
                    $"To: {account.Account.Address}",
                    summary.Preview)
                {
                    SizeBytes = summary.SizeBytes,
                    HasAttachment = summary.HasAttachment,
                    IsFlagged = summary.IsFlagged,
                    FollowUpComplete = summary.FollowUpComplete,
                    FollowUpDue = summary.FollowUpDue,
                    Importance = summary.Importance,
                    ThreadKey = Store.Lists.Arrangements.NormalisedSubject(summary.Subject),
                    FolderId = summary.FolderId,
                    FolderLabel = label,
                });
            }
        }

        _searchResultCount = Messages.Count;
        LoadCategoriesForVisible();
        IsSearching = true;
        Raise(nameof(SearchResultSummary));
        Rebuild();
        SelectedMessage = Messages.FirstOrDefault();
    }

    /// <summary>The results of a saved query, as the list draws a search: with folder labels.</summary>
    private void LoadSearchFolder(OpenAccount account, SearchFolder search)
    {
        Messages.Clear();
        var names = FolderNamesFor(account);
        var own = _accounts?.All.Select(a => a.Account.Address).ToList() ?? [];

        IReadOnlyList<MessageSummary> results;
        try
        {
            results = account.Mail.SearchFolderResults(search.Query, own, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Warn($"Search folder “{search.Name}” could not be run.", ex);
            results = [];
            StatusRight = $"The search folder “{search.Name}” could not be run.";
        }

        foreach (var summary in results)
        {
            Messages.Add(new MessageRow(
                summary.Id,
                summary.DisplayFrom,
                summary.Subject,
                summary.Preview,
                summary.Received,
                !summary.IsRead,
                $"To: {account.Account.Address}",
                summary.Preview)
            {
                SizeBytes = summary.SizeBytes,
                HasAttachment = summary.HasAttachment,
                IsFlagged = summary.IsFlagged,
                FollowUpComplete = summary.FollowUpComplete,
                FollowUpDue = summary.FollowUpDue,
                SnoozedUntil = summary.SnoozedUntil,
                Importance = summary.Importance,
                ThreadKey = Store.Lists.Arrangements.NormalisedSubject(summary.Subject),
                FolderId = summary.FolderId,
                FolderLabel = names.GetValueOrDefault(summary.FolderId, string.Empty),
            });
        }

        LoadCategoriesForVisible();
        Rebuild();
        SelectedMessage = Messages.FirstOrDefault();
    }

    /// <summary>The store folder an ordinary node stands for, for the pane's menu; null for headings and search folders.</summary>
    public (OpenAccount Account, Folder Folder)? FolderOf(FolderNode node)
        => _folderIds.TryGetValue(node, out var where) && where.Account.Mail.GetFolder(where.FolderId) is { } folder
            ? (where.Account, folder)
            : null;

    /// <summary>
    /// Selects the node standing for a store folder, after the pane has been rebuilt — the row
    /// in the account's tree rather than the one under Favourites, when the folder has both.
    /// </summary>
    public void SelectFolder(OpenAccount account, long folderId)
    {
        Refresh();
        var candidates = _folderIds
            .Where(kv => kv.Value.FolderId == folderId && kv.Value.Account.Account.Address == account.Account.Address)
            .Select(kv => kv.Key)
            .ToList();
        SelectedFolder = candidates.FirstOrDefault(n => n.Kind != FolderNodeKind.Favourite) ?? candidates.FirstOrDefault() ?? SelectedFolder;
    }

    /// <summary>A folder's names from the top of its account, joined by "/" — how Favourites name it.</summary>
    public static string FolderPath(IReadOnlyList<Folder> all, Folder folder)
    {
        var names = new List<string> { folder.Name };
        var parent = folder.ParentId;
        var guard = 0;
        while (parent is { } id && all.FirstOrDefault(f => f.Id == id) is { } up && guard++ < 64)
        {
            names.Insert(0, up.Name);
            parent = up.ParentId;
        }

        return string.Join('/', names);
    }

    /// <summary>Whether a folder is listed under Favourites.</summary>
    public bool IsFavourite(OpenAccount account, Folder folder)
        => App.Favourites.Contains(account.Account.Address, FolderPath(account.Mail.Folders(account.Account.Id), folder));

    /// <summary>Show in Favourites / Remove from Favourites.</summary>
    public void ToggleFavourite(OpenAccount account, Folder folder)
    {
        var path = FolderPath(account.Mail.Folders(account.Account.Id), folder);
        if (App.Favourites.Contains(account.Account.Address, path)) App.Favourites.Remove(account.Account.Address, path);
        else App.Favourites.Add(account.Account.Address, path);
        SelectFolder(account, folder.Id);
    }

    /// <summary>Mark All as Read: every message of a folder, at once.</summary>
    public int MarkFolderRead(OpenAccount account, long folderId)
    {
        var ids = account.Mail.Messages(folderId, int.MaxValue).Where(m => !m.IsRead).Select(m => m.Id).ToList();
        if (ids.Count == 0) return 0;
        var count = account.Mail.SetRead(ids, read: true);
        foreach (var row in Messages.Where(r => ids.Contains(r.Id))) row.IsUnread = false;
        RefreshCounts();
        Raise(nameof(StatusLeft));
        return count;
    }

    /// <summary>Empty Folder / Delete All: everything in a folder, gone for good.</summary>
    public int EmptyFolder(OpenAccount account, long folderId)
    {
        var ids = account.Mail.Messages(folderId, int.MaxValue).Select(m => m.Id).ToList();
        if (ids.Count == 0) return 0;
        var count = account.Mail.DeleteMessages(ids);
        Refresh();
        return count;
    }

    /// <summary>The account a search-folder node belongs to, for the pane's menu; null for other nodes.</summary>
    public OpenAccount? SearchFolderAccount(FolderNode node)
        => _searchFolderRoots.TryGetValue(node, out var root) ? root
            : _searchFolderIds.TryGetValue(node, out var search) ? search.Account
            : null;

    /// <summary>The saved query a node stands for, or null for the heading and ordinary folders.</summary>
    public SearchFolder? SearchFolderOf(FolderNode node)
        => _searchFolderIds.TryGetValue(node, out var search) ? search.Folder : null;

    /// <summary>Selects the search folder with this id after the pane has been rebuilt.</summary>
    public void SelectSearchFolder(long id)
    {
        Refresh();
        SelectedFolder = _searchFolderIds.FirstOrDefault(kv => kv.Value.Folder.Id == id).Key ?? SelectedFolder;
    }

    /// <summary>Folder id to display name for one account, for labelling a search result.</summary>
    private Dictionary<long, string> FolderNamesFor(OpenAccount account) =>
        account.Mail.Folders(account.Account.Id).ToDictionary(f => f.Id, f => f.Name);

    /// <summary>Fills in the category swatches for whatever is currently in <see cref="Messages"/>.</summary>
    private void LoadCategoriesForVisible()
    {
        // Group ids by account, because categories live in each account's own store.
        var byId = Messages.ToDictionary(m => m.Id, m => m);
        foreach (var account in _accounts?.All ?? [])
        {
            var mine = account.Mail.CategoriesFor([.. byId.Keys]);
            foreach (var (id, assigned) in mine)
            {
                if (byId.TryGetValue(id, out var row))
                {
                    row.CategoryTokens = [.. assigned.Select(c => c.ColourToken)];
                }
            }
        }
    }

    /// <summary>
    /// Drives a placeholder drawn behind the box rather than the control's own watermark. The
    /// watermark is dimmed by the control theme, which is unreadable against the light search
    /// fill, and its opacity is not reachable from a style selector.
    /// </summary>
    public bool ShowSearchPlaceholder => string.IsNullOrEmpty(_searchText);

    /// <summary>
    /// Just "Search". Measured off the reference, which does not name the folder here — the
    /// scope is shown in the dropdown the box opens, not in the placeholder.
    /// </summary>
    public string SearchPlaceholder => "Search";

    /// <summary>Magnifier shown inside the search box, at the measured 14px.</summary>
    public string SearchGlyph { get; } = IconGlyphs.Get("search", 16);

    /// <summary>
    /// Opens the Quick Access Toolbar's customize menu. It sits after the last QAT button and
    /// is always present, so it is not part of the rearrangeable command list.
    /// </summary>
    public string CustomizeGlyph { get; } = IconGlyphs.Get("chevron-down", 16);

    public ModuleTab[] Modules { get; } =
    [
        new(MailboxModule.Mail, "mail", isActive: true),
        new(MailboxModule.Calendar, "calendar", isActive: false),
        new(MailboxModule.People, "people", isActive: false),
        new(MailboxModule.Tasks, "tasks", isActive: false),
        new(MailboxModule.Notes, "notes", isActive: false),
        new(MailboxModule.Journal, "journal", isActive: false),
    ];

    public string WindowTitle => $"{SelectedFolderName} - you@example.com - Mailbox";

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value)) return;

            var id = _themes.Library.Ids.FirstOrDefault(
                t => _themes.DisplayName(t) == value) ?? OfficeThemes.Colorful;
            try
            {
                _themes.Apply(id);
            }
            catch (Mailbox.Theming.Tokens.ThemeResolutionException ex)
            {
                Log.Warn($"Theme \"{id}\" could not be applied: {ex.Message}");
            }
        }
    }

    public FolderNode? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!Set(ref _selectedFolder, value)) return;
            Raise(nameof(SelectedFolderName));

            // Choosing a folder leaves search behind — the reference drops out of results the
            // moment a folder is clicked. Clear the box quietly so it does not re-run a search.
            if (IsSearching || !string.IsNullOrEmpty(_searchText))
            {
                _searchText = string.Empty;
                IsSearching = false;
                _searchResultCount = 0;
                Raise(nameof(SearchText));
                Raise(nameof(ShowSearchPlaceholder));
            }

            LoadMessages(_selectedFolder);
            Raise(nameof(SearchPlaceholder));
            Raise(nameof(WindowTitle));
            Raise(nameof(StatusLeft));
            RaisePivot();
        }
    }

    /// <summary>
    /// What the list has selected, which may be a group header. Headers are not messages, so
    /// selecting one leaves the reading pane alone rather than blanking it.
    /// </summary>
    /// <remarks>
    /// Selecting a header folds its group and selecting a conversation opens it, and both of
    /// those replace the row list — which cannot be done from here. The list is in the middle of
    /// its own selection update when it sets this, and swapping its items under it leaves its
    /// selection model holding a row number past the end of the new list, which throws where
    /// nothing can catch it. So the fold happens on the next pass of the loop, by which time the
    /// update has finished. Pressing Home in a grouped list is what found this.
    /// </remarks>
    public object? SelectedRow
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            if (value is MessageRow row) SelectedMessage = row;
            if (value is GroupHeaderRow header) Later(() => ToggleGroupCollapsed(header.Header));
            if (value is ConversationRow thread)
            {
                SelectedMessage = thread.Newest;
                Later(() => ToggleConversation(thread));
            }
        }
    }

    /// <summary>
    /// Runs something on the next pass of the loop, or now where there is no loop to wait for —
    /// a test builds this class without one and would otherwise never see the result.
    /// </summary>
    private static void Later(Action what)
    {
        if (Avalonia.Application.Current is null) what();
        else Avalonia.Threading.Dispatcher.UIThread.Post(what, Avalonia.Threading.DispatcherPriority.Background);
    }

    public MessageRow? SelectedMessage
    {
        get => _selectedMessage;
        set => Set(ref _selectedMessage, value);
    }

    public string SelectedFolderName => SelectedFolder?.Name ?? "Inbox";

    // Status-bar and pane glyphs. Held here so the XAML never names an icon codepoint.
    public FontFamily IconFamily { get; } = IconFont.Family;
    // ---- List shaping ---------------------------------------------------------------------
    // Filtering, sorting and grouping run over the in-memory sample for now. They are view
    // state either way: Phase 2 swaps the source collection, not any of this.

    // ---- Ignore and Clean Up ---------------------------------------------------------------------

    /// <summary>Whether every selected row's conversation is already ignored — the button then reads Stop Ignoring.</summary>
    public bool IsIgnored(IReadOnlyList<MessageRow> rows)
        => rows.Count > 0 && Mail(rows) is { } mail && rows.All(r => mail.IsIgnored(StoreKey(r)));

    /// <summary>
    /// The store's thread key for a row. The row's own <see cref="MessageRow.ThreadKey"/> keeps
    /// the subject's case for the conversation view; the store folds it, and the ignore list is
    /// the store's.
    /// </summary>
    private static string StoreKey(MessageRow row) => MailRepository.ThreadKeyOf(row.Subject);

    /// <summary>
    /// Ignore Conversation: the selection's conversations go to Deleted Items — every message of
    /// each, in every folder — and stay ignored, so what arrives in them later goes there too.
    /// Stop Ignoring brings the conversation back to the Inbox and forgets it.
    /// </summary>
    public void IgnoreConversation(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail || CurrentAccount is not { } account) return;

        var deleted = mail.FolderWithRole(account.Account.Id, FolderRole.Deleted);
        var inbox = mail.FolderWithRole(account.Account.Id, FolderRole.Inbox);
        if (deleted is null || inbox is null) return;

        var keys = rows.Select(StoreKey).Where(k => k.Length > 0).Distinct().ToList();
        var stopping = keys.All(mail.IsIgnored);
        var moved = 0;

        foreach (var key in keys)
        {
            if (stopping)
            {
                mail.Unignore(key);
                var back = mail.MessagesInThread(key, includeDeleted: true)
                    .Where(m => m.FolderId == deleted.Id).Select(m => m.Id).ToList();
                if (back.Count > 0) moved += mail.MoveMessages(back, inbox.Id);
            }
            else
            {
                mail.Ignore(key, rows.First(r => StoreKey(r) == key).Subject, DateTimeOffset.UtcNow);
                var away = mail.MessagesInThread(key).Select(m => m.Id).ToList();
                if (away.Count > 0) moved += mail.MoveMessages(away, deleted.Id);
            }
        }

        ReloadCurrentView();
        RefreshCounts();
        StatusRight = stopping
            ? $"No longer ignoring {(keys.Count == 1 ? "the conversation" : $"{keys.Count} conversations")}; {Describe(moved)} back in the Inbox."
            : $"{(keys.Count == 1 ? "Conversation" : $"{keys.Count} conversations")} ignored; {Describe(moved)} moved to Deleted Items.";
    }

    /// <summary>
    /// Clean Up: the redundant messages of the selection's conversations, of the folder, or of the
    /// folder and its subfolders, go to Deleted Items. Returns how many went.
    /// </summary>
    public int CleanUp(IReadOnlyList<MessageRow> rows, bool wholeFolder, bool withSubfolders)
    {
        if (CurrentAccount is not { } account || CurrentMail is not { } mail) return 0;

        // Options › Mail's "Cleaned-up items will go to this folder", by name in this account; Deleted Items otherwise.
        var wanted = App.MailOptions.CleanUpFolder;
        var deleted = (wanted.Length > 0 ? mail.Folders(account.Account.Id).FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase)) : null)
                      ?? mail.FolderWithRole(account.Account.Id, FolderRole.Deleted);
        if (deleted is null) return 0;

        var policy = App.MailOptions.CleanUpPolicy;
        var folders = new List<long>();
        if (wholeFolder && CurrentFolder is { } folder)
        {
            folders.Add(folder.Id);
            if (withSubfolders)
            {
                var all = mail.Folders(account.Account.Id);
                var frontier = new Queue<long>([folder.Id]);
                while (frontier.TryDequeue(out var parent))
                {
                    foreach (var child in all.Where(f => f.ParentId == parent))
                    {
                        folders.Add(child.Id);
                        frontier.Enqueue(child.Id);
                    }
                }
            }
        }

        var keys = wholeFolder
            ? folders.SelectMany(f => mail.Messages(f, int.MaxValue)).Select(m => MailRepository.ThreadKeyOf(m.Subject)).Where(k => k.Length > 0).Distinct().ToList()
            : rows.Select(StoreKey).Where(k => k.Length > 0).Distinct().ToList();

        var doomed = new List<long>();
        foreach (var key in keys)
        {
            var thread = mail.MessagesInThread(key);
            if (thread.Count < 2) continue;

            var categorized = mail.CategoriesFor([.. thread.Select(m => m.Id)]);
            var candidates = thread.Select(m => new Mailbox.Core.Conversations.CleanUpCandidate(m.Id, m.Received, TextOf(mail, m))
            {
                IsUnread = !m.IsRead,
                IsCategorized = categorized.ContainsKey(m.Id),
                IsFlagged = m.IsFlagged,
                IsSigned = mail.Authentication(m.Id) is not null,
            }).ToList();

            doomed.AddRange(Mailbox.Core.Conversations.CleanUp.Redundant(candidates, policy));
        }

        if (doomed.Count > 0) mail.MoveMessages(doomed, deleted.Id);
        ReloadCurrentView();
        RefreshCounts();
        StatusRight = doomed.Count == 0
            ? "No redundant messages were found."
            : $"{Describe(doomed.Count)} moved to {deleted.Name} by Clean Up.";
        return doomed.Count;
    }

    /// <summary>A message's plain text for the containment check: the raw message parsed, or the preview.</summary>
    private static string TextOf(MailRepository mail, MessageSummary summary)
    {
        if (summary.BodyText.Length > 0) return summary.BodyText;
        if (mail.LoadRaw(summary.Id) is { } raw && Parse(raw) is { } message)
        {
            return message.TextBody ?? message.HtmlBody ?? summary.Preview;
        }

        return summary.Preview;
    }

    // ---- Focused Inbox (§12) ------------------------------------------------------------------
    // When the view is on and the Inbox is open, the All / Unread pivot becomes Focused / Other:
    // the Inbox lists one half at a time. Elsewhere the pivot is what it always was.

    /// <summary>Whether Focused Inbox is switched on, from the settings.</summary>
    public bool FocusedInboxOn
    {
        get => App.MailOptions.ShowFocusedInbox;
        set
        {
            if (App.MailOptions.ShowFocusedInbox == value) return;
            App.MailOptions.ShowFocusedInbox = value;
            RaisePivot();
            LoadMessages(_selectedFolder);
        }
    }

    /// <summary>Whether the pivot on show is Focused / Other rather than All / Unread.</summary>
    public bool ShowFocusedPivot => FocusedInboxOn && CurrentFolderRole == FolderRole.Inbox;

    /// <summary>Which half of the Inbox is on show while the Focused pivot is up: Other, or Focused.</summary>
    public bool ShowOther
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            RaisePivot();
            LoadMessages(_selectedFolder);
        }
    }

    public string PivotLeftLabel => ShowFocusedPivot ? "Focused" : "All";
    public string PivotRightLabel => ShowFocusedPivot ? "Other" : "Unread";
    public FontWeight PivotLeftWeight => ShowFocusedPivot ? (ShowOther ? FontWeight.Normal : FontWeight.SemiBold) : AllFilterWeight;
    public FontWeight PivotRightWeight => ShowFocusedPivot ? (ShowOther ? FontWeight.SemiBold : FontWeight.Normal) : UnreadFilterWeight;

    /// <summary>The pivot's left and right halves — All / Focused, Unread / Other — as commands.</summary>
    public RelayCommand PivotLeft => field ??= new RelayCommand(() =>
    {
        if (ShowFocusedPivot) ShowOther = false; else UnreadOnly = false;
    });

    public RelayCommand PivotRight => field ??= new RelayCommand(() =>
    {
        if (ShowFocusedPivot) ShowOther = true; else UnreadOnly = true;
    });

    private void RaisePivot()
    {
        Raise(nameof(ShowFocusedPivot));
        Raise(nameof(PivotLeftLabel));
        Raise(nameof(PivotRightLabel));
        Raise(nameof(PivotLeftWeight));
        Raise(nameof(PivotRightWeight));
    }

    /// <summary>Move to Other / Move to Focused over the rows, with "always" remembering their senders.</summary>
    public void SetFocused(IReadOnlyList<MessageRow> rows, bool focused, bool always)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        mail.SetFocused([.. rows.Select(r => r.Id)], focused);

        var senders = always ? Senders(rows) : [];
        foreach (var sender in senders) mail.SetFocusOverride(sender, focused, DateTimeOffset.UtcNow);

        ReloadCurrentView();
        RefreshCounts();

        var where = focused ? "Focused" : "Other";
        StatusRight = always && senders.Count > 0
            ? $"{Describe(rows.Count)} moved to {where}; mail from {(senders.Count == 1 ? senders[0] : $"{senders.Count} senders")} will always go there."
            : $"{Describe(rows.Count)} moved to {where}.";
    }

    /// <summary>The Filter Email menu's filters, one at a time, as the reference applies them.</summary>
    public enum ListFilter
    {
        None,
        Unread,
        HasAttachments,
        Flagged,
        Important,
        Categorized,
        ThisWeek,
    }

    /// <summary>Which filter the list is under. The header's Unread pivot is the same state.</summary>
    public ListFilter Filter
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Rebuild();
            Raise(nameof(UnreadOnly));
            Raise(nameof(AllFilterWeight));
            Raise(nameof(UnreadFilterWeight));
            RaisePivot();
        }
    }

    /// <summary>All, or only what has not been read — the header's pivot, and the Unread filter.</summary>
    public bool UnreadOnly
    {
        get => Filter == ListFilter.Unread;
        set => Filter = value ? ListFilter.Unread : (Filter == ListFilter.Unread ? ListFilter.None : Filter);
    }

    /// <summary>Whether a row passes the filter in force.</summary>
    private bool Passes(MessageRow row) => Filter switch
    {
        ListFilter.Unread => row.IsUnread,
        ListFilter.HasAttachments => row.HasAttachment,
        ListFilter.Flagged => row.IsFlagged,
        ListFilter.Important => row.Importance == 2,
        ListFilter.Categorized => row.HasCategories,
        ListFilter.ThisWeek => row.Received.ToLocalTime() >= StartOfThisWeek(),
        _ => true,
    };

    private static DateTimeOffset StartOfThisWeek()
    {
        var today = DateTimeOffset.Now.Date;
        return new DateTimeOffset(today.AddDays(-(int)today.DayOfWeek));
    }

    public FontWeight AllFilterWeight => UnreadOnly ? FontWeight.Normal : FontWeight.SemiBold;
    public FontWeight UnreadFilterWeight => UnreadOnly ? FontWeight.SemiBold : FontWeight.Normal;

    /// <summary>
    /// How the list is grouped and ordered. Not a sort: arranging by Date groups into Today and
    /// Yesterday, arranging by From groups by sender.
    /// </summary>
    public Arrangement Arrangement
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            _collapsed.Clear();
            Rebuild();
            Raise(nameof(ArrangementLabel));
            RememberSort();
        }
    } = Arrangement.Date;

    public string ArrangementLabel => $"By {Arrangements.Label(Arrangement)}";

    /// <summary>Every arrangement, for the menu behind the label.</summary>
    public IReadOnlyList<Arrangement> Arrangements_ => Arrangements.All;

    public bool SortDescending
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Rebuild();
            Raise(nameof(SortGlyph));
            RememberSort();
        }
    } = true;

    /// <summary>The arrow beside the arrangement label.</summary>
    public string SortGlyph => SortDescending ? "\u2193" : "\u2191";

    /// <summary>
    /// Which groups are folded shut, by header. By header rather than by index: the indices
    /// move whenever the arrangement or the filter changes, and a collapse that jumps to a
    /// different group is worse than one that is forgotten.
    /// </summary>
    private readonly HashSet<string> _collapsed = [];

    public void ToggleGroupCollapsed(string header)
    {
        if (!_collapsed.Remove(header)) _collapsed.Add(header);
        Rebuild();
    }

    /// <summary>
    /// The flat sequence the list draws: a header, then its rows unless it is folded shut.
    /// Rebuilt in one pass so nothing can disagree about what is on screen.
    /// </summary>
    /// <remarks>
    /// A list replaced wholesale, not an observable collection mutated in place. Filling a
    /// hundred thousand rows one Add at a time raises a hundred thousand collection-changed
    /// notifications and the list responds to every one of them — the panel virtualizes fine,
    /// the notifications are what does not.
    /// </remarks>
    public IReadOnlyList<object> VisibleRows
    {
        get;
        private set => Set(ref field, value);
    } = [];

    // ---- How the rows are drawn --------------------------------------------------------------

    /// <summary>
    /// Compact stacks two lines with a preview; single-line is the column grid. The reference
    /// switches between them by pane width, and lets Change View override.
    /// </summary>
    public bool CompactRows
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(RowHeight));
            Raise(nameof(ShowPreviewLine));
        }
    } = true;

    /// <summary>Preview lines under the subject: 0 to 3, as Message Preview offers.</summary>
    public int PreviewLines
    {
        get;
        set
        {
            if (!Set(ref field, Math.Clamp(value, 0, 3))) return;
            Raise(nameof(RowHeight));
            Raise(nameof(ShowPreviewLine));
            Raise(nameof(ShowCardPreview));
        }
    } = 1;

    public bool ShowPreviewLine => CompactRows && PreviewLines > 0 && !CardRows;

    /// <summary>The card's preview lines, when the view has any.</summary>
    public bool ShowCardPreview => CardRows && PreviewLines > 0;

    /// <summary>
    /// Row height follows the layout and the preview count, so a taller row is a taller row
    /// rather than a clipped one: the card stacks sender, subject and preview; the line has
    /// its preview beneath.
    /// </summary>
    public double RowHeight => CardRows
        ? 44 + (PreviewLines * 16)
        : CompactRows ? 26 + (PreviewLines * 18) : 24;

    /// <summary>
    /// Show as Conversations. Off, the list is one row per message; on, replies fold under the
    /// newest and the group counts what is on screen rather than what is behind it.
    /// </summary>
    public bool ShowAsConversations
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            _expanded.Clear();
            Rebuild();
        }
    }

    /// <summary>Which threads are open, by key — indices move, keys do not.</summary>
    private readonly HashSet<string> _expanded = [];

    public void ToggleConversation(ConversationRow row)
    {
        var key = row.Newest.ThreadKey;
        if (!_expanded.Remove(key)) _expanded.Add(key);
        Rebuild();
    }

    private void Rebuild()
    {
        // The list is replaced wholesale, and a replaced list has nothing selected: what was
        // selected is put back once the new list is in, when it is still there to select.
        var keep = SelectedRow as MessageRow ?? SelectedMessage;
        RebuildRows();
        if (keep is not null && VisibleRows.Contains(keep) && !ReferenceEquals(SelectedRow, keep))
        {
            SelectedRow = keep;
        }
    }

    private void RebuildRows()
    {
        var built = new List<object>();

        var rows = Filter == ListFilter.None ? Messages : Messages.Where(Passes);
        if (!_viewFilter.IsEmpty) rows = rows.Where(r => Mailbox.Core.Search.SearchMatcher.Matches(_viewFilter, r.Facts()));
        var groups = Store.Lists.Arrangements.Group(rows, GroupArrangement, GroupDescending);

        // Group By's "All collapsed": every group of this build starts shut, once.
        if (_collapseAllNext)
        {
            _collapseAllNext = false;
            foreach (var group in groups) _collapsed.Add(group.Header);
        }

        // Other Settings' "Show items in Groups" off: one run of rows, no headers.
        if (!CurrentView.ShowInGroups && groups.Count > 0)
        {
            var flat = groups.SelectMany(g => g.Items).ToList();
            var content = ShowAsConversations ? Threaded(flat) : [.. flat.Select(r => (object)Reset(r))];
            VisibleRows = content;
            Raise(nameof(VisibleCount));
            Raise(nameof(StatusLeft));
            return;
        }

        foreach (var group in groups)
        {
            var collapsed = _collapsed.Contains(group.Header);

            // The header counts what the group will show. With conversations on, a thread of
            // five is one row, and a header claiming five would not match what is beneath it.
            var content = ShowAsConversations
                ? Threaded(group.Items)
                : [.. group.Items.Select(r => (object)Reset(r))];

            built.Add(new GroupHeaderRow(group.Header, Countable(content), collapsed));

            if (collapsed) continue;

            built.AddRange(content);
        }

        VisibleRows = built;
        Raise(nameof(VisibleCount));
        Raise(nameof(StatusLeft));
    }

    /// <summary>Rows for one group with conversations folded, in the group's own order.</summary>
    private List<object> Threaded(IReadOnlyList<MessageRow> items)
    {
        var rows = new List<object>();

        foreach (var thread in Conversations.Build(items))
        {
            if (!thread.IsThread)
            {
                rows.Add(Reset(thread.Newest));
                continue;
            }

            var open = _expanded.Contains(thread.Newest.ThreadKey);
            Reset(thread.Newest);
            rows.Add(new ConversationRow(thread.Newest, thread.Count, open, thread.IsSplit));

            if (!open) continue;

            foreach (var message in thread.Messages)
            {
                message.Depth = 1;
                rows.Add(message);
            }
        }

        return rows;
    }

    /// <summary>Clears any indent left over from a previous conversation view.</summary>
    private MessageRow Reset(MessageRow row)
    {
        row.Depth = 0;
        row.IsCard = CardRows;
        row.UnreadStyling = _unreadStyled;
        row.Format = FormatFor(row);
        return row;
    }

    /// <summary>What a group header counts: conversations and loose messages, not both.</summary>
    private static int Countable(List<object> rows)
        => rows.Count(r => r is ConversationRow || (r is MessageRow m && m.Depth == 0));

    // ---- Acting on a selection -------------------------------------------------------------
    // Every one of these takes the rows explicitly rather than reading a selection property.
    // The list owns the selection, and a command that reaches back for it can act on something
    // other than what the user had highlighted when they pressed the key.

    /// <summary>Marks rows read or unread, in the store and on screen. Quiet leaves the status line alone — read by looking is not news.</summary>
    public void SetRead(IReadOnlyList<MessageRow> rows, bool read, bool quiet = false)
    {
        if (rows.Count == 0) return;

        Mail(rows)?.SetRead([.. rows.Select(r => r.Id)], read);
        foreach (var row in rows) row.IsUnread = !read;

        // Read by looking happens in the middle of a selection change; the pane's counts can
        // wait a moment rather than being rebuilt under it.
        if (quiet) RequestCountsRefresh();
        else RefreshCounts();
        if (!quiet) StatusRight = $"{Describe(rows.Count)} marked {(read ? "read" : "unread")}.";
    }

    private Avalonia.Threading.DispatcherTimer? _countsTimer;

    /// <summary>Refreshes the folder pane's counts shortly, once, however many changes ask.</summary>
    private void RequestCountsRefresh()
    {
        if (_countsTimer is not null) return;
        _countsTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _countsTimer.Tick += (_, _) =>
        {
            _countsTimer?.Stop();
            _countsTimer = null;
            RefreshCounts();
        };
        _countsTimer.Start();
    }

    /// <summary>True while a row is still in the list — one deleted or moved away is not read by looking.</summary>
    public bool IsListed(MessageRow row) => Messages.Contains(row);

    /// <summary>
    /// The folders a selection could be moved to: those of its own account, the one it is in
    /// left out.
    /// </summary>
    /// <remarks>
    /// Its own account only. Each account is its own store, and a move across stores is a copy
    /// and a delete over two files rather than a move — real, and Phase 8's, and not something
    /// to offer as a menu entry that quietly does something else.
    /// </remarks>
    public IReadOnlyList<FolderNode> FoldersOfSelection(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || CurrentAccount is not { } account) return [];

        var here = SelectedFolder;

        return
        [
            .. _folderIds
                .Where(kv => ReferenceEquals(kv.Value.Account, account) && !ReferenceEquals(kv.Key, here))
                .Where(kv => kv.Value.Role != FolderRole.Outbox)
                .Select(kv => kv.Key),
        ];
    }

    /// <summary>The categories this account defines, for the Categorize menu.</summary>
    /// <summary>
    /// The one set, which is what every module's Categorize menu lists.
    /// </summary>
    /// <remarks>
    /// The set rather than the open account's own rows: the categories are one list across the
    /// modules and across the accounts (§9), and a reader with two accounts should not be shown
    /// two lists. The account's rows are the mirror this is assigned <em>through</em> — matched
    /// by name in <see cref="Mirrored"/> — and a name the mirror has not got is put there rather
    /// than refused, so a category made while an account was closed still works on it.
    /// </remarks>
    public IReadOnlyList<Category> Categories() => App.Categories.All();

    /// <summary>The open account's own row for a category, made if this account has never seen it.</summary>
    private static Category? Mirrored(MailRepository mail, Category category)
        => mail.Categories().FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase))
           ?? mail.AddCategory(category.Name, category.ColourToken, category.Shortcut);

    /// <summary>Whether every one of these rows carries the category, for the menu's tick.</summary>
    public bool AllHave(IReadOnlyList<MessageRow> rows, Category category)
    {
        if (rows.Count == 0 || CurrentMail is not { } mail) return false;

        var assigned = mail.CategoriesFor([.. rows.Select(r => r.Id)]);
        return rows.All(r => assigned.TryGetValue(r.Id, out var list)
            && list.Any(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Puts a category on the rows, or takes it off them all if every one already has it.
    /// </summary>
    /// <remarks>
    /// The reference's Categorize menu is a toggle per category, and this is that: the same
    /// click that colours a message uncolours it. The strip on each row follows at once, from
    /// the store rather than by guessing, so what is shown is what was written.
    /// </remarks>
    public void ToggleCategory(IReadOnlyList<MessageRow> rows, Category category)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;
        if (Mirrored(mail, category) is not { } mirrored) return;

        var ids = rows.Select(r => r.Id).ToList();
        var remove = AllHave(rows, category);

        if (remove) mail.Unassign(ids, mirrored.Id);
        else mail.Assign(ids, mirrored.Id);

        var assigned = mail.CategoriesFor(ids);
        foreach (var row in rows)
        {
            row.CategoryTokens = assigned.TryGetValue(row.Id, out var list)
                ? [.. list.Select(c => c.ColourToken)]
                : [];
        }

        StatusRight = remove
            ? $"{category.Name} removed from {Describe(rows.Count)}."
            : $"{Describe(rows.Count)} categorised {category.Name}.";
    }

    /// <summary>Takes every category off the rows.</summary>
    public void ClearCategories(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var ids = rows.Select(r => r.Id).ToList();
        foreach (var category in mail.Categories()) mail.Unassign(ids, category.Id);
        foreach (var row in rows) row.CategoryTokens = [];

        StatusRight = $"Categories cleared on {Describe(rows.Count)}.";
    }

    public void SetFlagged(IReadOnlyList<MessageRow> rows, bool flagged)
    {
        if (rows.Count == 0) return;

        Mail(rows)?.SetFlagged([.. rows.Select(r => r.Id)], flagged);
        foreach (var row in rows) row.IsFlagged = flagged;

        StatusRight = flagged
            ? $"{Describe(rows.Count)} flagged for follow up."
            : $"Flag cleared on {Describe(rows.Count)}.";
    }

    /// <summary>
    /// Deletes rows: to Deleted Items normally, or for good when asked. Moving rather than
    /// deleting is the default because the store may hold the only copy.
    /// </summary>
    public void Delete(IReadOnlyList<MessageRow> rows, bool permanently)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var ids = rows.Select(r => r.Id).ToList();
        var deleted = CurrentAccount?.Mail.FolderWithRole(
            CurrentAccount.Account.Id, FolderRole.Deleted);

        if (permanently || deleted is null || SelectedFolder?.Name == deleted.Name)
        {
            mail.DeleteMessages(ids);
            StatusRight = $"{Describe(rows.Count)} permanently deleted.";
        }
        else
        {
            mail.MoveMessages(ids, deleted.Id);
            StatusRight = $"{Describe(rows.Count)} moved to Deleted Items.";
        }

        RemoveRows(rows);
        RefreshCounts();
    }

    /// <summary>Moves rows into another folder of the same account.</summary>
    public void MoveTo(IReadOnlyList<MessageRow> rows, FolderRole role)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;
        if (CurrentAccount?.Mail.FolderWithRole(CurrentAccount.Account.Id, role)
            is not { } target) return;

        mail.MoveMessages([.. rows.Select(r => r.Id)], target.Id);
        RemoveRows(rows);
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} moved to {target.Name}.";
    }

    /// <summary>
    /// Moves messages by id into a folder the pane is showing. Taken by id because the rows
    /// come back from a drag, which carries ids rather than objects.
    /// </summary>
    public void MoveToFolder(IReadOnlyList<long> ids, FolderNode target)
    {
        if (ids.Count == 0 || !_folderIds.TryGetValue(target, out var where)) return;

        var rows = Messages.Where(m => ids.Contains(m.Id)).ToList();
        if (rows.Count == 0) return;

        where.Account.Mail.MoveMessages(ids, where.FolderId);
        RemoveRows(rows);
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} moved to {target.Name}.";
    }

    /// <summary>The pane's node for a folder of an account, or null when it is not shown.</summary>
    public FolderNode? NodeFor(OpenAccount account, long folderId)
        => _folderIds.FirstOrDefault(kv =>
            string.Equals(kv.Value.Account.Account.Address, account.Account.Address, StringComparison.OrdinalIgnoreCase)
            && kv.Value.FolderId == folderId).Key;

    /// <summary>Copies rows into a folder of the same account; the originals stay where they are.</summary>
    public void CopyTo(IReadOnlyList<MessageRow> rows, Folder target)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var copied = mail.CopyMessages([.. rows.Select(r => r.Id)], target.Id);
        RefreshCounts();
        StatusRight = $"{Describe(copied)} copied to {target.Name}.";
    }

    /// <summary>Sets the importance the list's column shows.</summary>
    public void SetImportance(IReadOnlyList<MessageRow> rows, int level)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        mail.SetImportance([.. rows.Select(r => r.Id)], level);
        StatusRight = $"{Describe(rows.Count)} marked {level switch { 0 => "low", 2 => "high", _ => "normal" }} importance.";
    }

    /// <summary>Puts named categories on the rows — the ones that exist; a name that does not is skipped.</summary>
    public void AssignCategories(IReadOnlyList<MessageRow> rows, IReadOnlyList<string> names)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var ids = rows.Select(r => r.Id).ToList();
        foreach (var category in mail.Categories().Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
        {
            mail.Assign(ids, category.Id);
        }

        var assigned = mail.CategoriesFor(ids);
        foreach (var row in rows)
        {
            row.CategoryTokens = assigned.TryGetValue(row.Id, out var list) ? [.. list.Select(c => c.ColourToken)] : [];
        }
    }

    /// <summary>Selects a folder by what it is for, which is what Ctrl+Shift+I and friends do.</summary>
    public bool GoTo(FolderRole role)
    {
        foreach (var (node, where) in _folderIds)
        {
            var folder = where.Account.Mail.GetFolder(where.FolderId);
            if (folder?.Role != role) continue;

            SelectedFolder = node;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Selects a message by account and store id: opens the folder it is in, then selects its
    /// row. Returns the row, or null when there is no such message on this shell.
    /// </summary>
    /// <remarks>
    /// What a notification's click, a reminder's Open Item and anything else that arrives with an
    /// id rather than a row goes through. Search is left behind, as choosing a folder does.
    /// </remarks>
    public MessageRow? RevealMessage(string address, long id)
    {
        if (_accounts?.Find(address) is not { } account) return null;
        if (account.Mail.GetMessage(id)?.FolderId is not { } folderId) return null;

        // By address, not by instance: AccountStores.All hands out a fresh OpenAccount record
        // per call (each is stamped with whether it is the default), so the one found above is
        // never the same object the folder table was built from.
        var node = _folderIds
            .Where(kv => string.Equals(kv.Value.Account.Account.Address, address, StringComparison.OrdinalIgnoreCase)
                         && kv.Value.FolderId == folderId)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        if (node is null) return null;

        if (ReferenceEquals(SelectedFolder, node)) ReloadCurrentView();
        else SelectedFolder = node;

        var row = Messages.FirstOrDefault(m => m.Id == id);
        if (row is null) return null;

        // Unfold the group and the thread it sits in, or the row is selected and not on screen.
        _collapsed.Clear();
        if (ShowAsConversations) _expanded.Add(row.ThreadKey);
        Rebuild();

        SelectedRow = row;
        SelectedMessage = row;
        return row;
    }

    /// <summary>The address of the account whose folder is on screen, for a new message to come from.</summary>
    public string? CurrentAddress => CurrentAccount?.Account.Address;

    /// <summary>The folder on screen, from the store, or null while the sample is showing.</summary>
    public Folder? CurrentFolder =>
        SelectedFolder is { } folder && _folderIds.TryGetValue(folder, out var where)
            ? where.Account.Mail.GetFolder(where.FolderId)
            : null;

    /// <summary>What kind of folder is on screen, so a draft can be opened as one.</summary>
    public FolderRole CurrentFolderRole =>
        SelectedFolder is { } folder && _folderIds.TryGetValue(folder, out var where)
            ? where.Role
            : FolderRole.None;

    /// <summary>The account whose folder — or search folder — is on screen, or the first one.</summary>
    private OpenAccount? CurrentAccount =>
        SelectedFolder is { } folder
            ? _folderIds.TryGetValue(folder, out var where) ? where.Account
              : _searchFolderIds.TryGetValue(folder, out var search) ? search.Account
              : _searchFolderRoots.TryGetValue(folder, out var root) ? root
              : _accounts?.All.FirstOrDefault()
            : _accounts?.All.FirstOrDefault();

    /// <summary>The account whose categories a management dialog should edit — the current one.</summary>
    public OpenAccount? CurrentAccountForCategories() => CurrentAccount;

    /// <summary>Unread across every account's Inbox, for the tray icon's tooltip and badge.</summary>
    public int TotalUnread => Folders
        .Where(f => _folderIds.TryGetValue(f, out var w) && w.Role == FolderRole.Inbox)
        .Sum(f => f.Unread);

    /// <summary>Re-reads what is on screen — the search results, or the folder — after a change to a row's state.</summary>
    private void ReloadCurrentView()
    {
        if (IsSearching) RunSearch();
        else LoadMessages(_selectedFolder);
    }

    /// <summary>Flags the selection for follow-up, with an optional due date. The reference's flag menu.</summary>
    public void FlagForFollowUp(IReadOnlyList<MessageRow> rows, DateTimeOffset? due)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        mail.SetFollowUp([.. rows.Select(r => r.Id)], due);
        ReloadCurrentView();
        RefreshCounts();
        StatusRight = due is { } d
            ? $"{Describe(rows.Count)} flagged, due {d.LocalDateTime:ddd d MMM}."
            : $"{Describe(rows.Count)} flagged for follow-up.";
    }

    /// <summary>The store's row for a list row, for a dialog that shows its present values.</summary>
    public MessageSummary? SummaryOf(MessageRow row) => Mail([row])?.GetMessage(row.Id);

    /// <summary>The Custom flag dialog's whole flag: what it says, its dates, and its reminder.</summary>
    public void SetCustomFlag(IReadOnlyList<MessageRow> rows, CustomFlag flag)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        mail.SetCustomFollowUp([.. rows.Select(r => r.Id)], flag.Type, flag.Start, flag.Due, flag.Reminder);
        ReloadCurrentView();
        RefreshCounts();
        StatusRight = flag.Reminder is { } when
            ? $"{Describe(rows.Count)} flagged; reminder {SnoozeLabel(when)}."
            : flag.Due is { } d ? $"{Describe(rows.Count)} flagged, due {d.LocalDateTime:ddd d MMM}." : $"{Describe(rows.Count)} flagged.";
    }

    /// <summary>Marks the selection's follow-up complete: a check takes the flag's place.</summary>
    public void MarkFollowUpComplete(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        mail.CompleteFollowUp([.. rows.Select(r => r.Id)]);
        ReloadCurrentView();
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} marked complete.";
    }

    /// <summary>Clears the selection's follow-up flag entirely.</summary>
    public void ClearFollowUpFlag(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        mail.ClearFollowUp([.. rows.Select(r => r.Id)]);
        ReloadCurrentView();
        RefreshCounts();
        StatusRight = $"Flag cleared.";
    }

    /// <summary>
    /// Mark as Junk, or Not Junk when the selection is already in the Junk folder.
    /// </summary>
    /// <remarks>
    /// The reference's Junk button is contextual: pressed on inbox mail it trains the message as
    /// junk and moves it to Junk; pressed on a message already in Junk it is "Not Junk" — trains
    /// it as good and returns it to the inbox. The training is what makes the filter learn a
    /// person's own mail, and it reads the message from the store so it trains on the whole of
    /// it rather than the preview.
    /// </remarks>
    public void MarkJunk(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var notJunk = CurrentFolderRole == FolderRole.Junk;

        foreach (var row in rows)
        {
            if (mail.LoadRaw(row.Id) is not { } raw) continue;

            try
            {
                using var stream = new MemoryStream(raw);
                var message = MimeKit.MimeMessage.Load(stream);
                App.Junk.Train(mail, message, spam: !notJunk);
            }
            catch (Exception ex)
            {
                // A message that will not parse cannot be trained on, but it can still be moved.
                Log.Warn("Could not train the junk filter on a message.", ex);
            }
        }

        MoveTo(rows, notJunk ? FolderRole.Inbox : FolderRole.Junk);
    }

    // ---- Snooze (§12) -----------------------------------------------------------------------

    /// <summary>
    /// Show only the folder's snoozed messages — Filter Email › Snoozed. Off, the list shows
    /// what is awake, and a snoozed message is nowhere until its time comes.
    /// </summary>
    public bool ShowSnoozed
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            LoadMessages(_selectedFolder);
        }
    }

    /// <summary>Hides the rows until <paramref name="until"/>. They leave the list at once.</summary>
    public void Snooze(IReadOnlyList<MessageRow> rows, DateTimeOffset until)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        mail.Snooze([.. rows.Select(r => r.Id)], until);
        RemoveRows(rows);
        RefreshCounts();

        var local = until.LocalDateTime;
        var when = local.Date == DateTime.Today ? local.ToString("h:mm tt") : local.ToString("ddd d MMM, h:mm tt");
        StatusRight = $"{Describe(rows.Count)} snoozed until {when}.";
    }

    /// <summary>Brings the rows back now, unread and at the top of the folder.</summary>
    public void Unsnooze(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        mail.Unsnooze([.. rows.Select(r => r.Id)], DateTimeOffset.UtcNow);
        ReloadCurrentView();
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} back in the Inbox.";
    }

    /// <summary>
    /// Brings back every snoozed message whose time has come, across every account, and says
    /// which — for the toast, which treats a returned message as the new mail it now is.
    /// </summary>
    public IReadOnlyList<(string Address, long MessageId)> WakeSnoozed(DateTimeOffset now)
    {
        var woken = new List<(string, long)>();
        foreach (var account in _accounts?.All ?? [])
        {
            foreach (var (_, id) in account.Mail.WakeSnoozed(now))
            {
                woken.Add((account.Account.Address, id));
            }
        }

        if (woken.Count > 0)
        {
            ReloadCurrentView();
            RefreshCounts();
        }

        return woken;
    }

    /// <summary>The distinct sender addresses of the rows, lower-cased, read from the store.</summary>
    private List<string> Senders(IReadOnlyList<MessageRow> rows)
    {
        if (Mail(rows) is not { } mail) return [];

        return rows
            .Select(r => mail.GetMessage(r.Id)?.FromAddress.Trim().ToLowerInvariant())
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .ToList()!;
    }

    /// <summary>The distinct sender domains of the rows, for the Junk menu's domain entry.</summary>
    public IReadOnlyList<string> SenderDomains(IReadOnlyList<MessageRow> rows) =>
    [
        .. Senders(rows)
            .Select(a => a.LastIndexOf('@') is var at && at >= 0 && at < a.Length - 1 ? a[(at + 1)..] : null)
            .Where(d => d is not null)
            .Distinct()!,
    ];

    /// <summary>
    /// Block Sender: the senders join the Blocked Senders list, and the messages go to Junk with
    /// the filter trained on them — the reference's menu entry does all three.
    /// </summary>
    public void BlockSenders(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var now = DateTimeOffset.UtcNow;
        var senders = Senders(rows);
        foreach (var sender in senders)
        {
            mail.AddBlockedSender(sender, now);
            mail.RemoveSafeSender(sender);
        }

        if (CurrentFolderRole != FolderRole.Junk) MarkJunk(rows);

        StatusRight = senders.Count == 1
            ? $"{senders[0]} added to the Blocked Senders list."
            : $"{senders.Count} senders added to the Blocked Senders list.";
    }

    /// <summary>
    /// Never Block Sender, and Never Block Sender's Domain: the senders — or their whole domains
    /// — join the Safe Senders list and leave the blocked one, and a message in Junk comes back.
    /// </summary>
    public void NeverBlockSenders(IReadOnlyList<MessageRow> rows, bool domain)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var now = DateTimeOffset.UtcNow;
        var entries = domain
            ? SenderDomains(rows).Select(d => "@" + d).ToList()
            : Senders(rows);

        foreach (var entry in entries)
        {
            mail.AddSafeSender(entry, now);
            mail.RemoveBlockedSender(entry);
        }

        if (CurrentFolderRole == FolderRole.Junk) MarkJunk(rows);

        StatusRight = entries.Count == 1
            ? $"{entries[0]} added to the Safe Senders list."
            : $"{entries.Count} entries added to the Safe Senders list.";
    }

    /// <summary>
    /// Never Block this Group or Mailing List: the addresses the messages were sent to — the
    /// list, the alias — join the Safe Recipients list, and a message in Junk comes back.
    /// </summary>
    public void NeverBlockRecipients(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var now = DateTimeOffset.UtcNow;
        var own = new HashSet<string>(
            _accounts?.All.Select(a => a.Account.Address.ToLowerInvariant()) ?? [],
            StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();

        foreach (var row in rows)
        {
            if (mail.LoadRaw(row.Id) is not { } raw || Parse(raw) is not { } message) continue;

            // The list's own address, which is whatever the message was addressed to that is
            // not one of ours; falling back to ours when that is all there is.
            var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes)
                .Select(m => m.Address.Trim().ToLowerInvariant())
                .Where(a => a.Length > 0)
                .ToList();
            var lists = recipients.Where(a => !own.Contains(a)).ToList();

            foreach (var address in lists.Count > 0 ? lists : recipients)
            {
                mail.AddSafeRecipient(address, now);
                if (!added.Contains(address)) added.Add(address);
            }
        }

        if (CurrentFolderRole == FolderRole.Junk) MarkJunk(rows);

        StatusRight = added.Count switch
        {
            0 => "The message names no recipient to add.",
            1 => $"{added[0]} added to the Safe Recipients list.",
            _ => $"{added.Count} addresses added to the Safe Recipients list.",
        };
    }

    /// <summary>
    /// The store the rows belong to. Null while the sample is showing, which is what keeps the
    /// commands from pretending to act on mail that is not really there.
    /// </summary>
    private MailRepository? Mail(IReadOnlyList<MessageRow> rows)
        => rows.Count == 0 ? null : CurrentAccount?.Mail;

    /// <summary>
    /// The store behind what is on screen, for the reading pane.
    /// </summary>
    /// <remarks>
    /// Null while the sample is showing, which is what tells the pane to render the row's text
    /// rather than go looking for MIME that was never received.
    /// </remarks>
    public MailRepository? CurrentMail => HasAccount ? CurrentAccount?.Mail : null;

    /// <summary>The selected message as it arrived, or null when there is no such thing.</summary>
    public byte[]? SelectedRaw => SelectedMessage is { } row ? CurrentMail?.LoadRaw(row.Id) : null;

    private static string Describe(int count)
        => count == 1 ? "1 message" : $"{count:N0} messages";

    /// <summary>Re-reads the folder pane's unread counts after something changed.</summary>
    private void RefreshCounts()
    {
        var previous = Remember(SelectedFolder);
        if (_accounts is null || !LoadFromStore(selectFirst: false)) { Rebuild(); return; }

        // The pane was rebuilt with fresh counts; the same folder stays selected in it — by
        // the field, not the setter, so the list is not reloaded from the store and whatever is
        // selected in it stays selected. Callers keep Messages themselves.
        if (previous is { } was && SameNodeAs(was) is { } node && !ReferenceEquals(node, _selectedFolder))
        {
            _selectedFolder = node;
            Raise(nameof(SelectedFolder));
        }
    }

    /// <summary>
    /// Takes rows out of the list — deleted, moved, archived — and moves the selection to the
    /// row that followed the last of them, or the one before, as the reference does; the list
    /// never ends up showing nothing after a delete.
    /// </summary>
    private void RemoveRows(IReadOnlyList<MessageRow> rows)
    {
        var visible = VisibleRows.OfType<MessageRow>().ToList();
        var removed = rows.ToHashSet();
        var wasSelected = SelectedMessage is { } current && removed.Contains(current);
        MessageRow? next = null;
        if (wasSelected)
        {
            var last = rows.Select(r => visible.IndexOf(r)).DefaultIfEmpty(-1).Max();
            next = visible.Skip(last + 1).FirstOrDefault(r => !removed.Contains(r))
                   ?? visible.Take(Math.Max(0, last)).LastOrDefault(r => !removed.Contains(r));
        }

        foreach (var row in rows) Messages.Remove(row);
        Rebuild();

        if (wasSelected)
        {
            SelectedRow = next;
            SelectedMessage = next;
        }
    }

    /// <summary>Rows on show, headers excluded. What the status bar counts.</summary>
    public int VisibleCount => VisibleRows.Count(
        r => r is MessageRow { Depth: 0 } or ConversationRow);

    // ---- Pane layout ----------------------------------------------------------------------

    /// <summary>The folder pane collapses to nothing, leaving the rail.</summary>
    public bool NavCollapsed
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(NavVisible));
            Raise(nameof(CollapseGlyph));
        }
    }

    public bool NavVisible => !NavCollapsed;
    public string CollapseGlyph => NavCollapsed ? "\u203A" : "\u2039";

    /// <summary>Reading pane shown, or off entirely — the two the status bar offers.</summary>
    public bool ReadingPaneVisible
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            App.Settings.Set(OptionsPages.Keys.ShowReadingPane, value);
        }
    } = App.Settings.GetBool(OptionsPages.Keys.ShowReadingPane, true);

    /// <summary>
    /// Where a shown reading pane sits: beside the list (Right, the reference's default) or
    /// under it (Bottom), as View › Layout › Reading Pane offers. Under the list, the list is as
    /// wide as the window and so shows its single-line layout, as the reference's does there.
    /// </summary>
    public bool ReadingPaneAtBottom
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            App.Settings.Set(OptionsPages.Keys.ReadingPaneAtBottom, value);
        }
    } = App.Settings.GetBool(OptionsPages.Keys.ReadingPaneAtBottom, false);

    /// <summary>Zoom applies to the reading pane's body, which is what it scales.</summary>
    public double ReadingFontSize => 14.5 * (ZoomPercent / 100d);

    // ---- Commands for the controls that shape the view -------------------------------------
    // Built here rather than in the window so the state and the way it is changed sit together;
    // the window only wires the things that need a Window to act on.

    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ShowAll { get; }
    public RelayCommand ShowUnread { get; }
    public RelayCommand ToggleSort { get; }
    public RelayCommand ToggleNav { get; }

    public RelayCommand ShowReadingPane { get; }
    public RelayCommand HideReadingPane { get; }
    public RelayCommand ZoomIn { get; }
    public RelayCommand ZoomOut { get; }

    /// <summary>
    /// A column header re-arranges by that column, and clicking the same one again reverses.
    /// A column and an arrangement are the same thing in the reference — clicking From groups
    /// by sender, it does not merely sort within the date groups.
    /// </summary>
    public void SortBy(string column)
    {
        var wanted = column switch
        {
            "From" => Arrangement.From,
            "To" => Arrangement.To,
            "Subject" => Arrangement.Subject,
            "Received" or "Sent" or "Date" => Arrangement.Date,
            "Size" => Arrangement.Size,
            "Importance" => Arrangement.Importance,
            "Flag" => Arrangement.Flag,
            "Attachments" => Arrangement.Attachments,
            "Categories" => Arrangement.Categories,
            _ => (Arrangement?)null,
        };

        if (wanted is not { } arrangement) return;

        if (Arrangement == arrangement) SortDescending = !SortDescending;
        else { Arrangement = arrangement; SortDescending = true; }
    }

    // Glyphs for the buttons that appear on a row under the pointer.
    public string ArchiveGlyph { get; } = IconGlyphs.GetOrEmpty("archive", 16);
    public string DeleteGlyph { get; } = IconGlyphs.GetOrEmpty("delete", 16);
    public string FlagActionGlyph { get; } = IconGlyphs.GetOrEmpty("flag", 16);
    public string UnreadGlyph { get; } = IconGlyphs.GetOrEmpty("unread", 16);

    public string ReadingPaneGlyph { get; } = IconGlyphs.GetOrEmpty("reading-pane", 16);
    public string ReadingGlyph { get; } = IconGlyphs.GetOrEmpty("message-preview", 16);

    public double ZoomPercent
    {
        get;
        set
        {
            if (!Set(ref field, Math.Clamp(value, 50, 200))) return;
            Raise(nameof(ZoomLabel));
            Raise(nameof(ReadingFontSize));
        }
    } = 100;

    public string ZoomLabel => $"{ZoomPercent:0}%";

    /// <summary>
    /// The signed-in address. Phase 2 replaces this with the real account; until then the
    /// avatar and its flyout read from here so there is one source for both.
    /// </summary>
    public string AccountAddress { get; } = "you@example.com";

    /// <summary>
    /// The name and initials from Options. The initials are what the account disc draws when
    /// they are set — a user who typed their own would not expect the address's first letter.
    /// </summary>
    public string UserName => App.Settings.GetString(OptionsPages.Keys.UserName);

    public string UserInitials => App.Settings.GetString(OptionsPages.Keys.Initials);

    /// <summary>
    /// Display name shown above the address. Falls back to the address when the account has
    /// no name, which is what the reference does.
    /// </summary>
    public string AccountName => UserName is { Length: > 0 } named ? named : AccountAddress;

    /// <summary>First letter of the address, as the reference derives it.</summary>
    public string AccountInitial => UserInitials is { Length: > 0 } typed
        ? typed[..1].ToUpperInvariant()
        : AccountIdentity.Initial(AccountAddress);

    public string AccountTip => AccountAddress;


    /// <summary>
    /// Message-list columns, from the current view, in its order. The glyph columns —
    /// importance, reminder, item type, attachment — are drawn narrow and unlabelled.
    /// </summary>
    public IReadOnlyList<MessageColumn> Columns
    {
        get;
        private set => Set(ref field, value);
    } = [];

    public string StatusLeft => Module == MailboxModule.Mail
        ? $"Items: {VisibleCount}   Unread: {Messages.Count(m => m.IsUnread)}"
        : ModuleStatusLeft;

    /// <summary>
    /// What the left of the status bar reads while a module other than Mail is up. The calendar
    /// counts what its view is showing — "Items: 11" in the reference — and it is the module,
    /// not the shell, that knows what that number is.
    /// </summary>
    public string ModuleStatusLeft
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(StatusLeft)); }
    } = string.Empty;

    /// <summary>
    /// Which module is on screen. Setting it moves the rail's mark and swaps what the workspace
    /// shows; the shell is what swaps the ribbon with it.
    /// </summary>
    public MailboxModule Module
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            foreach (var tab in Modules) tab.IsActive = tab.Module == value;
            Raise(nameof(IsMailModule));
            Raise(nameof(IsCalendarModule));
            Raise(nameof(StatusLeft));
            Raise(nameof(ShowReadingPaneToggles));
        }
    } = MailboxModule.Mail;

    public bool IsMailModule => Module == MailboxModule.Mail;

    public bool IsCalendarModule => Module == MailboxModule.Calendar;

    /// <summary>
    /// The status bar's two layout buttons belong to the message list, so they go with it: the
    /// reference shows the calendar's own pair there instead, and an inert Normal/Reading pair
    /// over a calendar would be two buttons that do nothing.
    /// </summary>
    public bool ShowReadingPaneToggles => Module == MailboxModule.Mail;

    /// <summary>
    /// Empty at rest — the reference's status bar carries the counts on the left and the view
    /// and zoom controls on the right, with nothing between them. Transient messages land here
    /// and the connection state will once accounts exist in Phase 2.
    /// </summary>
    public string StatusRight
    {
        get;
        set { if (Set(ref field, value)) Raise(); }
    } = string.Empty;
}

/// <summary>One header cell in the message list's column strip.</summary>
public sealed class MessageColumn(string field, string title, double width, bool isGlyph = false)
{
    /// <summary>The view field this column shows — <see cref="Mailbox.Core.Views.ViewFields"/>.</summary>
    public string Field { get; } = field;

    public string Title { get; } = title;
    public double Width { get; } = width;

    /// <summary>Sorts the list by this column. Set by the shell, which owns the ordering.</summary>
    public System.Windows.Input.ICommand? Sort { get; set; }

    public string SortTip { get; } = isGlyph ? string.Empty : $"Sort by {title}";

    /// <summary>Icon-only columns render centred and unlabelled — importance, flag, attachment.</summary>
    public bool IsGlyph { get; } = isGlyph;

    /// <summary>The subject column takes what the others leave.</summary>
    public bool Stretches => Field == Mailbox.Core.Views.ViewFields.Subject;
}

/// <summary>Minimal ICommand for view-model-driven buttons.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null)
    : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
