using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Rules;
using Mailbox.Core.Search;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// New Search Folder: the reference's list of templates in their four groups, the parameter the
/// chosen one needs, and which account's mail to search — or, for an existing search folder,
/// Customize Search Folder over its name and parameter.
/// </summary>
public sealed class NewSearchFolderDialog : Window
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The account chosen to search, the folder's name and its query — or null when cancelled.</summary>
    public (OpenAccount Account, string Name, SearchFolderQuery Query)? Result { get; private set; }

    private SearchFolderQuery _query;
    private readonly TextBlock _parameter = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _choose = new() { Content = "Choose…" };
    private readonly TextBox _name = new() { Width = 300 };
    private OpenAccount? _account;

    /// <param name="preselected">The account to search, when opened from its Search Folders node.</param>
    /// <param name="existing">A search folder to customise, or null to make one.</param>
    public NewSearchFolderDialog(OpenAccount? preselected = null, SearchFolder? existing = null)
    {
        var editing = existing is not null;
        Title = editing ? "Customize Search Folder" : "New Search Folder";
        Width = 560;
        Height = editing ? 300 : 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var accounts = App.Accounts.All;
        _account = preselected ?? App.Accounts.Default;
        _query = existing?.Query ?? new SearchFolderQuery(SearchFolderKind.Unread);
        _name.Text = existing?.Name ?? _query.DefaultName();

        _choose.Click += async (_, _) => await ChooseAsync();
        Bind(_parameter, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        DialogChrome.Apply(this, editing ? CustomizeLayout(accounts) : NewLayout(accounts));
        Bind(this, BackgroundProperty, "dialog.background.brush");
        RefreshParameter();
    }

    // ---- New ---------------------------------------------------------------------------------

    private static readonly SearchFolderKind[] Kinds = Enum.GetValues<SearchFolderKind>();

    private Control NewLayout(IReadOnlyList<OpenAccount> accounts)
    {
        var list = new ListBox { Height = 300 };
        Bind(list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");

        var items = new List<object>();
        string? group = null;
        foreach (var kind in Kinds)
        {
            if (SearchFolderQuery.Group(kind) != group)
            {
                group = SearchFolderQuery.Group(kind);
                var header = new TextBlock { Text = group, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 6, 0, 2) };
                Bind(header, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
                items.Add(new ListBoxItem { Content = header, IsEnabled = false });
            }

            var label = new TextBlock { Text = SearchFolderQuery.Label(kind), Margin = new Thickness(16, 1, 0, 1) };
            Bind(label, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            items.Add(new ListBoxItem { Content = label, Tag = kind });
        }

        list.ItemsSource = items;
        list.SelectionChanged += (_, _) =>
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is SearchFolderKind kind)
            {
                _query = new SearchFolderQuery(kind) { Threshold = kind switch { SearchFolderKind.Large => 100, SearchFolderKind.Old => 90, _ => 0 } };
                _name.Text = _query.DefaultName();
                RefreshParameter();
            }
        };
        list.SelectedIndex = 1;

        var accountCombo = new ComboBox
        {
            ItemsSource = accounts.Select(a => a.Account.Address).ToList(),
            SelectedIndex = _account is null ? -1 : accounts.ToList().FindIndex(a => a.Account.Address == _account.Account.Address),
            MinWidth = 260,
        };
        accountCombo.SelectionChanged += (_, _) =>
        {
            if (accountCombo.SelectedIndex >= 0 && accountCombo.SelectedIndex < accounts.Count) _account = accounts[accountCombo.SelectedIndex];
        };

        var ok = new Button { Content = "OK", Width = 74, IsDefault = true };
        ok.Click += async (_, _) => await FinishAsync();
        var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var customize = new StackPanel { Spacing = 8 };
        customize.Children.Add(Label("Customize Search Folder:"));
        customize.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _choose, _parameter },
        });
        customize.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { Label("Name:"), _name },
        });

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { ok, cancel },
                },
                new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        Label("Select a Search Folder:"),
                        list,
                        Boxed(customize),
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            Children = { Label("Search mail in:"), accountCombo },
                        },
                    },
                },
            },
        };
    }

    // ---- Customize -----------------------------------------------------------------------------

    private Control CustomizeLayout(IReadOnlyList<OpenAccount> accounts)
    {
        var ok = new Button { Content = "OK", Width = 74, IsDefault = true };
        ok.Click += async (_, _) => await FinishAsync();
        var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var includeDeleted = new CheckBox
        {
            Content = "Search Deleted Items and Junk Email too",
            IsChecked = _query.IncludeDeleted,
        };
        Bind(includeDeleted, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
        includeDeleted.IsCheckedChanged += (_, _) => _query = _query with { IncludeDeleted = includeDeleted.IsChecked == true };

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { ok, cancel },
                },
                new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Label("Name:"), _name } },
                        Label(SearchFolderQuery.Label(_query.Kind)),
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _choose, _parameter } },
                        includeDeleted,
                    },
                },
            },
        };
    }

    // ---- The parameter -------------------------------------------------------------------------

    private void RefreshParameter()
    {
        _choose.IsVisible = SearchFolderQuery.NeedsParameter(_query.Kind);
        _parameter.Text = _query.Kind switch
        {
            SearchFolderKind.From or SearchFolderKind.FromOrTo =>
                _query.Values.Count == 0 ? "Choose the people whose mail to find." : string.Join("; ", _query.Values),
            SearchFolderKind.Categorized =>
                _query.Values.Count == 0 ? "Any category — or choose which." : string.Join(", ", _query.Values),
            SearchFolderKind.Large => $"Larger than {_query.Threshold} KB",
            SearchFolderKind.Old => $"Older than {_query.Threshold} days",
            SearchFolderKind.WithWords =>
                _query.Values.Count == 0 ? "Choose the words to look for." : string.Join(", ", _query.Values.Select(v => "\"" + v + "\"")),
            SearchFolderKind.Custom =>
                _query.Conditions.Count == 0 ? "Choose the conditions." : RuleDescription.Sentence(new MailRule { Conditions = _query.Conditions }).Replace("Apply this rule after the message arrives", "Mail").Replace('\n', ' '),
            _ => "Shows " + SearchFolderQuery.Label(_query.Kind).ToLowerInvariant() + " across the account.",
        };
    }

    private async Task ChooseAsync()
    {
        if (_account is null) return;
        var mail = _account.Mail;

        switch (_query.Kind)
        {
            case SearchFolderKind.From:
            case SearchFolderKind.FromOrTo:
                if (await RuleValues.PeopleAsync(this, "Select Names", _query.Values) is { } people)
                    _query = _query with { Values = people };
                break;

            case SearchFolderKind.Categorized:
            {
                var categories = mail.Categories();
                if (await PickListDialog.PickAsync(this, "Color Categories", "Categories (none for any):",
                        categories.Select(c => new PickListDialog.Item(c.Name, c.Name)).ToList(), _query.Values) is { } chosen)
                    _query = _query with { Values = chosen };
                break;
            }

            case SearchFolderKind.Large:
                if (await Prompt.AskAsync(this, "Mail Size", "Show mail larger than (KB):", _query.Threshold.ToString(CultureInfo.InvariantCulture)) is { } kb
                    && int.TryParse(kb, out var size) && size >= 0)
                    _query = _query with { Threshold = size };
                break;

            case SearchFolderKind.Old:
                if (await Prompt.AskAsync(this, "Old Mail", "Show mail older than (days):", _query.Threshold.ToString(CultureInfo.InvariantCulture)) is { } d
                    && int.TryParse(d, out var days) && days >= 0)
                    _query = _query with { Threshold = days };
                break;

            case SearchFolderKind.WithWords:
                if (await RuleValues.WordsAsync(this, _query.Values) is { } words)
                    _query = _query with { Values = words };
                break;

            case SearchFolderKind.Custom:
            {
                // The rules' conditions, chosen through the wizard's own page: a rule with no
                // actions is a query.
                var wizard = new RuleWizard(mail, _account.Account.Id, new MailRule { Name = "Search", Conditions = _query.Conditions, Actions = [new RuleAction(RuleActionKind.StopProcessing)] });
                await wizard.ShowDialog(this);
                if (wizard.Result is { } rule) _query = _query with { Conditions = rule.Conditions };
                break;
            }
        }

        _name.Text = _name.Text is { Length: > 0 } && !string.Equals(_name.Text, _query.DefaultName(), StringComparison.Ordinal) && Result is null && !IsDefaultishName(_name.Text)
            ? _name.Text
            : _query.DefaultName();
        RefreshParameter();
    }

    /// <summary>Whether the name is one this dialog wrote, so a parameter change may rewrite it.</summary>
    private static bool IsDefaultishName(string name)
        => name.StartsWith("Mail from", StringComparison.Ordinal) || name.StartsWith("Mail containing", StringComparison.Ordinal)
           || name.StartsWith("Large Mail", StringComparison.Ordinal) || name.StartsWith("Old Mail", StringComparison.Ordinal)
           || name.StartsWith("Categorized", StringComparison.Ordinal) || name.StartsWith("Custom Search", StringComparison.Ordinal);

    private async Task FinishAsync()
    {
        if (_account is null) return;

        var incomplete = _query.Kind switch
        {
            SearchFolderKind.From or SearchFolderKind.FromOrTo or SearchFolderKind.WithWords => _query.Values.Count == 0,
            SearchFolderKind.Custom => _query.Conditions.Count == 0,
            _ => false,
        };

        if (incomplete)
        {
            await Confirm.AskAsync(this, Title ?? "Search Folder", "Choose what the search folder should look for first.", "OK", destructive: false);
            return;
        }

        var name = _name.Text?.Trim() is { Length: > 0 } typed ? typed : _query.DefaultName();
        Result = (_account, name, _query);
        Close();
    }

    // ---- Building blocks -----------------------------------------------------------------------

    private static Border Boxed(Control content)
    {
        var box = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10), Child = content };
        Bind(box, Border.BorderBrushProperty, "dialog.border.brush");
        return box;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }
}
