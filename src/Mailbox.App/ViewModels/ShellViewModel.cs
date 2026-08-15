using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Mailbox.App.Options;
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
        }
    } = isUnread;

    public bool IsFlagged
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(FlagGlyph)); }
    }

    /// <summary>How the row writes its date: a time today, a weekday this week, else the date.</summary>
    public string ReceivedLabel => ShellViewModel.Received(Received);

    public string FlagGlyph => IsFlagged ? "\u2691" : string.Empty;

    public FontWeight SenderWeight => IsUnread ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight SubjectWeight => IsUnread ? FontWeight.SemiBold : FontWeight.Normal;
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
public sealed class ShellViewModel : ObservableObject
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
        _selectedTheme = OfficeThemes.DisplayName(themes.ThemeId);
        LayoutMode = layoutMode;

        Themes = new ObservableCollection<string>(
            OfficeThemes.All.Select(OfficeThemes.DisplayName));

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

        foreach (var column in Columns)
        {
            var title = column.Title;
            column.Sort = new RelayCommand(() => SortBy(title));
        }

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
    private readonly Dictionary<FolderNode, (OpenAccount Account, long FolderId, FolderRole Role)> _folderIds = [];

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
        }

        SelectedFolder = Folders.FirstOrDefault(f => _folderIds.ContainsKey(f));
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
        if (_accounts is null || folder is null
            || !_folderIds.TryGetValue(folder, out var where)) return;

        Messages.Clear();

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

        foreach (var summary in where.Account.Mail.Messages(where.FolderId))
        {
            Messages.Add(new MessageRow(
                summary.Id,
                summary.DisplayFrom,
                summary.Subject,
                summary.Preview,
                summary.Received,
                !summary.IsRead,
                $"To: {where.Account.Account.Address}",
                summary.Preview)
            {
                SizeBytes = summary.SizeBytes,
                HasAttachment = summary.HasAttachment,
                IsFlagged = summary.IsFlagged,
                ThreadKey = Store.Lists.Arrangements.NormalisedSubject(summary.Subject),
                FolderId = summary.FolderId,
            });
        }

        // One query for the page's categories rather than one per row.
        var categories = where.Account.Mail.CategoriesFor([.. Messages.Select(m => m.Id)]);
        foreach (var row in Messages)
        {
            if (categories.TryGetValue(row.Id, out var assigned))
            {
                row.CategoryTokens = [.. assigned.Select(c => c.ColourToken)];
            }
        }

        Rebuild();
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
    private void RunSearch()
    {
        if (_accounts is null) return;

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
        }
    }

    /// <summary>
    /// What the list has selected, which may be a group header. Headers are not messages, so
    /// selecting one leaves the reading pane alone rather than blanking it.
    /// </summary>
    public object? SelectedRow
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            if (value is MessageRow row) SelectedMessage = row;
            if (value is GroupHeaderRow header) ToggleGroupCollapsed(header.Header);
            if (value is ConversationRow thread)
            {
                SelectedMessage = thread.Newest;
                ToggleConversation(thread);
            }
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
            Rebuild();
            Raise(nameof(AllFilterWeight));
            Raise(nameof(UnreadFilterWeight));
        }
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
        }
    } = 1;

    public bool ShowPreviewLine => CompactRows && PreviewLines > 0;

    /// <summary>
    /// Row height follows the mode and the preview count, so a taller row is a taller row
    /// rather than a clipped one.
    /// </summary>
    public double RowHeight => CompactRows ? 26 + (PreviewLines * 18) : 24;

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
        var built = new List<object>();

        var rows = UnreadOnly ? Messages.Where(m => m.IsUnread) : Messages;
        var groups = Store.Lists.Arrangements.Group(rows, Arrangement, SortDescending);

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
    private static MessageRow Reset(MessageRow row)
    {
        row.Depth = 0;
        return row;
    }

    /// <summary>What a group header counts: conversations and loose messages, not both.</summary>
    private static int Countable(List<object> rows)
        => rows.Count(r => r is ConversationRow || (r is MessageRow m && m.Depth == 0));

    // ---- Acting on a selection -------------------------------------------------------------
    // Every one of these takes the rows explicitly rather than reading a selection property.
    // The list owns the selection, and a command that reaches back for it can act on something
    // other than what the user had highlighted when they pressed the key.

    /// <summary>Marks rows read or unread, in the store and on screen.</summary>
    public void SetRead(IReadOnlyList<MessageRow> rows, bool read)
    {
        if (rows.Count == 0) return;

        Mail(rows)?.SetRead([.. rows.Select(r => r.Id)], read);
        foreach (var row in rows) row.IsUnread = !read;

        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} marked {(read ? "read" : "unread")}.";
    }

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
    public IReadOnlyList<Category> Categories() => CurrentMail?.Categories() ?? [];

    /// <summary>Whether every one of these rows carries the category, for the menu's tick.</summary>
    public bool AllHave(IReadOnlyList<MessageRow> rows, Category category)
    {
        if (rows.Count == 0 || CurrentMail is not { } mail) return false;

        var assigned = mail.CategoriesFor([.. rows.Select(r => r.Id)]);
        return rows.All(r => assigned.TryGetValue(r.Id, out var list) && list.Any(c => c.Id == category.Id));
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

        var ids = rows.Select(r => r.Id).ToList();
        var remove = AllHave(rows, category);

        if (remove) mail.Unassign(ids, category.Id);
        else mail.Assign(ids, category.Id);

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

        foreach (var row in rows) Messages.Remove(row);
        Rebuild();
        RefreshCounts();
    }

    /// <summary>Moves rows into another folder of the same account.</summary>
    public void MoveTo(IReadOnlyList<MessageRow> rows, FolderRole role)
    {
        if (rows.Count == 0 || Mail(rows) is not { } mail) return;
        if (CurrentAccount?.Mail.FolderWithRole(CurrentAccount.Account.Id, role)
            is not { } target) return;

        mail.MoveMessages([.. rows.Select(r => r.Id)], target.Id);
        foreach (var row in rows) Messages.Remove(row);

        Rebuild();
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
        foreach (var row in rows) Messages.Remove(row);

        Rebuild();
        RefreshCounts();
        StatusRight = $"{Describe(rows.Count)} moved to {target.Name}.";
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

    /// <summary>The address of the account whose folder is on screen, for a new message to come from.</summary>
    public string? CurrentAddress => CurrentAccount?.Account.Address;

    /// <summary>What kind of folder is on screen, so a draft can be opened as one.</summary>
    public FolderRole CurrentFolderRole =>
        SelectedFolder is { } folder && _folderIds.TryGetValue(folder, out var where)
            ? where.Role
            : FolderRole.None;

    /// <summary>The account whose folder is on screen, or the first one.</summary>
    private OpenAccount? CurrentAccount =>
        SelectedFolder is { } folder && _folderIds.TryGetValue(folder, out var where)
            ? where.Account
            : _accounts?.All.FirstOrDefault();

    /// <summary>The account whose categories a management dialog should edit — the current one.</summary>
    public OpenAccount? CurrentAccountForCategories() => CurrentAccount;

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
        var selected = SelectedFolder?.Name;
        if (_accounts is null || !LoadFromStore()) { Rebuild(); return; }

        if (selected is not null)
        {
            SelectedFolder = Folders.FirstOrDefault(f => f.Name == selected) ?? SelectedFolder;
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
            "Received" => Arrangement.Date,
            "Size" => Arrangement.Size,
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
