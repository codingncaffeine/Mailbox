using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Feeds;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The filters dashboard: what a reader has asked not to see, and the box for adding another.
/// </summary>
/// <remarks>
/// The shape is Feedly's own — a list of filters with an Add box over it, each filter carrying a
/// word or phrase, what it covers, and how long it lasts, because "mute this for a week" is the
/// ordinary case and a permanent rule for a story that ends is one the reader has to remember to
/// come back and delete.
/// <para>
/// Two things are said on the page rather than left to be discovered. That a filter applies to
/// what arrives next, not to what is already filed — a filter is not a licence to delete
/// somebody's messages. And that this matches words, not meaning: Feedly's version can mute
/// "articles about layoffs" without being given a word because it ships a thousand trained topic
/// models, and saying so is better than a reader wondering why theirs does not.
/// </para>
/// </remarks>
public sealed class MuteFiltersDialog : Window
{
    private readonly MuteFilters _filters;
    private readonly FeedSubscriptions _feeds;
    private readonly DateTimeOffset _now;

    private readonly TextBox _text = new();
    private readonly ComboBox _scope = new();
    private readonly ComboBox _duration = new();
    private readonly CheckBox _titleOnly = Tick("Match the headline only");
    private readonly CheckBox _regex = Tick("This is a pattern");
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _note = new();
    private readonly Button _add;

    /// <summary>True when a filter was added or removed.</summary>
    public bool Changed { get; private set; }

    /// <summary>A phrase to start the box with, from "Mute This". Empty for none.</summary>
    public string Suggested { get; init; } = string.Empty;

    public MuteFiltersDialog(MuteFilters filters, FeedSubscriptions feeds, DateTimeOffset now)
    {
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));
        _now = now;

        Title = "Mute Filters";
        Width = 620;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _add = Push("Add", Commit);
        _add.IsDefault = true;
        _add.IsEnabled = false;

        var close = Push("Close", Close);
        close.IsCancel = true;

        DialogChrome.Apply(this, Layout(close));
        Fill();

        Opened += (_, _) =>
        {
            if (Suggested is { Length: > 0 } seed)
            {
                // The whole subject is never what somebody means to mute; it is a starting point
                // they edit down to the word that matters, so it arrives selected.
                _text.Text = seed;
                _text.SelectAll();
            }

            _text.Focus();
        };
    }

    private Control Layout(Button close)
    {
        var heading = Label("Mute Filters", bold: true, size: 15);

        var explain = Label(
            "Articles matching a filter are not delivered at all — they take no space and turn up "
            + "in no search. Filters apply to what arrives next; anything already filed stays "
            + "where it is.");
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 4, 0, 14);
        Bind(explain, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _text.PlaceholderText = "A word or a phrase";
        _text.TextChanged += (_, _) => Validate();
        _text.KeyDown += (_, e) =>
        {
            if (e.Key is not Key.Enter || !_add.IsEnabled) return;
            e.Handled = true;
            Commit();
        };

        _scope.ItemsSource = ScopeChoices().Select(c => c.Label).ToList();
        _scope.SelectedIndex = 0;
        _scope.MinWidth = 200;

        _duration.ItemsSource = new[] { "Forever", "For a day", "For a week", "For a month" };
        _duration.SelectedIndex = 0;
        _duration.MinWidth = 130;

        _regex.IsCheckedChanged += (_, _) => Validate();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_text, 0);
        row.Children.Add(_text);
        Grid.SetColumn(_scope, 1);
        row.Children.Add(_scope);
        Grid.SetColumn(_duration, 2);
        row.Children.Add(_duration);
        Grid.SetColumn(_add, 3);
        row.Children.Add(_add);

        var ticks = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _titleOnly, _regex },
        };

        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 8, 0, 0);
        Bind(_note, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var caveat = Label(
            "Filters match words and patterns, not meaning: a filter for “layoffs” finds the word, "
            + "not every article about them.");
        caveat.TextWrapping = TextWrapping.Wrap;
        caveat.FontSize = 11;
        caveat.Margin = new Thickness(0, 14, 0, 0);
        Bind(caveat, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var top = new StackPanel { Children = { heading, explain, row, ticks, _note } };
        DockPanel.SetDock(top, Dock.Top);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { close },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(caveat, Dock.Bottom);

        var scroll = new ScrollViewer
        {
            Content = _list,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children = { top, buttons, caveat, scroll },
        };
    }

    /// <summary>Everywhere, each heading, and each feed — what a filter can be pointed at.</summary>
    private List<(string Label, MuteScope Scope, string Target)> ScopeChoices()
    {
        var choices = new List<(string, MuteScope, string)> { ("In every feed", MuteScope.Everywhere, string.Empty) };

        foreach (var heading in _feeds.Categories) choices.Add(($"In {heading}", MuteScope.Heading, heading));
        foreach (var feed in _feeds.All) choices.Add(($"In {feed.Name} only", MuteScope.Feed, feed.Url));

        return choices;
    }

    private void Validate()
    {
        var typed = _text.Text?.Trim() ?? string.Empty;

        if (typed.Length == 0)
        {
            _add.IsEnabled = false;
            _note.Text = string.Empty;
            return;
        }

        if (_regex.IsChecked == true && !MuteFilters.IsValidPattern(typed))
        {
            _add.IsEnabled = false;
            _note.Text = "That is not a pattern this can read.";
            return;
        }

        _add.IsEnabled = true;
        _note.Text = _regex.IsChecked == true
            ? string.Empty
            : "Matched as whole words, so “AI” does not catch “Ukraine”.";
    }

    private void Commit()
    {
        var typed = _text.Text?.Trim() ?? string.Empty;
        if (typed.Length == 0) return;

        var (_, scope, target) = ScopeChoices()[Math.Max(0, _scope.SelectedIndex)];

        var expires = _duration.SelectedIndex switch
        {
            1 => _now.AddDays(1),
            2 => _now.AddDays(7),
            3 => _now.AddDays(30),
            _ => (DateTimeOffset?)null,
        };

        _filters.Add(new MuteFilter(typed, scope, target, _titleOnly.IsChecked == true, _regex.IsChecked == true, expires));
        Changed = true;

        _text.Text = string.Empty;
        Fill();
        _text.Focus();
    }

    private void Fill()
    {
        _list.Children.Clear();

        // Anything whose time is up goes, rather than sitting in the list looking active.
        if (_filters.Expire(_now) > 0) Changed = true;

        if (_filters.All.Count == 0)
        {
            var empty = Label("Nothing is muted.");
            empty.Margin = new Thickness(0, 8, 0, 0);
            Bind(empty, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            _list.Children.Add(empty);
            return;
        }

        foreach (var filter in _filters.All) _list.Children.Add(Row(filter));
    }

    private Control Row(MuteFilter filter)
    {
        var word = Label(filter.Text, bold: true);
        word.TextTrimming = TextTrimming.CharacterEllipsis;

        var kept = filter.Muted > 0
            ? $"{filter.Where} · {filter.Until(_now)} · kept out {filter.Muted}"
            : $"{filter.Where} · {filter.Until(_now)}";

        if (filter.TitleOnly) kept += " · headline only";
        if (filter.IsRegex) kept += " · pattern";

        var detail = Label(kept);
        detail.FontSize = 11;
        detail.Margin = new Thickness(0, 2, 0, 0);
        Bind(detail, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var remove = new Button
        {
            Classes = { "flat" },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            FontFamily = IconFont.Family,
            Content = IconGlyphs.GetOrEmpty("delete", 16),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(remove, "Stop muting this");
        remove.Click += (_, _) =>
        {
            _filters.Remove(filter);
            Changed = true;
            Fill();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Children = { word, detail } };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);

        var row = new Border { Padding = new Thickness(10, 8, 6, 8), Child = grid };
        row[!BackgroundProperty] = new DynamicResourceExtension("list.row.hover.brush");
        return row;
    }

    // ---- Small helpers ------------------------------------------------------------------------

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>
    /// A line of the dialog's text, in the dialog's own ink.
    /// </summary>
    /// <remarks>
    /// <c>dialog.*</c> rather than <c>text.*</c>: a dialog's ground is dark in two of the four
    /// themes while content surfaces are light, so the content ink is unreadable on it. The same
    /// trap has now been walked into three times — on the article list, on the pane's headings,
    /// and here — which is why it is written down at each of them.
    /// </remarks>
    private static TextBlock Label(string text, bool bold = false, double size = 12)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    /// <summary>A tick box in the dialog's own ink, for the reason on <see cref="Label"/>.</summary>
    private static CheckBox Tick(string label)
    {
        var box = new CheckBox { Content = label };
        Bind(box, CheckBox.ForegroundProperty, "dialog.foreground.brush");
        return box;
    }

    private static Button Push(string text, Action onClick)
    {
        var button = new Button { Content = text, MinWidth = 80 };
        button.Click += (_, _) => onClick();
        return button;
    }
}
