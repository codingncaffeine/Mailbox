using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Protocols;
using Mailbox.Store;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>One row of the feeds pane: a heading, a feed under one, or one of the standing views.</summary>
internal sealed record FeedNavRow(string Label, int Unread, FeedNavKind Kind)
{
    /// <summary>The subscription behind a feed row, or null for a heading or a standing view.</summary>
    public FeedSubscription? Feed { get; init; }

    /// <summary>The heading a heading row stands for.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>The store folders whose articles this row shows.</summary>
    public IReadOnlyList<long> Folders { get; init; } = [];

    /// <summary>The board a board row stands for, or null for everything else.</summary>
    public Board? Board { get; init; }

    /// <summary>
    /// Whether the number beside the row is everything on it rather than what is unread.
    /// </summary>
    /// <remarks>
    /// A board is a keep pile, and most of what a reader saves onto one they have already read.
    /// Counting the unread there would draw a nought against a board holding forty articles,
    /// which reads as an empty board rather than as a read one.
    /// </remarks>
    public bool CountIsTotal { get; init; }

    public bool IsExpanded { get; set; } = true;
}

/// <summary>
/// How the articles are laid out. The three the readers this is measured against offer, under
/// the names they use.
/// </summary>
internal enum FeedLayout
{
    /// <summary>Thumbnail, headline, source, snippet. The default, and what most people keep.</summary>
    Magazine,

    /// <summary>One line an article: headline, source, age. What heavy readers live in.</summary>
    TextOnly,

    /// <summary>A grid of pictures with the headline under each. For feeds that are photographs.</summary>
    Cards,
}

internal enum FeedNavKind
{
    Today,
    Unread,
    ReadLater,
    Board,
    Category,
    Feed,
}

/// <summary>
/// The Feeds module: subscriptions on the left, articles in the middle, the article itself on
/// the right.
/// </summary>
/// <remarks>
/// The reference has no such module — it keeps feeds as folders under Mail and stops there. This
/// is the owner's call, and it is the right one: a reader with fifty subscriptions wants their
/// own headings with their own counts, and an article list built for articles, which is a
/// picture, a headline, where it came from and the first line of it. A mail list is built for
/// correspondence and shows none of that well.
/// <para>
/// <b>Nothing here holds its own copy of anything.</b> The articles are the messages the receiver
/// filed under RSS Feeds, read through the same repository the mail module reads, so a feed item
/// read here is read there, deleting one deletes it, and search finds it either way. What this
/// module adds is a way of looking at them.
/// </para>
/// <para>
/// Every colour is a token. The reference pictures a reader would compare this against are all
/// white, and this follows whichever of the four themes is on — which is the point of having
/// them (see the theme discipline the rest of the shell holds to).
/// </para>
/// </remarks>
internal sealed class FeedsWorkspace : Border
{
    private const double NavWidth = 235;
    /// <summary>The narrowest the article list is allowed to get before the panes stop sharing.</summary>
    private const double ListMinimum = 380;

    /// <summary>The widest a column of articles is set, measured off what stays readable.</summary>
    private const double ListIdeal = 760;

    /// <summary>
    /// How much of the window the article goes in, once one is open.
    /// </summary>
    /// <remarks>
    /// Nothing until then. The reference has no reading pane at all — an article opens where it
    /// sits — and half a window of empty grey beside a list is the single thing that would make
    /// this look unfinished. So the pane is not there until there is something in it, and the
    /// list has the room in the meantime.
    /// </remarks>
    private const double ReadingWidth = 620;
    /// <summary>
    /// The thumbnail, at the proportions the readers this is measured against use.
    /// </summary>
    /// <remarks>
    /// 150×86 is close to 16:9 and is what both reference pictures draw: large enough that a
    /// photograph is worth having, small enough that four rows still fit on a screen. It was
    /// smaller, and a picture at that size is decoration rather than information.
    /// </remarks>
    private const double ThumbnailWidth = 150;
    private const double ThumbnailHeight = 86;

    private readonly FeedSubscriptions _feeds;
    private readonly Func<OpenAccount?> _account;
    private readonly FeedThumbnails _pictures;
    private readonly FeedPictureLookup? _lookup;

    private readonly StackPanel _nav = new();
    private readonly ListBox _articles = new();
    private readonly TextBlock _heading = new();
    private readonly TextBlock _subheading = new();
    private readonly ReadingPaneBody _reading;
    private readonly Border _empty;
    private readonly Panel _readingHost = new();

    private readonly List<FeedNavRow> _rows = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which subscription each folder belongs to, so an article knows where it came from.</summary>
    private readonly Dictionary<long, FeedSubscription> _feedByFolder = [];

    private FeedNavRow? _selected;
    private long _selectedMessage;

    // What is being searched for, and over what. Empty means the pane's own row decides the list.
    private string _query = string.Empty;
    private bool _everywhere;
    private bool _headlineOnly;
    private TimeSpan? _within;
    private FeedSubscription? _articleFeed;

    public FeedsWorkspace(
        FeedSubscriptions feeds,
        Func<OpenAccount?> account,
        FeedThumbnails pictures,
        FeedPictureLookup? lookup = null)
    {
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _pictures = pictures ?? throw new ArgumentNullException(nameof(pictures));
        _lookup = lookup;

        // The reading surface, not the list surface. Chrome is themed and content is light —
        // which is the owner's call and is also what both references do: the reference's own Dark
        // Gray keeps its reading pane light beside dark chrome, and the feed readers this is
        // measured against tint the sidebar and leave the articles on white.
        //
        // It is not cosmetic. Dark Gray's ink tokens are dark by design (#262626 primary, #505050
        // secondary); on the list's own #666666 ground the secondary line is nearly unreadable,
        // and on the reading surface's #D4D4D4 it is exactly right.
        this[!BackgroundProperty] = new DynamicResourceExtension("reading.background.brush");

        _reading = new ReadingPaneBody(App.Themes, () => _account()?.Mail);
        _empty = EmptyState();
        _readingHost.Children.Add(_reading);
        _readingHost.Children.Add(_empty);

        // A ListBox rather than a stack of buttons, for three reasons that all matter at the
        // size a feed reader runs at: it virtualises, so a folder of two hundred articles
        // realises the dozen on screen rather than all of them — which is also what stops two
        // hundred thumbnails being fetched the moment a heading is opened; it carries the
        // selection, so the bar knows what it is acting on; and it moves with the arrow keys
        // without any of that being written here.
        _articles.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _articles.ItemTemplate = new FuncDataTemplate<MessageSummary>((message, _) => Card(message), supportsRecycling: true);
        _articles.Background = Brushes.Transparent;
        _articles.BorderThickness = new Thickness(0);
        _articles.Padding = new Thickness(0);
        ScrollViewer.SetHorizontalScrollBarVisibility(_articles, ScrollBarVisibility.Disabled);
        _articles.SelectionChanged += (_, _) =>
        {
            if (_articles.SelectedItem is not MessageSummary chosen) return;

            // Which subscription it came from, so Update This Feed and Feed Settings act on the
            // right one when the reader is looking at Today rather than at one feed.
            _articleFeed = _feedByFolder.GetValueOrDefault(chosen.FolderId);
            _chosen = chosen;

            if (_openOnSelect) Open(chosen);
        };

        // Focusable so the single-key bindings reach it, and focused on the way in so a reader
        // can start pressing j without clicking first.
        Focusable = true;
        Child = Layout();
        Reload();
    }

    /// <summary>What the status bar says while this module is showing.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Raised when the reader asks for the subscriptions to be brought up to date.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the reader asks to add a feed.</summary>
    public event EventHandler? AddRequested;

    /// <summary>Raised when the reader asks for a new board, with the article to put on it if any.</summary>
    public event EventHandler? NewBoardRequested;

    /// <summary>Raised when the reader asks to save an address that arrived from nowhere.</summary>
    public event EventHandler? SaveLinkRequested;

    /// <summary>Raised with the control to hang the Save to Board menu off.</summary>
    public event EventHandler<Control>? SaveToBoardRequested;

    /// <summary>Raised to read one subscription now.</summary>
    public event EventHandler<FeedSubscription>? UpdateFeedRequested;

    /// <summary>Raised to give a feed a different name.</summary>
    public event EventHandler<FeedSubscription>? RenameFeedRequested;

    /// <summary>Raised to open one feed's settings.</summary>
    public event EventHandler<FeedSubscription>? FeedSettingsRequested;

    /// <summary>Raised to stop reading a feed.</summary>
    public event EventHandler<FeedSubscription>? UnsubscribeRequested;

    /// <summary>Raised to file a feed under a heading — empty for the top level.</summary>
    public event EventHandler<(FeedSubscription Feed, string Category)>? MoveFeedRequested;

    /// <summary>Raised to make a heading, optionally putting a feed straight into it.</summary>
    public event EventHandler<FeedSubscription?>? NewHeadingRequested;

    /// <summary>Raised to rename a heading, with the name it has now.</summary>
    public event EventHandler<string>? RenameHeadingRequested;

    /// <summary>Raised to remove a heading. Its feeds go to the top level.</summary>
    public event EventHandler<string>? RemoveHeadingRequested;

    /// <summary>Raised to open the Boards dialog.</summary>
    public event EventHandler<Board>? ManageBoardsRequested;

    /// <summary>Raised with text to put on the clipboard.</summary>
    public event EventHandler<string>? CopyRequested;

    /// <summary>Raised when the reader opens an article in a window of its own.</summary>
    public event EventHandler<long>? OpenRequested;

    /// <summary>Raised when what is selected changes, so the bar can re-decide what it can do.</summary>
    public event EventHandler? Changed;

    /// <summary>The subscription the pane has selected, or the one the selected article came from.</summary>
    public FeedSubscription? SelectedFeed => _selected?.Feed ?? _articleFeed;

    /// <summary>The board the pane has open, or null when the pane is on a feed or a view.</summary>
    public Board? SelectedBoard => _selected?.Board;

    /// <summary>
    /// The article the list has selected, read fresh from the store.
    /// </summary>
    /// <remarks>
    /// Off the list's own selection rather than off whatever was last <em>opened</em>: n and p
    /// move without opening, so the two part company the moment a reader skims. Read back by id
    /// rather than returned as the row holds it, because the row is a snapshot and the thing a
    /// command wants to know — is it read, is it flagged — is exactly what changes under it.
    /// </remarks>
    public MessageSummary? SelectedArticle
    {
        get
        {
            if (_account() is not { } account) return null;

            // In the Cards layout the list selects a row of three tiles, so its own selection
            // cannot say which article was meant and the tile that was pressed is the only thing
            // that can. Everywhere else the two agree.
            var article = _articles.SelectedItem as MessageSummary ?? _chosen;
            return article is null ? null : account.Mail.GetMessage(article.Id) ?? article;
        }
    }

    /// <summary>Whether the subscriptions pane is showing.</summary>
    public bool IsNavVisible
    {
        get;
        set { field = value; if (Child is Grid grid) grid.Children[0].IsVisible = value; }
    } = true;

    // ---- The frame -----------------------------------------------------------------------------

    private Control Layout()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{NavWidth},1,*,1,{ReadingWidth}"),
        };

        var nav = NavPane();
        Grid.SetColumn(nav, 0);
        grid.Children.Add(nav);

        var firstRule = Rule();
        Grid.SetColumn(firstRule, 1);
        grid.Children.Add(firstRule);

        var list = ArticleColumn();
        Grid.SetColumn(list, 2);
        grid.Children.Add(list);

        var secondRule = Rule();
        Grid.SetColumn(secondRule, 3);
        grid.Children.Add(secondRule);

        Grid.SetColumn(_readingHost, 4);
        grid.Children.Add(_readingHost);

        return grid;
    }

    private static Border Rule()
    {
        var rule = new Border { Width = 1 };
        rule[!BackgroundProperty] = new DynamicResourceExtension("list.separator.brush");
        return rule;
    }

    private Border NavPane()
    {
        var add = new Button
        {
            Classes = { "flat" },
            Margin = new Thickness(10, 10, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = Row(IconGlyphs.GetOrEmpty("add", 16), "Add a feed"),
        };
        ToolTip.SetTip(add, "Subscribe to a website or an RSS address");
        add.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);

        var refresh = new Button
        {
            Classes = { "flat" },
            Margin = new Thickness(10, 0, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = Row(IconGlyphs.GetOrEmpty("send-receive", 16), "Update feeds"),
        };
        ToolTip.SetTip(refresh, "Read every subscription that is due");
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        var stack = new StackPanel { Children = { add, refresh, _nav } };

        var pane = new Border
        {
            Child = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        pane[!BackgroundProperty] = new DynamicResourceExtension("nav.background.brush");
        return pane;
    }

    private static StackPanel Row(string glyph, string text)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        icon[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");

        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");

        return new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } };
    }

    private Control ArticleColumn()
    {
        _heading.FontSize = 21;
        _heading.FontWeight = FontWeight.SemiBold;
        _heading.Margin = new Thickness(18, 16, 18, 0);
        _heading[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");

        _subheading.FontSize = 12;
        _subheading.Margin = new Thickness(18, 3, 18, 12);
        _subheading[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        var header = new StackPanel { Children = { _heading, _subheading } };
        DockPanel.SetDock(header, Dock.Top);

        var search = SearchRow();
        DockPanel.SetDock(search, Dock.Top);

        var actions = HeaderActions();
        DockPanel.SetDock(actions, Dock.Top);

        // Capped and centred rather than filling whatever width is going. A headline set across
        // 1,200 pixels is one line of tiny text with an acre of grey beside it; every reader that
        // does this well keeps the column to something a person can read down.
        var panel = new DockPanel
        {
            MaxWidth = ListIdeal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { header, search, actions, _articles },
        };

        var host = new Border { MinWidth = ListMinimum, Child = panel };
        host[!BackgroundProperty] = new DynamicResourceExtension("reading.background.brush");
        return host;
    }

    /// <summary>
    /// The strip over the list: what a reader does to everything showing rather than to one
    /// article. The count on Mark All Read is the reference picture's own — it is what tells
    /// you what the button is about to do.
    /// </summary>
    /// <summary>
    /// The search box and what narrows it: where to look, how far back, and whether the
    /// headline is enough.
    /// </summary>
    /// <remarks>
    /// The engine underneath is the store's own — FTS5 with the reference's keyword grammar —
    /// so <c>from:</c>, <c>subject:</c> and the rest work here as they do in mail, and the
    /// controls are shorthands onto the same query rather than a second search.
    /// <para>
    /// Worth having at all because the readers this is measured against charge for it: the free
    /// tier of Feedly has no search whatsoever, and the paid one searches only as far back as it
    /// has kept, where a local store has kept everything.
    /// </para>
    /// </remarks>
    private Control SearchRow()
    {
        _search.PlaceholderText = "Search these articles";
        _search.MaxLength = 200;
        _search.MinWidth = 240;
        _search.TextChanged += (_, _) =>
        {
            _query = _search.Text?.Trim() ?? string.Empty;
            _filters.IsVisible = _query.Length > 0;
            Rerun();
        };

        _search.KeyDown += (_, e) =>
        {
            if (e.Key is not Key.Escape) return;

            e.Handled = true;
            _search.Text = string.Empty;
            Focus();
        };

        _scope.MinWidth = 150;
        _scope.ItemsSource = new[] { "Here", "Every feed" };
        _scope.SelectedIndex = 0;
        ToolTip.SetTip(_scope, "Where to search");
        _scope.SelectionChanged += (_, _) =>
        {
            _everywhere = _scope.SelectedIndex == 1;
            Rerun();
        };

        var when = new ComboBox { MinWidth = 130 };
        when.ItemsSource = new[] { "Any time", "Today", "Last 7 days", "Last 30 days", "Last year" };
        when.SelectedIndex = 0;
        ToolTip.SetTip(when, "How far back");
        when.SelectionChanged += (_, _) =>
        {
            _within = when.SelectedIndex switch
            {
                1 => TimeSpan.FromDays(1),
                2 => TimeSpan.FromDays(7),
                3 => TimeSpan.FromDays(30),
                4 => TimeSpan.FromDays(365),
                _ => null,
            };

            Rerun();
        };

        var headline = new CheckBox { Content = "Headline only", VerticalAlignment = VerticalAlignment.Center };
        headline[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
        headline.IsCheckedChanged += (_, _) =>
        {
            _headlineOnly = headline.IsChecked == true;
            Rerun();
        };

        _filters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _scope, when, headline },
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(18, 0, 12, 10),
            Children = { _search, _filters },
        };
    }

    private readonly TextBox _search = new();
    private readonly ComboBox _scope = new();
    private StackPanel _filters = new();

    /// <summary>The headlines the list is showing, for a harness run to read back.</summary>
    public IEnumerable<string> Showing => _showing.Select(m => $"{m.Subject}  ({m.DisplayFrom})");

    /// <summary>
    /// Types into the search box and runs the search, for a harness run.
    /// </summary>
    /// <remarks>
    /// The state is set and the search run here rather than left to the box's own TextChanged,
    /// which Avalonia raises on a later pass — so a run that set the text and then read the
    /// result back got the list as it was before the search, which is exactly the sort of
    /// "verified" that verifies nothing.
    /// </remarks>
    public void Pose(string query, bool everywhere = false, bool headlineOnly = false)
    {
        _everywhere = everywhere;
        _headlineOnly = headlineOnly;
        _query = query.Trim();

        // The controls move with the state, so a photograph of a posed run cannot show "Here"
        // over a list that was searched everywhere.
        _scope.SelectedIndex = everywhere ? 1 : 0;
        _search.Text = query;
        _filters.IsVisible = _query.Length > 0;
        Rerun();
    }

    /// <summary>
    /// Selects the nth article showing, without opening it, for a harness run.
    /// </summary>
    /// <remarks>
    /// A run has to be able to say "this article" before it presses a command that acts on one,
    /// and clicking a row is the one thing a capture cannot do.
    /// </remarks>
    public string PoseSelect(int at, bool open = false)
    {
        if (_showing.Count == 0) return "nothing is showing";
        if (at < 0 || at >= _showing.Count) return $"only {_showing.Count} article(s) are showing";

        Choose(_showing[at]);

        // What a click does is select and open, so a run that poses a click has to do both —
        // otherwise the reading pane in the photograph is the one the previous row left behind.
        if (open) Open(_showing[at]);

        return $"“{_showing[at].Subject}” ({_showing[at].DisplayFrom})"
            + (open ? $", opened; {Length(_showing[at])} characters of article" : string.Empty);
    }

    /// <summary>
    /// How much article a row actually carries, for a harness run.
    /// </summary>
    /// <remarks>
    /// The claim "you can read the article here" is a claim about a number of characters, and it
    /// is the one a screenshot of a reading pane cannot make: a pane showing one paragraph and a
    /// pane showing the whole piece are the same picture above the fold.
    /// </remarks>
    private string Length(MessageSummary article)
    {
        if (_account() is not { } account || account.Mail.LoadRaw(article.Id) is not { } raw) return "no";

        try
        {
            using var stream = new MemoryStream(raw);
            var message = MimeKit.MimeMessage.Load(stream);
            var text = message.TextBody ?? FeedParser.PlainText(message.HtmlBody ?? string.Empty);
            return text.Trim().Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            return "unreadable";
        }
    }

    /// <summary>Flips one of the row's two switches, for a harness run.</summary>
    public void PoseToggle(bool unreadOnly) => Toggle(unreadOnly ? UnreadOnlyKey : OrderKey);

    /// <summary>What is on the boards, as lines a harness run can read back.</summary>
    public IEnumerable<string> BoardReport()
    {
        if (_account() is not { } account) yield break;

        foreach (var board in account.Mail.Boards())
        {
            yield return $"{board.Name}: {board.Count} article(s)"
                + (board.Description.Length > 0 ? $" — {board.Description}" : string.Empty);

            foreach (var article in account.Mail.BoardMessages(board.Id).Take(5))
            {
                yield return $"    · {article.Subject}  ({article.DisplayFrom})";
            }
        }
    }

    /// <summary>Redraws the list for whatever the search now says.</summary>
    private void Rerun()
    {
        if (_selected is { } row) Select(row, keepReading: true);
    }

    private Control HeaderActions()
    {
        _markAllRead = new Button
        {
            Classes = { "flat" },
            Padding = new Thickness(8, 2, 8, 2),
            Content = ActionContent("unread", "Mark all as read"),
        };
        ToolTip.SetTip(_markAllRead, "Mark everything showing as read");
        _markAllRead.Click += (_, _) =>
        {
            MarkAllRead();
            Changed?.Invoke(this, EventArgs.Empty);
        };

        // Top right of the article area, which is where both readers this is measured against
        // put it, and per feed, which is how they remember it.
        var views = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        foreach (var (layout, icon, tip) in (( FeedLayout, string, string )[])
                 [
                     (FeedLayout.Magazine, "reading-pane", "Magazine"),
                     (FeedLayout.TextOnly, "bullets", "Text only"),
                     (FeedLayout.Cards, "apps", "Cards"),
                 ])
        {
            var chosen = layout;
            var button = new Button
            {
                Classes = { "flat" },
                Width = 28,
                Height = 26,
                Padding = new Thickness(0),
                FontFamily = IconFont.Family,
                FontSize = 14,
                Content = IconGlyphs.GetOrEmpty(icon, 16),
            };
            button[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => SetLayout(chosen);

            _viewButtons[layout] = button;
            views.Children.Add(button);
        }

        // Only over a board, where it is the thing a reader is there to do. Over a feed it would
        // be a button about somewhere else.
        _saveLink = new Button
        {
            Classes = { "flat" },
            Padding = new Thickness(8, 2, 8, 2),
            IsVisible = false,
            Content = ActionContent("add", "Save a link"),
        };
        ToolTip.SetTip(_saveLink, "Put any web address on this board");
        _saveLink.Click += (_, _) => SaveLinkRequested?.Invoke(this, EventArgs.Empty);

        _unreadOnly = new Button
        {
            Classes = { "flat" },
            Padding = new Thickness(8, 2, 8, 2),
            Content = ActionContent("filter", "Unread only"),
        };
        ToolTip.SetTip(_unreadOnly, "Show only what you have not read here");
        _unreadOnly.Click += (_, _) => Toggle(UnreadOnlyKey);

        _oldestFirst = new Button
        {
            Classes = { "flat" },
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            FontFamily = IconFont.Family,
            FontSize = 14,
            Content = IconGlyphs.GetOrEmpty("reverse-sort", 16),
        };
        _oldestFirst[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
        ToolTip.SetTip(_oldestFirst, "Read oldest first");
        _oldestFirst.Click += (_, _) => Toggle(OrderKey);

        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 8),
            Spacing = 10,
            Children = { _saveLink, _unreadOnly, _markAllRead, _oldestFirst, views },
        };

        return strip;
    }

    private Button? _unreadOnly;
    private Button? _oldestFirst;

    /// <summary>Flips one of the row's own switches and redraws for it.</summary>
    private void Toggle(Func<FeedNavRow, string> key)
    {
        if (_selected is not { } row) return;

        var name = key(row);
        App.Settings.Set(name, !App.Settings.GetBool(name, false));

        Select(row, keepReading: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Button? _saveLink;

    private static void Mark(Button? button, bool on)
    {
        if (button is null) return;

        button.Classes.Remove("active");
        if (on) button.Classes.Add("active");
    }

    private Button? _markAllRead;
    private readonly Dictionary<FeedLayout, Button> _viewButtons = [];

    private static StackPanel ActionContent(string icon, string text)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        return new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, label } };
    }

    private Border EmptyState()
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("rss", 24),
            FontFamily = IconFont.Family,
            FontSize = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.disabled.brush");

        var line = new TextBlock
        {
            Text = "Choose an article to read it here.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        line[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        return new Border
        {
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { glyph, line },
            },
        };
    }

    // ---- What is on screen ----------------------------------------------------------------------

    /// <summary>Rebuilds the pane and the list from the store.</summary>
    public void Reload()
    {
        BuildNav();

        // Keep whatever was selected if it is still there; otherwise open Today, which is what a
        // reader wants to see when they arrive.
        var wanted = _selected is { } previous
            ? _rows.FirstOrDefault(r => r.Kind == previous.Kind && r.Label == previous.Label)
            : null;

        Select(wanted ?? _rows.FirstOrDefault(), keepReading: wanted is not null);
    }

    private void BuildNav()
    {
        _rows.Clear();
        _nav.Children.Clear();
        _feedByFolder.Clear();

        var account = _account();
        var folders = account is null ? [] : account.Mail.Folders(account.Account.Id);
        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == Mailbox.Protocols.FeedReceiver.RootFolder);

        // Every folder that holds feed articles, by the path the subscription delivers to.
        var byPath = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
        if (root is not null)
        {
            foreach (var child in folders.Where(f => f.ParentId == root.Id))
            {
                byPath[child.Name] = child;
                foreach (var grandchild in folders.Where(f => f.ParentId == child.Id))
                {
                    byPath[$"{child.Name}/{grandchild.Name}"] = grandchild;
                }
            }
        }

        long UnreadIn(FeedSubscription feed)
            => byPath.TryGetValue(feed.FolderPath, out var folder) ? folder.Unread : 0;

        // Which subscription owns which folder, for every feed rather than for the rows that
        // happen to be drawn: a heading the reader has collapsed still has its articles in Today,
        // and they still need to know where they came from — which is what decides whether Update
        // This Feed acts on the right one, and whether a missing picture may be looked up.
        foreach (var feed in _feeds.All)
        {
            if (byPath.TryGetValue(feed.FolderPath, out var owned)) _feedByFolder[owned.Id] = feed;
        }

        IReadOnlyList<long> FoldersFor(IEnumerable<FeedSubscription> subscriptions)
            => [.. subscriptions.Select(f => byPath.TryGetValue(f.FolderPath, out var folder) ? folder.Id : 0).Where(id => id != 0)];

        var all = _feeds.All;
        var everything = FoldersFor(all);
        var totalUnread = (int)all.Sum(UnreadIn);

        _rows.Add(new FeedNavRow("Today", totalUnread, FeedNavKind.Today) { Folders = everything });
        _rows.Add(new FeedNavRow("Unread", totalUnread, FeedNavKind.Unread) { Folders = everything });
        _rows.Add(new FeedNavRow("Read Later", 0, FeedNavKind.ReadLater) { Folders = everything });

        foreach (var row in _rows.ToList()) _nav.Children.Add(NavButton(row, indent: 0));

        BuildBoards(account);

        _nav.Children.Add(SectionLabel("FEEDS"));

        // Headings first, in the order a reader would expect them, then the loose feeds.
        foreach (var category in _feeds.Categories)
        {
            var inside = all.Where(f => string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();

            var heading = new FeedNavRow(category, (int)inside.Sum(UnreadIn), FeedNavKind.Category)
            {
                Category = category,
                Folders = FoldersFor(inside),
                IsExpanded = !_collapsed.Contains(category),
            };

            _rows.Add(heading);
            _nav.Children.Add(NavButton(heading, indent: 0));

            if (!heading.IsExpanded) continue;

            foreach (var feed in inside)
            {
                var row = new FeedNavRow(feed.Name, (int)UnreadIn(feed), FeedNavKind.Feed)
                {
                    Feed = feed,
                    Category = category,
                    Folders = FoldersFor([feed]),
                };

                _rows.Add(row);
                _nav.Children.Add(NavButton(row, indent: 1));
            }
        }

        foreach (var feed in all.Where(f => f.Category.Length == 0))
        {
            var row = new FeedNavRow(feed.Name, (int)UnreadIn(feed), FeedNavKind.Feed)
            {
                Feed = feed,
                Folders = FoldersFor([feed]),
            };

            _rows.Add(row);
            _nav.Children.Add(NavButton(row, indent: 0));
        }

        if (all.Count == 0) _nav.Children.Add(NoFeedsYet());
    }

    /// <summary>
    /// The boards section of the pane, with New Board… at the end of it.
    /// </summary>
    /// <remarks>
    /// Above the feeds and below the standing views, which is where every reader that has boards
    /// puts them: they are places a reader goes deliberately, and the subscription list under
    /// them is long enough to push anything below it off the pane.
    /// <para>
    /// Drawn even when there are none, as one line offering to make the first — a feature nothing
    /// on screen mentions is a feature nobody finds, and this one is the difference between a
    /// reader who keeps things here and one who keeps them in their browser.
    /// </para>
    /// </remarks>
    private void BuildBoards(OpenAccount? account)
    {
        _nav.Children.Add(SectionLabel("BOARDS"));

        var boards = account?.Mail.Boards() ?? [];

        foreach (var board in boards)
        {
            var row = new FeedNavRow(board.Name, board.Count, FeedNavKind.Board)
            {
                Board = board,
                CountIsTotal = true,
            };

            _rows.Add(row);
            _nav.Children.Add(NavButton(row, indent: 0));
        }

        var make = new Button
        {
            Classes = { "flat" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4, 0, 0, 0),
            Height = 26,
            Content = Row(IconGlyphs.GetOrEmpty("add", 16), "New board…"),
        };
        ToolTip.SetTip(make, boards.Count == 0
            ? "A board is a collection you save articles into — and any web address can go on one"
            : "Make another collection to save articles into");
        make.Click += (_, _) => NewBoardRequested?.Invoke(this, EventArgs.Empty);
        _nav.Children.Add(make);
    }

    /// <summary>
    /// A heading in the pane. Takes the pane's own ink, not the content ink.
    /// </summary>
    /// <remarks>
    /// The same trap the article list fell into, one pane over: <c>text.secondary</c> is dark by
    /// design because content surfaces are light, and on the pane's dark ground it disappears.
    /// Anything drawn on the pane takes <c>nav.item.text</c>.
    /// </remarks>
    private static Control SectionLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.75,
            Margin = new Thickness(14, 14, 10, 4),
        };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        return label;
    }

    private Control NoFeedsYet()
    {
        var text = new TextBlock
        {
            Text = "No subscriptions yet. Add one with a website address — the feed behind it is found for you.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
            Margin = new Thickness(14, 6, 12, 0),
        };
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        return text;
    }

    /// <summary>One row of the pane: an optional twisty, an icon, a name, and its unread count.</summary>
    private Control NavButton(FeedNavRow row, int indent)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 26 };

        if (row.Kind == FeedNavKind.Category)
        {
            var twisty = new Button
            {
                Classes = { "flat" },
                Width = 18,
                Padding = new Thickness(0),
                FontFamily = IconFont.Family,
                FontSize = 11,
                Content = IconGlyphs.GetOrEmpty(row.IsExpanded ? "chevron-down" : "chevron-right", 16),
                VerticalAlignment = VerticalAlignment.Center,
            };
            twisty[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
            twisty.Click += (_, _) =>
            {
                if (!_collapsed.Remove(row.Category)) _collapsed.Add(row.Category);
                BuildNav();
            };

            Grid.SetColumn(twisty, 0);
            grid.Children.Add(twisty);
        }

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(Glyph(row.Kind), 16),
            FontFamily = IconFont.Family,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(row.Kind == FeedNavKind.Category ? 0 : 6, 0, 7, 0),
        };
        glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        Grid.SetColumn(glyph, 1);
        grid.Children.Add(glyph);

        var label = new TextBlock
        {
            Text = row.Label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,

            // Bold means "there is something here you have not read". A board's number is how
            // much is on it, which is not that, so a board is never bolded by its count.
            FontWeight = row.Unread > 0 && !row.CountIsTotal ? FontWeight.SemiBold : FontWeight.Normal,
        };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);

        if (row.Unread > 0)
        {
            var count = new TextBlock
            {
                Text = row.Unread > 999 ? "1K+" : row.Unread.ToString(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 10, 0),
            };
            count[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
                row.CountIsTotal ? "nav.item.text.brush" : "nav.unreadcount.brush");
            if (row.CountIsTotal) count.Opacity = 0.7;
            Grid.SetColumn(count, 3);
            grid.Children.Add(count);
        }

        // A feed that is not answering says so where the reader is looking, rather than only in
        // the log and the Account Settings tab.
        if (row.Feed is { IsFailing: true } failing)
        {
            ToolTip.SetTip(grid, $"{failing.Name} — {failing.LastError}");
            glyph.Text = IconGlyphs.GetOrEmpty("warning", 16);
            glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("status.warning.brush");
        }
        else if (row.Feed is { } feed)
        {
            ToolTip.SetTip(grid, feed.Description.Length > 0 ? $"{feed.Name}\n{feed.Description}" : feed.Name);
        }
        else if (row.Board is { } board)
        {
            ToolTip.SetTip(grid, board.Description.Length > 0 ? $"{board.Name}\n{board.Description}" : board.Name);
        }

        var button = new Button
        {
            Classes = { "flat" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4 + (indent * 14), 0, 0, 0),
            Content = grid,
        };

        button.Click += (_, _) => Select(row, keepReading: false);
        if (ReferenceEquals(row, _selected)) button.Classes.Add("active");

        // Every reader has one of these on its sidebar, and this had none at all — which meant
        // renaming a feed, moving it, or copying its address had no gesture anywhere.
        button.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            Select(row, keepReading: false);
            NavMenu(row).ShowAt(button, showAtPointer: true);
        };

        return button;
    }

    /// <summary>
    /// What right-clicking a row of the pane offers: a feed, a heading, or one of the views.
    /// </summary>
    /// <remarks>
    /// The entries are the ones a reader reaches for on a sidebar and cannot otherwise reach at
    /// all — the ribbon has the commands, but nobody looks on a ribbon to rename the thing they
    /// are pointing at.
    /// </remarks>
    private MenuFlyout NavMenu(FeedNavRow row)
    {
        var menu = new MenuFlyout();

        MenuItem Entry(string label, string icon, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            if (icon.Length > 0) item.Icon = new Mailbox.Controls.Ribbon.RibbonArtwork(icon, 16);
            item.Click += (_, _) => act();
            return item;
        }

        menu.Items.Add(Entry("Mark All as Read", "mark-all-read", () =>
        {
            MarkAllRead();
            Changed?.Invoke(this, EventArgs.Empty);
        }));

        if (row.Kind == FeedNavKind.Feed && row.Feed is { } feed)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("Update This Feed", "refresh", () => UpdateFeedRequested?.Invoke(this, feed)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MoveMenu(feed));
            menu.Items.Add(Entry("Rename…", "folder-rename", () => RenameFeedRequested?.Invoke(this, feed)));
            menu.Items.Add(Entry("Feed Settings…", "settings", () => FeedSettingsRequested?.Invoke(this, feed)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("Copy Feed Address", "copy", () => CopyRequested?.Invoke(this, feed.Url)));
            menu.Items.Add(Entry("Open the Site", "link", () => OpenExternally(
                feed.SiteUrl is { Length: > 0 } site ? site : feed.Url)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("Unsubscribe", "remove-feed", () => UnsubscribeRequested?.Invoke(this, feed)));
        }
        else if (row.Kind == FeedNavKind.Category)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("Rename Heading…", "folder-rename", () => RenameHeadingRequested?.Invoke(this, row.Category)));
            menu.Items.Add(Entry("Remove Heading", "delete-folder", () => RemoveHeadingRequested?.Invoke(this, row.Category)));
        }
        else if (row.Kind == FeedNavKind.Board && row.Board is { } board)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("Manage Boards…", "settings", () => ManageBoardsRequested?.Invoke(this, board)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Add a Feed…", "add", () => AddRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(Entry("New Heading…", "new-folder", () => NewHeadingRequested?.Invoke(this, row.Feed)));

        return menu;
    }

    /// <summary>The headings this feed could be filed under, with a tick against the one it is.</summary>
    private MenuItem MoveMenu(FeedSubscription feed)
    {
        var move = new MenuItem
        {
            Header = "Move to Heading",
            Icon = new Mailbox.Controls.Ribbon.RibbonArtwork("folder-move", 16),
        };

        void Add(string label, string category)
        {
            var here = string.Equals(feed.Category, category, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem { Header = label, IsEnabled = !here };
            if (here) item.Icon = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("mark-complete", 16),
                FontFamily = IconFont.Family,
                FontSize = 12,
            };

            item.Click += (_, _) => MoveFeedRequested?.Invoke(this, (feed, category));
            move.Items.Add(item);
        }

        Add("(no heading)", string.Empty);
        foreach (var heading in _feeds.Categories) Add(heading, heading);

        move.Items.Add(new Separator());

        var made = new MenuItem { Header = "New Heading…" };
        made.Click += (_, _) => NewHeadingRequested?.Invoke(this, feed);
        move.Items.Add(made);

        return move;
    }

    private static string Glyph(FeedNavKind kind) => kind switch
    {
        FeedNavKind.Today => "calendar",
        FeedNavKind.Unread => "unread",
        FeedNavKind.ReadLater => "flag",
        FeedNavKind.Board => "bookmark",
        FeedNavKind.Category => "folder",
        _ => "rss",
    };

    // ---- The article list ------------------------------------------------------------------------

    private void Select(FeedNavRow? row, bool keepReading)
    {
        _selected = row;
        _chosen = null;
        if (row is null)
        {
            _heading.Text = "Feeds";
            _subheading.Text = "Nothing is subscribed to yet.";
            _articles.ItemsSource = null;
            ShowReading(false);
            Status = "No feeds";
            return;
        }

        var articles = Articles(row);

        _layout = LayoutFor(row);
        ApplyLayout();

        _heading.Text = _query.Length > 0 ? $"“{_query}”" : row.Label;
        _subheading.Text = _query.Length > 0
            ? articles.Count == 0
                ? $"Nothing found {(_everywhere ? "in any feed" : $"in {row.Label}")}."
                : $"{articles.Count} article{(articles.Count == 1 ? string.Empty : "s")} "
                  + $"{(_everywhere ? "across every feed" : $"in {row.Label}")}."
            : Describe(row, articles.Count);
        _showing = articles;
        _articles.ItemsSource = Shaped(articles);
        if (_articles.Scroll is { } top) top.Offset = new Vector(0, 0);

        if (_saveLink is { } saving) saving.IsVisible = row.Kind == FeedNavKind.Board;

        // The two switches show their state rather than only acting: a filter nothing marks is a
        // filter a reader forgets is on and then reports as missing articles.
        Mark(_unreadOnly, UnreadOnlyFor(row));
        Mark(_oldestFirst, OldestFirstFor(row));

        // Neither means anything on a board, which is a keep pile in the order it was kept.
        if (_unreadOnly is { } filter) filter.IsVisible = row.Kind != FeedNavKind.Board;
        if (_oldestFirst is { } order) order.IsVisible = row.Kind != FeedNavKind.Board;

        // A board's search is a filter over what is on it, so "Here" and "Every feed" would be
        // two names for the same answer. A control that cannot change anything is worse than no
        // control, so it goes rather than sitting there being pressed to no effect.
        _scope.IsVisible = row.Kind != FeedNavKind.Board;

        if (!keepReading)
        {
            _selectedMessage = 0;
            ShowReading(false);
        }

        Status = $"{row.Label}: {articles.Count} article{(articles.Count == 1 ? string.Empty : "s")}";

        // Rebuilt so the pressed row takes the mark. Cheap: the pane is tens of rows.
        BuildNav();
    }

    private string Describe(FeedNavRow row, int count) => row.Kind switch
    {
        FeedNavKind.Today => "Everything your subscriptions have published, newest first.",
        FeedNavKind.Unread => "What you have not read yet.",
        FeedNavKind.ReadLater => "Articles you flagged to come back to.",
        FeedNavKind.Board => row.Board?.Description is { Length: > 0 } why
            ? why
            : $"{count} article{(count == 1 ? string.Empty : "s")} saved here, newest first.",
        FeedNavKind.Category => $"{count} article{(count == 1 ? string.Empty : "s")} across "
            + $"{_feeds.All.Count(f => string.Equals(f.Category, row.Category, StringComparison.OrdinalIgnoreCase))} feeds.",
        _ => row.Feed?.Description is { Length: > 0 } described
            ? described
            : row.Feed?.SiteUrl ?? string.Empty,
    };

    /// <summary>The articles a pane row stands for, newest first — or what a search found.</summary>
    private List<MessageSummary> Articles(FeedNavRow row)
    {
        if (_account() is not { } account) return [];

        // A board is not a folder — it is a set of articles that are still filed wherever they
        // came from — so it is read through its own membership and then narrowed by the search
        // rather than being handed to a query that thinks in folders.
        if (row.Kind == FeedNavKind.Board && row.Board is { } board) return OnBoard(account, board);

        // "Every feed" widens the search past the row the pane has selected; without a search
        // there is nothing to widen, and the row is the whole question.
        var folders = _query.Length > 0 && _everywhere
            ? _rows.FirstOrDefault(r => r.Kind == FeedNavKind.Today)?.Folders ?? row.Folders
            : row.Folders;

        if (folders.Count == 0) return [];

        var found = _query.Length > 0
            ? account.Mail.Search(Query(), folders, limit: 500)
            : Everything(account, folders);

        if (_query.Length > 0)
        {
            Log.Debug($"Feeds: search “{_query}” over {folders.Count} folder(s) matched {found.Count}.");
        }

        IEnumerable<MessageSummary> filtered = row.Kind switch
        {
            FeedNavKind.Unread => found.Where(m => !m.IsRead),
            FeedNavKind.ReadLater => found.Where(m => m.IsFlagged),
            _ => found,
        };

        // The reader's own two choices for this row: what to show, and which end to start at.
        if (UnreadOnlyFor(row)) filtered = filtered.Where(m => !m.IsRead);

        var ordered = OldestFirstFor(row)
            ? filtered.OrderBy(m => m.Received)
            : filtered.OrderByDescending(m => m.Received);

        return [.. ordered.Take(500)];
    }

    private static List<MessageSummary> Everything(OpenAccount account, IReadOnlyList<long> folders)
    {
        var found = new List<MessageSummary>();
        foreach (var folder in folders) found.AddRange(account.Mail.Messages(folder));
        return found;
    }

    /// <summary>
    /// What is on a board, in the order it was saved, narrowed by whatever is in the search box.
    /// </summary>
    /// <remarks>
    /// The order is the point and is why this does not end with the same <c>OrderByDescending</c>
    /// every other row does: a board is read newest-<em>saved</em> first, so a piece from last
    /// year that the reader put on it this morning is at the top where they left it, rather than
    /// buried under the week's headlines.
    /// <para>
    /// Searching a board is a filter over its membership rather than a query with the board in
    /// it: the store's search thinks in folders, and a board's articles are still filed in the
    /// feeds they arrived from. Running the same query over the whole scope and keeping what is
    /// on the board gives the same answer and needs nothing new in the store.
    /// </para>
    /// </remarks>
    private List<MessageSummary> OnBoard(OpenAccount account, Board board)
    {
        var saved = account.Mail.BoardMessages(board.Id);
        if (_query.Length == 0) return [.. saved];

        var matched = account.Mail
            .Search(Query(), folderIds: null, limit: 2000)
            .Select(m => m.Id)
            .ToHashSet();

        return [.. saved.Where(m => matched.Contains(m.Id))];
    }

    /// <summary>
    /// What was typed, as a query the store understands.
    /// </summary>
    /// <remarks>
    /// The controls are shorthands onto the same grammar rather than a second search: "headline
    /// only" moves the bare words into the subject column, and the date range becomes the
    /// received bound the parser would have built from <c>received:</c>. So a reader who knows
    /// the keywords can type them and a reader who does not can press the buttons, and both end
    /// up in the same place.
    /// </remarks>
    private Mailbox.Core.Search.SearchQuery Query()
    {
        var query = Mailbox.Core.Search.SearchQuery.Parse(_query);

        if (_headlineOnly && query.Words.Count > 0)
        {
            query = query with
            {
                Subject = [.. query.Subject.Concat(query.Words)],
                Words = [],
            };
        }

        if (_within is { } within)
        {
            query = query with { Received = (DateTimeOffset.UtcNow - within, null) };
        }

        return query;
    }

    /// <summary>
    /// One article, as the reference pictures draw it: the picture on the left, the headline, who
    /// published it and when, and the first two lines of it.
    /// </summary>
    private Control Card(MessageSummary message)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{ThumbnailWidth},*,Auto"),
            Margin = new Thickness(18, 14, 12, 14),
        };

        var picture = Thumbnail(message);
        Grid.SetColumn(picture, 0);
        grid.Children.Add(picture);

        // The headline carries the row. In the reference pictures it is half again the size of
        // everything around it and set in the page's own ink, not a link colour — an article
        // list is nearly all unread, and a list of blue headlines reads as a page of links.
        var title = new TextBlock
        {
            Text = message.Subject,
            FontSize = 15.5,
            LineHeight = 21,
            FontWeight = message.IsRead ? FontWeight.Normal : FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Read is the same headline in the quieter ink, which is how the reference marks one:
        // a read article is still worth being able to read the title of.
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
            message.IsRead ? "text.secondary.brush" : "text.primary.brush");

        var source = new TextBlock
        {
            Text = $"{message.DisplayFrom} · {Ago(message.Received)}",
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        source[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        var snippet = new TextBlock
        {
            Text = Snippet(message),
            FontSize = 13,
            LineHeight = 19,
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.85,
        };
        snippet[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        var text = new StackPanel
        {
            Margin = new Thickness(20, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Children = { title, source, snippet },
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = RowActions(message);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        // The buttons appear under the pointer, as the reference's do: a row carrying four
        // buttons at all times is a row nobody can read. The row tints with them, so it is
        // obvious which one they belong to.
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(6, 0, 6, 0),
            Child = grid,
        };

        row.PointerEntered += (_, _) =>
        {
            actions.IsVisible = true;
            row[!BackgroundProperty] = new DynamicResourceExtension("list.row.hover.brush");
        };

        row.PointerExited += (_, _) =>
        {
            actions.IsVisible = false;
            row.Background = Brushes.Transparent;
        };

        row.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, message.Id);
        };

        row.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            Choose(message);
            ArticleMenu(message).ShowAt(row, showAtPointer: true);
        };

        return row;
    }

    /// <summary>
    /// What right-clicking an article offers.
    /// </summary>
    /// <remarks>
    /// The hover buttons are the four most common things; this is the rest, and it is where a
    /// reader looks for them. Everything on it acts on the row that was pointed at rather than on
    /// whatever the list happened to have selected, which is what <see cref="Choose"/> settles
    /// before the menu is built.
    /// </remarks>
    private MenuFlyout ArticleMenu(MessageSummary message)
    {
        var menu = new MenuFlyout();

        MenuItem Entry(string label, string icon, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            if (icon.Length > 0) item.Icon = new Mailbox.Controls.Ribbon.RibbonArtwork(icon, 16);
            item.Click += (_, _) => act();
            return item;
        }

        menu.Items.Add(Entry("Open in a Window", "new-window", () => OpenRequested?.Invoke(this, message.Id)));
        menu.Items.Add(Entry("Open the Original", "link",
            () => OpenExternally(message.FeedLink), message.FeedLink.Length > 0));

        menu.Items.Add(new Separator());
        menu.Items.Add(Entry(message.IsRead ? "Mark as Unread" : "Mark as Read", "unread",
            () => SetRead(message, !message.IsRead)));
        menu.Items.Add(Entry(message.IsFlagged ? "Take off Read Later" : "Read Later", "flag", ToggleReadLater));

        if (_selected is { Kind: FeedNavKind.Board, Board: { } open })
        {
            menu.Items.Add(Entry($"Take Off {open.Name}", "remove-feed", () => RemoveFromOpenBoard()));
        }
        else
        {
            var save = Entry("Save to a Board…", "bookmark", () => { });
            save.Click += (_, _) => SaveToBoardRequested?.Invoke(this, this);
            menu.Items.Add(save);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Copy Link", "copy",
            () => CopyRequested?.Invoke(this, message.FeedLink), message.FeedLink.Length > 0));
        menu.Items.Add(Entry("Copy Headline", "copy", () => CopyRequested?.Invoke(this, message.Subject)));

        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Delete", "delete", DeleteSelected));

        return menu;
    }

    /// <summary>
    /// The four buttons the reference puts on a row under the pointer: keep it for later, open
    /// the original, mark it read, and take it out of the way.
    /// </summary>
    private Control RowActions(MessageSummary message)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Spacing = 2,
            IsVisible = false,
        };

        strip.Children.Add(RowButton(
            message.IsFlagged ? "flag" : "flag",
            message.IsFlagged ? "Take off Read Later" : "Read Later",
            () =>
            {
                if (_account() is not { } account) return;
                account.Mail.SetFlagged(message.Id, !message.IsFlagged);
                KeepingPlace(() => { BuildNav(); RefreshCards(); });
                Changed?.Invoke(this, EventArgs.Empty);
            }));

        // On a board, the second button takes the article off the board it is showing — which is
        // the one thing a reader looking at a board wants that nothing else offers. Everywhere
        // else it is the way onto one.
        if (_selected is { Kind: FeedNavKind.Board, Board: { } open })
        {
            strip.Children.Add(RowButton("remove-feed", $"Take off {open.Name}", () =>
            {
                if (_account() is not { } account) return;

                account.Mail.RemoveFromBoard([message.Id], open.Id);
                Status = $"“{message.Subject}” taken off {open.Name}.";
                RefreshBoards();
                Changed?.Invoke(this, EventArgs.Empty);
            }));
        }
        else
        {
            strip.Children.Add(RowButton("bookmark", "Save to a board", button =>
            {
                Choose(message);
                SaveToBoardRequested?.Invoke(this, button);
            }));
        }

        if (message.FeedLink.Length > 0)
        {
            strip.Children.Add(RowButton("link", "Open the original", () => OpenExternally(message.FeedLink)));
        }

        strip.Children.Add(RowButton(
            "unread",
            message.IsRead ? "Mark as unread" : "Mark as read",
            () => SetRead(message, !message.IsRead)));

        strip.Children.Add(RowButton("delete", "Delete", () =>
        {
            if (_account() is not { } account) return;
            account.Mail.DeleteMessage(message.Id);
            if (_selectedMessage == message.Id)
            {
                _selectedMessage = 0;
                ShowReading(false);
            }

            Reload();
            Changed?.Invoke(this, EventArgs.Empty);
        }));

        return strip;
    }

    /// <summary>A row button whose press wants the button itself — a menu has to hang off one.</summary>
    /// <summary>
    /// Points the selection at a row without opening it, so a button on that row acts on it.
    /// </summary>
    /// <remarks>
    /// The buttons appear under the pointer, which is not where the selection is: a reader can
    /// hover the fourth row while the first is selected, and a menu opened from the fourth row's
    /// button that saved the first one would be saving something they cannot see.
    /// </remarks>
    private void Choose(MessageSummary message)
    {
        _chosen = message;
        _articleFeed = _feedByFolder.GetValueOrDefault(message.FolderId);

        var at = _showing.FindIndex(m => m.Id == message.Id);
        if (at < 0) return;

        var index = _layout == FeedLayout.Cards ? at / TilesAcross : at;
        if (index == _articles.SelectedIndex) return;

        _openOnSelect = false;
        _articles.SelectedIndex = index;
        _openOnSelect = true;
    }

    /// <summary>
    /// The row a button was pressed on, which is what a menu opened from one acts on.
    /// </summary>
    /// <remarks>
    /// Kept beside the list's own selection rather than read out of it, because in the Cards
    /// layout the list selects a row of three tiles and cannot say which of the three was meant.
    /// </remarks>
    private MessageSummary? _chosen;

    private static Button RowButton(string icon, string tip, Action<Button> onClick)
    {
        Button? made = null;
        made = RowButton(icon, tip, () => onClick(made!));
        return made;
    }

    private static Button RowButton(string icon, string tip, Action onClick)
    {
        var button = new Button
        {
            Classes = { "flat" },
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            FontFamily = IconFont.Family,
            FontSize = 13,
            Content = IconGlyphs.GetOrEmpty(icon, 16),
        };
        button[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
        ToolTip.SetTip(button, tip);

        // The row itself opens the article; a button on it does its own thing and nothing else.
        button.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return button;
    }

    /// <summary>
    /// The article's picture, or a lettered tile where there is none.
    /// </summary>
    /// <remarks>
    /// The tile rather than a gap: a list where some rows have a picture and some have a hole in
    /// them reads as broken, and a great many feeds publish no picture at all.
    /// </remarks>
    private Control Thumbnail(MessageSummary message, double width = ThumbnailWidth, double height = ThumbnailHeight)
    {
        var image = new Image
        {
            Width = width,
            Height = height,
            Stretch = Stretch.UniformToFill,
            IsVisible = false,
        };

        var initial = new TextBlock
        {
            Text = message.DisplayFrom is { Length: > 0 } who ? who[..1].ToUpperInvariant() : "?",
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        initial[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.disabled.brush");

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Panel { Children = { initial, image } },
        };
        tile[!BackgroundProperty] = new DynamicResourceExtension("list.row.hover.brush");

        void Draw(string url)
        {
            if (_pictures.Ready(url) is { } already)
            {
                image.Source = already;
                image.IsVisible = true;
                return;
            }

            _pictures.Want(url, bitmap =>
            {
                image.Source = bitmap;
                image.IsVisible = true;
            });
        }

        if (message.FeedImage is { Length: > 0 } url)
        {
            Draw(url);
        }
        else if (AllowsLookup(message))
        {
            // The feed sent no picture. Nearly every article published has one, so rather than a
            // lettered tile the publisher's own page is asked — the same og:image every social
            // network reads — and what comes back is kept on the row.
            _lookup?.Want(message, Draw);
        }

        return tile;
    }

    /// <summary>
    /// The line under the headline, with any address trailing it taken off.
    /// </summary>
    /// <remarks>
    /// A feed item's body ends with the article's own address, so a plain-text reader can reach
    /// it; for an entry whose summary is one sentence that address is most of the preview, and a
    /// list of rows trailing "https://…" is a list nobody can skim. The poll no longer writes one
    /// into the column — but every article filed before it stopped still carries one, and those
    /// are exactly the rows a reader is looking at today.
    /// </remarks>
    private static string Snippet(MessageSummary message)
    {
        var text = message.Preview;

        while (text.LastIndexOfAny([' ', '\n', '\t']) is var space and > 0
               && IsAddress(text.AsSpan(space + 1)))
        {
            text = text[..space].TrimEnd();
        }

        return text.Length > 0 ? text : message.Preview;
    }

    private static bool IsAddress(ReadOnlySpan<char> word)
        => word.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || word.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the publisher's page may be read for this article's picture.
    /// </summary>
    /// <remarks>
    /// The reader's own switch for the feed it came from — the same one that governs reading the
    /// article itself, because it is the same request to the same page. An article whose feed is
    /// not among the subscriptions, a saved link, keeps whatever it arrived with.
    /// </remarks>
    private bool AllowsLookup(MessageSummary message)
        => _feedByFolder.TryGetValue(message.FolderId, out var feed) && feed.ReadFullArticle;

    /// <summary>"2h", "3d" — what a feed reader shows instead of a date.</summary>
    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when.ToLocalTime();

        if (span < TimeSpan.Zero) return when.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture);
        if (span < TimeSpan.FromMinutes(1)) return "now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}m";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}h";
        if (span < TimeSpan.FromDays(7)) return $"{(int)span.TotalDays}d";

        return when.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture);
    }

    // ---- Reading -----------------------------------------------------------------------------------

    private void Open(MessageSummary message)
    {
        if (_account() is not { } account) return;

        _selectedMessage = message.Id;

        var raw = account.Mail.LoadRaw(message.Id);
        if (raw is null)
        {
            ShowReading(false);
            return;
        }

        try
        {
            using var stream = new MemoryStream(raw);
            _reading.Show(MimeKit.MimeMessage.Load(stream), message.Preview);
            ShowReading(true);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Log.Warn($"Feeds: “{message.Subject}” could not be shown.", ex);
            ShowReading(false);
            return;
        }

        // A feed that sends a sentence and a link is the ordinary case, not the exotic one, and
        // "click it to read it" has to mean something. The teaser is shown first and the article
        // replaces it when the page has been read — rather than an empty pane and a wait.
        if (ArticleFill.LooksLikeTeaser(message) && _tried.Add(message.Id))
        {
            FullTextWanted?.Invoke(this, message);
        }

        // Reading it marks it read, as it does in the mail list — and the counts in the pane move
        // with it, which is the thing a feed reader is judged on.
        //
        // Keeping the place while that happens is not a nicety: rebuilding the list drops the
        // selection, and with it every key that means "the next one". Pressing j three times
        // used to open the first article three times.
        if (!message.IsRead)
        {
            account.Mail.SetRead(message.Id, true);
            KeepingPlace(() => { BuildNav(); RefreshCards(); });
        }
    }

    /// <summary>
    /// Runs a rebuild without losing where the reader was.
    /// </summary>
    /// <remarks>
    /// The list is rebuilt from immutable snapshots, so a row that changes — read, flagged —
    /// means a new list. Without this, every such change throws the selection away, and the
    /// keyboard, which is entirely about "the next one", stops working after the first press.
    /// </remarks>
    private void KeepingPlace(Action rebuild)
    {
        var at = _articles.SelectedIndex;
        var offset = _articles.Scroll?.Offset;

        rebuild();

        if (at >= 0 && at < _showing.Count)
        {
            _openOnSelect = false;
            _articles.SelectedIndex = at;
            _openOnSelect = true;
        }

        if (offset is { } where && _articles.Scroll is { } scroll) scroll.Offset = where;
    }

    /// <summary>Redraws the list without moving the scroll, so a changed row redraws in place.</summary>
    private void RefreshCards()
    {
        if (_selected is not { } row) return;

        var offset = _articles.Scroll?.Offset;
        _showing = Articles(row);
        _articles.ItemsSource = Shaped(_showing);
        if (offset is { } where && _articles.Scroll is { } scroll) scroll.Offset = where;
    }

    /// <summary>What the list is showing, in the order it is showing it.</summary>
    private List<MessageSummary> _showing = [];

    private void ShowReading(bool showing)
    {
        _reading.IsVisible = showing;
        _empty.IsVisible = false;
        _readingHost.IsVisible = showing;

        if (Child is not Grid grid) return;

        // The column goes to nothing with the pane, so the list spreads into it rather than the
        // window carrying a wide grey margin.
        grid.ColumnDefinitions[4].Width = showing ? new GridLength(ReadingWidth) : new GridLength(0);
        grid.Children[3].IsVisible = showing;
    }
    // ---- What the bar and the row buttons press ---------------------------------------------------

    /// <summary>Redraws the list from the store, keeping the place and the selection.</summary>
    public void Refresh() => RefreshCards();

    /// <summary>
    /// Redraws after a board has changed: the pane's counts, and the list if a board is open.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Reload"/>. A full reload rebuilds the list from scratch and drops the
    /// selection with it, so saving an article to a board would leave nothing selected — and the
    /// next command a reader pressed, or the next <c>j</c>, would act on nothing. Saving is not a
    /// gesture that should cost you your place.
    /// </remarks>
    public void RefreshBoards() => KeepingPlace(() => { BuildNav(); RefreshCards(); });

    /// <summary>
    /// Marks everything showing as read, which is the button a reader presses most.
    /// </summary>
    /// <returns>How many were not already read.</returns>
    public int MarkAllRead()
    {
        if (_selected is not { } row || _account() is not { } account) return 0;

        var unread = Articles(row).Where(m => !m.IsRead).Select(m => m.Id).ToList();
        if (unread.Count == 0) return 0;

        account.Mail.SetRead(unread, read: true);
        Reload();
        return unread.Count;
    }

    /// <summary>
    /// The article a board command acts on, and the control to hang its menu off.
    /// </summary>
    /// <remarks>
    /// The ribbon has no row to hang a flyout from, so it hands over its own button; a row's own
    /// button hands over itself. Either way the menu opens where the press was.
    /// </remarks>
    public MessageSummary? ArticleForBoard => SelectedArticle;

    /// <summary>Takes the selected article off the board the pane has open.</summary>
    /// <returns>False when the pane is not on a board, which is what the bar greys.</returns>
    public bool RemoveFromOpenBoard()
    {
        if (_selected is not { Kind: FeedNavKind.Board, Board: { } board }) return false;
        if (SelectedArticle is not { } article || _account() is not { } account) return false;

        account.Mail.RemoveFromBoard([article.Id], board.Id);
        Status = $"“{article.Subject}” taken off {board.Name}.";

        RefreshBoards();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Opens the board the pane's row stands for, by name. What a save jumps to.</summary>
    public bool ShowBoard(string name)
    {
        if (_rows.FirstOrDefault(r => r.Kind == FeedNavKind.Board
                                      && string.Equals(r.Label, name, StringComparison.OrdinalIgnoreCase))
            is not { } row) return false;

        Select(row, keepReading: false);
        return true;
    }

    /// <summary>Keeps the selected article to come back to, or stops keeping it.</summary>
    public void ToggleReadLater()
    {
        if (SelectedArticle is not { } article || _account() is not { } account) return;

        account.Mail.SetFlagged(article.Id, !article.IsFlagged);
        Status = article.IsFlagged
            ? $"“{article.Subject}” taken off Read Later."
            : $"“{article.Subject}” kept for later.";

        KeepingPlace(() => { BuildNav(); RefreshCards(); });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the article on the publisher's own site.</summary>
    public void OpenOriginal()
    {
        if (SelectedArticle is not { } article) return;
        if (article.FeedLink is not { Length: > 0 } link)
        {
            Status = "That article carries no address of its own.";
            return;
        }

        OpenExternally(link);
    }

    /// <summary>
    /// Hands an address to the desktop.
    /// </summary>
    /// <remarks>
    /// Through xdg-open rather than a browser named here: which browser is the desktop's
    /// business, and naming one is how a Linux application ends up opening the wrong thing.
    /// </remarks>
    private void OpenExternally(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            Status = "That is not an address that can be opened.";
            return;
        }

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true },
            };
            process.Start();
            Status = $"Opened {address.Host}.";
            Log.Info($"Feeds: opened {address.AbsoluteUri} in the desktop's browser.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "The desktop could not open that address.";
            Log.Warn($"Feeds: {address.AbsoluteUri} could not be opened.", ex);
        }
    }

    /// <summary>Deletes the selected article. It is a message, so this is the mail delete.</summary>
    public void DeleteSelected()
    {
        if (SelectedArticle is not { } article || _account() is not { } account) return;

        // An article somebody put on a board is not deleted by clearing out the feed it arrived
        // in. Deleting it would take it off every board it is on — the join cascades — and a keep
        // pile that quietly loses things is not one anybody keeps using. So it is moved to where
        // saved things live and the reader is told, and the way to actually let it go is to take
        // it off the board first, which is what the button on a board row does.
        if (account.Mail.IsOnAnyBoard(article.Id) && _selected is not { Kind: FeedNavKind.Board })
        {
            if (SavedFolder(account) is { } keep && article.FolderId != keep.Id)
            {
                account.Mail.MoveMessage(article.Id, keep.Id);
                Status = $"“{article.Subject}” is on a board, so it was kept rather than deleted.";
            }
            else
            {
                Status = $"“{article.Subject}” is on a board and was kept.";
            }
        }
        else
        {
            account.Mail.DeleteMessage(article.Id);
            Status = $"“{article.Subject}” deleted.";
        }

        _selectedMessage = 0;
        ShowReading(false);
        Reload();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where a saved thing lives, or null when nothing has been saved yet.</summary>
    private static Folder? SavedFolder(OpenAccount account)
    {
        var folders = account.Mail.Folders(account.Account.Id);
        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == Mailbox.Protocols.FeedReceiver.RootFolder);

        return root is null
            ? null
            : folders.FirstOrDefault(f => f.ParentId == root.Id && f.Name == Mailbox.Protocols.SavedLinks.SavedFolder);
    }

    /// <summary>Marks one article read or unread, from a row's own button.</summary>
    private void SetRead(MessageSummary article, bool read)
    {
        if (_account() is not { } account) return;

        account.Mail.SetRead(article.Id, read);
        KeepingPlace(() => { BuildNav(); RefreshCards(); });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- The keyboard -------------------------------------------------------------------------------

    /// <summary>
    /// The single-key bindings every feed reader has had since Google Reader.
    /// </summary>
    /// <remarks>
    /// Deliberately the same letters Feedly and Inoreader use, so somebody arriving from either
    /// keeps their hands. They are module-local rather than entries in the key map: a bare "j"
    /// registered globally would be a letter the rest of the application could never use, and
    /// these only mean anything while a list of articles has the focus. The commands themselves
    /// are in the catalogue and rebindable in the ordinary way.
    /// <para>
    /// Bubbled, not tunnelled, so the list keeps the arrow keys and anything with a text box in
    /// it keeps its letters.
    /// </para>
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            // Move and open, which is what a reader does nine times out of ten.
            case Key.J when !shift:
                Step(1, open: true);
                break;

            case Key.K when !shift:
                Step(-1, open: true);
                break;

            // Move without opening, for skimming headlines.
            case Key.N:
                Step(1, open: false);
                break;

            case Key.P:
                Step(-1, open: false);
                break;

            // Between feeds rather than between articles.
            case Key.J when shift:
                StepFeed(1);
                break;

            case Key.K when shift:
                StepFeed(-1);
                break;

            case Key.O or Key.Enter when SelectedArticle is { } opening:
                OpenRequested?.Invoke(this, opening.Id);
                break;

            case Key.V:
                OpenOriginal();
                break;

            case Key.M when SelectedArticle is { } toggled:
                SetRead(toggled, !toggled.IsRead);
                break;

            // Mark read and move on: the one that empties a folder.
            case Key.X when SelectedArticle is { } moving:
                SetRead(moving, true);
                Step(1, open: false);
                break;

            case Key.S:
                ToggleReadLater();
                break;

            // The letter both readers this is measured against use for it.
            case Key.B when !shift && SelectedArticle is not null:
                SaveToBoardRequested?.Invoke(this, this);
                break;

            case Key.B when shift:
                if (!RemoveFromOpenBoard()) Status = "Open a board first to take an article off it.";
                break;

            case Key.R:
                RefreshRequested?.Invoke(this, EventArgs.Empty);
                break;

            case Key.A when shift:
                MarkAllRead();
                Changed?.Invoke(this, EventArgs.Empty);
                break;

            case Key.Delete when SelectedArticle is not null:
                DeleteSelected();
                break;

            case Key.OemQuestion when shift:
                ShowShortcuts();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Moves the selection through the list, opening as it goes or not.</summary>
    private void Step(int by, bool open)
    {
        if (_showing.Count == 0) return;

        var at = _articles.SelectedIndex + by;
        if (at < 0 || at >= _showing.Count) return;

        // Setting the index raises SelectionChanged, which opens the article. When the reader
        // asked only to move, the opening is suppressed for that one change.
        _openOnSelect = open;
        _articles.SelectedIndex = at;
        _openOnSelect = true;

        _articles.ScrollIntoView(at);
    }

    private bool _openOnSelect = true;

    /// <summary>Moves to the next or previous feed in the pane.</summary>
    private void StepFeed(int by)
    {
        var feeds = _rows.Where(r => r.Kind is FeedNavKind.Feed or FeedNavKind.Category
                                     or FeedNavKind.Today or FeedNavKind.Unread or FeedNavKind.ReadLater).ToList();
        if (feeds.Count == 0) return;

        var at = _selected is null ? 0 : feeds.FindIndex(r => r.Kind == _selected.Kind && r.Label == _selected.Label);
        at = Math.Clamp(at + by, 0, feeds.Count - 1);

        Select(feeds[at], keepReading: false);
    }

    /// <summary>The bindings, as a list the reader can read. What "?" opens.</summary>
    private void ShowShortcuts()
    {
        (string Key, string Does)[] bindings =
        [
            ("J / K", "Next and previous article, opening it"),
            ("N / P", "Next and previous without opening"),
            ("Shift+J / Shift+K", "Next and previous feed"),
            ("O or Enter", "Open the article in a window"),
            ("V", "Open the original on the publisher's site"),
            ("M", "Mark as read or unread"),
            ("X", "Mark read and move on"),
            ("S", "Keep for Read Later"),
            ("B", "Save to a board"),
            ("Shift+B", "Take off the board you are reading"),
            ("R", "Update the feeds"),
            ("Shift+A", "Mark everything showing as read"),
            ("Delete", "Delete the article"),
            ("?", "This list"),
        ];

        var rows = new StackPanel { Spacing = 6 };
        foreach (var (key, does) in bindings)
        {
            var name = new TextBlock { Text = key, FontWeight = FontWeight.SemiBold, Width = 150 };
            name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");

            var text = new TextBlock { Text = does };
            text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

            rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { name, text },
            });
        }

        ShortcutsRequested?.Invoke(this, rows);
    }

    /// <summary>Raised when the reader presses "?", with the list to show them.</summary>
    public event EventHandler<Control>? ShortcutsRequested;

    /// <summary>
    /// Raised when an article was opened that is only a teaser, so its page can be read.
    /// </summary>
    /// <remarks>
    /// The workspace does not do it itself: reading a publisher's page is a network request and
    /// this is a view. What comes back reaches it again through <see cref="Reopen"/>.
    /// </remarks>
    public event EventHandler<MessageSummary>? FullTextWanted;

    /// <summary>
    /// Articles whose page has already been asked for this session.
    /// </summary>
    /// <remarks>
    /// A page that yields nothing usable would otherwise be re-fetched every time its row is
    /// opened, which for a reader flicking through a folder is a request per keystroke.
    /// </remarks>
    private readonly HashSet<long> _tried = [];

    /// <summary>
    /// Shows an article again from the store, after something has changed underneath it.
    /// </summary>
    /// <remarks>
    /// Only when it is still the one on screen: reading a page takes a moment, and a reader who
    /// has moved on in the meantime must not have the article they were reading replaced by the
    /// one they left.
    /// </remarks>
    public void Reopen(long messageId)
    {
        if (_selectedMessage != messageId) return;
        if (_account() is not { } account || account.Mail.GetMessage(messageId) is not { } article) return;

        Open(article);
        KeepingPlace(() => RefreshCards());
    }

    // ---- Layout -------------------------------------------------------------------------------------

    /// <summary>How the articles are laid out right now.</summary>
    private FeedLayout _layout = FeedLayout.Magazine;

    /// <summary>Where a feed's chosen layout is kept, keyed by the row it belongs to.</summary>
    private static string LayoutKey(FeedNavRow row)
        => $"rss.view.{row.Kind}.{row.Label}".ToLowerInvariant();

    /// <summary>Where a row's reading order is kept.</summary>
    private static string OrderKey(FeedNavRow row)
        => $"rss.order.{row.Kind}.{row.Label}".ToLowerInvariant();

    /// <summary>Where a row's "hide what I have read" is kept.</summary>
    private static string UnreadOnlyKey(FeedNavRow row)
        => $"rss.unreadonly.{row.Kind}.{row.Label}".ToLowerInvariant();

    /// <summary>
    /// Whether this row is read oldest first.
    /// </summary>
    /// <remarks>
    /// Per row, because it is per row that it matters: a news feed is read newest first and a
    /// serialised blog or a podcast is read in the order it was written, and a reader should not
    /// have to keep switching. Every reader worth the name offers this and this one did not.
    /// </remarks>
    private bool OldestFirstFor(FeedNavRow row) => App.Settings.GetBool(OrderKey(row), false);

    /// <summary>Whether this row shows only what has not been read.</summary>
    /// <remarks>
    /// The standing Unread view answers this across everything; a reader clearing out one busy
    /// feed wants it for that feed, which is what every reader's "hide read articles" is.
    /// </remarks>
    private bool UnreadOnlyFor(FeedNavRow row) => App.Settings.GetBool(UnreadOnlyKey(row), false);

    /// <summary>
    /// The layout this row was last read in.
    /// </summary>
    /// <remarks>
    /// Per feed rather than one setting for all of them, which is how both readers this is
    /// measured against remember it — and it is the right way round: a photography feed wants
    /// Cards and a headline feed wants one line each, and a reader should not have to keep
    /// switching.
    /// </remarks>
    private FeedLayout LayoutFor(FeedNavRow row)
        => Enum.TryParse<FeedLayout>(App.Settings.GetString(LayoutKey(row)), out var kept)
            ? kept
            : FeedLayout.Magazine;

    private void SetLayout(FeedLayout layout)
    {
        if (_selected is { } row) App.Settings.Set(LayoutKey(row), layout.ToString());

        _layout = layout;
        ApplyLayout();
        RefreshCards();
    }

    /// <summary>Points the list at the template for the layout, and marks the button that is on.</summary>
    private void ApplyLayout()
    {
        foreach (var (which, button) in _viewButtons)
        {
            button.Classes.Remove("active");
            if (which == _layout) button.Classes.Add("active");
        }

        // Cards go across, the other two go down — and all three still virtualise, because a
        // wrap panel does not: the grid is a stack of rows, each row a few tiles.
        _articles.ItemTemplate = _layout switch
        {
            FeedLayout.TextOnly => new FuncDataTemplate<MessageSummary>((m, _) => Line(m), supportsRecycling: true),
            FeedLayout.Cards => new FuncDataTemplate<MessageSummary[]>((row, _) => TileRow(row), supportsRecycling: true),
            _ => new FuncDataTemplate<MessageSummary>((m, _) => Card(m), supportsRecycling: true),
        };

        _articles.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());

        // A grid row selects as one thing, which is not what a reader means by choosing an
        // article; in Cards the tile itself carries the press.
        _articles.SelectionMode = _layout == FeedLayout.Cards ? SelectionMode.Single : SelectionMode.Single;
    }

    /// <summary>
    /// One article as a single line: headline, source, age. What a reader skimming two hundred
    /// headlines actually wants, and the densest of the three.
    /// </summary>
    private Control Line(MessageSummary message)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(16, 6, 10, 6),
        };

        var title = new TextBlock
        {
            Text = message.Subject,
            FontSize = 13.5,
            FontWeight = message.IsRead ? FontWeight.Normal : FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
            message.IsRead ? "text.secondary.brush" : "text.primary.brush");
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var source = new TextBlock
        {
            Text = message.DisplayFrom,
            FontSize = 11,
            Margin = new Thickness(12, 0, 0, 0),
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        source[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
        Grid.SetColumn(source, 1);
        grid.Children.Add(source);

        var age = new TextBlock
        {
            Text = Ago(message.Received),
            FontSize = 11,
            Width = 46,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        age[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
        Grid.SetColumn(age, 2);
        grid.Children.Add(age);

        var row = new Border { Background = Brushes.Transparent, Child = grid };
        row.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, message.Id);
        };

        return row;
    }

    /// <summary>
    /// One article as a tile: the picture large, the headline under it. For the feeds that are
    /// mostly photographs, where a thumbnail the size of a stamp is no use.
    /// </summary>
    private Control CardTile(MessageSummary message)
    {
        var picture = Thumbnail(message, TileWidth, TileHeight);

        var title = new TextBlock
        {
            Text = message.Subject,
            FontSize = 13.5,
            FontWeight = message.IsRead ? FontWeight.Normal : FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 8, 0, 0),
        };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
            message.IsRead ? "text.secondary.brush" : "text.primary.brush");

        var source = new TextBlock
        {
            Text = $"{message.DisplayFrom} · {Ago(message.Received)}",
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        source[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        var tile = new Border
        {
            Width = TileWidth,
            Background = Brushes.Transparent,
            Margin = new Thickness(16, 10, 0, 10),
            Child = new StackPanel { Children = { picture, title, source } },
        };

        tile.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            e.Handled = true;

            // The list selects a row of three, so the tile says which of the three was meant.
            Choose(message);
            Open(message);
        };

        tile.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, message.Id);
        };

        return tile;
    }

    private const double TileWidth = 216;
    private const double TileHeight = 128;

    /// <summary>How many tiles fit across the capped column.</summary>
    private const int TilesAcross = 3;

    /// <summary>
    /// The list as the current layout wants it: articles one after another, or grouped into rows
    /// of tiles for the grid.
    /// </summary>
    private System.Collections.IEnumerable Shaped(List<MessageSummary> articles)
        => _layout == FeedLayout.Cards ? articles.Chunk(TilesAcross).ToList() : articles;

    /// <summary>One row of the grid.</summary>
    private Control TileRow(MessageSummary[] articles)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var article in articles) row.Children.Add(CardTile(article));
        return row;
    }

}
