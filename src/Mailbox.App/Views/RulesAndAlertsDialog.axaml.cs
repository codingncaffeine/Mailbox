using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Rules;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Rules and Alerts: the account's rules in running order, each with its switch, the buttons the
/// reference puts above them — New Rule, Change Rule, Copy, Delete, Move Up and Down, Run Rules
/// Now — and the description of the selected rule below, its values clickable to edit.
/// </summary>
/// <remarks>
/// Rules are per account, so the dialog opens on one and offers the rest in a picker when there
/// are several. Every change writes as it goes; Close is the only way out and there is nothing
/// to cancel. Manage Alerts is deliberately absent: it subscribes to SharePoint alert sources,
/// which this application has no way to reach and no plan to.
/// </remarks>
public sealed class RulesAndAlertsDialog : Window
{
    private readonly ListBox _list = new() { Height = 200 };
    private readonly RuleDescriptionView _description = new();
    private readonly ComboBox _account = new() { MinWidth = 220 };
    private readonly TextBlock _serverStatus = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 440 };
    private readonly Button _retry = new() { Content = "Retry", Padding = new Thickness(9, 3), IsVisible = false };
    private OpenAccount? _current;
    private bool _publishing;
    private bool _publishAgain;

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <param name="address">The account to open on, or null for the default.</param>
    public RulesAndAlertsDialog(string? address = null)
    {
        Title = "Rules and Alerts";
        Width = 660;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var accounts = App.Accounts.All;
        _current = (address is { Length: > 0 } ? App.Accounts.Find(address) : null) ?? App.Accounts.Default;

        _account.ItemsSource = accounts.Select(a => a.Account.Address).ToList();
        _account.SelectedIndex = _current is null ? -1 : accounts.ToList().FindIndex(a => a.Account.Address == _current.Account.Address);
        _account.SelectionChanged += (_, _) =>
        {
            if (_account.SelectedIndex >= 0 && _account.SelectedIndex < accounts.Count)
            {
                _current = accounts[_account.SelectedIndex];
                Reload();
            }
        };

        _list.ItemTemplate = new FuncDataTemplate<RuleRow>((row, _) => Row(row));
        _list.SelectionChanged += (_, _) => _description.Show((_list.SelectedItem as RuleRow)?.Rule);
        _description.ValueClicked += async (_, index) => await EditClauseAsync(index);

        DialogChrome.Apply(this, Layout());
        Bind(this, BackgroundProperty, "dialog.background.brush");
        Reload();
    }

    private sealed record RuleRow(MailRule Rule)
    {
        public bool Enabled { get; set; } = Rule.Enabled;
    }

    private Control Row(RuleRow row)
    {
        var box = new CheckBox { IsChecked = row.Enabled, VerticalAlignment = VerticalAlignment.Center };
        box.IsCheckedChanged += async (_, _) =>
        {
            if (row.Enabled == (box.IsChecked == true)) return;
            row.Enabled = box.IsChecked == true;
            _current?.Mail.SetRuleEnabled(row.Rule.Id, row.Enabled);
            if (row.Rule.ServerSide) await SyncServerAsync();
            else ShowServerState();
        };

        var name = new TextBlock
        {
            Text = row.Rule.Name,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        Bind(name, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2) };
        stack.Children.Add(box);
        stack.Children.Add(name);

        if (row.Rule.ServerSide)
        {
            var tag = new TextBlock
            {
                Text = "on the server",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Opacity = 0.65,
                FontSize = 12,
            };
            Bind(tag, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            stack.Children.Add(tag);
        }

        return stack;
    }

    private Control Layout()
    {
        var heading = new TextBlock { Text = "Email Rules", FontWeight = FontWeight.SemiBold };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var applyTo = new TextBlock { Text = "Apply changes to this account:", VerticalAlignment = VerticalAlignment.Center };
        Bind(applyTo, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 6),
            Children =
            {
                Tool("New Rule…", NewRuleAsync),
                ChangeRuleButton(),
                Tool("Copy…", CopyAsync),
                Tool("Delete", DeleteAsync),
                Tool("▲", () => { Move(-1); return Task.CompletedTask; }),
                Tool("▼", () => { Move(1); return Task.CompletedTask; }),
                Tool("Run Rules Now…", RunNowAsync),
            },
        };

        Bind(_list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(_list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");

        var columns = new TextBlock { Text = "Rule (applied in the order shown)", Margin = new Thickness(0, 0, 0, 2) };
        Bind(columns, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var describe = new TextBlock
        {
            Text = "Rule description (click an underlined value to edit it):",
            Margin = new Thickness(0, 12, 0, 4),
        };
        Bind(describe, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, Width = 74 };
        close.Click += (_, _) => Close();

        Bind(_serverStatus, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        _retry.Click += async (_, _) => await PublishAsync();
        var serverRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _serverStatus, _retry },
        };

        var pickerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { applyTo, _account },
        };

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new DockPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children =
                    {
                        new StackPanel
                        {
                            [DockPanel.DockProperty] = Dock.Right,
                            Orientation = Orientation.Horizontal,
                            Children = { close },
                        },
                        serverRow,
                    },
                },
                new StackPanel { Children = { heading, pickerRow, toolbar, columns, _list, describe, _description } },
            },
        };

        return body;
    }

    private Control ChangeRuleButton()
    {
        var button = new Button { Content = "Change Rule ▾", Padding = new Thickness(9, 4) };
        var flyout = new MenuFlyout();

        var edit = new MenuItem { Header = "Edit Rule Settings…" };
        edit.Click += async (_, _) => await EditAsync();
        flyout.Items.Add(edit);

        var rename = new MenuItem { Header = "Rename Rule…" };
        rename.Click += async (_, _) => await RenameAsync();
        flyout.Items.Add(rename);

        button.Flyout = flyout;
        return button;
    }

    private static Button Tool(string label, Func<Task> run)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 4) };
        button.Click += async (_, _) => await run();
        return button;
    }

    private void Reload(long? select = null)
    {
        var rules = _current?.Mail.Rules() ?? [];
        var rows = rules.Select(r => new RuleRow(r)).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Rule.Id == select) ?? rows.FirstOrDefault();
        _description.Show((_list.SelectedItem as RuleRow)?.Rule);
        ShowServerState();
    }

    // ---- Rules on the server ------------------------------------------------------------------
    //
    // An IMAP account's server-side rules are put on the server after every change here, and
    // the line beside Close says where the server stands: how many rules it runs, or that it is
    // behind and why, with Retry. Nothing is asked of a POP3 account's server, which has no
    // rules to run.

    /// <summary>The line beside Close, from the store's state.</summary>
    private void ShowServerState()
    {
        if (_current is null || !SieveSync.Supports(_current))
        {
            _serverStatus.Text = string.Empty;
            _retry.IsVisible = false;
            return;
        }

        var onServer = _current.Mail.Rules().Count(r => r.Enabled && r.ServerSide);
        var state = _current.Mail.SieveState();
        if (state is null && onServer == 0)
        {
            _serverStatus.Text = "No rules run on the server.";
            _retry.IsVisible = false;
        }
        else if (state is null || state.Stale)
        {
            _serverStatus.Text = _publishing ? "Updating the server's rules…" : "The server's rules are out of date.";
            _retry.IsVisible = !_publishing;
        }
        else
        {
            _serverStatus.Text = $"{onServer} rule{(onServer == 1 ? "" : "s")} on the server, updated {state.Published.ToLocalTime():d MMM HH:mm}.";
            _retry.IsVisible = false;
        }
    }

    /// <summary>After a change: publish when the account has, or had, rules on the server. One at a time; a change during a publish queues another.</summary>
    private async Task SyncServerAsync()
    {
        if (_current is null || !SieveSync.Supports(_current)) return;
        var needed = _current.Mail.SieveState() is not null || _current.Mail.Rules().Any(r => r.ServerSide);
        if (!needed) { ShowServerState(); return; }
        await PublishAsync();
    }

    private async Task PublishAsync()
    {
        if (_current is null) return;
        if (_publishing) { _publishAgain = true; return; }

        var account = _current;
        _publishing = true;
        ShowServerState();
        try
        {
            do
            {
                _publishAgain = false;
                var outcome = await SieveSync.PublishAsync(account);
                if (!outcome.Ok) Log.Warn($"Rules and Alerts: {outcome.Message}");
                if (ReferenceEquals(account, _current) || account.Account.Address == _current?.Account.Address)
                {
                    _serverStatus.Text = outcome.Message;
                }
            }
            while (_publishAgain);
        }
        finally
        {
            _publishing = false;
        }

        // The list may show a rule the publisher sent back to this computer.
        if (account.Account.Address == _current?.Account.Address)
        {
            var selected = Selected?.Id;
            var rules = _current!.Mail.Rules();
            var rows = rules.Select(r => new RuleRow(r)).ToList();
            _list.ItemsSource = rows;
            _list.SelectedItem = rows.FirstOrDefault(r => r.Rule.Id == selected) ?? rows.FirstOrDefault();
            _retry.IsVisible = _current.Mail.SieveState() is { Stale: true } || (_current.Mail.SieveState() is null && rules.Any(r => r.Enabled && r.ServerSide));
        }
    }

    private MailRule? Selected => (_list.SelectedItem as RuleRow)?.Rule;

    private async Task NewRuleAsync()
    {
        if (_current is null) return;

        var wizard = new RuleWizard(_current.Mail, _current.Account.Id);
        await wizard.ShowDialog(this);
        if (wizard.Result is not { } rule) return;

        var stored = _current.Mail.AddRule(rule, DateTimeOffset.UtcNow);
        Reload(stored.Id);

        if (wizard.RunNow) await RunOnInboxAsync(stored);
        await SyncServerAsync();
    }

    private async Task EditAsync()
    {
        if (_current is null || Selected is not { } rule) return;

        var wizard = new RuleWizard(_current.Mail, _current.Account.Id, rule);
        await wizard.ShowDialog(this);
        if (wizard.Result is not { } edited) return;

        _current.Mail.UpdateRule(edited with { Id = rule.Id });
        Reload(rule.Id);

        if (wizard.RunNow) await RunOnInboxAsync(edited with { Id = rule.Id });
        await SyncServerAsync();
    }

    private async Task RenameAsync()
    {
        if (_current is null || Selected is not { } rule) return;

        var name = await Prompt.AskAsync(this, "Rename Rule", "New name of rule:", rule.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        _current.Mail.UpdateRule(rule with { Name = name.Trim() });
        Reload(rule.Id);
        await SyncServerAsync();
    }

    private async Task CopyAsync()
    {
        if (_current is null || Selected is not { } rule) return;

        var copy = _current.Mail.AddRule(rule with { Id = 0, Name = "Copy of " + rule.Name }, DateTimeOffset.UtcNow);
        Reload(copy.Id);
        await SyncServerAsync();
    }

    private async Task DeleteAsync()
    {
        if (_current is null || Selected is not { } rule) return;

        var go = await Confirm.AskAsync(this, "Rules and Alerts", $"Delete rule \"{rule.Name}\"?", "Delete");
        if (!go) return;

        _current.Mail.DeleteRule(rule.Id);
        Reload();
        await SyncServerAsync();
    }

    private void Move(int direction)
    {
        if (_current is null || Selected is not { } rule) return;

        var order = _current.Mail.Rules().Select(r => r.Id).ToList();
        var index = order.IndexOf(rule.Id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= order.Count) return;

        (order[index], order[target]) = (order[target], order[index]);
        _current.Mail.OrderRules(order);
        Reload(rule.Id);
        _ = SyncServerAsync();
    }

    private async Task RunNowAsync()
    {
        if (_current is null) return;
        await new RunRulesNowDialog(_current).ShowDialog(this);
    }

    private async Task RunOnInboxAsync(MailRule rule)
    {
        if (_current is null) return;
        if (_current.Mail.FolderWithRole(_current.Account.Id, FolderRole.Inbox) is not { } inbox) return;

        var count = App.Rules.RunNow(_current.Mail, inbox, [rule]);
        await Confirm.AskAsync(this, "Rules and Alerts",
            count == 0 ? "The rule matched no messages in the Inbox." : $"The rule was applied to {count} message{(count == 1 ? "" : "s")} in the Inbox.",
            "OK", destructive: false);
    }

    /// <summary>A click on an underlined value in the description edits it in place and saves the rule.</summary>
    private async Task EditClauseAsync(int index)
    {
        if (_current is null || Selected is not { } rule) return;

        var i = index - 1;
        MailRule? edited = null;

        if (i >= 0 && i < rule.Conditions.Count)
        {
            if (await RuleValues.EditAsync(this, rule.Conditions[i], _current.Mail) is { } c)
            {
                var list = rule.Conditions.ToList();
                list[i] = c;
                edited = rule with { Conditions = list };
            }
        }
        else if (i >= 0 && (i -= rule.Conditions.Count) < rule.Actions.Count)
        {
            if (await RuleValues.EditAsync(this, rule.Actions[i], _current.Mail, _current.Account.Id) is { } a)
            {
                var list = rule.Actions.ToList();
                list[i] = a;
                edited = rule with { Actions = list };
            }
        }
        else if (i >= 0 && (i -= rule.Actions.Count) < rule.Exceptions.Count)
        {
            if (await RuleValues.EditAsync(this, rule.Exceptions[i], _current.Mail) is { } e)
            {
                var list = rule.Exceptions.ToList();
                list[i] = e;
                edited = rule with { Exceptions = list };
            }
        }

        if (edited is null) return;
        _current.Mail.UpdateRule(edited);
        Reload(rule.Id);
        await SyncServerAsync();
    }
}

/// <summary>
/// Run Rules Now: which rules, in which folder, over which messages — then Run Now.
/// </summary>
public sealed class RunRulesNowDialog : Window
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public RunRulesNowDialog(OpenAccount account)
    {
        Title = "Run Rules Now";
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var rules = account.Mail.Rules();
        var chosen = new HashSet<long>();

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6, 4) };
        foreach (var rule in rules)
        {
            var box = new CheckBox { Content = rule.Name };
            Bind(box, TemplatedControl.ForegroundProperty, "dialog.surface.text.brush");
            var id = rule.Id;
            box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) chosen.Add(id); else chosen.Remove(id); };
            rows.Children.Add(box);
        }

        var list = new Border { Height = 160, BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = rows } };
        Bind(list, Border.BackgroundProperty, "dialog.surface.brush");
        Bind(list, Border.BorderBrushProperty, "dialog.border.brush");

        var selectAll = new Button { Content = "Select All" };
        selectAll.Click += (_, _) => { foreach (var box in rows.Children.OfType<CheckBox>()) box.IsChecked = true; };
        var unselectAll = new Button { Content = "Unselect All" };
        unselectAll.Click += (_, _) => { foreach (var box in rows.Children.OfType<CheckBox>()) box.IsChecked = false; };

        var folders = account.Mail.Folders(account.Account.Id).Where(f => f.Role != FolderRole.Outbox).ToList();
        var folder = new ComboBox { ItemsSource = folders.Select(f => f.Name).ToList(), MinWidth = 220 };
        folder.SelectedIndex = Math.Max(0, folders.FindIndex(f => f.Role == FolderRole.Inbox));

        var which = new ComboBox { ItemsSource = new List<string> { "All Messages", "Unread Messages", "Read Messages" }, SelectedIndex = 0, MinWidth = 180 };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Bind(status, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var run = new Button { Content = "Run Now", Width = 90, IsDefault = true };
        run.Click += (_, _) =>
        {
            if (folder.SelectedIndex < 0 || chosen.Count == 0)
            {
                status.Text = "Choose at least one rule.";
                return;
            }

            var target = folders[folder.SelectedIndex];
            var selected = rules.Where(r => chosen.Contains(r.Id)).ToList();
            var filter = which.SelectedIndex switch
            {
                1 => (Func<MessageSummary, bool>)(m => !m.IsRead),
                2 => m => m.IsRead,
                _ => _ => true,
            };

            var count = App.Rules.RunNow(account.Mail, target, selected, filter);
            status.Text = count == 0
                ? $"No message in {target.Name} matched."
                : $"{count} message{(count == 1 ? "" : "s")} in {target.Name} acted on.";
        };

        var close = new Button { Content = "Close", Width = 74, IsCancel = true };
        close.Click += (_, _) => Close();

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                Label("Select rules to run:"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { list, new StackPanel { Spacing = 6, Width = 110, Children = { selectAll, unselectAll } } },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Label("Run in Folder:"), folder } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Label("Apply rules to:"), which } },
                status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { run, close },
                },
            },
        };

        list.Width = 340;
        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }
}
