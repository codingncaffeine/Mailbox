using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
/// are several.
/// <para>
/// <b>Nothing here writes until OK or Apply.</b> Every button works on a list held in memory and
/// the store is not touched until one of those is pressed, so Cancel really does mean the rule
/// you deleted is still there. This is the reference's own contract and it is worth the extra
/// machinery: a rules dialog where Cancel had already saved is how somebody loses the filing
/// that a busy mailbox depends on, and they would find out days later.
/// </para>
/// <para>
/// The exceptions are the two things that are not settings: Run Rules Now and the wizard's "run
/// this rule now" both act on messages immediately, so both commit first — a rule cannot be run
/// before it exists, and pretending otherwise would run a different rule from the one on screen.
/// </para>
/// <para>
/// <b>This is a system dialog.</b> The reference draws it, and the wizard behind it, with the
/// desktop's own controls — light grey on a white list, in every theme, exactly as Account
/// Settings is drawn (`rules and alerts/manage rules &amp; alerts.png`, where the dialog is
/// #F0F0F0 over a dark backstage). So it takes <c>systemdialog.*</c> and the classic kit and
/// names no colour of its own.
/// </para>
/// </remarks>
public sealed class RulesAndAlertsDialog : Window
{
    private readonly ClassicListView _list = new();

    /// <summary>Where the Actions column starts, and where its header starts with it.</summary>
    private const double NameColumn = 300;
    private readonly RuleDescriptionView _description = new();
    private readonly ComboBox _account = new() { MinWidth = 220 };
    private readonly TextBlock _empty = new()
    {
        Text = "Select the \"New Rule\" button to make a rule.",
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 24, 0, 0),
        IsHitTestVisible = false,
    };

    private readonly CheckBox _rss = new() { Content = "Enable rules on all messages downloaded from RSS Feeds" };

    /// <summary>The working list. The store holds none of this until OK or Apply.</summary>
    private readonly List<RuleRow> _rows = [];

    /// <summary>What the store held when the list was last read or written, to diff against.</summary>
    private List<MailRule> _baseline = [];

    private Button? _ok;
    private Button? _cancel;
    private Button? _apply;
    private long _nextTemporaryId = -1;
    private bool _rssBaseline;
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
        Width = 575;
        Height = 520;
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

        SystemDialogChrome.Apply(this, Layout());
        Reload();
    }

    /// <summary>
    /// One line of the working list. <see cref="Key"/> is the store's id, or a negative number
    /// for a rule made here and not yet written — a new rule has to be selectable, movable and
    /// deletable before it has an id of its own.
    /// </summary>
    private sealed class RuleRow(MailRule rule, long key)
    {
        public MailRule Rule { get; set; } = rule;

        public long Key { get; } = key;

        public bool Enabled { get; set; } = rule.Enabled;
    }

    /// <summary>The list's rows: the tick, the rule's name, and what it does.</summary>
    private void FillList(long? select)
    {
        var rows = _rows
            .Select(r => new ClassicRow(
                [r.Rule.Name + (r.Rule.ServerSide ? "  (on the server)" : string.Empty), RuleDescription.Actions(r.Rule)],
                Tag: r,
                Checked: r.Enabled))
            .ToList();

        _list.SetRows(rows);
        var index = _rows.FindIndex(r => r.Key == select);
        _list.SelectedIndex = index >= 0 ? index : _rows.Count > 0 ? 0 : -1;
        _empty.IsVisible = _rows.Count == 0;
    }

    private Control Layout()
    {
        _list.Columns =
        [
            new ClassicColumn("Rule (applied in the order shown)", NameColumn),
            new ClassicColumn("Actions", 220),
        ];
        _list.Height = 170;
        _list.SelectionChanged += (_, _) => _description.Show(SelectedRow?.Rule);
        _list.ItemActivated += async (_, _) => await EditAsync();
        _list.RowToggled += (_, index) =>
        {
            if (index < 0 || index >= _rows.Count) return;
            _rows[index].Enabled = !_rows[index].Enabled;
            FillList(_rows[index].Key);
            ShowServerState();
            Refresh();
        };

        _description.UseSystemPalette();
        _description.ValueClicked += async (_, index) => await EditClauseAsync(index);

        var toolbar = SystemDialogKit.Toolbar(
            SystemDialogKit.ToolButton("new", "New Rule...", NewRuleAsync),
            ChangeRuleButton(),
            SystemDialogKit.ToolButton("change", "Copy...", Copy),
            SystemDialogKit.ToolButton("remove", "Delete", DeleteAsync),
            SystemDialogKit.ToolButton("up", string.Empty, () => Move(-1)),
            SystemDialogKit.ToolButton("down", string.Empty, () => Move(1)),
            SystemDialogKit.ToolButton(string.Empty, "Run Rules Now...", RunNowAsync),
            SystemDialogKit.ToolButton(string.Empty, "Options", OptionsAsync));

        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.Margin = new Thickness(0, 30, 0, 0);
        SystemDialogKit.Bind(_empty, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

        var described = new Border
        {
            Height = 120,
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                Content = _description,
                Padding = new Thickness(4),
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            },
        };
        SystemDialogKit.Bind(described, Border.BackgroundProperty, "systemdialog.list.background.brush");
        SystemDialogKit.Bind(described, Border.BorderBrushProperty, "systemdialog.field.border.brush");

        SystemDialogKit.Bind(_rss, TemplatedControl.ForegroundProperty, "systemdialog.foreground.brush");
        _rss.Margin = new Thickness(0, 12, 0, 0);
        _rss.IsCheckedChanged += (_, _) => Refresh();

        var rules = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 10),
            Children =
            {
                toolbar,
                new Panel { Margin = new Thickness(0, 6, 0, 0), Children = { _list, _empty } },
                new TextBlock
                {
                    Text = "Rule description (click an underlined value to edit):",
                    Margin = new Thickness(0, 12, 0, 4),
                    [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("systemdialog.foreground.brush"),
                },
                described,
                _rss,
            },
        };

        // Only when there is more than one account: with one, the reference's dialog has no such
        // row and neither should this. Rules belong to an account, so with several there has to
        // be a way to say which — and it goes above the toolbar, where the thing it scopes is.
        if (App.Accounts.All.Count > 1)
        {
            rules.Children.Insert(0, new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 8),
                Children = { SystemDialogKit.Label("Apply changes to this account:"), _account },
            });
        }

        var tabs = new ClassicTabControl { Margin = new Thickness(10, 8, 10, 0) };
        tabs.AddTab("Email Rules", rules);
        tabs.AddTab("Manage Alerts", ManageAlerts());

        _ok = SystemDialogKit.PushButton("OK", async () => { if (await CommitAsync()) Close(); });
        _cancel = SystemDialogKit.PushButton("Cancel", Close);
        _apply = SystemDialogKit.PushButton("Apply", async () => await CommitAsync());

        SystemDialogKit.Bind(_serverStatus, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        _retry.Click += async (_, _) => await PublishAsync();

        var footer = new DockPanel
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Margin = new Thickness(12, 10, 12, 12),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Right,
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { _ok, _cancel, _apply },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { _serverStatus, _retry },
                },
            },
        };

        return new DockPanel { Children = { footer, tabs } };
    }

    /// <summary>
    /// The Manage Alerts tab, which says what it is instead of pretending to be it.
    /// </summary>
    /// <remarks>
    /// <b>A stated divergence.</b> The reference's tab subscribes to alerts raised by a document
    /// library on a corporate server — §3 puts that service out of scope and there is nothing
    /// here to subscribe to. Drawn rather than dropped because the reference draws it, and a tab
    /// that quietly vanished would read as a fault in the one dialog people open when their
    /// filing has stopped working. What it must not do is look like it is waiting for something.
    /// </remarks>
    private Control ManageAlerts() => new StackPanel
    {
        Margin = new Thickness(14, 16, 14, 14),
        Spacing = 10,
        Children =
        {
            SystemDialogKit.Paragraph("Alerts come from a document library on a corporate server, telling you "
                + "when somebody there adds or changes a file."),
            SystemDialogKit.Paragraph("Mailbox has no such server to subscribe to, and no plan to add one, so "
                + "there is nothing to manage here."),
            SystemDialogKit.Paragraph("The alerts Mailbox does raise — the New Item Alert window, a Desktop "
                + "Alert, a sound — are actions a rule takes, and are set on the Email Rules tab with the "
                + "rest of the rule."),
        },
    };

    /// <summary>Apply is only worth pressing when something is staged, and says so by going grey.</summary>
    private void Refresh()
    {
        if (_apply is not null) _apply.IsEnabled = Dirty;
    }

    /// <summary>
    /// The toolbar's Options: export the account's rules to a file, or bring some in from one.
    /// </summary>
    /// <remarks>
    /// The reference's Options is Import and Export, and both are worth having for their own
    /// sake — a set of rules somebody has spent years on is the thing they would most want to
    /// carry to another machine, and this application has no other way to move one. The file is
    /// this application's own JSON rather than the reference's undocumented binary.
    /// <para>
    /// <b>Import adds rather than replaces</b>, and lands in the working list rather than the
    /// store, so an import that brought the wrong file is undone by Cancel like anything else
    /// here. Export writes what is on screen, not what is in the store: handing somebody a file
    /// that did not match the page they were looking at would be the worse surprise.
    /// </para>
    /// </remarks>
    private async Task OptionsAsync()
    {
        if (_current is null) return;

        var choice = await Chooser.AskAsync(this, "Options", "Rules:",
        [
            new Choice("Export Rules to a file…", "export"),
            new Choice("Import Rules from a file…", "import"),
        ], "export");

        if (choice == "export") await ExportRulesAsync();
        else if (choice == "import") await ImportRulesAsync();
    }

    private async Task ExportRulesAsync()
    {
        if (_current is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Rules",
            SuggestedFileName = "rules.json",
            DefaultExtension = "json",
            FileTypeChoices = [RuleFiles],
        });

        if (file?.TryGetLocalPath() is not { } path) return;

        var document = RuleTransfer.Write([.. _rows.Select(r => r.Rule with { Enabled = r.Enabled })]);
        try
        {
            await File.WriteAllTextAsync(path, document);
            _serverStatus.Text = $"{_rows.Count} rule{(_rows.Count == 1 ? "" : "s")} written to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Log.Warn("Rules could not be exported.", ex);
            _serverStatus.Text = "The rules could not be written: " + ex.Message;
        }
    }

    private async Task ImportRulesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Rules",
            AllowMultiple = false,
            FileTypeFilter = [RuleFiles],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        IReadOnlyList<MailRule> read;
        try
        {
            read = RuleTransfer.Read(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            Log.Warn("Rules could not be imported.", ex);
            _serverStatus.Text = "That file could not be read as rules.";
            return;
        }

        if (read.Count == 0)
        {
            _serverStatus.Text = "That file holds no rules.";
            return;
        }

        foreach (var rule in read) _rows.Add(new RuleRow(rule with { Id = 0 }, _nextTemporaryId--));
        Redraw(_rows[^1].Key);
        _serverStatus.Text = $"{read.Count} rule{(read.Count == 1 ? "" : "s")} added. Apply to keep them.";
    }

    private static readonly FilePickerFileType RuleFiles = new("Mailbox rules") { Patterns = ["*.json"] };

    private Button ChangeRuleButton()
    {
        var button = SystemDialogKit.ToolButton("change", "Change Rule", () => { });
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

    /// <summary>Reads the store into the working list, discarding whatever was staged.</summary>
    private void Reload(long? select = null)
    {
        _baseline = [.. _current?.Mail.Rules() ?? []];
        _rows.Clear();
        _rows.AddRange(_baseline.Select(r => new RuleRow(r, r.Id)));
        _rssBaseline = App.MailOptions.RulesOnFeeds;
        _rss.IsChecked = _rssBaseline;
        Redraw(select);
    }

    /// <summary>Puts the working list on screen without touching it.</summary>
    private void Redraw(long? select = null)
    {
        FillList(select);
        _description.Show(SelectedRow?.Rule);
        ShowServerState();
        Refresh();
    }

    /// <summary>Whether anything is staged — what makes Apply worth pressing.</summary>
    private bool Dirty
    {
        get
        {
            if (_rss.IsChecked == true != _rssBaseline) return true;
            if (_rows.Count != _baseline.Count) return true;

            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Rule.Id != _baseline[i].Id) return true;
                if (!Same(_rows[i].Rule with { Enabled = _rows[i].Enabled }, _baseline[i])) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Whether two rules are the same rule. By their document rather than by value.
    /// </summary>
    /// <remarks>
    /// A record compares its lists by reference, so two rules with identical conditions built a
    /// moment apart are never equal to <c>==</c> — the trap that would make every rule look
    /// edited and rewrite the lot on every Apply. The definition JSON is what the store keeps and
    /// is exactly the comparison that matters.
    /// </remarks>
    private static bool Same(MailRule a, MailRule b)
        => a.Id == b.Id
           && a.Enabled == b.Enabled
           && a.ServerSide == b.ServerSide
           && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
           && string.Equals(a.DefinitionJson(), b.DefinitionJson(), StringComparison.Ordinal);

    /// <summary>
    /// Writes the working list to the store: what went, what is new, what changed, and the order.
    /// </summary>
    /// <remarks>
    /// Deletes first, so a rule removed and another added in its place cannot collide; adds next,
    /// which is what gives a new rule its id; then the order, over the ids the list now holds.
    /// A rule that has not changed is not rewritten — an untouched server-side rule would
    /// otherwise mark the Sieve script stale on every Apply and republish the lot.
    /// </remarks>
    private async Task<bool> CommitAsync()
    {
        if (_current is null) return false;
        if (!Dirty) return true;

        var mail = _current.Mail;
        var live = _rows.Select(r => r.Rule with { Enabled = r.Enabled }).ToList();

        foreach (var gone in _baseline.Where(b => _rows.All(r => r.Rule.Id != b.Id)))
        {
            mail.DeleteRule(gone.Id);
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            var staged = live[i];
            if (staged.Id == 0)
            {
                var stored = mail.AddRule(staged, DateTimeOffset.UtcNow);
                _rows[i].Rule = staged with { Id = stored.Id };
                continue;
            }

            if (_baseline.FirstOrDefault(b => b.Id == staged.Id) is { } was && !Same(staged, was))
            {
                mail.UpdateRule(staged);
            }
        }

        mail.OrderRules([.. _rows.Select(r => r.Rule.Id)]);

        if (_rss.IsChecked == true != _rssBaseline) App.MailOptions.RulesOnFeeds = _rss.IsChecked == true;

        Reload(Selected?.Id);
        await SyncServerAsync();
        return true;
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

        // The publisher may have sent a rule back to this computer, so the list is read again —
        // and safely, because publishing only ever follows a commit, so there is nothing staged
        // for this to throw away.
        if (account.Account.Address == _current?.Account.Address)
        {
            Reload(Selected?.Id);
        }
    }

    private RuleRow? SelectedRow => _list.SelectedRow?.Tag as RuleRow;

    private MailRule? Selected => SelectedRow?.Rule;

    private async Task NewRuleAsync()
    {
        if (_current is null) return;

        var wizard = new RuleWizard(_current.Mail, _current.Account.Id);
        await wizard.ShowDialog(this);
        if (wizard.Result is not { } rule) return;

        var row = new RuleRow(rule with { Id = 0 }, _nextTemporaryId--);
        _rows.Add(row);
        Redraw(row.Key);

        if (wizard.RunNow) await RunStagedAsync(row);
    }

    private async Task EditAsync()
    {
        if (_current is null || SelectedRow is not { } row) return;

        var wizard = new RuleWizard(_current.Mail, _current.Account.Id, row.Rule);
        await wizard.ShowDialog(this);
        if (wizard.Result is not { } edited) return;

        row.Rule = edited with { Id = row.Rule.Id };
        row.Enabled = row.Rule.Enabled;
        Redraw(row.Key);

        if (wizard.RunNow) await RunStagedAsync(row);
    }

    private async Task RenameAsync()
    {
        if (SelectedRow is not { } row) return;

        var name = await Prompt.AskAsync(this, "Rename Rule", "New name of rule:", row.Rule.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        row.Rule = row.Rule with { Name = name.Trim() };
        Redraw(row.Key);
    }

    private void Copy()
    {
        if (Selected is not { } rule) return;

        var row = new RuleRow(rule with { Id = 0, Name = "Copy of " + rule.Name }, _nextTemporaryId--);
        _rows.Add(row);
        Redraw(row.Key);
    }

    private async Task DeleteAsync()
    {
        if (SelectedRow is not { } row) return;

        var go = await Confirm.AskAsync(this, "Rules and Alerts", $"Delete rule \"{row.Rule.Name}\"?", "Delete");
        if (!go) return;

        _rows.Remove(row);
        Redraw();
    }

    private void Move(int direction)
    {
        if (SelectedRow is not { } row) return;

        var index = _rows.IndexOf(row);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _rows.Count) return;

        (_rows[index], _rows[target]) = (_rows[target], _rows[index]);
        Redraw(row.Key);
    }

    /// <summary>
    /// Run Rules Now, which acts on messages — so what is staged is written first.
    /// </summary>
    /// <remarks>
    /// Running the list on screen while the store held a different one is the one outcome worth
    /// ruling out here: the reader would be watching a rule they can see do something they cannot
    /// account for. Saying so before saving is fair warning — the alternative is a Cancel that no
    /// longer cancels.
    /// </remarks>
    private async Task RunNowAsync()
    {
        if (_current is null) return;

        if (Dirty)
        {
            var go = await Confirm.AskAsync(this, "Run Rules Now",
                "Running rules saves the changes on this page first.\n\n"
                + "Rules act on messages, and a rule has to be saved before it can run.",
                "Save and Run");
            if (!go) return;
            if (!await CommitAsync()) return;
        }

        await new RunRulesNowDialog(_current).ShowDialog(this);
    }

    /// <summary>The wizard's "run this rule now": the same bargain, for the one rule.</summary>
    private async Task RunStagedAsync(RuleRow row)
    {
        if (!await CommitAsync()) return;

        // After a commit the row holds the rule with the id the store gave it, which is the one
        // that can actually be run.
        var saved = _rows.FirstOrDefault(r => r.Key == row.Key)?.Rule ?? row.Rule;
        if (saved.Id > 0) await RunOnInboxAsync(saved);
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

        if (edited is null || SelectedRow is not { } row) return;

        row.Rule = edited;
        Redraw(row.Key);
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
