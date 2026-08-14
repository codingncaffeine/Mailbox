using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The account list: add, remove, reorder, and choose which one sends by default.
/// </summary>
/// <remarks>
/// The reference gives this seven tabs. Six are here. SharePoint Lists is the one left out: it
/// is a SharePoint feature this application does not have and will not get, and showing it empty
/// would be a promise rather than a gap. Published Calendars is kept — the reference publishes
/// to its own service, but publishing a calendar is CalDAV here, which the calendar module
/// brings.
/// <para>
/// Data Files is translated rather than copied. The reference lists a .pst per account; there
/// is one store here holding every account, so the tab shows that file, its size and what is in
/// it. Presenting a per-account file that does not exist would be a fiction with a Remove
/// button next to it.
/// </para>
/// </remarks>
public sealed class AccountSettingsDialog : Window
{
    private readonly ListBox _accounts = new() { Height = 190 };
    private readonly TextBlock _delivery = new();
    private readonly Button _change;
    private readonly Button _repair;
    private readonly Button _remove;
    private readonly Button _setDefault;
    private readonly Button _up;
    private readonly Button _down;

    /// <summary>True when something changed, so the shell knows to reload.</summary>
    public bool Changed { get; private set; }

    public AccountSettingsDialog()
    {
        Title = "Account Settings";
        Width = 640;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _change = ToolButton("Change…", async () => await ChangeSelectedAsync());
        _repair = ToolButton("Repair…", async () => await RepairSelectedAsync());
        _remove = ToolButton("Remove", RemoveSelected);
        _setDefault = ToolButton("Set as Default", SetDefault);
        _up = ToolButton("↑", () => Move(-1));
        _down = ToolButton("↓", () => Move(1));

        _accounts.SelectionChanged += (_, _) => UpdateButtons();
        _accounts.ItemTemplate = new FuncDataTemplate<AccountRow>((row, _) => Row(row));

        Content = Layout();
        Bind(this, BackgroundProperty, "surface.ground.brush");
        Reload();
    }

    /// <summary>One line in the list: the two columns the reference shows.</summary>
    private sealed record AccountRow(OpenAccount Open)
    {
        public Account Account => Open.Account;

        public string Name => Account.DisplayName.Length > 0
            ? Account.DisplayName
            : Account.Address;

        public string Type => Open.IsDefault
            ? $"{Account.TypeLabel} (send from this account by default)"
            : Account.TypeLabel;

        public string Marker => Open.IsDefault ? "✓" : string.Empty;
    }

    private Control Row(AccountRow row)
    {
        var marker = new TextBlock { Text = row.Marker, Width = 20 };
        Bind(marker, TextBlock.ForegroundProperty, "accent.rest.brush");

        var name = new TextBlock { Text = row.Name, Width = 300 };
        Bind(name, TextBlock.ForegroundProperty, "text.primary.brush");

        var type = new TextBlock { Text = row.Type };
        Bind(type, TextBlock.ForegroundProperty, "text.secondary.brush");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { marker, name, type },
        };
    }

    private Control Layout()
    {
        var heading = new TextBlock { Text = "Email Accounts", FontWeight = FontWeight.SemiBold };
        Bind(heading, TextBlock.ForegroundProperty, "text.primary.brush");

        var explain = new TextBlock
        {
            Text = "You can add or remove an account. You can select an account and change its "
                   + "settings.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 14),
        };
        Bind(explain, TextBlock.ForegroundProperty, "text.secondary.brush");

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                ToolButton("New…", async () => await AddAsync()),
                _repair, _change, _setDefault, _remove, _up, _down,
            },
        };

        Bind(_delivery, TextBlock.ForegroundProperty, "text.secondary.brush");
        _delivery.TextWrapping = TextWrapping.Wrap;
        _delivery.Margin = new Thickness(0, 14, 0, 0);

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var email = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                heading, explain, toolbar, _accounts,
                new TextBlock
                {
                    Text = "Selected account delivers new messages to the following location:",
                    Margin = new Thickness(0, 14, 0, 4),
                },
                _delivery,
            },
        };

        var stack = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "Email", Content = email },
                new TabItem { Header = "Data Files", Content = DataFilesTab() },
                new TabItem { Header = "RSS Feeds", Content = ComingLater(
                    "RSS Feeds",
                    "You can add or remove an RSS Feed. You can select an RSS Feed and change "
                    + "its settings.",
                    "Feed subscriptions arrive with the RSS reader.") },
                new TabItem { Header = "Internet Calendars", Content = ComingLater(
                    "Internet Calendars",
                    "You can add or remove a calendar. You can select a calendar and change its "
                    + "settings.",
                    "Subscribed calendars arrive with the calendar module.") },
                new TabItem { Header = "Published Calendars", Content = ComingLater(
                    "Published Calendars",
                    "You can publish a calendar so other people can subscribe to it, and change "
                    + "or remove one you have published.",
                    "Publishing arrives with the calendar module, over CalDAV rather than the "
                    + "reference's own service.") },
                new TabItem { Header = "Address Books", Content = ComingLater(
                    "Directories and Address Books",
                    "You can choose a directory or address book below to change or remove it.",
                    "Address books arrive with the People module.") },
            },
        };

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
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { close },
                },
                stack,
            },
        };
    }

    /// <summary>
    /// The store, described honestly: one file, its size, and what is filed in it.
    /// </summary>
    /// <summary>
    /// One file per account, listed by name. This is the tab the arrangement earns: each row is
    /// a file that can be backed up, copied to another machine or deleted on its own.
    /// </summary>
    private Control DataFilesTab()
    {
        var rows = new StackPanel { Spacing = 6 };

        foreach (var account in App.Accounts.All)
        {
            var messages = account.Mail.Folders(account.Account.Id).Sum(f => f.Total);
            rows.Children.Add(Detail(
                Path.GetFileName(account.Path),
                $"{MailboxCleanupDialog.Size(account.Bytes)}  ·  {messages:N0} messages"));
        }

        if (App.Accounts.All.Count == 0) rows.Children.Add(Detail("No accounts yet", string.Empty));

        var open = new Button { Content = "Open File Location…", Padding = new Thickness(9, 4) };
        open.Click += (_, _) => OpenStoreFolder();

        rows.Children.Add(Detail("Folder", App.Accounts.Directory_));
        rows.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { open },
        });

        return Panel(
            "Data Files",
            "Each account is a file of its own, named after the address. Copy one somewhere "
            + "safe and that account is backed up; delete one and only that account goes.",
            rows);
    }

    private void OpenStoreFolder()
    {
        try
        {
            var folder = App.Accounts.Directory_;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Mailbox.Core.Diagnostics.Log.Warn("Could not open the store's folder.", ex);
        }
    }

    private Control Detail(string label, string value)
    {
        var name = new TextBlock { Text = label, Width = 90 };
        Bind(name, TextBlock.ForegroundProperty, "text.secondary.brush");

        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        Bind(text, TextBlock.ForegroundProperty, "text.primary.brush");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { name, text },
        };
    }

    /// <summary>
    /// A tab whose feature belongs to a later phase. It says what it will hold and which part
    /// of the application brings it, rather than showing an empty list and a dead New button.
    /// </summary>
    private Control ComingLater(string heading, string explain, string note)
    {
        var pending = new TextBlock
        {
            Text = note,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 16, 0, 0),
        };
        Bind(pending, TextBlock.ForegroundProperty, "text.secondary.brush");
        return Panel(heading, explain, pending);
    }

    private Control Panel(string heading, string explain, Control body)
    {
        var title = new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold };
        Bind(title, TextBlock.ForegroundProperty, "text.primary.brush");

        var description = new TextBlock
        {
            Text = explain,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 500,
            Margin = new Thickness(0, 2, 0, 12),
        };
        Bind(description, TextBlock.ForegroundProperty, "text.secondary.brush");

        return new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Children = { title, description, body },
        };
    }

    private Button ToolButton(string label, Func<Task> onClick)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 4) };
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private Button ToolButton(string label, Action onClick)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Reload()
    {
        var selectedAddress = Selected?.Account.Address;

        _accounts.ItemsSource = App.Accounts.All.Select(a => new AccountRow(a)).ToList();
        _accounts.SelectedIndex = _accounts.Items
            .OfType<AccountRow>()
            .ToList()
            .FindIndex(r => r.Account.Address == selectedAddress);

        if (_accounts.SelectedIndex < 0 && _accounts.ItemCount > 0) _accounts.SelectedIndex = 0;
        UpdateButtons();
    }

    private AccountRow? Selected => _accounts.SelectedItem as AccountRow;

    private void UpdateButtons()
    {
        var row = Selected;
        _change.IsEnabled = row is not null;
        _repair.IsEnabled = row is not null;
        _remove.IsEnabled = row is not null;
        _setDefault.IsEnabled = row is not null && !row.Open.IsDefault;
        _up.IsEnabled = row is not null && _accounts.SelectedIndex > 0;
        _down.IsEnabled = row is not null && _accounts.SelectedIndex < _accounts.ItemCount - 1;

        _delivery.Text = row is null
            ? "No account selected."
            : $"{row.Account.Address}\\Inbox  —  in {row.Open.Path}";
    }

    private async Task AddAsync()
    {
        var wizard = new AccountWizard();
        await wizard.ShowDialog(this);

        if (wizard.Created is null) return;
        Changed = true;
        Reload();
    }

    private async Task ChangeSelectedAsync()
    {
        if (Selected is not { } row) return;

        var dialog = new ServerSettingsDialog(row.Account);
        await dialog.ShowDialog(this);

        if (!dialog.Saved) return;
        Changed = true;
        Reload();
    }

    /// <summary>
    /// Re-runs autoconfig and offers what it finds. The reference calls this Repair and has it
    /// re-run autodiscover; the equivalent here is to work the servers out again from the
    /// address, which is what fixes an account that was set up wrong or whose provider moved.
    /// </summary>
    private async Task RepairSelectedAsync()
    {
        if (Selected is not { } row) return;

        var current = AccountSettings.Load(App.Settings, row.Account.Address);
        var found = Autoconfig.ForAddress(
            row.Account.Address,
            row.Account.Protocol == MailProtocol.Imap
                ? MailProtocolKind.Imap
                : MailProtocolKind.Pop3);

        var proposed = AccountSettings.From(found);
        var unchanged = current is not null
                        && current.IncomingHost == proposed.IncomingHost
                        && current.IncomingPort == proposed.IncomingPort
                        && current.OutgoingHost == proposed.OutgoingHost
                        && current.OutgoingPort == proposed.OutgoingPort;

        if (unchanged)
        {
            await Confirm.AskAsync(this, "Repair account",
                $"The settings for {row.Account.Address} already match what Mailbox would work "
                + "out from the address. Nothing to change.",
                "OK", destructive: false);
            return;
        }

        var apply = await Confirm.AskAsync(
            this,
            "Repair account",
            $"Replace the server settings for {row.Account.Address} with these?\n\n"
            + $"Incoming    {proposed.IncomingHost}:{proposed.IncomingPort}\n"
            + $"Outgoing    {proposed.OutgoingHost}:{proposed.OutgoingPort}\n\n"
            + (found.IsKnownProvider
                ? "These are the published settings for this provider."
                : "These are a guess from the domain, not published settings."),
            "Replace",
            destructive: false);

        if (!apply) return;

        (current is null ? proposed : proposed with
        {
            LeaveOnServer = current.LeaveOnServer,
            DeleteAfterDays = current.DeleteAfterDays,
        }).Save(App.Settings, row.Account.Address);

        Changed = true;
        Reload();
    }

    private void SetDefault()
    {
        if (Selected is not { } row) return;

        App.AccountOrder.DefaultAddress = row.Account.Address;
        Changed = true;
        Reload();
    }

    private void Move(int direction)
    {
        if (Selected is not { } row) return;

        App.AccountOrder.Move(row.Account.Address, direction);
        Changed = true;
        Reload();
    }

    /// <summary>
    /// Removes an account and everything filed under it. Confirmed first, and the wording says
    /// what actually goes: with POP3 the store may be the only copy left.
    /// </summary>
    private async void RemoveSelected()
    {
        if (Selected is not { } row) return;

        var messages = row.Open.Mail.Folders(row.Account.Id).Sum(f => f.Total);

        var confirmed = await Confirm.AskAsync(
            this,
            "Remove account",
            $"Remove {row.Account.Address}?\n\n" +
            (messages > 0
                ? $"{messages:N0} message{(messages == 1 ? "" : "s")} will be deleted with it. " +
                  "Where mail was downloaded and removed from the server, this is the only " +
                  $"copy.\n\nThe file {Path.GetFileName(row.Open.Path)} will be deleted."
                : $"No mail is filed under this account. The file " +
                  $"{Path.GetFileName(row.Open.Path)} will be deleted."),
            "Remove");

        if (!confirmed) return;

        App.Accounts.Remove(row.Account.Address);
        Changed = true;
        Reload();
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
