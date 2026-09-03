using Mailbox.Core.Localization;
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
using Mailbox.Core.Settings;
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

    /// <summary>The "Favorites" heading at the top of the pane.</summary>
    FavouritesHeading,

    /// <summary>A folder listed under Favourites — the same folder as its row in the tree below.</summary>
    Favourite,

    /// <summary>The "All Accounts" heading of the unified mailbox, when it is switched on.</summary>
    UnifiedRoot,

    /// <summary>One of the unified mailbox's folders — every account's Inbox at once, and so on.</summary>
    Unified,
}

/// <summary>
/// A row that can say itself. The list templates bind their items' automation name to
/// <see cref="Spoken"/>, so every shape a list can hold writes the one sentence a screen
/// reader hears for it — the interface is what lets one binding cover them all.
/// </summary>
public interface ISpokenRow
{
    /// <summary>The row as a screen reader should say it.</summary>
    string Spoken { get; }
}

public sealed class FolderNode(string name, int depth, int unread, bool bold = false, FolderNodeKind kind = FolderNodeKind.Folder) : ISpokenRow
{
    public string Name { get; } = name;
    public int Unread { get; } = unread;
    public FolderNodeKind Kind { get; } = kind;
    public Thickness IndentMargin { get; } = new(depth * 14, 0, 0, 0);
    public FontWeight Weight { get; } = bold || unread > 0 ? FontWeight.SemiBold : FontWeight.Normal;
    public string UnreadDisplay { get; } = unread > 0 ? unread.ToString() : string.Empty;

    /// <summary>The folder as a screen reader should say it: its name, and what is waiting in it.</summary>
    public string Spoken { get; } = unread > 0 ? $"{name}, {unread} unread" : name;
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
    /// Settable and observed since the rail got a second module to switch to: the mark
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

    /// <summary>
    /// What the button says and draws — the command's own name and icon unless Modify… gave it
    /// others.
    /// </summary>
    /// <remarks>
    /// Read once, when the bar is built, because that is when it is rebuilt: every path that
    /// changes a toolbar goes through <c>RebuildQuickAccess</c>, so there is nothing here that
    /// has to notice a change on its own.
    /// </remarks>
    public string Label { get; private init; } = command.Label;

    public string Glyph { get; private init; } = IconGlyphs.GetOrEmpty(command.Icon, 16);

    /// <summary>True when the bar is set to write names beside the icons.</summary>
    public bool ShowLabel { get; private init; }

    public FontFamily IconFamily { get; } = IconFont.Family;

    /// <summary>This button as the toolbar's own settings want it drawn.</summary>
    /// <remarks>
    /// An icon the set does not have falls back to the command's own rather than to nothing. The
    /// picker cannot offer one, but the settings file is meant to be edited by hand and is
    /// therefore allowed to be wrong — and a button drawn blank looks like a bug in the toolbar
    /// rather than a typo in a file.
    /// </remarks>
    public QuickAccessButton As(QuickAccessOverride? modified, bool labels) => new(command)
    {
        IsSeparator = IsSeparator,
        Label = modified?.Name is { Length: > 0 } named ? named : Label,
        Glyph = modified?.Icon is { Length: > 0 } icon && IconGlyphs.Has(icon)
            ? IconGlyphs.GetOrEmpty(icon, 16)
            : Glyph,
        ShowLabel = labels,
        Invoke = Invoke,
    };

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
    string body) : ObservableObject, IThreadable, ISpokenRow
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

    /// <summary>The kind of item this is — a meeting message, a receipt — or null for mail.</summary>
    public string? ItemType { get; init; }

    /// <summary>Which folder it is filed in, so a conversation can tell it spans two.</summary>
    public long FolderId { get; init; }

    /// <summary>
    /// Which account's store this row came out of, empty for a view that is one account's.
    /// </summary>
    /// <remarks>
    /// Every store numbers its own rows from one, so an id alone does not say which store to act
    /// on — the same trap the to-do list has with tasks and flagged mail. A list that draws two
    /// accounts at once therefore has to carry the address on the row, and every command resolves
    /// the store from it rather than from the folder on screen.
    /// </remarks>
    public string Address { get; init; } = string.Empty;

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

    /// <summary>What By To groups on: the people this row is addressed to.</summary>
    public IReadOnlyList<string> ToNames => To;

    /// <summary>What By Account groups on: the account the row carries, in any view.</summary>
    public string AccountName => Address;
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

    /// <summary>Whether a reply to this message went out — the Icon column's left arrow.</summary>
    public bool IsAnswered
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>Whether this message was forwarded — the Icon column's right arrow.</summary>
    public bool IsForwarded
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>Whether a follow-up on this message has been marked complete.</summary>
    public bool FollowUpComplete { get; init; }

    /// <summary>When a follow-up is due, for the tooltip and the flag menu's state.</summary>
    public DateTimeOffset? FollowUpDue { get; init; }

    /// <summary>When the follow-up starts, for the arrangement that lists by it.</summary>
    public DateTimeOffset? FollowUpStart { get; init; }

    /// <summary>
    /// True for a header whose message has not been downloaded — Send/Receive's Download
    /// Headers wrote the row, and the reading pane says so rather than showing an empty message.
    /// </summary>
    public bool IsHeaderOnly { get; init; }

    /// <summary>When a snoozed message comes back, or null for one that is awake.</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }

    public bool IsSnoozed => SnoozedUntil is not null;

    /// <summary>0 low, 1 normal, 2 high — the message's own importance, for the "!" column and Filter Email.</summary>
    public int Importance { get; init; } = 1;

    /// <summary>Format Columns' choice for the date columns of the view on screen, by field id.</summary>
    public static IReadOnlyDictionary<string, Mailbox.Core.Views.DateFormat> DateFormats { get; set; } = new Dictionary<string, Mailbox.Core.Views.DateFormat>();

    /// <summary>How the row writes its date: a time today, a weekday this week, else the date — or as Format Columns says.</summary>
    public string ReceivedLabel => DateLabel(Received, Mailbox.Core.Views.ViewFields.Received);

    /// <summary>
    /// The row as a screen reader should say it.
    /// </summary>
    /// <remarks>
    /// A row is a grid of eight or nine drawn cells, several of them glyphs, and a reader
    /// traversing it heard the parts in layout order or nothing at all — the accessibility pass
    /// had reached the ribbon and no list. What is said is what somebody would say if asked what
    /// the row is: whether it has been read, who it is from, what it is about, when it came, and
    /// then the marks that would otherwise be silent glyphs.
    /// </remarks>
    public string Spoken
    {
        get
        {
            var said = new System.Text.StringBuilder();

            if (IsUnread) said.Append("Unread. ");
            said.Append(From).Append(". ").Append(Subject.Length > 0 ? Subject : "No subject").Append(". ");
            said.Append(ReceivedLabel).Append('.');

            if (HasAttachment) said.Append(" With attachment.");
            if (IsFlagged) said.Append(" Flagged.");
            if (IsAnswered) said.Append(" Replied to.");
            else if (IsForwarded) said.Append(" Forwarded.");
            if (FolderLabel.Length > 0) said.Append(" In ").Append(FolderLabel).Append('.');

            return said.ToString();
        }
    }

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
public sealed class ConversationRow(MessageRow newest, int count, bool expanded, bool split) : ISpokenRow
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

    /// <summary>The thread as a screen reader should say it: the newest message, and how many.</summary>
    public string Spoken => $"Conversation of {Count}. {Newest.Spoken}";
    public FontWeight SenderWeight => IsUnread ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight SubjectWeight => IsUnread ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>
/// A group header in the list. Sits in the same flat sequence as the rows it heads, which is
/// what lets one virtualizing list draw both without nesting a panel per group.
/// </summary>
public sealed class GroupHeaderRow(string header, int count, bool collapsed) : ISpokenRow
{
    public string Header { get; } = header;
    public int Count { get; } = count;
    public bool IsCollapsed { get; } = collapsed;

    public string Glyph => IsCollapsed ? "\u203A" : "\u2304";
    public string CountLabel => $"({Count})";

    /// <summary>The header as a screen reader should say it, including whether it is folded.</summary>
    public string Spoken => $"{Header}, {Count} {(Count == 1 ? "message" : "messages")}"
        + (IsCollapsed ? ", collapsed" : string.Empty);
}

/// <summary>Which kind of more a footer row offers.</summary>
public enum MoreRowKind
{
    /// <summary>The store holds more of this folder than the page shows.</summary>
    LocalPage,

    /// <summary>The server holds mail older than the offline window.</summary>
    ServerOlder,

    /// <summary>A search that could also ask the server, where the window has left mail.</summary>
    ServerSearch,
}

/// <summary>
/// The row after the last message: what this list is not showing, and the offer to show it.
/// Sits in the same flat sequence the group headers do, so the one virtualizing list draws it.
/// </summary>
public sealed class MoreRow(MoreRowKind kind, string label) : ISpokenRow
{
    public MoreRowKind Kind { get; } = kind;
    public string Label { get; } = label;

    /// <summary>The row as a screen reader should say it.</summary>
    public string Spoken => Label;
}

/// <summary>What a footer row asked for, with everything the handler needs to do it.</summary>
public sealed record MoreRequest(MoreRowKind Kind, OpenAccount Account, long FolderId, string SearchText);

/// <summary>
/// The shell's own state, filled from the store. With no account there is nothing to fill and
/// the panes stay empty — nothing is invented: an invented mailbox reads as a real account,
/// and Account Settings can neither list nor remove it.
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
            ToolbarButtons(catalog, quickAccess?.Commands ?? layout.QuickAccess, quickAccess));

        ReadingPaneActions = new ObservableCollection<QuickAccessButton>(
            new[] { MailCommands.Reply.Id, MailCommands.ReplyAll.Id, MailCommands.Forward.Id }
                .Where(id => catalog.TryGet(id, out _))
                .Select(id => new QuickAccessButton(catalog.Get(id))));

        // The modern command bar: New mail, then the actions that used to be the Delete,
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

        // Empty until the store fills them. There used to be an invented mailbox here so an
        // unconfigured shell had something to draw, but it read as a real account that could
        // not be removed; now the first-run answer is the account wizard, not a pretence.
        Folders = [];
        Messages = [];

        LoadFromStore();

        _selectedMessage = Messages.FirstOrDefault();

        RebuildColumns();

        ShowAll = new RelayCommand(() => UnreadOnly = false);
        ShowUnread = new RelayCommand(() => UnreadOnly = true);
        ToggleSort = new RelayCommand(() => SortDescending = !SortDescending);
        ToggleNav = new RelayCommand(ToggleFolderPane);
        SelectFolderRow = new RelayCommand<FolderNode>(node => SelectedFolder = node);

        ClearSearchCommand = new RelayCommand(ClearSearch);
        ShowReadingPane = new RelayCommand(() => ReadingPaneVisible = true);
        HideReadingPane = new RelayCommand(() => ReadingPaneVisible = false);
        ZoomIn = new RelayCommand(() => ZoomPercent += 10);
        ZoomOut = new RelayCommand(() => ZoomPercent -= 10);

        // Nothing is on screen until the rows have been grouped, and grouping is what the list
        // binds to. Last, so it sees what the store loaded above.
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
        && QuickAccessCustomization?.Placement != QuickAccessPlacement.BelowRibbon
        && !BackstageOpen;

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
        foreach (var button in ToolbarButtons(_catalog, customization.Commands, customization))
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
        CommandCatalog catalog, IEnumerable<CommandId> ids, QuickAccessLayout? customization = null)
    {
        var labels = customization?.ShowLabels ?? false;

        foreach (var id in ids)
        {
            // A rule is not a command and Modify… cannot reach one: there is no name to change
            // and no icon to pick, and several of them share one id.
            if (id == RibbonItem.SeparatorId) yield return QuickAccessButton.Separator;
            else if (catalog.TryGet(id, out var command))
            {
                yield return new QuickAccessButton(command).As(customization?.OverrideFor(id), labels);
            }
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
    public bool ShowHeaderSearch => !BackstageOpen;

    /// <summary>
    /// Whether the Backstage takeover is open. It starts below the title bar — the reference's
    /// does — and while it is up the bar sheds its search box and toolbar and shows the window
    /// title where they were, keeping the avatar and the caption buttons in reach: the one
    /// thing a takeover must never take is the close button.
    /// </summary>
    public bool BackstageOpen
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(ShowHeaderSearch));
            Raise(nameof(IsQuickAccessAbove));
        }
    }
    public bool ShowListSearch => false;

    /// <summary>
    /// The vertical app rail is present in both. Classic gained it in the same update that
    /// moved the modules off the bottom of the folder pane.
    /// </summary>
    public bool ShowAppRail => true;

    /// <summary>Superseded by the app rail; kept for the pre-move classic look.</summary>
    public bool ShowBottomModuleStrip => false;

    // No "try the new one" toggle. That pill exists in the reference to move people onto the
    // vendor's web app; Mailbox is the thing it is nagging them away from. The space stays empty.

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

    /// <summary>True once an account exists and its store has been read.</summary>
    public bool HasAccount { get; private set; }

    /// <summary>
    /// Which account and folder each row stands for. Every account has its own store, so a
    /// folder id alone is not enough to find its mail.
    /// </summary>
    private readonly Dictionary<FolderNode, (OpenAccount Account, long FolderId, FolderRole Role)> _folderIds = [];

    /// <summary>How many rows the open folder's page holds; grown by the footer row's press.</summary>
    private int _listLimit = Store.MailRepository.ListPage;

    /// <summary>Which folder the page size belongs to — a switch starts back at one page.</summary>
    private FolderNode? _pagedFolder;

    /// <summary>A page size a posed run chose, so a seeded store can fill a page. Capture runs only.</summary>
    private int? _posedPage;

    /// <summary>Harness only: shrinks the page so the footer row is reachable over a seeded store.</summary>
    internal void PoseListPage(int limit)
    {
        _posedPage = Math.Max(1, limit);
        _listLimit = _posedPage.Value;
    }

    /// <summary>A footer row was pressed and the shell cannot answer it alone: the server half.</summary>
    public event EventHandler<MoreRequest>? MoreRequested;

    /// <summary>Which role each unified folder gathers, when the unified mailbox is on.</summary>
    private readonly Dictionary<FolderNode, FolderRole> _unifiedRoles = [];

    /// <summary>
    /// The folders the unified mailbox offers, in the reference's own order.
    /// </summary>
    /// <remarks>
    /// The well-known roles and no others. A folder somebody made themselves is theirs and lives
    /// in the account it belongs to; gathering two accounts' "Projects" folders because they share
    /// a name would be guessing that they mean the same thing.
    /// </remarks>
    private static readonly FolderRole[] UnifiedRoles =
    [
        FolderRole.Inbox,
        FolderRole.Drafts,
        FolderRole.Sent,
        FolderRole.Deleted,
        FolderRole.Junk,
        FolderRole.Archive,
    ];

    private static string UnifiedName(FolderRole role) => role switch
    {
        FolderRole.Inbox => "Inbox",
        FolderRole.Drafts => "Drafts",
        FolderRole.Sent => "Sent Items",
        FolderRole.Deleted => "Deleted Items",
        FolderRole.Junk => "Junk Email",
        FolderRole.Archive => "Archive",
        _ => role.ToString(),
    };

    /// <summary>The search-folder nodes, and the saved query each stands for.</summary>
    private readonly Dictionary<FolderNode, (OpenAccount Account, SearchFolder Folder)> _searchFolderIds = [];

    /// <summary>Each account's "Search Folders" heading, so a right-click on it knows the account.</summary>
    private readonly Dictionary<FolderNode, OpenAccount> _searchFolderRoots = [];

    /// <summary>
    /// Fills the panes with what the store holds. Returns false when there is no account,
    /// which leaves them empty.
    /// </summary>
    private bool LoadFromStore(bool selectFirst = true)
    {
        if (_accounts is null) return false;

        var accounts = _accounts.All;
        if (accounts.Count == 0) return false;

        // Set here rather than by the constructor, so the first account the wizard adds
        // switches the shell on without a restart — Refresh lands here too.
        HasAccount = true;

        Folders.Clear();
        _accountHeadings.Clear();
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

        // The unified mailbox, above everything, when it is on. It is a way of reading the
        // accounts rather than a place of its own: nothing is stored under it, and switching it
        // off leaves every folder exactly where it was.
        if (App.MailOptions.UnifiedMailbox && accounts.Count > 1)
        {
            Folders.Add(new FolderNode("All Accounts", 0, 0, bold: true, kind: FolderNodeKind.UnifiedRoot));

            foreach (var role in UnifiedRoles)
            {
                var unread = accounts
                    .Select(a => a.Mail.FolderWithRole(a.Account.Id, role))
                    .Where(f => f is not null)
                    .Sum(f => f!.Unread);

                var node = new FolderNode(UnifiedName(role), 1, unread, kind: FolderNodeKind.Unified);
                _unifiedRoles[node] = role;
                Folders.Add(node);
            }
        }

        Folders.Add(new FolderNode("Favorites", 0, 0, bold: true, kind: FolderNodeKind.FavouritesHeading));
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
            // The account's own heading, which opens the summary page — where the reference puts
            // it too. It is filed by address so the page knows whose day it is showing.
            var heading = new FolderNode(account.Account.Address, 0, 0, bold: true);
            _accountHeadings[heading] = account.Account.Address;
            Folders.Add(heading);

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

        // The feeds store, as a root of its own beside the accounts rather than inside one of
        // them — which is what it used to be, filed into whichever account happened to sort
        // first. Off unless asked for: there is a whole module for feeds, and a reader who wants
        // them does not go looking in Mail. Searching reaches the articles either way.
        if (App.MailOptions.FeedsInMailPane && App.FeedStore?.Account is { } feedStore)
        {
            var tree = feedStore.Mail.Folders(feedStore.Account.Id);
            if (tree.Count > 0)
            {
                var heading = new FolderNode(FeedStores.DisplayName, 0, 0, bold: true);
                Folders.Add(heading);

                var feedDepths = new Dictionary<long, int>();
                foreach (var folder in OrderedForTree(tree))
                {
                    var depth = folder.ParentId is { } parent && feedDepths.TryGetValue(parent, out var up)
                        ? up + 1
                        : 1;
                    feedDepths[folder.Id] = depth;

                    // The root is the store, so its own "RSS Feeds" folder would read as
                    // "RSS Feeds / RSS Feeds". Its children hang off the heading instead.
                    if (folder.ParentId is null && folder.Name == FeedStores.DisplayName)
                    {
                        feedDepths[folder.Id] = 0;
                        continue;
                    }

                    var node = new FolderNode(folder.Name, depth, folder.Unread);
                    _folderIds[node] = (feedStore, folder.Id, folder.Role);
                    Folders.Add(node);
                }
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
    /// <summary>Whose account a folder row belongs to, for a caller that has only its address.</summary>
    public string FolderAddress(FolderNode node)
        => _folderIds.TryGetValue(node, out var where) ? where.Account.Account.Address : string.Empty;

    /// <summary>The account headings in the pane, and whose each one is.</summary>
    private readonly Dictionary<FolderNode, string> _accountHeadings = [];

    /// <summary>
    /// Whether the summary page is what the pane's selection is asking for.
    /// </summary>
    /// <remarks>
    /// An account's heading is not a folder and never was: selecting one used to fall out of
    /// <c>LoadMessages</c> without doing anything at all. It opens the summary page now, which is
    /// what the reference opens from the same row.
    /// </remarks>
    public bool IsTodayShowing
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(ShowsWorkspace));

            // The status bar reads this too: Today is a page inside Mail, so the counts have to
            // stand down for the page's own line and come back when it closes.
            Raise(nameof(StatusLeft));
        }
    }

    /// <summary>Whose day the summary page is showing, or empty when it is not up.</summary>
    public string TodayAccount { get; private set; } = string.Empty;

    /// <summary>Raised when the summary page should be shown or taken away.</summary>
    public event EventHandler<string>? TodayRequested;

    /// <summary>
    /// Whether the workspace host is covering the mail panes: a module other than Mail, or the
    /// summary page, which lives in the same cell.
    /// </summary>
    public bool ShowsWorkspace => !IsMailModule || IsTodayShowing;

    private void LoadMessages(FolderNode? folder)
    {
        if (_accounts is null || folder is null) return;

        // A different folder starts back at one page; reloading the same one keeps its growth,
        // which is what lets the footer row's press survive the reload it causes.
        if (!ReferenceEquals(folder, _pagedFolder))
        {
            _pagedFolder = folder;
            _listLimit = _posedPage ?? Store.MailRepository.ListPage;
        }

        // The account's heading: the summary page rather than a list of nothing.
        if (_accountHeadings.TryGetValue(folder, out var whose))
        {
            ClearList();
            Rebuild();
            SelectedMessage = null;
            TodayAccount = whose;
            IsTodayShowing = true;
            TodayRequested?.Invoke(this, whose);
            return;
        }

        if (IsTodayShowing)
        {
            IsTodayShowing = false;
            TodayAccount = string.Empty;
            TodayRequested?.Invoke(this, string.Empty);
        }


        // A search folder: the saved query's results, each row saying which folder it is in.
        if (_searchFolderIds.TryGetValue(folder, out var search))
        {
            LoadSearchFolder(search.Account, search.Folder);
            return;
        }

        // The Search Folders heading itself holds nothing; the list empties.
        if (_searchFolderRoots.ContainsKey(folder))
        {
            ClearList();
            Rebuild();
            SelectedMessage = null;
            return;
        }

        if (_unifiedRoles.TryGetValue(folder, out var unified))
        {
            LoadUnified(unified);
            return;
        }

        if (!_folderIds.TryGetValue(folder, out var where)) return;

        ClearList();
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
            : where.Account.Mail.Messages(where.FolderId, half, _listLimit);

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
                // Carried in every view, not only the unified ones: By Account has to know it
                // wherever the row is drawn.
                Address = where.Account.Account.Address,
                SizeBytes = summary.SizeBytes,
                HasAttachment = summary.HasAttachment,
                IsFlagged = summary.IsFlagged,
                IsAnswered = summary.IsAnswered,
                IsForwarded = summary.IsForwarded,
                FollowUpComplete = summary.FollowUpComplete,
                FollowUpDue = summary.FollowUpDue,
                FollowUpStart = summary.FollowUpStart,
                IsHeaderOnly = summary.HeaderOnly,
                SnoozedUntil = summary.SnoozedUntil,
                Importance = summary.Importance,
                ThreadKey = summary.ThreadKey,
                ItemType = summary.ItemType,
                FolderId = summary.FolderId,
                FromAddress = summary.FromAddress,
                To = summary.To,
                Cc = summary.Cc,
                Sent = summary.Sent,
                HasReminder = summary.Reminder is not null,
            });
        }

        // The page is full, so the folder may hold more than the list: count the store the
        // way the pane beside it does, or the bar reports the cap as the folder's size. The
        // reachability half — an "older messages" affordance — is the queue's, built once for
        // the offline window and this page together.
        if (!ShowSnoozed && Messages.Count >= MailRepository.ListPage
            && where.Account.Mail.Folders(where.Account.Account.Id)
                .FirstOrDefault(f => f.Id == where.FolderId) is { } stored
            && stored.Total > Messages.Count)
        {
            _folderBeyondList = (stored.Total, stored.Unread);
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

    /// <summary>
    /// One unified folder: every account's folder of that role, newest first.
    /// </summary>
    /// <remarks>
    /// Merged here rather than in the store, because there is no "here" in the store to merge in:
    /// each account is its own file, and that is the property the whole design rests on. So
    /// the view reads each and interleaves, and every row carries the address it came from — an id
    /// alone says nothing when two stores both number their rows from one.
    /// <para>
    /// The row's To line shows which account it arrived at, which in this view is the one thing a
    /// reader cannot work out from the message itself.
    /// </para>
    /// </remarks>
    private void LoadUnified(FolderRole role)
    {
        ClearList();
        if (_accounts is null) return;

        foreach (var account in _accounts.All)
        {
            if (account.Mail.FolderWithRole(account.Account.Id, role) is not { } folder) continue;

            // Focused Inbox is a per-account setting and this view is not per-account, so the
            // unified Inbox shows both halves. Filtering to one would hide mail from whichever
            // accounts disagreed with the switch.
            var summaries = ShowSnoozed
                ? account.Mail.Snoozed(folder.Id)
                : account.Mail.Messages(folder.Id);

            var rows = new List<MessageRow>();

            foreach (var summary in summaries)
            {
                var preview = summary.SnoozedUntil is { } until
                    ? $"Snoozed until {SnoozeLabel(until)} — {summary.Preview}"
                    : summary.Preview;

                rows.Add(new MessageRow(
                    summary.Id,
                    summary.DisplayFrom,
                    summary.Subject,
                    preview,
                    summary.Received,
                    !summary.IsRead,
                    $"To: {account.Account.Address}",
                    summary.Preview)
                {
                    Address = account.Account.Address,
                    SizeBytes = summary.SizeBytes,
                    HasAttachment = summary.HasAttachment,
                    IsFlagged = summary.IsFlagged,
                    IsAnswered = summary.IsAnswered,
                    IsForwarded = summary.IsForwarded,
                    FollowUpComplete = summary.FollowUpComplete,
                    FollowUpDue = summary.FollowUpDue,
                FollowUpStart = summary.FollowUpStart,
                IsHeaderOnly = summary.HeaderOnly,
                    SnoozedUntil = summary.SnoozedUntil,
                    Importance = summary.Importance,
                    ThreadKey = summary.ThreadKey,
                ItemType = summary.ItemType,
                    FolderId = summary.FolderId,

                    // Which account a row came from is what this view exists to show, and it is
                    // the one thing the message itself does not say.
                    FolderLabel = account.Account.Address,
                    FromAddress = summary.FromAddress,
                    To = summary.To,
                    Cc = summary.Cc,
                    Sent = summary.Sent,
                    HasReminder = summary.Reminder is not null,
                });
            }

            // One query per account for its own categories, as a single folder does for its own.
            var categories = account.Mail.CategoriesFor([.. rows.Select(r => r.Id)]);
            foreach (var row in rows)
            {
                if (categories.TryGetValue(row.Id, out var assigned))
                {
                    row.CategoryTokens = [.. assigned.Select(c => c.ColourToken)];
                    row.CategoryNames = [.. assigned.Select(c => c.Name)];
                }
            }

            foreach (var row in rows) Messages.Add(row);
        }

        // Interleaved once at the end rather than merged as they arrive: the arrangement engine
        // sorts what it is given, and this is only about the order the rows reach it in.
        var ordered = Messages.OrderByDescending(m => m.Received).ToList();
        ClearList();
        foreach (var row in ordered) Messages.Add(row);

        Rebuild();
        SelectedMessage = Messages.FirstOrDefault();
    }

    /// <summary>How a snooze time is written: a time today, else the day and time.</summary>
    internal static string SnoozeLabel(DateTimeOffset until, DateTimeOffset? today = null)
    {
        var now = today ?? Mailbox.Core.PosedClock.Now;
        var local = until.ToLocalTime();
        return local.Date == now.Date ? local.ToString("h:mm tt") : local.ToString("ddd d MMM, h:mm tt");
    }

    /// <summary>
    /// How the list writes a date: a time for today, a weekday within the week, otherwise the
    /// date. Matches what the reference shows.
    /// </summary>
    internal static string Received(DateTimeOffset when, DateTimeOffset? today = null)
    {
        var now = today ?? Mailbox.Core.PosedClock.Now;
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

    /// <summary>True once the reader has picked a scope by hand during this search.</summary>
    private bool _scopeTouched;

    /// <summary>The scope the search box runs against, re-run when it changes.</summary>
    public SearchScope Scope
    {
        get => _scope;
        set
        {
            if (!Set(ref _scope, value)) return;
            _scopeTouched = true;
            Raise(nameof(ScopeLabel));
            Raise(nameof(ScopeIndex));
            if (IsSearching) RunSearch();
        }
    }

    /// <summary>
    /// Puts the scope where the Options page's Search radios say a search begins, at the moment
    /// one begins.
    /// </summary>
    /// <remarks>
    /// Resolved per search rather than once, because the shipped default — "Current folder.
    /// Current mailbox when searching from the Inbox" — is conditional on where the reader is
    /// standing, and because the radios can be changed while the application runs. A scope the
    /// reader picked by hand outlives the keystrokes of its own search and nothing else: the
    /// next search starts from the radios again.
    /// </remarks>
    private void BeginSearchScope()
    {
        if (_scopeTouched) return;

        _scope = App.Settings.GetString(MailOptions.SearchScopeDefaultKey) switch
        {
            "Current folder" => SearchScope.ThisFolder,
            "Current mailbox" => SearchScope.CurrentMailbox,
            "All mailboxes" => SearchScope.AllMailboxes,
            _ => CurrentFolderRole == FolderRole.Inbox ? SearchScope.CurrentMailbox : SearchScope.ThisFolder,
        };

        Raise(nameof(ScopeLabel));
        Raise(nameof(ScopeIndex));
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
            _scopeTouched = false;
            _searchResultCount = 0;
            LoadMessages(_selectedFolder);
            return;
        }

        // The first keystroke of a search is where the Options page's default scope lands.
        if (!IsSearching) BeginSearchScope();

        ClearList();

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
                    IsAnswered = summary.IsAnswered,
                    IsForwarded = summary.IsForwarded,
                    FollowUpComplete = summary.FollowUpComplete,
                    FollowUpDue = summary.FollowUpDue,
                FollowUpStart = summary.FollowUpStart,
                IsHeaderOnly = summary.HeaderOnly,
                    Importance = summary.Importance,
                    ThreadKey = summary.ThreadKey,
                ItemType = summary.ItemType,
                    FolderId = summary.FolderId,
                    FolderLabel = label,

                    // A search over All Mailboxes draws two stores at once, so a row has to say
                    // which one it came from — an id alone means a different message in each.
                    Address = account.Account.Address,
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
        ClearList();
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
                IsAnswered = summary.IsAnswered,
                IsForwarded = summary.IsForwarded,
                FollowUpComplete = summary.FollowUpComplete,
                FollowUpDue = summary.FollowUpDue,
                FollowUpStart = summary.FollowUpStart,
                IsHeaderOnly = summary.HeaderOnly,
                SnoozedUntil = summary.SnoozedUntil,
                Importance = summary.Importance,
                ThreadKey = summary.ThreadKey,
                ItemType = summary.ItemType,
                FolderId = summary.FolderId,
                FolderLabel = names.GetValueOrDefault(summary.FolderId, string.Empty),
                Address = account.Account.Address,
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

    /// <summary>
    /// Fills in the category swatches for whatever is currently in <see cref="Messages"/>.
    /// </summary>
    /// <remarks>
    /// Grouped by the account each row came from rather than by id. An id is only unique inside
    /// one account's store — every store numbers its rows from one — so putting the visible rows
    /// in a dictionary keyed by id **threw** the moment a search spanned two accounts, and before
    /// that it would have painted one account's categories onto another's message.
    /// </remarks>
    private void LoadCategoriesForVisible()
    {
        foreach (var account in _accounts?.All ?? [])
        {
            // A row with no address came from a view that is one account's, so it belongs to
            // whichever account is asked — which is what the fallback in AccountFor says too.
            var mine = Messages
                .Where(m => m.Address.Length == 0
                            || string.Equals(m.Address, account.Account.Address, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (mine.Count == 0) continue;

            var assigned = account.Mail.CategoriesFor([.. mine.Select(m => m.Id)]);
            foreach (var row in mine)
            {
                if (assigned.TryGetValue(row.Id, out var categories))
                {
                    row.CategoryTokens = [.. categories.Select(c => c.ColourToken)];
                    row.CategoryNames = [.. categories.Select(c => c.Name)];
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
        new(MailboxModule.Feeds, "rss", isActive: false),
    ];

    /// <summary>
    /// The window's own title — the folder, the account, the application, as the reference's is.
    /// </summary>
    /// <remarks>
    /// Bound by <c>MainWindow.axaml</c>. It was computed, raised on every folder change, and read
    /// by nothing: the window declared a literal "Mailbox" and kept it for the whole session,
    /// while this carried a hard-coded address that was never anybody's. The title bar draws no
    /// title text — the reference's does not either — so this is what the desktop's task switcher
    /// and window list show, and it was the same string for every folder of every account.
    /// </remarks>
    public string WindowTitle => AccountAddress is { Length: > 0 } signedIn
        ? $"{SelectedFolderName} - {signedIn} - Mailbox"
        : $"{SelectedFolderName} - Mailbox";

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
                _themes.ApplyFresh(id);
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
            if (value is MoreRow more) Later(() => ActOnMore(more));
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
        set
        {
            // A new message is drawn from its row until the pane says otherwise, which it does when
            // the message turns out to carry its own header fields (RFC 9788). Cleared here so the
            // last protected subject does not head the next message — and cleared <em>before</em>
            // the change is announced, because announcing it is what makes the pane open the
            // message, and the pane's own answer comes back inside this call. Clearing afterwards
            // threw that answer away, which is what a header protected message being drawn as
            // "[...]" looked like.
            // The same row again is not a new message, and the list re-asserts its selection as it
            // lays out: clearing on that pass threw away what the pane had already found, and the
            // pane never runs again because nothing changed.
            if (ReferenceEquals(_selectedMessage, value)) return;

            _readingSubject = null;
            _readingFrom = null;

            Set(ref _selectedMessage, value);
            Raise(nameof(ReadingTo));
        }
    }

    /// <summary>
    /// What the reading pane's header draws, which is not always what the list row says.
    /// </summary>
    /// <remarks>
    /// The list holds what arrived, and for an encrypted message what arrived says <c>[...]</c> where
    /// its subject should be — that being the point of header protection. The pane opens the message
    /// and knows better, so it hands back what it found and these are what the header binds to. The
    /// row is left alone: it is what the folder holds, and nothing has changed about that.
    /// </remarks>
    public string ReadingSubject => _readingSubject ?? SelectedMessage?.Subject ?? string.Empty;

    /// <summary>The sender the pane's header draws. See <see cref="ReadingSubject"/>.</summary>
    public string ReadingFrom => _readingFrom ?? SelectedMessage?.From ?? string.Empty;

    /// <summary>
    /// The recipients line the pane's header draws — the row's, empty when nothing is
    /// selected. Bound here rather than through <c>SelectedMessage.ToLine</c>, whose chain
    /// breaks (and logs) whenever no row is selected.
    /// </summary>
    public string ReadingTo => SelectedMessage?.ToLine ?? string.Empty;

    /// <summary>Draws the pane's header from a message's own protected header fields, or from its row.</summary>
    public void ReadFrom(string? subject, string? from)
    {
        _readingSubject = subject;
        _readingFrom = from;

        Raise(nameof(ReadingSubject));
        Raise(nameof(ReadingFrom));
    }

    private string? _readingSubject;
    private string? _readingFrom;

    public string SelectedFolderName => SelectedFolder?.Name ?? "Inbox";

    // Status-bar and pane glyphs. Held here so the XAML never names an icon codepoint.
    public FontFamily IconFamily { get; } = IconFont.Family;
    // ---- List shaping ---------------------------------------------------------------------
    // Filtering, sorting and grouping are view state: they run over whatever rows the store
    // put in the list, never against the store itself.

    // ---- Ignore and Clean Up ---------------------------------------------------------------------

    /// <summary>Whether every selected row's conversation is already ignored — the button then reads Stop Ignoring.</summary>
    public bool IsIgnored(IReadOnlyList<MessageRow> rows)
        => rows.Count > 0 && Mail(rows) is { } mail && rows.All(r => mail.IsIgnored(StoreKey(r)));

    /// <summary>The store's thread key for a row, which the row carries as stored.</summary>
    private static string StoreKey(MessageRow row) => row.ThreadKey;

    /// <summary>
    /// Ignore Conversation: the selection's conversations go to Deleted Items — every message of
    /// each, in every folder — and stay ignored, so what arrives in them later goes there too.
    /// Stop Ignoring brings the conversation back to the Inbox and forgets it.
    /// </summary>
    public void IgnoreConversation(IReadOnlyList<MessageRow> rows)
    {
        if (Split(rows, group => IgnoreConversation(group))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail || AccountOf(rows) is not { } account) return;

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
        if (AccountOf(rows) is not { } account || account.Mail is not { } mail) return 0;

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
            ? folders.SelectMany(f => mail.Messages(f, int.MaxValue)).Select(m => m.ThreadKey).Where(k => k.Length > 0).Distinct().ToList()
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

    // ---- Focused Inbox ------------------------------------------------------------------
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
        if (Split(rows, group => SetFocused(group, focused, always))) return;

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
        var today = Mailbox.Core.PosedClock.Now.Date;
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

            // The header's arrow says which column the list is sorted by, so it goes stale the
            // moment the arrangement changes unless the columns are built again — which is
            // exactly what pressing a header does.
            RebuildColumns();
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

            // Which way the header's arrow points is this value, so the columns are built again
            // for the same reason they are when the arrangement changes.
            RebuildColumns();
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

    /// <summary>Folds one group shut or opens it — the View tab's Expand/Collapse, on one group.</summary>
    public void SetGroupCollapsed(string header, bool collapsed)
    {
        if (collapsed ? !_collapsed.Add(header) : !_collapsed.Remove(header)) return;

        Rebuild();
    }

    /// <summary>
    /// Every group of the list at once, which is the other half of Expand/Collapse.
    /// </summary>
    /// <remarks>
    /// Over the headers on screen rather than every header ever seen: a collapse remembered for
    /// a group that a change of arrangement has dissolved is a collapse nobody asked for.
    /// </remarks>
    public void SetAllGroupsCollapsed(bool collapsed)
    {
        var changed = false;
        foreach (var header in VisibleRows.OfType<GroupHeaderRow>().Select(g => g.Header).ToList())
        {
            changed |= collapsed ? _collapsed.Add(header) : _collapsed.Remove(header);
        }

        if (changed) Rebuild();
    }

    /// <summary>
    /// The group the selection sits in, for Expand/Collapse's first two entries. Null when the
    /// list has no groups, or when nothing is selected.
    /// </summary>
    public string? SelectedGroupHeader
    {
        get
        {
            if (SelectedRow is GroupHeaderRow chosen) return chosen.Header;

            string? header = null;
            foreach (var row in VisibleRows)
            {
                if (row is GroupHeaderRow group) header = group.Header;
                else if (ReferenceEquals(row, SelectedRow)) return header;
            }

            return null;
        }
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
        // The pinned clock, not the machine's: the date bands are Today, Yesterday and the named
        // days of the past week, so a capture taken against the real clock is a different picture
        // every day and the wording cannot be held to a reference.
        var groups = Store.Lists.Arrangements.Group(
            rows, GroupArrangement, GroupDescending, Mailbox.Core.PosedClock.Now);

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
            content.AddRange(FooterRows());
            VisibleRows = content;
            Raise(nameof(VisibleCount));
            Raise(nameof(StatusLeft));
            return;
        }

        // With conversations on, the whole folder threads first and each conversation is
        // banded by its newest message — threading one band at a time drew a thread spanning
        // Today and Yesterday as one conversation per band, its own newest message detached
        // from it.
        if (ShowAsConversations)
        {
            var bandOf = new Dictionary<MessageRow, string>();
            foreach (var group in groups)
            {
                foreach (var row in group.Items) bandOf[row] = group.Header;
            }

            var perBand = groups.ToDictionary(g => g.Header, _ => new List<object>());
            var current = groups.Count > 0 ? groups[0].Header : string.Empty;

            foreach (var unit in Threaded([.. groups.SelectMany(g => g.Items)]))
            {
                // A conversation row bands by its newest message; an expanded child follows
                // whichever band its conversation went to, so a thread never splits again.
                current = unit switch
                {
                    ConversationRow conversation when bandOf.TryGetValue(conversation.Newest, out var band) => band,
                    MessageRow { Depth: 0 } top when bandOf.TryGetValue(top, out var band) => band,
                    _ => current,
                };

                if (perBand.TryGetValue(current, out var band2)) band2.Add(unit);
            }

            foreach (var group in groups)
            {
                var content = perBand[group.Header];
                if (content.Count == 0) continue;

                var collapsed2 = _collapsed.Contains(group.Header);
                built.Add(new GroupHeaderRow(group.Header, Countable(content), collapsed2));
                if (!collapsed2) built.AddRange(content);
            }
        }
        else
        {
            foreach (var group in groups)
            {
                var collapsed = _collapsed.Contains(group.Header);
                var content = group.Items.Select(r => (object)Reset(r)).ToList();

                built.Add(new GroupHeaderRow(group.Header, Countable(content), collapsed));

                if (collapsed) continue;

                built.AddRange(content);
            }
        }

        built.AddRange(FooterRows());
        VisibleRows = built;
        Raise(nameof(VisibleCount));
        Raise(nameof(StatusLeft));
    }

    /// <summary>
    /// The rows after the last message: what this list is not showing, and the offer to show
    /// it. At most one — the nearest wall first: the local page before the server's older mail,
    /// because a reader wants what is already here before what has to be fetched.
    /// </summary>
    private List<object> FooterRows()
    {
        var footer = new List<object>();
        if (_accounts is null) return footer;

        // Under a search: the offer to take the query to the server, where the window has left
        // mail this store cannot match against.
        if (IsSearching)
        {
            // The offer stands for the folder in front of the reader, in the scopes where that
            // folder is what is being searched — a query taken to the server is folder-scoped,
            // and All Mailboxes has no one folder to take it to.
            if (_scope is SearchScope.ThisFolder or SearchScope.CurrentMailbox
                && _selectedFolder is { } searched && _folderIds.TryGetValue(searched, out var scope)
                && scope.Account.Account.Protocol == MailProtocol.Imap
                && scope.Account.Mail.GetFolder(scope.FolderId) is { ServerOlder: > 0 } deep)
            {
                footer.Add(new MoreRow(
                    MoreRowKind.ServerSearch,
                    $"Older mail is on the server — search it too "
                    + $"({deep.ServerOlder:N0} message{(deep.ServerOlder == 1 ? string.Empty : "s")} beyond the offline window)"));
            }

            return footer;
        }

        if (ShowSnoozed || IsTodayShowing || ShowOther) return footer;
        if (_selectedFolder is not { } folder || !_folderIds.TryGetValue(folder, out var where)) return footer;
        if (where.Role == FolderRole.Outbox) return footer;
        if (where.Account.Mail.GetFolder(where.FolderId) is not { } stored) return footer;

        if (Messages.Count >= _listLimit && stored.Total > Messages.Count)
        {
            footer.Add(new MoreRow(
                MoreRowKind.LocalPage,
                $"Showing the newest {Messages.Count:N0} of {stored.Total:N0} — show more"));
        }
        else if (stored.ServerOlder > 0 && where.Account.Account.Protocol == MailProtocol.Imap)
        {
            footer.Add(new MoreRow(
                MoreRowKind.ServerOlder,
                $"{stored.ServerOlder:N0} older message{(stored.ServerOlder == 1 ? string.Empty : "s")} "
                + $"on the server — download the next {Math.Min(stored.ServerOlder, OlderBatch):N0}"));
        }

        return footer;
    }

    /// <summary>How many older messages one press of the footer row fetches.</summary>
    public const int OlderBatch = 100;

    /// <summary>Answers a pressed footer row: the local page here, the server halves by event.</summary>
    private void ActOnMore(MoreRow more)
    {
        if (more.Kind == MoreRowKind.LocalPage)
        {
            GrowPage();
            return;
        }

        if (_selectedFolder is { } folder && _folderIds.TryGetValue(folder, out var where))
        {
            MoreRequested?.Invoke(this, new MoreRequest(more.Kind, where.Account, where.FolderId, _searchText.Trim()));
        }
    }

    /// <summary>One more page of the open folder, with the reader's place kept.</summary>
    private void GrowPage()
    {
        var keep = SelectedMessage?.Id;
        _listLimit += _posedPage ?? Store.MailRepository.ListPage;
        LoadMessages(_selectedFolder);

        if (keep is { } id && Messages.FirstOrDefault(m => m.Id == id) is { } again)
        {
            SelectedRow = again;
        }
    }

    /// <summary>Reloads the open folder after mail arrived outside the sync's own flow.</summary>
    public void ReloadAfterFetch()
    {
        if (IsSearching)
        {
            RunSearch();
            return;
        }

        var keep = SelectedMessage?.Id;
        LoadMessages(_selectedFolder);
        if (keep is { } id && Messages.FirstOrDefault(m => m.Id == id) is { } again)
        {
            SelectedRow = again;
        }
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

    // ---- Undo --------------------------------------------------------------------------------

    /// <summary>
    /// What Ctrl+Z takes back: the shell's own actions, in the order they were done.
    /// </summary>
    /// <remarks>
    /// Every command below that changes mail records a step before it acts. What is recorded is
    /// the state it is about to overwrite — which folder each message was in, whether it was
    /// read, what its flag said, which categories it carried — because the store commits the
    /// change immediately and there is nothing left to read afterwards.
    /// <para>
    /// The rule is that a command records or it cannot be reached by Ctrl+Z at all, because a
    /// stack with holes in it is worse than none: a press that finds no step for the command just
    /// used silently takes back the one before it, which is the mail the reader was not thinking
    /// about. Four things deliberately record nothing, and each is safe from that because it
    /// cannot be undone by anybody:
    /// </para>
    /// <list type="bullet">
    /// <item>a permanent delete, and a message already handed to a server — there is nothing left
    /// to put back;</item>
    /// <item>Ignore Conversation, which is a standing instruction with Stop Ignoring as its own
    /// reversal rather than a change to a message;</item>
    /// <item>Focused and Other, which writes a preference about a sender.</item>
    /// </list>
    /// <para>
    /// A command that is several operations — Junk, a Quick Step — opens
    /// <see cref="UndoStack.Batch"/> so that one press takes back one press.
    /// </para>
    /// </remarks>
    public UndoStack Undo { get; } = new();

    /// <summary>Where each of these messages is now, so a move can be put back.</summary>
    private List<(OpenAccount Account, long Id, long FolderId)> Where(IReadOnlyList<MessageRow> rows)
    {
        var places = new List<(OpenAccount, long, long)>(rows.Count);

        foreach (var row in rows)
        {
            if (AccountFor(row) is not { } account) continue;
            if (account.Mail.GetMessage(row.Id) is not { } message) continue;

            places.Add((account, row.Id, message.FolderId));
        }

        return places;
    }

    /// <summary>Puts every message back where it was, one move per folder it came from.</summary>
    private void PutBack(List<(OpenAccount Account, long Id, long FolderId)> places)
    {
        foreach (var group in places.GroupBy(p => (p.Account.Account.Address, p.FolderId)))
        {
            var account = group.First().Account;
            account.Mail.MoveMessages([.. group.Select(p => p.Id)], group.Key.FolderId);
        }

        AfterUndo();
    }

    /// <summary>What every undone step ends with: the list and the counts as they now are.</summary>
    private void AfterUndo()
    {
        ReloadCurrentView();
        RefreshCounts();
    }

    /// <summary>What each of these messages carries now, so a flag or a read mark can be put back.</summary>
    private List<(OpenAccount Account, MessageSummary Message)> State(IReadOnlyList<MessageRow> rows)
    {
        var state = new List<(OpenAccount, MessageSummary)>(rows.Count);

        foreach (var row in rows)
        {
            if (AccountFor(row) is not { } account) continue;
            if (account.Mail.GetMessage(row.Id) is { } message) state.Add((account, message));
        }

        return state;
    }

    /// <summary>Puts back the flag each message had, whatever it was.</summary>
    private void RestoreFlags(List<(OpenAccount Account, MessageSummary Message)> state)
    {
        foreach (var (account, message) in state)
        {
            if (message.FollowUpComplete) account.Mail.CompleteFollowUp([message.Id]);
            else if (message.IsFlagged)
            {
                account.Mail.SetCustomFollowUp(
                    [message.Id], message.FollowUpType, message.FollowUpStart, message.FollowUpDue, message.Reminder);
            }
            else account.Mail.ClearFollowUp([message.Id]);
        }

        AfterUndo();
    }

    /// <summary>Puts back whether each message had been read.</summary>
    private void RestoreRead(List<(OpenAccount Account, MessageSummary Message)> state)
    {
        foreach (var group in state.GroupBy(s => (s.Account.Account.Address, s.Message.IsRead)))
        {
            var account = group.First().Account;
            account.Mail.SetRead([.. group.Select(s => s.Message.Id)], group.Key.IsRead);
        }

        AfterUndo();
    }

    /// <summary>Puts back the flag column, which is not the follow-up beside it.</summary>
    /// <remarks>
    /// <c>SetFlagged</c> writes one column and journals one flag to the server; the follow-up
    /// paths write the type, the dates and the reminder. Taking a flag back through
    /// <see cref="RestoreFlags"/> would therefore rewrite a follow-up the reader never touched.
    /// </remarks>
    private void RestoreFlagged(List<(OpenAccount Account, MessageSummary Message)> state)
    {
        foreach (var group in state.GroupBy(s => (s.Account.Account.Address, s.Message.IsFlagged)))
        {
            var account = group.First().Account;
            account.Mail.SetFlagged([.. group.Select(s => s.Message.Id)], group.Key.IsFlagged);
        }

        AfterUndo();
    }

    /// <summary>Puts back the importance each message carried.</summary>
    private void RestoreImportance(List<(OpenAccount Account, MessageSummary Message)> state)
    {
        foreach (var group in state.GroupBy(s => (s.Account.Account.Address, s.Message.Importance)))
        {
            var account = group.First().Account;
            account.Mail.SetImportance([.. group.Select(s => s.Message.Id)], group.Key.Importance);
        }

        AfterUndo();
    }

    /// <summary>Puts back the snooze each message was under, or the absence of one.</summary>
    /// <remarks>
    /// One message at a time, because the three columns snoozing writes differ per message and
    /// the row's own arrival time is one of them — see <c>MailRepository.RestoreSnooze</c>.
    /// </remarks>
    private void RestoreSnooze(List<(OpenAccount Account, MessageSummary Message)> state)
    {
        foreach (var (account, message) in state)
        {
            account.Mail.RestoreSnooze(message.Id, message.SnoozedUntil, message.IsRead, message.Received);
        }

        AfterUndo();
    }

    /// <summary>Which categories each of these messages carries, so a categorization can be put back.</summary>
    private List<(OpenAccount Account, long Id, long[] Categories)> Categorised(IReadOnlyList<MessageRow> rows)
    {
        var state = new List<(OpenAccount, long, long[])>(rows.Count);

        foreach (var group in rows.GroupBy(AccountFor))
        {
            if (group.Key is not { } account) continue;

            var assigned = account.Mail.CategoriesFor([.. group.Select(r => r.Id)]);
            foreach (var row in group)
            {
                state.Add((account, row.Id, assigned.TryGetValue(row.Id, out var list)
                    ? [.. list.Select(c => c.Id)]
                    : []));
            }
        }

        return state;
    }

    /// <summary>
    /// Puts back exactly the categories each message had.
    /// </summary>
    /// <remarks>
    /// Everything off and the recorded set on again, rather than the difference: the commands
    /// that record this one add, remove and clear, and one restore that always leaves the row as
    /// it was found is worth more than three that each undo their own kind of change.
    /// </remarks>
    private void RestoreCategories(List<(OpenAccount Account, long Id, long[] Categories)> state)
    {
        foreach (var group in state.GroupBy(s => s.Account.Account.Address, StringComparer.OrdinalIgnoreCase))
        {
            var account = group.First().Account;
            var ids = group.Select(s => s.Id).ToList();

            foreach (var category in account.Mail.Categories()) account.Mail.Unassign(ids, category.Id);

            foreach (var (_, id, categories) in group)
            {
                foreach (var category in categories) account.Mail.Assign([id], category);
            }
        }

        AfterUndo();
    }

    // ---- Acting on a selection -------------------------------------------------------------
    // Every one of these takes the rows explicitly rather than reading a selection property.
    // The list owns the selection, and a command that reaches back for it can act on something
    // other than what the user had highlighted when they pressed the key.

    /// <summary>Marks rows read or unread, in the store and on screen. Quiet leaves the status line alone — read by looking is not news.</summary>
    public void SetRead(IReadOnlyList<MessageRow> rows, bool read, bool quiet = false)
    {
        if (Split(rows, group => SetRead(group, read, quiet))) return;

        if (rows.Count == 0) return;

        // Read by looking is not an action somebody took, so it records nothing: a Ctrl+Z that
        // marked a message unread again because the reader glanced at it would be a surprise.
        var before = quiet ? [] : State(rows);

        Mail(rows)?.SetRead([.. rows.Select(r => r.Id)], read);
        foreach (var row in rows) row.IsUnread = !read;

        if (!quiet)
        {
            Undo.Push(
                read ? "Mark as Read" : "Mark as Unread",
                () => RestoreRead(before),
                () => SetRead(rows, read));
        }

        // Read by looking happens in the middle of a selection change; the pane's counts can
        // wait a moment rather than being rebuilt under it. Deliberately only the counts: a
        // search folder whose query this read falls out of — Unread Mail, most of every day —
        // keeps the row on screen until the reader leaves, exactly as the reference does. A row
        // that vanished as it was read would take the reader's place in the list with it.
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
    /// and a delete over two files rather than a move — real, still to be built, and not something
    /// to offer as a menu entry that quietly does something else.
    /// </remarks>
    public IReadOnlyList<FolderNode> FoldersOfSelection(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0 || AccountOf(rows) is not { } account) return [];

        var here = SelectedFolder;

        // One entry per folder. A favourite is drawn twice in the pane — under Favourites and
        // in its account's tree — and both rows are registered here, so a Move menu built
        // straight off this map offered "Inbox" twice and left the reader to guess.
        return
        [
            .. _folderIds
                .Where(kv => ReferenceEquals(kv.Value.Account, account) && !ReferenceEquals(kv.Key, here))
                .Where(kv => kv.Value.Role != FolderRole.Outbox)
                .GroupBy(kv => kv.Value.FolderId)
                .Select(group => group.FirstOrDefault(kv => kv.Key.Kind != FolderNodeKind.Favourite).Key
                                 ?? group.First().Key),
        ];
    }

    /// <summary>The categories this account defines, for the Categorize menu.</summary>
    /// <summary>
    /// The one set, which is what every module's Categorize menu lists.
    /// </summary>
    /// <remarks>
    /// The set rather than the open account's own rows: the categories are one list across the
    /// modules and across the accounts, and a reader with two accounts should not be shown
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
        if (Split(rows, group => ToggleCategory(group, category))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;
        if (Mirrored(mail, category) is not { } mirrored) return;

        var ids = rows.Select(r => r.Id).ToList();
        var remove = AllHave(rows, category);
        var before = Categorised(rows);

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

        // Redo goes back through the toggle rather than repeating the half it chose: put back as
        // it was, the toggle reads the rows the same way again and takes the same branch.
        Undo.Push(
            remove ? "Clear Category" : "Categorize",
            () => RestoreCategories(before),
            () => ToggleCategory(rows, category));
    }

    /// <summary>Takes every category off the rows.</summary>
    public void ClearCategories(IReadOnlyList<MessageRow> rows)
    {
        if (Split(rows, group => ClearCategories(group))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var ids = rows.Select(r => r.Id).ToList();
        var before = Categorised(rows);

        foreach (var category in mail.Categories()) mail.Unassign(ids, category.Id);
        foreach (var row in rows) row.CategoryTokens = [];

        StatusRight = $"Categories cleared on {Describe(rows.Count)}.";

        Undo.Push("Clear Categories", () => RestoreCategories(before), () => ClearCategories(rows));
    }

    public void SetFlagged(IReadOnlyList<MessageRow> rows, bool flagged)
    {
        if (Split(rows, group => SetFlagged(group, flagged))) return;

        // The store first, as every command here does: without one there is nothing to change,
        // and a row that showed a flag the store never took would say so until the list reloaded.
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var before = State(rows);

        mail.SetFlagged([.. rows.Select(r => r.Id)], flagged);
        foreach (var row in rows) row.IsFlagged = flagged;

        StatusRight = flagged
            ? $"{Describe(rows.Count)} flagged for follow up."
            : $"Flag cleared on {Describe(rows.Count)}.";

        Undo.Push(
            flagged ? "Follow Up" : "Clear Flag",
            () => RestoreFlagged(before),
            () => SetFlagged(rows, flagged));
    }

    /// <summary>
    /// Deletes rows: to Deleted Items normally, or for good when asked. Moving rather than
    /// deleting is the default because the store may hold the only copy.
    /// </summary>
    public void Delete(IReadOnlyList<MessageRow> rows, bool permanently)
    {
        if (Split(rows, group => Delete(group, permanently))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var ids = rows.Select(r => r.Id).ToList();
        // The account's own Deleted Items — the rows', not the folder's on screen, which in a
        // unified folder is nobody's.
        var deleted = AccountOf(rows) is { } owner
            ? owner.Mail.FolderWithRole(owner.Account.Id, FolderRole.Deleted)
            : null;

        // By id, not by name. The rule is "these rows are already in their own account's
        // Deleted Items, so there is nowhere left to move them"; comparing names made it "the
        // folder on screen is called the same thing as some account's Deleted Items", which a
        // nested folder or an imported tree from a Maildir or a .pst can easily be — and the
        // difference between the two readings is a message moved and a message gone for good,
        // reached by the Delete key with no prompt.
        var inDeletedItems = SelectedFolder is { } selected
            && _folderIds.TryGetValue(selected, out var where)
            && where.FolderId == deleted?.Id;

        if (permanently || deleted is null || inDeletedItems)
        {
            // Nothing is recorded for this one: the rows are gone from the store and there is
            // nothing left to put back. An undo that quietly did nothing would be worse than
            // Ctrl+Z saying there is nothing to undo.
            mail.DeleteMessages(ids);
            StatusRight = $"{Describe(rows.Count)} permanently deleted.";
        }
        else
        {
            var from = Where(rows);
            mail.MoveMessages(ids, deleted.Id);
            StatusRight = $"{Describe(rows.Count)} moved to Deleted Items.";

            Undo.Push("Delete", () => PutBack(from), () => Delete(rows, permanently: false));
        }

        RemoveRows(rows);
        RefreshCounts();
    }

    /// <summary>Moves rows into another folder of the same account.</summary>
    /// <summary>
    /// Where a role move lands: the folder the account names for it when it names one, else the
    /// folder wearing the role.
    /// </summary>
    /// <remarks>
    /// Only Archive can be renamed — the reference's Set Archive Folder, which is about the
    /// one-press archive alone. A named folder that has since been deleted or renamed falls back
    /// to the role rather than refusing to archive, because a button that stops working because
    /// a folder moved is a button nobody can fix without knowing why.
    /// </remarks>
    private static Folder? ArchiveTarget(OpenAccount owner, FolderRole role)
    {
        if (role == FolderRole.Archive
            && AccountSettings.ArchiveFolderName(App.Settings, owner.Account.Address) is { Length: > 0 } named
            && owner.Mail.Folders(owner.Account.Id)
                .FirstOrDefault(f => string.Equals(f.Name, named, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            return chosen;
        }

        return owner.Mail.FolderWithRole(owner.Account.Id, role);
    }

    public void MoveTo(IReadOnlyList<MessageRow> rows, FolderRole role)
    {
        if (Split(rows, group => MoveTo(group, role))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        // The target folder belongs to the rows' own account, not to the folder on screen —
        // which in a unified folder is nobody's.
        if (AccountOf(rows) is not { } owner || ArchiveTarget(owner, role) is not { } target)
        {
            return;
        }

        var from = Where(rows);
        mail.MoveMessages([.. rows.Select(r => r.Id)], target.Id);
        RemoveRows(rows);
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} moved to {target.Name}.";

        Undo.Push(role == FolderRole.Archive ? "Archive" : "Move", () => PutBack(from), () => MoveTo(rows, role));
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

        var from = Where(rows);
        where.Account.Mail.MoveMessages(ids, where.FolderId);
        RemoveRows(rows);
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} moved to {target.Name}.";

        Undo.Push("Move", () => PutBack(from), () => MoveToFolder(ids, target));
    }

    /// <summary>The pane's node for a folder of an account, or null when it is not shown.</summary>
    public FolderNode? NodeFor(OpenAccount account, long folderId)
        => _folderIds.FirstOrDefault(kv =>
            string.Equals(kv.Value.Account.Account.Address, account.Account.Address, StringComparison.OrdinalIgnoreCase)
            && kv.Value.FolderId == folderId).Key;

    /// <summary>Copies rows into a folder of the same account; the originals stay where they are.</summary>
    public void CopyTo(IReadOnlyList<MessageRow> rows, Folder target)
    {
        if (Split(rows, group => CopyTo(group, target))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var ids = rows.Select(r => r.Id).ToList();
        var copies = mail.CopyMessages(ids, target.Id);
        RefreshCounts();
        StatusRight = $"{Describe(copies.Count)} copied to {target.Name}.";

        // The copies are the only thing to take back — the originals never moved. Doing it again
        // makes new rows with new ids, so the step keeps the ones it is holding up to date, or a
        // second undo would go looking for rows that no longer exist.
        Undo.Push(
            "Copy",
            () => { mail.DeleteMessages(copies); AfterUndo(); },
            () => { copies = mail.CopyMessages(ids, target.Id); AfterUndo(); });
    }

    /// <summary>
    /// Moves rows into a folder of their own account named by the store rather than by the pane.
    /// </summary>
    /// <remarks>
    /// The pane does not draw every folder — a collapsed tree, a folder hidden from Favourites —
    /// so a picker can name one that has no node. Moving into another account's folder is not
    /// this: two stores share no row, and a message would have to be copied and deleted.
    /// </remarks>
    public void MoveToStoreFolder(IReadOnlyList<MessageRow> rows, OpenAccount account, Folder target)
    {
        if (Split(rows, group => MoveToStoreFolder(group, account, target))) return;
        if (rows.Count == 0 || AccountOf(rows) is not { } owner) return;

        if (!ReferenceEquals(owner, account))
        {
            StatusRight = $"“{target.Name}” belongs to {account.Account.Address}; "
                + "a message can only be moved within its own account.";
            return;
        }

        var from = Where(rows);
        owner.Mail.MoveMessages([.. rows.Select(r => r.Id)], target.Id);
        RemoveRows(rows);
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} moved to {target.Name}.";

        Undo.Push("Move", () => PutBack(from), () => MoveToStoreFolder(rows, account, target));
    }

    /// <summary>Sets the importance the list's column shows.</summary>
    public void SetImportance(IReadOnlyList<MessageRow> rows, int level)
    {
        if (Split(rows, group => SetImportance(group, level))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var before = State(rows);

        mail.SetImportance([.. rows.Select(r => r.Id)], level);
        StatusRight = $"{Describe(rows.Count)} marked {level switch { 0 => "low", 2 => "high", _ => "normal" }} importance.";

        Undo.Push("Importance", () => RestoreImportance(before), () => SetImportance(rows, level));
    }

    /// <summary>Puts named categories on the rows — the ones that exist; a name that does not is skipped.</summary>
    public void AssignCategories(IReadOnlyList<MessageRow> rows, IReadOnlyList<string> names)
    {
        if (Split(rows, group => AssignCategories(group, names))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var ids = rows.Select(r => r.Id).ToList();
        var before = Categorised(rows);

        foreach (var category in mail.Categories().Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
        {
            mail.Assign(ids, category.Id);
        }

        var assigned = mail.CategoriesFor(ids);
        foreach (var row in rows)
        {
            row.CategoryTokens = assigned.TryGetValue(row.Id, out var list) ? [.. list.Select(c => c.ColourToken)] : [];
        }

        Undo.Push("Categorize", () => RestoreCategories(before), () => AssignCategories(rows, names));
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

    /// <summary>The folder on screen, from the store, or null while no account exists.</summary>
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
    /// <summary>
    /// Everything unread in every account's Inbox, which is what the tray icon and its tooltip
    /// are about: mail waiting to be read, not mail sitting in Drafts or Junk.
    /// </summary>
    /// <remarks>
    /// Each folder counted once. The pane draws a favourite Inbox twice — once under Favourites
    /// and once in its account's tree, which is what the reference does — and both rows are
    /// registered, so the sum used to count the default account's Inbox twice. The badge on the
    /// tray has been saying twenty-two for fourteen unread messages since favourites were
    /// seeded.
    /// </remarks>
    public int TotalUnread
    {
        get
        {
            var counted = new HashSet<(string Address, long Folder)>();
            var total = 0;

            foreach (var node in Folders)
            {
                if (!_folderIds.TryGetValue(node, out var where) || where.Role != FolderRole.Inbox) continue;
                if (!counted.Add((where.Account.Account.Address, where.FolderId))) continue;

                total += node.Unread;
            }

            return total;
        }
    }

    /// <summary>Re-reads what is on screen — the search results, or the folder — after a change to a row's state.</summary>
    /// <remarks>
    /// Keeps the reader on the message they were on. The reload builds fresh <see
    /// cref="MessageRow"/> objects out of the store, so the row this class is holding afterwards
    /// is not one the new list contains and an identity comparison re-selects nothing: every
    /// command that reloads — the flag toggle, Mark Complete, Clear Flag — and every undo left
    /// the selection empty. That is how pressing the flag key twice came to leave a message
    /// flagged rather than complete, the second press acting on nothing and silently doing
    /// nothing.
    /// <para>
    /// Matched on the store id <em>and</em> the address, because every account's store numbers its
    /// own rows from one and a unified view holds several at once. A row that is no longer in the
    /// list — moved, deleted, filtered out — leaves the reload's own choice standing rather than
    /// hunting for a substitute; what should be selected after a delete is a separate question,
    /// and one the reading-pane setting owns.
    /// </para>
    /// </remarks>
    private void ReloadCurrentView()
    {
        var wanted = (SelectedRow as MessageRow) ?? SelectedMessage;
        var id = wanted?.Id;
        var address = wanted?.Address;

        if (IsSearching) RunSearch();
        else LoadMessages(_selectedFolder);

        if (id is not { } was) return;
        if (Messages.FirstOrDefault(m => m.Id == was && m.Address == address) is not { } again) return;

        SelectedMessage = again;
        SelectedRow = again;
    }

    /// <summary>Flags the selection for follow-up, with an optional due date. The reference's flag menu.</summary>
    public void FlagForFollowUp(IReadOnlyList<MessageRow> rows, DateTimeOffset? due)
    {
        if (Split(rows, group => FlagForFollowUp(group, due))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var before = State(rows);
        mail.SetFollowUp([.. rows.Select(r => r.Id)], due);
        ReloadCurrentView();
        RefreshCounts();
        Undo.Push("Follow Up", () => RestoreFlags(before), () => FlagForFollowUp(rows, due));
        StatusRight = due is { } d
            ? $"{Describe(rows.Count)} flagged, due {d.LocalDateTime:ddd d MMM}."
            : $"{Describe(rows.Count)} flagged for follow-up.";
    }

    /// <summary>The store's row for a list row, for a dialog that shows its present values.</summary>
    public MessageSummary? SummaryOf(MessageRow row) => Mail([row])?.GetMessage(row.Id);

    /// <summary>The Custom flag dialog's whole flag: what it says, its dates, and its reminder.</summary>
    public void SetCustomFlag(IReadOnlyList<MessageRow> rows, CustomFlag flag)
    {
        if (Split(rows, group => SetCustomFlag(group, flag))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var before = State(rows);
        mail.SetCustomFollowUp([.. rows.Select(r => r.Id)], flag.Type, flag.Start, flag.Due, flag.Reminder);
        ReloadCurrentView();
        RefreshCounts();
        Undo.Push("Follow Up", () => RestoreFlags(before), () => SetCustomFlag(rows, flag));
        StatusRight = flag.Reminder is { } when
            ? $"{Describe(rows.Count)} flagged; reminder {SnoozeLabel(when)}."
            : flag.Due is { } d ? $"{Describe(rows.Count)} flagged, due {d.LocalDateTime:ddd d MMM}." : $"{Describe(rows.Count)} flagged.";
    }

    /// <summary>Marks the selection's follow-up complete: a check takes the flag's place.</summary>
    public void MarkFollowUpComplete(IReadOnlyList<MessageRow> rows)
    {
        if (Split(rows, group => MarkFollowUpComplete(group))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var before = State(rows);
        mail.CompleteFollowUp([.. rows.Select(r => r.Id)]);
        ReloadCurrentView();
        RefreshCounts();
        Undo.Push("Mark Complete", () => RestoreFlags(before), () => MarkFollowUpComplete(rows));
        StatusRight = $"{Describe(rows.Count)} marked complete.";
    }

    /// <summary>Clears the selection's follow-up flag entirely.</summary>
    public void ClearFollowUpFlag(IReadOnlyList<MessageRow> rows)
    {
        if (Split(rows, group => ClearFollowUpFlag(group))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var before = State(rows);
        mail.ClearFollowUp([.. rows.Select(r => r.Id)]);
        ReloadCurrentView();
        RefreshCounts();
        Undo.Push("Clear Flag", () => RestoreFlags(before), () => ClearFollowUpFlag(rows));
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
        if (Split(rows, group => MarkJunk(group))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var notJunk = CurrentFolderRole == FolderRole.Junk;
        var spam = !notJunk;

        // One press, two changes to the machine's state: the corpus learns from the message and
        // the message moves. Ctrl+Z has to take back both or it lies about what it did — so both
        // are collected into the one step this batch closes with.
        using var batch = Undo.Batch(notJunk ? "Not Junk" : "Junk");

        // The tokens rather than the messages: a step lives as long as twenty-four others, and a
        // mailbox full of attachments held in a closure is a different kind of defect.
        var trained = new List<IReadOnlyList<string>>(rows.Count);

        foreach (var row in rows)
        {
            if (mail.LoadRaw(row.Id) is not { } raw) continue;

            try
            {
                using var stream = new MemoryStream(raw);
                var message = MimeKit.MimeMessage.Load(stream);
                var tokens = JunkService.TokensOf(message);
                App.Junk.Train(mail, tokens, spam);
                trained.Add(tokens);
            }
            catch (Exception ex)
            {
                // A message that will not parse cannot be trained on, but it can still be moved.
                Log.Warn("Could not train the junk filter on a message.", ex);
            }
        }

        if (trained.Count > 0)
        {
            Undo.Push(
                notJunk ? "Not Junk" : "Junk",
                () => { foreach (var tokens in trained) App.Junk.Untrain(mail, tokens, spam); },
                () => { foreach (var tokens in trained) App.Junk.Train(mail, tokens, spam); });
        }

        MoveTo(rows, notJunk ? FolderRole.Inbox : FolderRole.Junk);
    }

    // ---- Snooze -----------------------------------------------------------------------

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
        if (Split(rows, group => Snooze(group, until))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var before = State(rows);

        mail.Snooze([.. rows.Select(r => r.Id)], until);
        RemoveRows(rows);
        RefreshCounts();

        // "Today" against the posed day, not the machine's: the line reads "snoozed until 8:00 AM"
        // only when the message comes back on the day the list is showing.
        var local = until.LocalDateTime;
        var today = Mailbox.Core.PosedClock.Now.LocalDateTime.Date;
        var when = local.Date == today ? local.ToString("h:mm tt") : local.ToString("ddd d MMM, h:mm tt");
        StatusRight = $"{Describe(rows.Count)} snoozed until {when}.";

        Undo.Push("Snooze", () => RestoreSnooze(before), () => Snooze(rows, until));
    }

    /// <summary>Brings the rows back now, unread and at the top of the folder.</summary>
    public void Unsnooze(IReadOnlyList<MessageRow> rows)
    {
        if (Split(rows, group => Unsnooze(group))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        var before = State(rows);

        mail.Unsnooze([.. rows.Select(r => r.Id)], DateTimeOffset.UtcNow);
        ReloadCurrentView();
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} back in the Inbox.";

        // Not a re-snooze: bringing a message back also marks it unread and moves its arrival to
        // now, and putting it back means putting all three columns back.
        Undo.Push("Unsnooze", () => RestoreSnooze(before), () => Unsnooze(rows));
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
    /// <summary>Who sent these, for a menu that offers to find the rest of their mail.</summary>
    public IReadOnlyList<string> SenderAddresses(IReadOnlyList<MessageRow> rows) => Senders(rows);

    /// <summary>
    /// Whether the account these rows came from has somewhere to archive to.
    /// </summary>
    /// <remarks>
    /// The reference's Archive… asks the first time and never again, so the menu has to know
    /// whether the asking is still owed.
    /// </remarks>
    public bool HasArchiveFolder(IReadOnlyList<MessageRow> rows)
        => AccountOf(rows) is { } owner
           && owner.Mail.FolderWithRole(owner.Account.Id, FolderRole.Archive) is not null;

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

    /// <summary>What a safe or blocked list literally holds, for comparing entries against.</summary>
    private static HashSet<string> Held(IEnumerable<string> entries)
        => new(entries, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Block Sender: the senders join the Blocked Senders list, and the messages go to Junk with
    /// the filter trained on them — the reference's menu entry does all three.
    /// </summary>
    public void BlockSenders(IReadOnlyList<MessageRow> rows)
    {
        if (Split(rows, group => BlockSenders(group))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        // One step for the whole press: the list write and the junking underneath it are one
        // thing the reader did, and MarkJunk's own batch joins this one rather than making a
        // second. The description is this command's, not "Junk", because that is the button.
        using var batch = Undo.Batch("Block Sender");

        var now = DateTimeOffset.UtcNow;
        var senders = Senders(rows);

        // What each list literally held, so the step puts both back exactly. Read as rows rather
        // than asked with IsBlockedSender, which answers for a wildcard entry too: a sender
        // already covered by a blocked domain still gets a row of their own here, and an undo
        // that skipped it would leave one behind.
        var (blocked, safe) = (Held(mail.BlockedSenders()), Held(mail.SafeSenders()));
        var newlyBlocked = senders.Where(s => !blocked.Contains(s)).ToList();
        var noLongerSafe = senders.Where(safe.Contains).ToList();

        foreach (var sender in senders)
        {
            mail.AddBlockedSender(sender, now);
            mail.RemoveSafeSender(sender);
        }

        if (newlyBlocked.Count > 0 || noLongerSafe.Count > 0)
        {
            Undo.Push(
                "Block Sender",
                () =>
                {
                    foreach (var sender in newlyBlocked) mail.RemoveBlockedSender(sender);
                    foreach (var sender in noLongerSafe) mail.AddSafeSender(sender, now);
                },
                () =>
                {
                    foreach (var sender in senders)
                    {
                        mail.AddBlockedSender(sender, now);
                        mail.RemoveSafeSender(sender);
                    }
                });
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
        if (Split(rows, group => NeverBlockSenders(group, domain))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        using var batch = Undo.Batch(domain ? "Never Block Sender's Domain" : "Never Block Sender");

        var now = DateTimeOffset.UtcNow;
        var entries = domain
            ? SenderDomains(rows).Select(d => "@" + d).ToList()
            : Senders(rows);

        var (safe, blocked) = (Held(mail.SafeSenders()), Held(mail.BlockedSenders()));
        var newlySafe = entries.Where(e => !safe.Contains(e)).ToList();
        var noLongerBlocked = entries.Where(blocked.Contains).ToList();

        foreach (var entry in entries)
        {
            mail.AddSafeSender(entry, now);
            mail.RemoveBlockedSender(entry);
        }

        if (newlySafe.Count > 0 || noLongerBlocked.Count > 0)
        {
            Undo.Push(
                domain ? "Never Block Sender's Domain" : "Never Block Sender",
                () =>
                {
                    foreach (var entry in newlySafe) mail.RemoveSafeSender(entry);
                    foreach (var entry in noLongerBlocked) mail.AddBlockedSender(entry, now);
                },
                () =>
                {
                    foreach (var entry in entries)
                    {
                        mail.AddSafeSender(entry, now);
                        mail.RemoveBlockedSender(entry);
                    }
                });
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
        if (Split(rows, group => NeverBlockRecipients(group))) return;

        if (rows.Count == 0 || Mail(rows) is not { } mail) return;

        using var batch = Undo.Batch("Never Block this Group");

        var now = DateTimeOffset.UtcNow;
        var own = new HashSet<string>(
            _accounts?.All.Select(a => a.Account.Address.ToLowerInvariant()) ?? [],
            StringComparer.OrdinalIgnoreCase);
        var already = Held(mail.SafeRecipients());
        var added = new List<string>();
        var newlySafe = new List<string>();

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
                if (!already.Contains(address) && !newlySafe.Contains(address)) newlySafe.Add(address);
            }
        }

        if (newlySafe.Count > 0)
        {
            Undo.Push(
                "Never Block this Group",
                () => { foreach (var address in newlySafe) mail.RemoveSafeRecipient(address); },
                () => { foreach (var address in newlySafe) mail.AddSafeRecipient(address, now); });
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
    /// The store the rows belong to. Null while no account exists, which is what keeps the
    /// commands from pretending to act on mail that is not really there.
    /// </summary>
    private MailRepository? Mail(IReadOnlyList<MessageRow> rows)
        => rows.Count == 0 ? null : AccountOf(rows)?.Mail;

    /// <summary>
    /// The account a row belongs to: its own where it says, the folder's otherwise.
    /// </summary>
    /// <remarks>
    /// A row only carries an address in a view that draws more than one account — the unified
    /// folders. Everywhere else the folder on screen is the answer and always was, which is why
    /// an empty address falls back to it rather than being treated as a fault.
    /// </remarks>
    private OpenAccount? AccountFor(MessageRow row)
        => row.Address.Length > 0
            ? _accounts?.All.FirstOrDefault(
                a => string.Equals(a.Account.Address, row.Address, StringComparison.OrdinalIgnoreCase))
              ?? CurrentAccount
            : CurrentAccount;

    /// <summary>The one account a set of rows belongs to, or the first where they disagree.</summary>
    private OpenAccount? AccountOf(IReadOnlyList<MessageRow> rows)
        => rows.Count == 0 ? CurrentAccount : AccountFor(rows[0]);

    /// <summary>
    /// Splits a selection that spans accounts, running the command once per account.
    /// </summary>
    /// <remarks>
    /// One line at the top of each command that acts on rows, and it is what makes the unified
    /// folders safe: without it, deleting a mixed selection would take the first row's account and
    /// hand it every id, and ids from another store would land on whatever those numbers happen to
    /// mean there. Deleting the wrong mail is the worst thing this application could do.
    /// <para>
    /// The command's own body is left exactly as it was, and runs unchanged for the ordinary case
    /// of one account — which is every selection outside a unified folder. Each group is one
    /// account, so the recursion is one deep.
    /// </para>
    /// </remarks>
    private bool Split(IReadOnlyList<MessageRow> rows, Action<IReadOnlyList<MessageRow>> command)
    {
        if (rows.Count < 2) return false;

        var groups = rows
            .GroupBy(r => AccountFor(r)?.Account.Address ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count < 2) return false;

        // One press, one step. Each group runs the command again and each records itself, so
        // without this a selection spanning three accounts left three steps on the stack: Ctrl+Z
        // took back one account's share and the rest stayed done, which is exactly the hole the
        // undo contract above says is worse than no undo at all. The batch is unnamed, so the
        // step keeps the command's own description rather than a word invented here.
        using var batch = Undo.Batch(string.Empty);

        foreach (var group in groups) command([.. group]);

        // And the line at the bottom. Every one of these commands opens its status with
        // Describe(count) — "1 message …", "8 messages …" — and the last group's is the one left
        // on the bar, so deleting fourteen messages across three accounts reported four. Rewritten
        // only where the line really does start with the last group's own count, so a command that
        // words its status some other way is left alone.
        var last = Describe(groups[^1].Count());
        if (StatusRight.StartsWith(last, StringComparison.Ordinal))
        {
            StatusRight = Describe(rows.Count) + StatusRight[last.Length..];
        }

        return true;
    }

    /// <summary>
    /// The store behind what is on screen, for the reading pane.
    /// </summary>
    /// <remarks>
    /// Null while no account exists, which is what tells the pane there is no MIME to go
    /// looking for.
    /// </remarks>
    public MailRepository? CurrentMail => HasAccount ? CurrentAccount?.Mail : null;

    /// <summary>The selected message as it arrived, or null when there is no such thing.</summary>
    public byte[]? SelectedRaw => SelectedMessage is { } row ? CurrentMail?.LoadRaw(row.Id) : null;

    /// <summary>
    /// One row's bytes as they arrived, from that row's own store.
    /// </summary>
    /// <remarks>
    /// Through <see cref="AccountFor"/> rather than <see cref="CurrentMail"/> for the reason
    /// every row command goes through <c>Split</c>: in a unified folder the row's id means
    /// nothing without the account it was numbered in, and reading the wrong store would hand
    /// back somebody else's message under this one's name.
    /// </remarks>
    public byte[]? RawOf(MessageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return AccountFor(row)?.Mail.LoadRaw(row.Id);
    }

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
    /// never ends up showing nothing after a delete. Options › Mail's "After moving or deleting
    /// an open item" turns that preference around: "open the previous item" prefers the row
    /// above. Either way the other direction is the fallback rather than an empty list.
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
            var below = visible.Skip(last + 1).FirstOrDefault(r => !removed.Contains(r));
            var above = visible.Take(Math.Max(0, last)).LastOrDefault(r => !removed.Contains(r));
            next = App.MailOptions.AfterOpenItem == AfterOpenItem.PreviousItem
                ? above ?? below
                : below ?? above;
        }

        foreach (var row in rows) Messages.Remove(row);
        Rebuild();

        if (wasSelected)
        {
            SelectedRow = next;
            SelectedMessage = next;
        }
    }

    /// <summary>
    /// Takes one row out of the list after an open window changed its store row — moved it,
    /// mostly. The deed is already done; the list just has to stop showing the row, without the
    /// full reload that would throw the selection away.
    /// </summary>
    public void DropRow(MessageRow row)
    {
        if (!Messages.Contains(row)) return;
        RemoveRows([row]);
        RefreshCounts();
    }

    /// <summary>Rows on show, headers excluded. What the status bar counts.</summary>
    public int VisibleCount => VisibleRows.Count(
        r => r is MessageRow { Depth: 0 } or ConversationRow);

    /// <summary>
    /// The folder's own store counts, kept while the list holds only the newest page of it —
    /// null whenever the list holds the whole folder, or holds something else entirely.
    /// </summary>
    private (int Total, int Unread)? _folderBeyondList;

    /// <summary>Empties the list, and with it the beyond-the-page note that belonged to it.</summary>
    private void ClearList()
    {
        Messages.Clear();
        _folderBeyondList = null;
    }

    // ---- Pane layout ----------------------------------------------------------------------

    /// <summary>The folder pane is minimized to its strip, by the reader or by the width.</summary>
    public bool NavCollapsed
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            RaisePaneLayout();
        }
    }

    /// <summary>
    /// How wide the window is, as the shell last saw it. What sheds below which width is
    /// <see cref="PaneShedding"/>'s to say.
    /// </summary>
    public double ShellWidth
    {
        get;
        set
        {
            // Nothing is not narrow. A window reports a width of zero until it has been laid out,
            // and reading that as "too narrow for the folder pane" minimised the pane for the
            // instant before the real width arrived — long enough for the list to be fitted
            // against a 48-wide nav column and keep the room that gave it, which left the reading
            // pane at half its own floor for the rest of the run.
            if (value <= 0 || double.IsNaN(value)) return;

            if (Math.Abs(field - value) < 0.5) return;
            field = value;

            // A reader who asked for the pane back at a narrow width keeps it until they change
            // the window again; resizing is what puts the width back in charge.
            _navExpandedAtWidth = false;
            RaisePaneLayout();
        }
    } = double.PositiveInfinity;

    /// <summary>
    /// Set when the reader expands the pane at a width that had minimized it for them. Their ask
    /// wins over the width until the window is resized, because a chevron that does nothing when
    /// pressed is worse than a folder pane that is wide for the window it is in.
    /// </summary>
    private bool _navExpandedAtWidth;

    /// <summary>The folder pane at its full width \u2014 neither minimised nor crowded out.</summary>
    public bool NavVisible => !NavCollapsed
        && (_navExpandedAtWidth || !PaneShedding.MinimizesFolderPane(ShellWidth));

    /// <summary>
    /// The strip the pane becomes. Minimized is not hidden: the reference keeps the favorites
    /// on their sides and the chevron that brings the pane back, and a pane that vanished with no
    /// way back is what this used to be.
    /// </summary>
    public bool NavStripVisible => !NavVisible;

    /// <summary>
    /// The reading pane as drawn, which is the reader's setting until the window is too narrow to
    /// hold one. Kept apart from <see cref="ReadingPaneVisible"/> so that widening the window
    /// gives back the pane the reader asked for rather than one they have to ask for again.
    /// </summary>
    public bool ReadingPaneShown =>
        ReadingPaneVisible && !PaneShedding.HidesReadingPane(ShellWidth);

    /// <summary>
    /// The chevron, from either side. Minimizing is the reader's setting; expanding a pane the
    /// width minimized is an override that lasts until the window is resized.
    /// </summary>
    private void ToggleFolderPane()
    {
        if (NavVisible)
        {
            NavCollapsed = true;
            return;
        }

        NavCollapsed = false;
        _navExpandedAtWidth = true;
        RaisePaneLayout();
    }

    private void RaisePaneLayout()
    {
        Raise(nameof(NavVisible));
        Raise(nameof(NavStripVisible));
        Raise(nameof(ReadingPaneShown));
        Raise(nameof(CollapseGlyph));
    }

    public string CollapseGlyph => NavVisible ? "\u2039" : "\u203A";

    /// <summary>
    /// The favorites, drawn on their sides down the minimized pane. The same rows the pane's
    /// own Favorites section holds, so picking one here and picking one there are one thing.
    /// </summary>
    public IEnumerable<FolderNode> CollapsedFolders =>
        Folders.Where(f => f.Kind == FolderNodeKind.Favourite);

    /// <summary>
    /// Opens a folder named on the minimized strip. The strip is not a selection control of its
    /// own — the pane's list still holds the selection — so this sets the same property the list
    /// binds, and the two agree by construction.
    /// </summary>
    public RelayCommand<FolderNode> SelectFolderRow { get; }

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
    /// <summary>True when the list is currently sorted by this column's field.</summary>
    /// <remarks>
    /// Asked by the header strip so the sorted column can be marked. It reads the same table
    /// <see cref="SortBy"/> writes through, because a header that marked a different column from
    /// the one a press would act on is worse than no mark at all.
    /// </remarks>
    public bool SortsBy(string column) => Wanted(column) == Arrangement;

    public void SortBy(string column)
    {
        if (Wanted(column) is not { } arrangement) return;

        // Pressing the column the list is already sorted by reverses it, which is what every
        // list with sortable headers does and what the reference's own arrow implies.
        if (Arrangement == arrangement) SortDescending = !SortDescending;
        else { Arrangement = arrangement; SortDescending = true; }
    }

    /// <summary>The arrangement a column sorts by, or null for one that does not sort.</summary>
    private static Arrangement? Wanted(string column)
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

        return wanted;
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

            // Kept, like the pane placement above it: a reader who chose a size did not choose
            // it for one run. Clamped before it is written, so a hand-edited settings file
            // cannot put the pane somewhere the slider can never bring it back from.
            App.Settings.Set(OptionsPages.Keys.ZoomPercent, field);
            Raise(nameof(ZoomLabel));
            Raise(nameof(ReadingFontSize));
        }
    } = Math.Clamp(App.Settings.GetNumber(OptionsPages.Keys.ZoomPercent, 100), 50, 200);

    public string ZoomLabel => $"{ZoomPercent:0}%";

    /// <summary>
    /// The signed-in address — the default account's, or the first one open.
    /// </summary>
    /// <remarks>
    /// It was the literal "you@example.com" for as long as there was nothing to read, and the
    /// avatar, its tooltip, its initials and the account flyout all draw from here — so a reader
    /// with three accounts open still saw the placeholder in the title bar. One source for the
    /// four, which is what made replacing it one edit.
    /// <para>
    /// Read once, at construction: the shell is rebuilt when accounts change, and a disc that
    /// re-read itself mid-session would be answering a question nobody asked.
    /// </para>
    /// </remarks>
    public string AccountAddress { get; } =
        App.Accounts?.Default?.Account.Address is { Length: > 0 } signedIn ? signedIn : string.Empty;

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

    /// <summary>
    /// The left of the status bar: the mail counts while the message list is what is showing,
    /// and otherwise whatever the module put there.
    /// </summary>
    /// <remarks>
    /// Today is the case that needed saying. It is a page inside Mail, so asking the module
    /// alone answered with the mail counts and the bar read "Items: 0   Unread: 0" over a page
    /// listing eight — while <see cref="TodayWorkspace"/> computed a status, assigned it, and
    /// could never reach here. <see cref="IsTodayShowing"/> is the one bit of state that tells
    /// the two apart.
    /// </remarks>
    /// <remarks>
    /// A folder larger than the page the list loads is counted from the store, as the folder
    /// pane beside it counts — the bar reporting the page's size as the folder's was how 4,500
    /// messages went missing without a word. A filtered or conversation-arranged list still
    /// counts what it shows, which is what the reference's bar does.
    /// </remarks>
    /// <summary>
    /// The bar's two counts, in the interface's language and its own digits.
    /// </summary>
    /// <remarks>
    /// The labels are the reference's own words, and they are labels rather than sentences —
    /// "Items" does not become "Item" for one. The numbers are grouped for the culture, which is
    /// what makes 4,500 read as a count rather than as an identifier.
    /// </remarks>
    private static string Counts(int total, int unread)
        => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Strings.T("Items: {0}   Unread: {1}"),
            total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            unread.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

    public string StatusLeft => Module == MailboxModule.Mail && !IsTodayShowing
        ? _folderBeyondList is { } beyond && Filter == ListFilter.None
            ? Counts(beyond.Total, beyond.Unread)
            : Counts(VisibleCount, Messages.Count(m => m.IsUnread))
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
            Raise(nameof(IsFeedsModule));
            Raise(nameof(ShowsWorkspace));
            Raise(nameof(StatusLeft));
            Raise(nameof(ShowReadingPaneToggles));
        }
    } = MailboxModule.Mail;

    public bool IsMailModule => Module == MailboxModule.Mail;

    public bool IsCalendarModule => Module == MailboxModule.Calendar;

    public bool IsFeedsModule => Module == MailboxModule.Feeds;

    /// <summary>
    /// The status bar's two layout buttons belong to the message list, so they go with it: the
    /// reference shows the calendar's own pair there instead, and an inert Normal/Reading pair
    /// over a calendar would be two buttons that do nothing.
    /// </summary>
    public bool ShowReadingPaneToggles => Module == MailboxModule.Mail;

    /// <summary>
    /// Empty at rest — the reference's status bar carries the counts on the left and the view
    /// and zoom controls on the right, with nothing between them. Transient messages land here
    /// and the connection state will once something reports it.
    /// </summary>
    public string StatusRight
    {
        get;
        set { if (Set(ref field, value)) Raise(); }
    } = string.Empty;

    /// <summary>
    /// True while a send/receive is running, which is what puts the bar in the status bar.
    /// </summary>
    /// <remarks>
    /// The reference shows "Send/Receive" and a progress bar at the right of the status bar for
    /// exactly as long as the transfer takes, and nothing there at rest. It is the only indication
    /// a reader gets when the progress dialog has been told not to appear, and without it a
    /// send/receive that is quietly failing looks the same as one that never started.
    /// </remarks>
    public bool IsTransferring
    {
        get;
        set { if (Set(ref field, value)) Raise(); }
    }

    /// <summary>How far the running send/receive has got, 0 to 1.</summary>
    public double TransferProgress
    {
        get;
        set { if (Set(ref field, value)) Raise(); }
    }

    /// <summary>What the bar's tooltip says: which account is being worked on.</summary>
    public string TransferTip
    {
        get;
        set { if (Set(ref field, value)) Raise(); }
    } = "Send/Receive";
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

    /// <summary>
    /// The arrow drawn after the title when the list is sorted by this column, empty otherwise.
    /// </summary>
    /// <remarks>
    /// The reference marks the sorted column and only the sorted column — its own capture shows
    /// "Received ▼" and nothing on the rest. Without it a reader pressing a header sees the rows
    /// move and has nothing to say which column they moved for, or which way round it is now.
    /// </remarks>
    public string SortMark { get; set; } = string.Empty;

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

/// <summary>
/// The same, for a button in a template that acts on the row it was drawn for.
/// </summary>
/// <remarks>
/// A row of the wrong kind is ignored rather than thrown at: the parameter comes from a binding,
/// and a binding that has not resolved yet hands over null on the way to handing over the row.
/// </remarks>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null)
    : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value) execute(value);
    }

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
