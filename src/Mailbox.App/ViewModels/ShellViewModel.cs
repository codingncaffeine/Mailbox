using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Mailbox.App.Options;
using Mailbox.Core;
using Mailbox.Core.Accounts;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Store;
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

public sealed class FolderNode(string name, int depth, int unread, bool bold = false)
{
    public string Name { get; } = name;
    public int Unread { get; } = unread;
    public Thickness IndentMargin { get; } = new(depth * 14, 0, 0, 0);
    public FontWeight Weight { get; } = bold || unread > 0 ? FontWeight.SemiBold : FontWeight.Normal;
    public string UnreadDisplay { get; } = unread > 0 ? unread.ToString() : string.Empty;
    public override string ToString() => Name;
}

/// <summary>
/// One module entry. Classic renders these as a horizontal strip at the foot of the folder
/// pane; Modern renders the same collection vertically in the left app rail.
/// </summary>
public sealed class ModuleTab(MailboxModule module, string icon, bool isActive)
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
    public bool IsActive { get; } = isActive;

    public string StyleClass { get; } = isActive ? "module active" : "module";
}

/// <summary>One command on the Quick Access Toolbar in the title bar.</summary>
public sealed class QuickAccessButton(MailboxCommand command)
{
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

public sealed class MessageRow(
    string from,
    string subject,
    string preview,
    string received,
    bool isUnread,
    string toLine,
    string body)
{
    public string From { get; } = from;
    public string Subject { get; } = subject;
    public string Preview { get; } = preview;
    public string Received { get; } = received;
    public bool IsUnread { get; } = isUnread;
    public string ToLine { get; } = toLine;
    public string Body { get; } = body;

    public FontWeight SenderWeight => IsUnread ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight SubjectWeight => IsUnread ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>
/// Phase 0 shell state. Backed by sample data — the point of this phase is that the chrome
/// passes a squint test against real the reference application, not that it moves mail. Phases 2 onward replace
/// each collection with the real store.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly ThemeService _themes;
    private FolderNode? _selectedFolder;
    private MessageRow? _selectedMessage;
    private string _selectedTheme;
    private string _searchText = string.Empty;

    private readonly AccountStores? _accounts;

    public ShellViewModel(
        ThemeService themes,
        CommandCatalog catalog,
        RibbonLayout layout,
        ShellLayoutMode layoutMode,
        AccountStores? accounts = null)
    {
        _accounts = accounts;
        _themes = themes;
        _selectedTheme = OfficeThemes.DisplayName(themes.ThemeId);
        LayoutMode = layoutMode;

        Themes = new ObservableCollection<string>(
            OfficeThemes.All.Select(OfficeThemes.DisplayName));

        QuickAccess = new ObservableCollection<QuickAccessButton>(
            layout.QuickAccess
                .Where(id => catalog.TryGet(id, out _))
                .Select(id => new QuickAccessButton(catalog.Get(id))));

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

        Messages =
        [
            new MessageRow(
                "Alice Chen", "Re: Q3 numbers",
                "Thanks for pulling those together — the variance on line 14 is the one I'd want to talk through before Thursday.",
                "9:41 AM", true, "To: you@example.com",
                "Thanks for pulling those together.\n\nThe variance on line 14 is the one I'd want to " +
                "talk through before Thursday. Everything else reconciles against what finance sent " +
                "over last week.\n\nAlice"),
            new MessageRow(
                "Build Notifications", "mailbox/main — build passed",
                "Commit 4f2a1c9 built successfully on linux-x64. 0 warnings, 0 errors.",
                "9:12 AM", true, "To: you@example.com",
                "Commit 4f2a1c9 built successfully on linux-x64.\n\n0 warnings, 0 errors.\nElapsed 00:00:04.62"),
            new MessageRow(
                "Dana Whitfield", "Lunch Thursday?",
                "There's a new place near the office that does a decent laksa. Around 12:30?",
                "8:55 AM", false, "To: you@example.com; Sam Reyes",
                "There's a new place near the office that does a decent laksa.\n\nAround 12:30?\n\nD"),
            new MessageRow(
                "Sam Reyes", "Draft agenda attached",
                "Rough cut for Monday. Shout if there's anything you want added before I send it round.",
                "Yesterday", false, "To: you@example.com",
                "Rough cut for Monday.\n\nShout if there's anything you want added before I send it round."),
            new MessageRow(
                "Fastmail", "Your account statement is ready",
                "Your monthly statement for August is available to download.",
                "Yesterday", false, "To: you@example.com",
                "Your monthly statement for August is available to download."),
            new MessageRow(
                "Priya Raman", "Re: Font substitution question",
                "Confirmed — Carlito is metric-compatible with Calibri, so the layout holds either way.",
                "Mon 11:02", false, "To: you@example.com",
                "Confirmed — Carlito is metric-compatible with Calibri, so the layout holds either way.\n\n" +
                "Worth noting DejaVu is *not* metric-compatible with Verdana, whatever the internet says."),
        ];

        _selectedFolder = Folders[5];
        // With an account configured the shell shows that account. Without one it shows the
        // sample above, which is what makes an unconfigured Mailbox worth looking at — and is
        // replaced the moment there is real mail rather than mixed with it.
        if (LoadFromStore()) HasAccount = true;

        _selectedMessage = Messages.FirstOrDefault();

        foreach (var column in Columns)
        {
            var title = column.Title;
            column.Sort = new RelayCommand(() => SortBy(title));
        }

        ShowAll = new RelayCommand(() => UnreadOnly = false);
        ShowUnread = new RelayCommand(() => UnreadOnly = true);
        ToggleGroup = new RelayCommand(() => GroupCollapsed = !GroupCollapsed);
        ToggleNav = new RelayCommand(() => NavCollapsed = !NavCollapsed);
        ToggleSortDirection = new RelayCommand(() => SortDescending = !SortDescending);
        ShowReadingPane = new RelayCommand(() => ReadingPaneVisible = true);
        HideReadingPane = new RelayCommand(() => ReadingPaneVisible = false);
        ZoomIn = new RelayCommand(() => ZoomPercent += 10);
        ZoomOut = new RelayCommand(() => ZoomPercent -= 10);
    }

    public ObservableCollection<string> Themes { get; }
    public ObservableCollection<QuickAccessButton> QuickAccess { get; }
    public ObservableCollection<QuickAccessButton> ReadingPaneActions { get; }

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
    /// True once the calendar peek has been docked to the right edge, where it takes the
    /// reading pane's place until closed.
    /// </summary>
    public bool IsCalendarDocked
    {
        get;
        set { if (Set(ref field, value)) Raise(); }
    }
    public ObservableCollection<FolderNode> Folders { get; }
    public ObservableCollection<MessageRow> Messages { get; }

    /// <summary>True once an account exists. Until then the shell is showing the sample.</summary>
    public bool HasAccount { get; private set; }

    public bool ShowSampleNotice => !HasAccount;

    /// <summary>
    /// Which account and folder each row stands for. Every account has its own store, so a
    /// folder id alone is not enough to find its mail.
    /// </summary>
    private readonly Dictionary<FolderNode, (OpenAccount Account, long FolderId)> _folderIds = [];

    /// <summary>
    /// Replaces the sample with what the store holds. Returns false when there is no account,
    /// which leaves the sample in place.
    /// </summary>
    private bool LoadFromStore()
    {
        if (_accounts is null) return false;

        var accounts = _accounts.All;
        if (accounts.Count == 0) return false;

        Folders.Clear();
        _folderIds.Clear();

        foreach (var account in accounts)
        {
            Folders.Add(new FolderNode(account.Account.Address, 0, 0, bold: true));

            foreach (var folder in account.Mail.Folders(account.Account.Id))
            {
                var node = new FolderNode(folder.Name, 1, folder.Unread);
                _folderIds[node] = (account, folder.Id);
                Folders.Add(node);
            }
        }

        SelectedFolder = Folders.FirstOrDefault(f => _folderIds.ContainsKey(f));
        return true;
    }

    /// <summary>Loads a folder's mail into the list. Called when the selection changes.</summary>
    private void LoadMessages(FolderNode? folder)
    {
        if (_accounts is null || folder is null
            || !_folderIds.TryGetValue(folder, out var where)) return;

        Messages.Clear();
        foreach (var summary in where.Account.Mail.Messages(where.FolderId))
        {
            Messages.Add(new MessageRow(
                summary.DisplayFrom,
                summary.Subject,
                summary.Preview,
                Received(summary.Received),
                !summary.IsRead,
                $"To: {where.Account.Account.Address}",
                summary.Preview));
        }

        Raise(nameof(VisibleMessages));
        Raise(nameof(StatusLeft));
        SelectedMessage = Messages.FirstOrDefault();
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

        var selected = SelectedFolder;
        LoadFromStore();
        SelectedFolder = selected is null
            ? Folders.FirstOrDefault(f => _folderIds.ContainsKey(f))
            : Folders.FirstOrDefault(f => f.Name == selected.Name) ?? SelectedFolder;
    }

    /// <summary>The reference application puts search above the message list, not in the title bar.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) Raise(nameof(ShowSearchPlaceholder));
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

            var id = OfficeThemes.All.FirstOrDefault(
                t => OfficeThemes.DisplayName(t) == value) ?? OfficeThemes.Colorful;
            _themes.Apply(id);
        }
    }

    public FolderNode? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!Set(ref _selectedFolder, value)) return;
            Raise(nameof(SelectedFolderName));
            LoadMessages(_selectedFolder);
            Raise(nameof(SearchPlaceholder));
            Raise(nameof(WindowTitle));
            Raise(nameof(StatusLeft));
        }
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

    /// <summary>All, or only what has not been read.</summary>
    public bool UnreadOnly
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(VisibleMessages));
            Raise(nameof(AllFilterWeight));
            Raise(nameof(UnreadFilterWeight));
        }
    }

    public FontWeight AllFilterWeight => UnreadOnly ? FontWeight.Normal : FontWeight.SemiBold;
    public FontWeight UnreadFilterWeight => UnreadOnly ? FontWeight.SemiBold : FontWeight.Normal;

    /// <summary>Which column orders the list. Empty means the arrangement's own order.</summary>
    public string SortColumn
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(VisibleMessages)); }
    } = string.Empty;

    public bool SortDescending
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(VisibleMessages));
            Raise(nameof(SortGlyph));
        }
    } = true;

    /// <summary>The arrow beside the arrangement label.</summary>
    public string SortGlyph => SortDescending ? "\u2193" : "\u2191";

    /// <summary>Collapsing the group hides its rows, as clicking the header does.</summary>
    public bool GroupCollapsed
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(VisibleMessages));
            Raise(nameof(GroupGlyph));
        }
    }

    public string GroupGlyph => GroupCollapsed ? "\u203A" : "\u2304";

    /// <summary>
    /// What the list actually shows. Recomputed rather than mutated so every control that
    /// shapes it — filter, sort, group — goes through one path and cannot disagree.
    /// </summary>
    public IEnumerable<MessageRow> VisibleMessages
    {
        get
        {
            if (GroupCollapsed) return [];

            var rows = UnreadOnly ? Messages.Where(m => m.IsUnread) : Messages;

            return SortColumn switch
            {
                "From" => Ordered(rows, m => m.From),
                "Subject" => Ordered(rows, m => m.Subject),
                "Received" => Ordered(rows, m => m.Received),
                _ => rows,
            };
        }
    }

    private IEnumerable<MessageRow> Ordered<T>(IEnumerable<MessageRow> rows,
        Func<MessageRow, T> key)
        => SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    /// <summary>Number of rows on show, which is what the status bar counts.</summary>
    public int VisibleCount => VisibleMessages.Count();

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

    /// <summary>Reading pane on the right, or off entirely — the two the status bar offers.</summary>
    public bool ReadingPaneVisible
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            App.Settings.Set(OptionsPages.Keys.ShowReadingPane, value);
        }
    } = App.Settings.GetBool(OptionsPages.Keys.ShowReadingPane, true);

    /// <summary>Zoom applies to the reading pane's body, which is what it scales.</summary>
    public double ReadingFontSize => 14.5 * (ZoomPercent / 100d);

    // ---- Commands for the controls that shape the view -------------------------------------
    // Built here rather than in the window so the state and the way it is changed sit together;
    // the window only wires the things that need a Window to act on.

    public RelayCommand ShowAll { get; }
    public RelayCommand ShowUnread { get; }
    public RelayCommand ToggleGroup { get; }
    public RelayCommand ToggleNav { get; }
    public RelayCommand ToggleSortDirection { get; }
    public RelayCommand ShowReadingPane { get; }
    public RelayCommand HideReadingPane { get; }
    public RelayCommand ZoomIn { get; }
    public RelayCommand ZoomOut { get; }

    /// <summary>Sorts by a column, flipping direction when it is already the sort column.</summary>
    public void SortBy(string column)
    {
        if (string.IsNullOrEmpty(column)) return;

        if (string.Equals(SortColumn, column, StringComparison.Ordinal)) SortDescending = !SortDescending;
        else { SortColumn = column; SortDescending = true; }
    }

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

    public string ArrangementLabel => "By Date";

    /// <summary>
    /// Message-list columns in the reference's own order. The first four are icon-only glyph
    /// columns — importance, reminder, item type and attachment.
    /// </summary>
    public MessageColumn[] Columns { get; } =
    [
        new("!", 18, isGlyph: true), new("\u2302", 18, isGlyph: true),
        new("\u25A4", 18, isGlyph: true), new("\u25EF", 18, isGlyph: true),
        new("From", 150), new("Subject", 300), new("Received", 100),
        new("Size", 55), new("Categories", 90), new("Mention", 70),
    ];

    public string StatusLeft =>
        $"Items: {VisibleCount}   Unread: {Messages.Count(m => m.IsUnread)}";

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
public sealed class MessageColumn(string title, double width, bool isGlyph = false)
{
    public string Title { get; } = title;
    public double Width { get; } = width;

    /// <summary>Sorts the list by this column. Set by the shell, which owns the ordering.</summary>
    public System.Windows.Input.ICommand? Sort { get; set; }

    public string SortTip { get; } = isGlyph ? string.Empty : $"Sort by {title}";

    /// <summary>Icon-only columns render centred and unlabelled — importance, flag, attachment.</summary>
    public bool IsGlyph { get; } = isGlyph;
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
