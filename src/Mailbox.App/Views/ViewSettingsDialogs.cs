using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Search;
using Mailbox.Core.Views;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>What the view dialogs share: the themed labels, boxes and button rows.</summary>
internal static class ViewDialogKit
{
    internal static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    internal static TextBlock Label(string text, bool bold = false, bool subtle = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (bold) block.FontWeight = FontWeight.SemiBold;
        Bind(block, TextBlock.ForegroundProperty, subtle ? "dialog.foreground.subtle.brush" : "dialog.foreground.brush");
        return block;
    }

    /// <summary>A checkbox or radio in the dialog's ink.</summary>
    internal static T Ink<T>(T control) where T : TemplatedControl
    {
        Bind(control, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
        return control;
    }

    internal static Border Boxed(Control content, double? width = null, double? height = null)
    {
        var box = new Border { BorderThickness = new Thickness(1), Child = content };
        if (width is { } w) box.Width = w;
        if (height is { } h) box.Height = h;
        Bind(box, Border.BackgroundProperty, "dialog.surface.brush");
        Bind(box, Border.BorderBrushProperty, "dialog.border.brush");
        return box;
    }

    /// <summary>A list box on the dialog's surface, its rows in the surface's ink.</summary>
    internal static ListBox SurfaceList(double width, double height)
    {
        var list = new ListBox { Width = width, Height = height };
        Bind(list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");
        list.BorderThickness = new Thickness(1);
        return list;
    }

    internal static TextBlock SurfaceText(string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Bind(block, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        return block;
    }

    internal static StackPanel Buttons(params Control[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    internal static Button Ok(Action click, string label = "OK")
    {
        var button = new Button { Content = label, IsDefault = true, Width = 74 };
        button.Click += (_, _) => click();
        return button;
    }

    internal static Button Cancel(Window window, string label = "Cancel")
    {
        var button = new Button { Content = label, IsCancel = true, Width = 74 };
        button.Click += (_, _) => window.Close();
        return button;
    }

    /// <summary>The window every view dialog is: sized as told, themed, centred on its owner.</summary>
    internal static Window Dialog(string title, double width, double height, Control body)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window, body);
        Bind(window, TemplatedControl.BackgroundProperty, "dialog.background.brush");
        return window;
    }

    /// <summary>The arrangement names a Group By or Sort By combo offers, in the Arrange By order.</summary>
    internal static readonly IReadOnlyList<string> SortFields =
        ["Date", "From", "To", "Categories", "Flag", "Size", "Subject", "Type", "Attachments", "Account", "Importance"];

    /// <summary>The ink tokens a rule may choose, by the name the reader sees.</summary>
    internal static readonly IReadOnlyList<(string Label, string? Token)> InkChoices =
    [
        ("Automatic", null),
        ("Red", "status.danger"),
        ("Blue", "list.row.unread.text"),
        ("Green", "category.green"),
        ("Orange", "category.orange"),
        ("Purple", "category.purple"),
        ("Yellow", "category.yellow"),
        ("Grey", "text.secondary"),
    ];
}

/// <summary>
/// Advanced View Settings: the reference's dialog of seven buttons, each opening the editor
/// for one part of the view, with the part's summary beside it; Reset Current View; OK.
/// </summary>
public sealed class AdvancedViewSettingsDialog : Window
{
    private MailView _view;
    private readonly OpenAccount? _account;
    private readonly Dictionary<string, TextBlock> _summaries = [];

    /// <summary>The edited view when OK was pressed; null when cancelled.</summary>
    public MailView? Result { get; private set; }

    /// <summary>True when Reset Current View was pressed — the caller resets rather than applies.</summary>
    public bool ResetRequested { get; private set; }

    public AdvancedViewSettingsDialog(MailView view, OpenAccount? account)
    {
        _view = view;
        _account = account;

        Title = $"Advanced View Settings: {view.Name}";
        Width = 560;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        DialogChrome.Apply(this, Layout());
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
        Refresh();
    }

    private Control Layout()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
        };

        void Row(int row, string key, string label, Func<Task> open)
        {
            var button = new Button { Content = label, Width = 178, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            button.Click += async (_, _) => await open();
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 0);
            grid.Children.Add(button);

            var summary = ViewDialogKit.Label(string.Empty);
            summary.Margin = new Thickness(6, 0, 0, 8);
            summary.MaxWidth = 340;
            Grid.SetRow(summary, row);
            Grid.SetColumn(summary, 1);
            grid.Children.Add(summary);
            _summaries[key] = summary;
        }

        Row(0, "columns", "Columns…", ColumnsAsync);
        Row(1, "group", "Group By…", GroupByAsync);
        Row(2, "sort", "Sort…", SortAsync);
        Row(3, "filter", "Filter…", FilterAsync);
        Row(4, "other", "Other Settings…", OtherSettingsAsync);
        Row(5, "formats", "Conditional Formatting…", ConditionalFormattingAsync);
        Row(6, "columnformats", "Format Columns…", FormatColumnsAsync);

        var reset = new Button { Content = "Reset Current View", Padding = new Thickness(10, 4), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => { ResetRequested = true; Close(); };

        var ok = ViewDialogKit.Ok(() => { Result = _view; Close(); });
        var cancel = ViewDialogKit.Cancel(this);

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right);
        bottom.Children.Add(reset);

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Children = { bottom } },
                new StackPanel { Children = { ViewDialogKit.Label("Description", bold: true), grid } },
            },
        };
    }

    /// <summary>The summaries beside the buttons, from the working copy.</summary>
    private void Refresh()
    {
        _summaries["columns"].Text = string.Join(", ", _view.Columns.Select(c => ViewFields.Label(c.Id)));
        _summaries["group"].Text = !_view.ShowInGroups ? "None"
            : _view.GroupBy is { } by ? $"{by} ({(_view.GroupAscending ? "ascending" : "descending")})"
            : "Automatically group according to arrangement";
        _summaries["sort"].Text = $"{_view.SortField} ({(_view.SortDescending ? "descending" : "ascending")})";
        _summaries["filter"].Text = _view.Filter.Trim().Length == 0 ? "Off" : _view.Filter.Trim();
        _summaries["other"].Text = "Fonts and other Table View settings";
        _summaries["formats"].Text = "User defined fonts on each message";
        _summaries["columnformats"].Text = "Specify the display formats for each field";
    }

    private async Task ColumnsAsync()
    {
        var dialog = new ShowColumnsDialog(_view.Columns);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } columns) { _view = _view with { Columns = columns }; Refresh(); }
    }

    private async Task GroupByAsync()
    {
        var dialog = new GroupByDialog(_view);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } edited) { _view = edited; Refresh(); }
    }

    private async Task SortAsync()
    {
        var dialog = new SortDialog(_view);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } edited) { _view = edited; Refresh(); }
    }

    private async Task FilterAsync()
    {
        var dialog = new FilterDialog(_view.Filter, "Filter");
        await dialog.ShowDialog(this);
        if (dialog.Result is { } filter) { _view = _view with { Filter = filter }; Refresh(); }
    }

    private async Task OtherSettingsAsync()
    {
        var dialog = new OtherSettingsDialog(_view);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } edited) { _view = edited; Refresh(); }
    }

    private async Task ConditionalFormattingAsync()
    {
        var dialog = new ConditionalFormattingDialog(_view.Formats);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } formats) { _view = _view with { Formats = formats }; Refresh(); }
    }

    private async Task FormatColumnsAsync()
    {
        var dialog = new FormatColumnsDialog(_view);
        await dialog.ShowDialog(this);
        if (dialog.Result is { } edited) { _view = edited; Refresh(); }
    }
}

/// <summary>Show Columns: the available fields on the left, the shown ones in order on the right, and the buttons between.</summary>
public sealed class ShowColumnsDialog : Window
{
    private readonly List<string> _shown;
    private readonly Dictionary<string, double> _widths;
    private readonly ListBox _available = ViewDialogKit.SurfaceList(200, 300);
    private readonly ListBox _chosen = ViewDialogKit.SurfaceList(200, 300);

    public IReadOnlyList<ViewColumn>? Result { get; private set; }

    public ShowColumnsDialog(IReadOnlyList<ViewColumn> columns)
    {
        _shown = [.. columns.Select(c => c.Id)];
        _widths = columns.ToDictionary(c => c.Id, c => c.Width);

        Title = "Show Columns";
        Width = 600;
        Height = 440;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _available.ItemTemplate = new FuncDataTemplate<string>((id, _) => ViewDialogKit.SurfaceText(ViewFields.Label(id)));
        _chosen.ItemTemplate = new FuncDataTemplate<string>((id, _) => ViewDialogKit.SurfaceText(ViewFields.Label(id)));

        DialogChrome.Apply(this, Layout());
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
        Refresh();
    }

    private Control Layout()
    {
        var add = new Button { Content = "Add ->", Width = 100 };
        add.Click += (_, _) => { if (_available.SelectedItem is string id) { _shown.Add(id); Refresh(id); } };
        var remove = new Button { Content = "<- Remove", Width = 100 };
        remove.Click += (_, _) => { if (_chosen.SelectedItem is string id) { _shown.Remove(id); Refresh(); } };
        var up = new Button { Content = "Move Up", Width = 100 };
        up.Click += (_, _) => Move(-1);
        var down = new Button { Content = "Move Down", Width = 100 };
        down.Click += (_, _) => Move(1);

        var middle = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
        middle.Children.Add(add);
        middle.Children.Add(remove);
        middle.Children.Add(new Panel { Height = 12 });
        middle.Children.Add(up);
        middle.Children.Add(down);

        var columns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Available columns:"), _available } },
                middle,
                new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Show these columns in this order:"), _chosen } },
            },
        };

        var ok = ViewDialogKit.Ok(() =>
        {
            Result = [.. _shown.Select(id => new ViewColumn(id, _widths.TryGetValue(id, out var w) ? w : ViewFields.DefaultWidth(id)))];
            Close();
        });

        return new StackPanel
        {
            Margin = new Thickness(18),
            Children = { columns, ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(this)) },
        };
    }

    private void Move(int direction)
    {
        if (_chosen.SelectedItem is not string id) return;
        var index = _shown.IndexOf(id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _shown.Count) return;
        (_shown[index], _shown[target]) = (_shown[target], _shown[index]);
        Refresh(id);
    }

    private void Refresh(string? select = null)
    {
        // The built-in fields in the reference's order, then the plugins' columns — ordinary
        // offerings here, placed and widened like any field.
        _available.ItemsSource = ViewFields.All
            .Concat(App.Plugins.Columns().Select(c => c.Id))
            .Where(f => !_shown.Contains(f))
            .ToList();
        _chosen.ItemsSource = _shown.ToList();
        if (select is not null) _chosen.SelectedItem = select;
    }
}

/// <summary>Group By: automatic by arrangement, or a field of the reader's, and how the groups open.</summary>
public sealed class GroupByDialog : Window
{
    public MailView? Result { get; private set; }

    public GroupByDialog(MailView view)
    {
        Title = "Group By";
        Width = 460;
        Height = 330;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var automatic = ViewDialogKit.Ink(new CheckBox { Content = "Automatically group according to arrangement", IsChecked = view.GroupBy is null && view.ShowInGroups });
        var field = new ComboBox { Width = 200, ItemsSource = new[] { "(none)" }.Concat(ViewDialogKit.SortFields).ToList() };
        field.SelectedIndex = view.GroupBy is { } by ? Math.Max(0, ViewDialogKit.SortFields.ToList().IndexOf(by) + 1) : 0;
        var ascending = ViewDialogKit.Ink(new RadioButton { Content = "Ascending", GroupName = "groupdir", IsChecked = view.GroupAscending });
        var descending = ViewDialogKit.Ink(new RadioButton { Content = "Descending", GroupName = "groupdir", IsChecked = !view.GroupAscending });
        var expand = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "As last viewed", "All expanded", "All collapsed" } };
        expand.SelectedIndex = view.GroupsExpanded switch { true => 1, false => 2, _ => 0 };

        void Enable() { var manual = automatic.IsChecked != true; field.IsEnabled = manual; ascending.IsEnabled = manual; descending.IsEnabled = manual; }
        automatic.IsCheckedChanged += (_, _) => Enable();
        Enable();

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                automatic,
                ViewDialogKit.Label("Group items by"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { field, ascending, descending } },
                ViewDialogKit.Label("Expand/collapse defaults:"),
                expand,
                ViewDialogKit.Buttons(ViewDialogKit.Ok(() =>
                {
                    var manual = automatic.IsChecked != true;
                    var chosen = manual && field.SelectedIndex > 0 ? ViewDialogKit.SortFields[field.SelectedIndex - 1] : null;
                    Result = view with
                    {
                        GroupBy = chosen,
                        ShowInGroups = !manual || chosen is not null,
                        GroupAscending = ascending.IsChecked == true,
                        GroupsExpanded = expand.SelectedIndex switch { 1 => true, 2 => false, _ => null },
                    };
                    Close();
                }), ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }
}

/// <summary>Sort: which field, which way. The date is always the tiebreak beneath.</summary>
public sealed class SortDialog : Window
{
    public MailView? Result { get; private set; }

    public SortDialog(MailView view)
    {
        Title = "Sort";
        Width = 420;
        Height = 240;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var field = new ComboBox { Width = 200, ItemsSource = ViewDialogKit.SortFields };
        field.SelectedIndex = Math.Max(0, ViewDialogKit.SortFields.ToList().IndexOf(view.SortField));
        var ascending = ViewDialogKit.Ink(new RadioButton { Content = "Ascending", GroupName = "sortdir", IsChecked = !view.SortDescending });
        var descending = ViewDialogKit.Ink(new RadioButton { Content = "Descending", GroupName = "sortdir", IsChecked = view.SortDescending });

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                ViewDialogKit.Label("Sort items by"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { field, ascending, descending } },
                ViewDialogKit.Label("Then by the date received, newest first.", subtle: true),
                ViewDialogKit.Buttons(ViewDialogKit.Ok(() =>
                {
                    Result = view with { SortField = ViewDialogKit.SortFields[Math.Max(0, field.SelectedIndex)], SortDescending = descending.IsChecked == true };
                    Close();
                }), ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }
}

/// <summary>
/// Filter — and a conditional-formatting rule's Condition, which is the same dialog: the
/// reference's Messages and More Choices fields, compiled to the search box's syntax, with the
/// query itself shown and editable beneath so nothing the fields cannot say is lost.
/// </summary>
public sealed class FilterDialog : Window
{
    private static readonly string[] InChoices = ["subject field only", "subject field and message body", "frequently-used text fields"];
    private static readonly string[] WhereChoices = ["the only person on the To line", "on the To line with other people", "on the CC line with other people"];
    private static readonly string[] TimeFields = ["none", "received", "sent", "due"];
    private static readonly (string Label, string Value)[] TimeSpans =
    [
        ("anytime", ""), ("yesterday", "yesterday"), ("today", "today"), ("in the last 7 days", "last7days"),
        ("last week", "lastweek"), ("this week", "thisweek"), ("last month", "lastmonth"), ("this month", "thismonth"), ("this year", "thisyear"),
    ];

    private readonly TextBox _words = new() { Width = 300 };
    private readonly ComboBox _in = new() { Width = 240, ItemsSource = InChoices, SelectedIndex = 0 };
    private readonly TextBox _from = new() { Width = 300 };
    private readonly TextBox _sentTo = new() { Width = 300 };
    private readonly CheckBox _whereIAm = ViewDialogKit.Ink(new CheckBox { Content = "Where I am:" });
    private readonly ComboBox _where = new() { Width = 240, ItemsSource = WhereChoices, SelectedIndex = 0 };
    private readonly ComboBox _timeField = new() { Width = 110, ItemsSource = TimeFields, SelectedIndex = 0 };
    private readonly ComboBox _timeSpan = new() { Width = 160, ItemsSource = TimeSpans.Select(t => t.Label).ToList(), SelectedIndex = 0 };
    private readonly TextBox _categories = new() { Width = 300 };
    private readonly ComboBox _read = new() { Width = 160, ItemsSource = new[] { "either", "unread", "read" }, SelectedIndex = 0 };
    private readonly ComboBox _attachments = new() { Width = 160, ItemsSource = new[] { "either", "one or more attachments", "no attachments" }, SelectedIndex = 0 };
    private readonly ComboBox _importance = new() { Width = 160, ItemsSource = new[] { "any", "high", "normal", "low" }, SelectedIndex = 0 };
    private readonly ComboBox _flagged = new() { Width = 160, ItemsSource = new[] { "either", "flagged", "not flagged" }, SelectedIndex = 0 };
    private readonly ComboBox _sizeBound = new() { Width = 130, ItemsSource = new[] { "doesn't matter", "less than", "greater than" }, SelectedIndex = 0 };
    private readonly NumericUpDown _sizeKb = new() { Width = 100, Minimum = 1, Maximum = 1_000_000, Value = 100, Increment = 10 };
    private readonly TextBox _query = new() { Width = 460 };
    private bool _refreshing;

    /// <summary>The query when OK was pressed — empty for no filter — or null when cancelled.</summary>
    public string? Result { get; private set; }

    public FilterDialog(string query, string title)
    {
        Title = title;
        Width = 560;
        Height = 700;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        DialogChrome.Apply(this, Layout());
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");

        Load(query);
        foreach (var control in new Control[] { _words, _from, _sentTo, _categories, _query }) ((TextBox)control).TextChanged += (_, _) => Sync(control);
        foreach (var combo in new[] { _in, _where, _timeField, _timeSpan, _read, _attachments, _importance, _flagged, _sizeBound }) combo.SelectionChanged += (_, _) => Sync(combo);
        _whereIAm.IsCheckedChanged += (_, _) => Sync(_whereIAm);
        _sizeKb.ValueChanged += (_, _) => Sync(_sizeKb);
    }

    private Control Layout()
    {
        var page = new StackPanel { Spacing = 8 };

        Control Row(string label, Control control, double labelWidth = 150)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var caption = ViewDialogKit.Label(label);
            caption.Width = labelWidth;
            row.Children.Add(caption);
            row.Children.Add(control);
            return row;
        }

        page.Children.Add(ViewDialogKit.Label("Messages", bold: true));
        page.Children.Add(Row("Search for the word(s):", _words));
        page.Children.Add(Row("In:", _in));
        page.Children.Add(Row("From:", _from));
        page.Children.Add(Row("Sent To:", _sentTo));
        var where = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _whereIAm.Width = 150;
        where.Children.Add(_whereIAm);
        where.Children.Add(_where);
        page.Children.Add(where);
        page.Children.Add(Row("Time:", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _timeField, _timeSpan } }));

        page.Children.Add(new Panel { Height = 4 });
        page.Children.Add(ViewDialogKit.Label("More Choices", bold: true));
        page.Children.Add(Row("Categories:", _categories));
        page.Children.Add(Row("Only items that are:", _read));
        page.Children.Add(Row("Only items with:", _attachments));
        page.Children.Add(Row("Whose importance is:", _importance));
        page.Children.Add(Row("Only items which are:", _flagged));
        page.Children.Add(Row("Size (kilobytes):", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _sizeBound, _sizeKb } }));

        page.Children.Add(new Panel { Height = 4 });
        page.Children.Add(ViewDialogKit.Label("Query", bold: true));
        page.Children.Add(ViewDialogKit.Label("The above in the search box's own words; anything typed here that the fields cannot say is kept.", subtle: true));
        page.Children.Add(_query);

        var clear = new Button { Content = "Clear All", Padding = new Thickness(10, 4) };
        clear.Click += (_, _) => Load(string.Empty);

        var ok = ViewDialogKit.Ok(() => { Result = (_query.Text ?? string.Empty).Trim(); Close(); });
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, ViewDialogKit.Cancel(this) } };
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right);
        bottom.Children.Add(clear);

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Children = { bottom } },
                page,
            },
        };
    }

    /// <summary>Fills the fields from a query — what they can say of it — and the query box with all of it.</summary>
    private void Load(string query)
    {
        _refreshing = true;
        try
        {
            var parsed = SearchQuery.Parse(query);
            _words.Text = string.Join(' ', parsed.Words.Concat(parsed.Subject).Concat(parsed.Body).Distinct());
            _in.SelectedIndex = parsed.Body.Count > 0 && parsed.Subject.Count > 0 ? 1 : parsed.Words.Count > 0 && parsed.Subject.Count == 0 && parsed.Body.Count == 0 ? 2 : 0;
            _from.Text = string.Join(' ', parsed.From);
            _sentTo.Text = string.Join(' ', parsed.To);
            _whereIAm.IsChecked = false;
            _where.SelectedIndex = 0;
            _categories.Text = string.Join(", ", parsed.Categories);
            _read.SelectedIndex = parsed.IsRead switch { false => 1, true => 2, _ => 0 };
            _attachments.SelectedIndex = parsed.HasAttachment switch { true => 1, false => 2, _ => 0 };
            _importance.SelectedIndex = parsed.Importance switch { 2 => 1, 1 => 2, 0 => 3, _ => 0 };
            _flagged.SelectedIndex = parsed.IsFlagged switch { true => 1, false => 2, _ => 0 };
            _sizeBound.SelectedIndex = parsed.Size?.Bound switch { Bound.Before => 1, Bound.After => 2, _ => 0 };
            if (parsed.Size is { } size) _sizeKb.Value = Math.Max(1, size.Bytes / 1024);
            _timeField.SelectedIndex = 0;
            _timeSpan.SelectedIndex = 0;

            // The time words are read back from the text itself; a span cannot be told from its word.
            foreach (var token in SearchQuery.Tokens(query))
            {
                var colon = token.IndexOf(':');
                if (colon <= 0) continue;
                var key = token[..colon].ToLowerInvariant();
                var value = token[(colon + 1)..].Trim('"');
                var field = Array.IndexOf(TimeFields, key);
                var span = Array.FindIndex(TimeSpans, t => t.Value.Length > 0 && string.Equals(t.Value, value, StringComparison.OrdinalIgnoreCase));
                if (field > 0 && span > 0) { _timeField.SelectedIndex = field; _timeSpan.SelectedIndex = span; }
            }

            _query.Text = query.Trim();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>A field changed: the query is recompiled from the fields — or the fields reread from the query.</summary>
    private void Sync(Control source)
    {
        if (_refreshing) return;
        if (ReferenceEquals(source, _query))
        {
            var text = _query.Text ?? string.Empty;
            Load(text);
            _query.Text = text;
            return;
        }

        _refreshing = true;
        try
        {
            _query.Text = Compile();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>The fields as the search box would have them typed.</summary>
    private string Compile()
    {
        var parts = new List<string>();
        string Q(string word) => word.Any(char.IsWhiteSpace) ? "\"" + word + "\"" : word;

        var words = (_words.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var word in words)
        {
            switch (_in.SelectedIndex)
            {
                case 0: parts.Add("subject:" + Q(word)); break;
                case 1: parts.Add("subject:" + Q(word)); parts.Add("body:" + Q(word)); break;
                default: parts.Add(Q(word)); break;
            }
        }

        foreach (var w in (_from.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) parts.Add("from:" + Q(w));
        foreach (var w in (_sentTo.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) parts.Add("to:" + Q(w));
        if (_whereIAm.IsChecked == true && App.Accounts.Default is { } me)
        {
            parts.Add(_where.SelectedIndex == 2 ? "cc:" + Q(me.Account.Address) : "to:" + Q(me.Account.Address));
        }

        if (_timeField.SelectedIndex > 0 && _timeSpan.SelectedIndex > 0)
        {
            parts.Add($"{TimeFields[_timeField.SelectedIndex]}:{TimeSpans[_timeSpan.SelectedIndex].Value}");
        }

        foreach (var c in (_categories.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) parts.Add("category:" + Q(c));
        if (_read.SelectedIndex == 1) parts.Add("read:no");
        if (_read.SelectedIndex == 2) parts.Add("read:yes");
        if (_attachments.SelectedIndex == 1) parts.Add("hasattachment:yes");
        if (_attachments.SelectedIndex == 2) parts.Add("hasattachment:no");
        if (_importance.SelectedIndex > 0) parts.Add("importance:" + new[] { "", "high", "normal", "low" }[_importance.SelectedIndex]);
        if (_flagged.SelectedIndex == 1) parts.Add("flagged:yes");
        if (_flagged.SelectedIndex == 2) parts.Add("flagged:no");
        if (_sizeBound.SelectedIndex > 0)
        {
            parts.Add($"size:{(_sizeBound.SelectedIndex == 1 ? "<" : ">")}{(long)(_sizeKb.Value ?? 100)}kb");
        }

        return string.Join(' ', parts);
    }
}

/// <summary>Other Settings: groups, the preview lines, and when Compact draws the card.</summary>
public sealed class OtherSettingsDialog : Window
{
    public MailView? Result { get; private set; }

    public OtherSettingsDialog(MailView view)
    {
        Title = "Other Settings";
        Width = 500;
        Height = 380;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var groups = ViewDialogKit.Ink(new CheckBox { Content = "Show items in Groups", IsChecked = view.ShowInGroups });
        var preview = new ComboBox { Width = 140, ItemsSource = new[] { "Off", "1 line", "2 lines", "3 lines" }, SelectedIndex = Math.Clamp(view.PreviewLines, 0, 3) };
        var auto = ViewDialogKit.Ink(new RadioButton { Content = "Use compact layout in widths smaller than", GroupName = "compact", IsChecked = view.CompactMode == CompactMode.Auto });
        var chars = new NumericUpDown { Width = 90, Minimum = 20, Maximum = 400, Value = view.CompactBelowChars, Increment = 5 };
        var alwaysSingle = ViewDialogKit.Ink(new RadioButton { Content = "Always use single-line layout", GroupName = "compact", IsChecked = view.CompactMode == CompactMode.AlwaysSingleLine });
        var alwaysCompact = ViewDialogKit.Ink(new RadioButton { Content = "Always use compact layout", GroupName = "compact", IsChecked = view.CompactMode == CompactMode.AlwaysCompact });

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                ViewDialogKit.Label("Grid Lines and Group Headings", bold: true),
                groups,
                ViewDialogKit.Label("Message Preview", bold: true),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ViewDialogKit.Label("Preview lines:"), preview } },
                ViewDialogKit.Label("Other Options", bold: true),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { auto, chars, ViewDialogKit.Label("characters") } },
                alwaysSingle,
                alwaysCompact,
                ViewDialogKit.Buttons(ViewDialogKit.Ok(() =>
                {
                    Result = view with
                    {
                        ShowInGroups = groups.IsChecked == true,
                        PreviewLines = Math.Max(0, preview.SelectedIndex),
                        CompactMode = alwaysSingle.IsChecked == true ? CompactMode.AlwaysSingleLine : alwaysCompact.IsChecked == true ? CompactMode.AlwaysCompact : CompactMode.Auto,
                        CompactBelowChars = (int)(chars.Value ?? 125),
                    };
                    Close();
                }), ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }
}

/// <summary>Conditional Formatting: the rules in order, each with its switch, font and condition.</summary>
public sealed class ConditionalFormattingDialog : Window
{
    private readonly List<ConditionalFormat> _rules;
    private readonly ListBox _list = ViewDialogKit.SurfaceList(300, 220);
    private readonly TextBox _name = new() { Width = 220 };
    private readonly TextBlock _fontSummary = ViewDialogKit.Label(string.Empty, subtle: true);
    private readonly TextBlock _conditionSummary = ViewDialogKit.Label(string.Empty, subtle: true);
    private readonly Button _delete = new() { Content = "Delete", Width = 100 };
    private readonly Button _font = new() { Content = "Font…", Width = 100 };
    private readonly Button _condition = new() { Content = "Condition…", Width = 100 };
    private bool _refreshing;

    public IReadOnlyList<ConditionalFormat>? Result { get; private set; }

    public ConditionalFormattingDialog(IReadOnlyList<ConditionalFormat> rules)
    {
        _rules = [.. rules];

        Title = "Conditional Formatting";
        Width = 560;
        Height = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemTemplate = new FuncDataTemplate<ConditionalFormat>((rule, _) =>
        {
            if (rule is null) return new Control();

            var box = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center };
            ViewDialogKit.Bind(box, TemplatedControl.ForegroundProperty, "dialog.surface.text.brush");
            box.IsCheckedChanged += (_, _) =>
            {
                var index = _rules.FindIndex(r => ReferenceEquals(r, rule));
                if (index >= 0) _rules[index] = _rules[index] with { Enabled = box.IsChecked == true };
            };
            var name = ViewDialogKit.SurfaceText(rule.Name);
            name.Margin = new Thickness(6, 0, 0, 0);
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { box, name } };
        });
        _list.SelectionChanged += (_, _) => ShowSelected();
        _name.TextChanged += (_, _) =>
        {
            if (_refreshing || _list.SelectedIndex < 0 || _list.SelectedIndex >= _rules.Count) return;
            var rule = _rules[_list.SelectedIndex];
            if (rule.BuiltIn) return;
            _rules[_list.SelectedIndex] = rule with { Name = _name.Text ?? string.Empty };
        };

        DialogChrome.Apply(this, Layout());
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
        Refresh(0);
    }

    private Control Layout()
    {
        var add = new Button { Content = "Add", Width = 100 };
        add.Click += (_, _) => { _rules.Add(new ConditionalFormat("Untitled")); Refresh(_rules.Count - 1); };
        _delete.Click += (_, _) =>
        {
            var index = _list.SelectedIndex;
            if (index < 0 || index >= _rules.Count || _rules[index].BuiltIn) return;
            _rules.RemoveAt(index);
            Refresh(Math.Min(index, _rules.Count - 1));
        };
        var up = new Button { Content = "Move Up", Width = 100 };
        up.Click += (_, _) => Move(-1);
        var down = new Button { Content = "Move Down", Width = 100 };
        down.Click += (_, _) => Move(1);
        _font.Click += async (_, _) => await FontAsync();
        _condition.Click += async (_, _) => await ConditionAsync();

        var side = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 0, 0), Children = { add, _delete, up, down } };
        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Rules for this view:"), _list } }, side },
        };

        var properties = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        void PropertyRow(int row, Control left, Control right)
        {
            left.Margin = new Thickness(0, 0, 0, 8);
            right.Margin = new Thickness(8, 0, 0, 8);
            Grid.SetRow(left, row); Grid.SetColumn(left, 0); properties.Children.Add(left);
            Grid.SetRow(right, row); Grid.SetColumn(right, 1); properties.Children.Add(right);
        }

        PropertyRow(0, ViewDialogKit.Label("Name:"), _name);
        PropertyRow(1, _font, _fontSummary);
        PropertyRow(2, _condition, _conditionSummary);

        var ok = ViewDialogKit.Ok(() => { Result = _rules.ToList(); Close(); });
        return new StackPanel
        {
            Margin = new Thickness(18),
            Children = { top, ViewDialogKit.Label("Properties of selected rule", bold: true), properties, ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(this)) },
        };
    }

    private void Move(int direction)
    {
        var index = _list.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _rules.Count) return;
        (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
        Refresh(target);
    }

    private void Refresh(int select)
    {
        _list.ItemsSource = _rules.ToList();
        _list.SelectedIndex = _rules.Count == 0 ? -1 : Math.Clamp(select, 0, _rules.Count - 1);
        ShowSelected();
    }

    private void ShowSelected()
    {
        _refreshing = true;
        try
        {
            var index = _list.SelectedIndex;
            var rule = index >= 0 && index < _rules.Count ? _rules[index] : null;
            _name.Text = rule?.Name ?? string.Empty;
            _name.IsEnabled = rule is { BuiltIn: false };
            _delete.IsEnabled = rule is { BuiltIn: false };
            _condition.IsEnabled = rule is { BuiltIn: false };
            _font.IsEnabled = rule is not null;
            _fontSummary.Text = rule is null ? string.Empty : FontWords(rule);
            _conditionSummary.Text = rule is null ? string.Empty : rule.Condition.Length == 0 ? "(no condition — every message)" : rule.Condition;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string FontWords(ConditionalFormat rule)
    {
        var parts = new List<string>();
        if (rule.Bold) parts.Add("Bold");
        if (rule.Italic) parts.Add("Italic");
        parts.Add(ViewDialogKit.InkChoices.FirstOrDefault(c => c.Token == rule.ColourToken).Label ?? "Automatic");
        return string.Join(", ", parts);
    }

    private async Task FontAsync()
    {
        var index = _list.SelectedIndex;
        if (index < 0 || index >= _rules.Count) return;
        var rule = _rules[index];

        var bold = ViewDialogKit.Ink(new CheckBox { Content = "Bold", IsChecked = rule.Bold });
        var italic = ViewDialogKit.Ink(new CheckBox { Content = "Italic", IsChecked = rule.Italic });
        var colour = new ComboBox { Width = 160, ItemsSource = ViewDialogKit.InkChoices.Select(c => c.Label).ToList() };
        colour.SelectedIndex = Math.Max(0, ViewDialogKit.InkChoices.ToList().FindIndex(c => c.Token == rule.ColourToken));

        Window? window = null;
        var ok = ViewDialogKit.Ok(() =>
        {
            _rules[index] = rule with { Bold = bold.IsChecked == true, Italic = italic.IsChecked == true, ColourToken = ViewDialogKit.InkChoices[Math.Max(0, colour.SelectedIndex)].Token };
            window?.Close();
        });
        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
        };
        window = ViewDialogKit.Dialog("Font", 340, 240, body);
        body.Children.Add(ViewDialogKit.Label("Font style", bold: true));
        body.Children.Add(bold);
        body.Children.Add(italic);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ViewDialogKit.Label("Colour:"), colour } });
        body.Children.Add(ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(window)));
        await window.ShowDialog(this);
        Refresh(index);
    }

    private async Task ConditionAsync()
    {
        var index = _list.SelectedIndex;
        if (index < 0 || index >= _rules.Count || _rules[index].BuiltIn) return;
        var dialog = new FilterDialog(_rules[index].Condition, "Filter");
        await dialog.ShowDialog(this);
        if (dialog.Result is { } condition) { _rules[index] = _rules[index] with { Condition = condition }; Refresh(index); }
    }
}

/// <summary>Format Columns: a label of the reader's own, a width, and how a date column writes its dates.</summary>
public sealed class FormatColumnsDialog : Window
{
    private readonly MailView _original;
    private readonly List<ViewColumn> _columns;
    private readonly Dictionary<string, ColumnFormat> _formats;
    private readonly ListBox _list = ViewDialogKit.SurfaceList(200, 260);
    private readonly TextBox _label = new() { Width = 220 };
    private readonly NumericUpDown _width = new() { Width = 110, Minimum = 18, Maximum = 800, Increment = 10 };
    private readonly ComboBox _format = new() { Width = 160, ItemsSource = new[] { "Best fit", "Short", "Long", "Time only" } };
    private bool _refreshing;

    public MailView? Result { get; private set; }

    public FormatColumnsDialog(MailView view)
    {
        _original = view;
        _columns = [.. view.Columns];
        _formats = view.ColumnFormats.ToDictionary(kv => kv.Key, kv => kv.Value);

        Title = "Format Columns";
        Width = 560;
        Height = 400;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemTemplate = new FuncDataTemplate<ViewColumn>((c, _) => c is null ? new Control() : ViewDialogKit.SurfaceText(ViewFields.Label(c.Id)));
        _list.ItemsSource = _columns.ToList();
        _list.SelectionChanged += (_, _) => ShowSelected();
        _label.TextChanged += (_, _) => Save();
        _width.ValueChanged += (_, _) => Save();
        _format.SelectionChanged += (_, _) => Save();

        DialogChrome.Apply(this, Layout());
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
        _list.SelectedIndex = _columns.Count > 0 ? 0 : -1;
        ShowSelected();
    }

    private Control Layout()
    {
        var properties = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(16, 0, 0, 0) };
        void PropertyRow(int row, string label, Control control)
        {
            var caption = ViewDialogKit.Label(label);
            caption.Margin = new Thickness(0, 0, 0, 10);
            control.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(caption, row); Grid.SetColumn(caption, 0); properties.Children.Add(caption);
            Grid.SetRow(control, row); Grid.SetColumn(control, 1); properties.Children.Add(control);
        }

        PropertyRow(0, "Label:", _label);
        PropertyRow(1, "Width:", _width);
        PropertyRow(2, "Format:", _format);

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Available fields:"), _list } }, properties },
        };

        var ok = ViewDialogKit.Ok(() =>
        {
            Result = _original with { Columns = _columns.ToList(), ColumnFormats = _formats.ToDictionary(kv => kv.Key, kv => kv.Value) };
            Close();
        });
        return new StackPanel { Margin = new Thickness(18), Children = { top, ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(this)) } };
    }

    private void ShowSelected()
    {
        _refreshing = true;
        try
        {
            var index = _list.SelectedIndex;
            var column = index >= 0 && index < _columns.Count ? _columns[index] : null;
            _label.IsEnabled = column is not null;
            _width.IsEnabled = column is not null;
            _format.IsEnabled = column is not null && ViewFields.IsDate(column.Id);
            _label.Text = column is null ? string.Empty : _formats.TryGetValue(column.Id, out var f) && f.Label is { } label ? label : ViewFields.Label(column.Id);
            _width.Value = (decimal)(column?.Width ?? 100);
            _format.SelectedIndex = column is not null && _formats.TryGetValue(column.Id, out var format) ? (int)format.DateFormat : 0;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Save()
    {
        if (_refreshing) return;
        var index = _list.SelectedIndex;
        if (index < 0 || index >= _columns.Count) return;
        var column = _columns[index];
        _columns[index] = column with { Width = (double)(_width.Value ?? (decimal)column.Width) };
        var label = (_label.Text ?? string.Empty).Trim();
        _formats[column.Id] = new ColumnFormat
        {
            Label = label.Length == 0 || label == ViewFields.Label(column.Id) ? null : label,
            DateFormat = (DateFormat)Math.Max(0, _format.SelectedIndex),
        };
    }
}

/// <summary>
/// Manage All Views: the views this folder can use — the three that ship, the reader's own —
/// with New, Copy, Modify, Rename, Reset and Delete, and Apply View.
/// </summary>
public sealed class ManageViewsDialog : Window
{
    private readonly OpenAccount _account;
    private readonly MailView _current;
    private readonly ListBox _list = ViewDialogKit.SurfaceList(420, 220);
    private readonly Button _rename = new() { Content = "Rename…", Width = 100 };
    private readonly Button _delete = new() { Content = "Delete", Width = 100 };

    /// <summary>The view Apply View chose, or null.</summary>
    public MailView? Applied { get; private set; }

    /// <summary>True when the current view was modified in place — the caller re-applies.</summary>
    public MailView? CurrentModified { get; private set; }

    private sealed record Entry(string Name, MailView View, bool IsCurrent, bool IsSaved);

    public ManageViewsDialog(OpenAccount account, MailView current, string folderName)
    {
        _account = account;
        _current = current;

        Title = "Manage All Views";
        Width = 600;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemTemplate = new FuncDataTemplate<Entry>((entry, _) =>
        {
            if (entry is null) return new Control();

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,140,*"), Margin = new Thickness(4, 2) };
            var name = ViewDialogKit.SurfaceText(entry.Name);
            var scope = ViewDialogKit.SurfaceText(entry.IsCurrent ? $"\"{folderName}\"" : "All Mail folders");
            var type = ViewDialogKit.SurfaceText("Table");
            Grid.SetColumn(scope, 1);
            Grid.SetColumn(type, 2);
            grid.Children.Add(name);
            grid.Children.Add(scope);
            grid.Children.Add(type);
            return grid;
        });
        _list.SelectionChanged += (_, _) =>
        {
            var entry = _list.SelectedItem as Entry;
            _rename.IsEnabled = entry is { IsSaved: true };
            _delete.IsEnabled = entry is { IsSaved: true };
        };

        DialogChrome.Apply(this, Layout());
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
        Reload();
    }

    private Control Layout()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("200,140,*"), Margin = new Thickness(4, 0) };
        var h1 = ViewDialogKit.Label("View Name", subtle: true);
        var h2 = ViewDialogKit.Label("Can Be Used On", subtle: true);
        var h3 = ViewDialogKit.Label("View Type", subtle: true);
        Grid.SetColumn(h2, 1);
        Grid.SetColumn(h3, 2);
        header.Children.Add(h1);
        header.Children.Add(h2);
        header.Children.Add(h3);

        var @new = new Button { Content = "New…", Width = 100 };
        @new.Click += async (_, _) => await NewAsync();
        var copy = new Button { Content = "Copy…", Width = 100 };
        copy.Click += async (_, _) => await CopyAsync();
        var modify = new Button { Content = "Modify…", Width = 100 };
        modify.Click += async (_, _) => await ModifyAsync();
        _rename.Click += async (_, _) => await RenameAsync();
        var reset = new Button { Content = "Reset", Width = 100 };
        reset.Click += (_, _) => Reset();
        _delete.Click += async (_, _) => await DeleteAsync();

        var side = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 0, 0), Children = { @new, copy, modify, _rename, reset, _delete } };
        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Views for folder:"), header, _list } }, side },
        };

        var apply = ViewDialogKit.Ok(() =>
        {
            if (_list.SelectedItem is Entry entry) { Applied = entry.View; Close(); }
        }, "Apply View");
        apply.Width = 100;
        return new StackPanel { Margin = new Thickness(18), Children = { top, ViewDialogKit.Buttons(apply, ViewDialogKit.Cancel(this, "Close")) } };
    }

    private void Reload(string? select = null)
    {
        var entries = new List<Entry>
        {
            new("<Current view settings>", _current, true, false),
            new(MailView.CompactName, MailView.Compact, false, false),
            new(MailView.SingleName, MailView.Single, false, false),
            new(MailView.PreviewName, MailView.Preview, false, false),
        };
        entries.AddRange(_account.Mail.Views().Select(v => new Entry(v.Name, MailView.FromJson(v.Definition) with { Name = v.Name }, false, true)));
        _list.ItemsSource = entries;
        _list.SelectedItem = entries.FirstOrDefault(e => e.Name == select) ?? entries[0];
    }

    private async Task NewAsync()
    {
        var name = await Prompt.AskAsync(this, "Create a New View", "Name of new view:", "New view");
        if (string.IsNullOrWhiteSpace(name) || MailView.BuiltIn(name.Trim()) is not null) return;
        var view = MailView.Compact with { Name = name.Trim() };
        var dialog = new AdvancedViewSettingsDialog(view, _account);
        await dialog.ShowDialog(this);
        var edited = dialog.Result ?? view;
        _account.Mail.SaveView(edited.Name, edited.ToJson(), DateTimeOffset.UtcNow);
        Reload(edited.Name);
    }

    private async Task CopyAsync()
    {
        if (_list.SelectedItem is not Entry entry) return;
        var name = await Prompt.AskAsync(this, "Copy View", "Name of new view:", "Copy of " + (entry.IsCurrent ? entry.View.Name : entry.Name));
        if (string.IsNullOrWhiteSpace(name) || MailView.BuiltIn(name.Trim()) is not null) return;
        var copy = entry.View with { Name = name.Trim() };
        _account.Mail.SaveView(copy.Name, copy.ToJson(), DateTimeOffset.UtcNow);
        Reload(copy.Name);
    }

    private async Task ModifyAsync()
    {
        if (_list.SelectedItem is not Entry entry) return;
        var dialog = new AdvancedViewSettingsDialog(entry.View, _account);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } edited) return;

        if (entry.IsSaved) _account.Mail.SaveView(entry.Name, edited.ToJson(), DateTimeOffset.UtcNow);
        else CurrentModified = edited;
        Reload(entry.Name);
    }

    private async Task RenameAsync()
    {
        if (_list.SelectedItem is not Entry { IsSaved: true } entry) return;
        var name = await Prompt.AskAsync(this, "Rename View", "New name of view:", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || MailView.BuiltIn(name.Trim()) is not null) return;
        if (_account.Mail.ViewNamed(entry.Name) is { } saved) _account.Mail.RenameView(saved.Id, name.Trim());
        Reload(name.Trim());
    }

    private void Reset()
    {
        if (_list.SelectedItem is not Entry entry || entry.IsSaved) return;
        CurrentModified = MailView.BuiltIn(_current.Name) ?? MailView.Compact;
        Reload();
    }

    private async Task DeleteAsync()
    {
        if (_list.SelectedItem is not Entry { IsSaved: true } entry) return;
        if (!await Confirm.AskAsync(this, "Manage All Views", $"Delete the view \"{entry.Name}\"?", "Delete")) return;
        if (_account.Mail.ViewNamed(entry.Name) is { } saved) _account.Mail.DeleteView(saved.Id);
        Reload();
    }
}

/// <summary>Apply Current View to Other Mail Folders: the account's folders, ticked.</summary>
public sealed class ApplyViewToFoldersDialog : Window
{
    /// <summary>The folder ids ticked when OK was pressed, or null.</summary>
    public IReadOnlyList<long>? Result { get; private set; }

    public ApplyViewToFoldersDialog(OpenAccount account, long? currentFolderId)
    {
        Title = "Apply View";
        Width = 420;
        Height = 460;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var folders = account.Mail.Folders(account.Account.Id).Where(f => f.Role != FolderRole.Outbox).ToList();
        var boxes = new List<CheckBox>();
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6, 4) };

        void Add(long? parent, int depth)
        {
            foreach (var folder in folders.Where(f => f.ParentId == parent).OrderBy(f => f.Ordinal).ThenBy(f => f.Name))
            {
                var box = new CheckBox { Content = folder.Name, Tag = folder.Id, Margin = new Thickness(depth * 16, 0, 0, 0), IsEnabled = folder.Id != currentFolderId };
                ViewDialogKit.Bind(box, TemplatedControl.ForegroundProperty, "dialog.surface.text.brush");
                boxes.Add(box);
                rows.Children.Add(box);
                Add(folder.Id, depth + 1);
            }
        }

        Add(null, 0);

        var all = new Button { Content = "Select All", Padding = new Thickness(10, 4) };
        all.Click += (_, _) => { foreach (var box in boxes.Where(b => b.IsEnabled)) box.IsChecked = true; };
        var none = new Button { Content = "Clear All", Padding = new Thickness(10, 4) };
        none.Click += (_, _) => { foreach (var box in boxes) box.IsChecked = false; };

        var ok = ViewDialogKit.Ok(() =>
        {
            Result = [.. boxes.Where(b => b.IsChecked == true && b.Tag is long).Select(b => (long)b.Tag!)];
            Close();
        });

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                ViewDialogKit.Label("Apply the current view to these folders:"),
                ViewDialogKit.Boxed(new ScrollViewer { Content = rows }, 380, 280),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { all, none } },
                ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }
}
