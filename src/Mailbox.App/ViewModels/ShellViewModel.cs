using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Mailbox.Core;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
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

    public ShellViewModel(
        ThemeService themes,
        CommandCatalog catalog,
        RibbonLayout layout,
        ShellLayoutMode layoutMode)
    {
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
        _selectedMessage = Messages[0];
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

    /// <summary>The reference application puts search above the message list, not in the title bar.</summary>
    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value);
    }

    public string SearchPlaceholder => $"Search {SelectedFolderName}";

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
    public string ReadingPaneGlyph { get; } = IconGlyphs.GetOrEmpty("reading-pane", 16);
    public string ReadingGlyph { get; } = IconGlyphs.GetOrEmpty("message-preview", 16);
    public string CollapseGlyph { get; } = IconGlyphs.GetOrEmpty("chevron-left", 16);

    public double ZoomPercent
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(ZoomLabel)); }
    } = 100;

    public string ZoomLabel => $"{ZoomPercent:0}%";

    public string AccountInitial => "A";
    public string AccountTip => "you@example.com";

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
        $"Items: {Messages.Count}   Unread: {Messages.Count(m => m.IsUnread)}";

    /// <summary>
    /// Phase 0 shows the rendering diagnostics that the text-rendering investigation needs.
    /// Replaced by the real connection state once accounts exist in Phase 2.
    /// </summary>
    public string StatusRight
    {
        get;
        set { if (Set(ref field, value)) Raise(); }
    } = "Connected";
}

/// <summary>One header cell in the message list's column strip.</summary>
public sealed class MessageColumn(string title, double width, bool isGlyph = false)
{
    public string Title { get; } = title;
    public double Width { get; } = width;

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
