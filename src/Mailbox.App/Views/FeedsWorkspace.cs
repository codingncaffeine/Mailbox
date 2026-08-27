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
    private const double ThumbnailWidth = 132;
    private const double ThumbnailHeight = 76;

    private readonly FeedSubscriptions _feeds;
    private readonly Func<OpenAccount?> _account;
    private readonly FeedThumbnails _pictures;

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

    public FeedsWorkspace(FeedSubscriptions feeds, Func<OpenAccount?> account, FeedThumbnails pictures)
    {
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _pictures = pictures ?? throw new ArgumentNullException(nameof(pictures));

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

    /// <summary>Raised when the reader opens an article in a window of its own.</summary>
    public event EventHandler<long>? OpenRequested;

    /// <summary>Raised when what is selected changes, so the bar can re-decide what it can do.</summary>
    public event EventHandler? Changed;

    /// <summary>The subscription the pane has selected, or the one the selected article came from.</summary>
    public FeedSubscription? SelectedFeed => _selected?.Feed ?? _articleFeed;

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
        => _articles.SelectedItem is MessageSummary chosen && _account() is { } account
            ? account.Mail.GetMessage(chosen.Id) ?? chosen
            : null;

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

        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 8),
            Spacing = 10,
            Children = { _markAllRead, views },
        };

        return strip;
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

        IReadOnlyList<long> FoldersFor(IEnumerable<FeedSubscription> subscriptions)
            => [.. subscriptions.Select(f => byPath.TryGetValue(f.FolderPath, out var folder) ? folder.Id : 0).Where(id => id != 0)];

        var all = _feeds.All;
        var everything = FoldersFor(all);
        var totalUnread = (int)all.Sum(UnreadIn);

        _rows.Add(new FeedNavRow("Today", totalUnread, FeedNavKind.Today) { Folders = everything });
        _rows.Add(new FeedNavRow("Unread", totalUnread, FeedNavKind.Unread) { Folders = everything });
        _rows.Add(new FeedNavRow("Read Later", 0, FeedNavKind.ReadLater) { Folders = everything });

        foreach (var row in _rows.ToList()) _nav.Children.Add(NavButton(row, indent: 0));

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

                foreach (var id in row.Folders) _feedByFolder[id] = feed;

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

            foreach (var id in row.Folders) _feedByFolder[id] = feed;

            _rows.Add(row);
            _nav.Children.Add(NavButton(row, indent: 0));
        }

        if (all.Count == 0) _nav.Children.Add(NoFeedsYet());
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
            FontWeight = row.Unread > 0 ? FontWeight.SemiBold : FontWeight.Normal,
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
            count[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.unreadcount.brush");
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

        return button;
    }

    private static string Glyph(FeedNavKind kind) => kind switch
    {
        FeedNavKind.Today => "calendar",
        FeedNavKind.Unread => "unread",
        FeedNavKind.ReadLater => "flag",
        FeedNavKind.Category => "folder",
        _ => "rss",
    };

    // ---- The article list ------------------------------------------------------------------------

    private void Select(FeedNavRow? row, bool keepReading)
    {
        _selected = row;
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

        return [.. filtered.OrderByDescending(m => m.Received).Take(500)];
    }

    private static List<MessageSummary> Everything(OpenAccount account, IReadOnlyList<long> folders)
    {
        var found = new List<MessageSummary>();
        foreach (var folder in folders) found.AddRange(account.Mail.Messages(folder));
        return found;
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
            Margin = new Thickness(16, 10, 10, 10),
        };

        var picture = Thumbnail(message);
        Grid.SetColumn(picture, 0);
        grid.Children.Add(picture);

        var title = new TextBlock
        {
            Text = message.Subject,
            FontSize = 14,
            FontWeight = message.IsRead ? FontWeight.Normal : FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
            message.IsRead ? "list.row.read.text.brush" : "list.row.unread.text.brush");

        var source = new TextBlock
        {
            Text = $"{message.DisplayFrom} · {Ago(message.Received)}",
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        source[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        var snippet = new TextBlock
        {
            Text = message.Preview,
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        snippet[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("list.row.preview.text.brush");

        var text = new StackPanel { Margin = new Thickness(14, 0, 8, 0), Children = { title, source, snippet } };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = RowActions(message);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        // The buttons appear under the pointer, as the reference's do: a row carrying four
        // buttons at all times is a row nobody can read.
        var row = new Border { Background = Brushes.Transparent, Child = grid };
        row.PointerEntered += (_, _) => actions.IsVisible = true;
        row.PointerExited += (_, _) => actions.IsVisible = false;
        row.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, message.Id);
        };

        return row;
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

        if (message.FeedImage is { Length: > 0 } url)
        {
            if (_pictures.Ready(url) is { } already)
            {
                image.Source = already;
                image.IsVisible = true;
            }
            else
            {
                _pictures.Want(url, bitmap =>
                {
                    image.Source = bitmap;
                    image.IsVisible = true;
                });
            }
        }

        return tile;
    }

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

        account.Mail.DeleteMessage(article.Id);
        _selectedMessage = 0;
        ShowReading(false);
        Reload();

        Status = $"“{article.Subject}” deleted.";
        Changed?.Invoke(this, EventArgs.Empty);
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

    // ---- Layout -------------------------------------------------------------------------------------

    /// <summary>How the articles are laid out right now.</summary>
    private FeedLayout _layout = FeedLayout.Magazine;

    /// <summary>Where a feed's chosen layout is kept, keyed by the row it belongs to.</summary>
    private static string LayoutKey(FeedNavRow row)
        => $"rss.view.{row.Kind}.{row.Label}".ToLowerInvariant();

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
            FontSize = 13,
            FontWeight = message.IsRead ? FontWeight.Normal : FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
            message.IsRead ? "list.row.read.text.brush" : "list.row.unread.text.brush");
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
            FontSize = 13,
            FontWeight = message.IsRead ? FontWeight.Normal : FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 8, 0, 0),
        };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
            message.IsRead ? "list.row.read.text.brush" : "list.row.unread.text.brush");

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
            _openOnSelect = false;
            _articles.SelectedIndex = _showing.FindIndex(m => m.Id == message.Id) / TilesAcross;
            _openOnSelect = true;
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
