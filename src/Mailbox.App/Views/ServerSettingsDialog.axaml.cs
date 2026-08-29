using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MailKit.Security;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Server names, ports, encryption and the POP3 policy for one account.
/// </summary>
/// <remarks>
/// The reference splits this across "Server Settings" and "Account Name and Sync Settings".
/// Both are three fields about the same account, and separating them means guessing which of
/// two dialogs holds the setting you want. They are one dialog here, in the sections the
/// reference names.
/// </remarks>
public sealed class ServerSettingsDialog : Window
{
    private readonly Account _account;

    private readonly TextBox _displayName = new() { Classes = { "sysfield" }, Width = 260 };
    private readonly TextBox _incomingHost = new() { Classes = { "sysfield" }, Width = 260 };
    private readonly TextBox _incomingPort = new() { Classes = { "sysfield" }, Width = 80 };
    private readonly TextBox _incomingUser = new() { Classes = { "sysfield" }, Width = 260 };
    private readonly TextBox _outgoingHost = new() { Classes = { "sysfield" }, Width = 260 };
    private readonly TextBox _outgoingPort = new() { Classes = { "sysfield" }, Width = 80 };
    private readonly TextBox _outgoingUser = new() { Classes = { "sysfield" }, Width = 260 };
    private readonly ComboBox _incomingSecurity = SecurityCombo();
    private readonly ComboBox _outgoingSecurity = SecurityCombo();
    private readonly CheckBox _leaveOnServer = new() { Content = "Leave a copy of messages on the server" };
    private readonly CheckBox _deleteAfter = new() { Content = "Remove from the server after" };
    private readonly NumericUpDown _deleteDays = new()
    {
        Minimum = 1, Maximum = 3650, Value = 14, Width = 80,
    };

    // IMAP keeps its folders and flags on the server, so it has no "leave on server" — it has
    // "how much to keep offline" instead. 0 means everything.
    private readonly ComboBox _offlineMonths = new()
    {
        ItemsSource = new[] { "1 month", "3 months", "12 months", "24 months", "All" },
        Width = 160,
    };

    // Server-side rules reach the server by ManageSieve: the incoming host unless the account
    // says otherwise, and port 4190 by convention.
    private readonly TextBox _sieveHost = new() { Classes = { "sysfield" }, Width = 260, PlaceholderText = "same as the incoming server" };
    private readonly TextBox _sievePort = new() { Classes = { "sysfield" }, Width = 80 };

    private bool IsImap => _account.Protocol == MailProtocol.Imap;

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };

    /// <summary>True when settings were saved.</summary>
    public bool Saved { get; private set; }

    public ServerSettingsDialog(Account account)
    {
        _account = account;
        Title = $"Server Settings — {account.Address}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // A member of the Account Settings family — drawn the way the desktop draws its own
        // dialogs and light in every theme, never DialogChrome's theme-following palette. And no
        // Background bind after it: WindowFrame sets the window transparent so its rounded,
        // clipping border is the only thing that paints.
        SystemDialogChrome.Apply(this, Layout());
        Load();
    }

    private static ComboBox SecurityCombo() => new()
    {
        ItemsSource = new[] { "SSL/TLS", "STARTTLS", "None", "Automatic" },
        Width = 130,
    };

    private static SecureSocketOptions ToSecurity(int index) => index switch
    {
        0 => SecureSocketOptions.SslOnConnect,
        1 => SecureSocketOptions.StartTls,
        2 => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };

    private static int FromSecurity(SecureSocketOptions options) => options switch
    {
        SecureSocketOptions.SslOnConnect => 0,
        SecureSocketOptions.StartTls or SecureSocketOptions.StartTlsWhenAvailable => 1,
        SecureSocketOptions.None => 2,
        _ => 3,
    };

    private void Load()
    {
        _displayName.Text = _account.DisplayName;

        // The account's own protocol, not autoconfiguration's default. Asked without it, a POP3
        // account whose servers have never been recorded was offered the IMAP guess — imap.<domain>
        // on 993 — under the POP3 delivery section, and pressing Save wrote it down.
        var settings = AccountSettings.Load(App.Settings, _account.Address)
                       ?? AccountSettings.From(Autoconfig.ForAddress(
                           _account.Address,
                           IsImap ? MailProtocolKind.Imap : MailProtocolKind.Pop3));

        _incomingHost.Text = settings.IncomingHost;
        _incomingPort.Text = settings.IncomingPort.ToString();
        _incomingUser.Text = settings.IncomingUser;
        _incomingSecurity.SelectedIndex = FromSecurity(settings.IncomingSecurity);
        _outgoingHost.Text = settings.OutgoingHost;
        _outgoingPort.Text = settings.OutgoingPort.ToString();
        _outgoingUser.Text = settings.OutgoingUser;
        _outgoingSecurity.SelectedIndex = FromSecurity(settings.OutgoingSecurity);
        _leaveOnServer.IsChecked = settings.LeaveOnServer;
        _deleteAfter.IsChecked = settings.DeleteAfterDays is not null;
        if (settings.DeleteAfterDays is { } days) _deleteDays.Value = days;
        _offlineMonths.SelectedIndex = OfflineIndex(settings.OfflineMonths);
        _sieveHost.Text = settings.SieveHost;
        _sievePort.Text = settings.SievePort.ToString();

        // Removing from the server only means anything while a copy is being left there.
        _leaveOnServer.IsCheckedChanged += (_, _) => UpdatePolicyEnabled();
        UpdatePolicyEnabled();
    }

    private void UpdatePolicyEnabled()
    {
        var leaving = _leaveOnServer.IsChecked == true;
        _deleteAfter.IsEnabled = leaving;
        _deleteDays.IsEnabled = leaving && _deleteAfter.IsChecked == true;
        _deleteAfter.IsCheckedChanged -= OnDeleteAfterChanged;
        _deleteAfter.IsCheckedChanged += OnDeleteAfterChanged;
    }

    private void OnDeleteAfterChanged(object? sender, RoutedEventArgs e)
        => _deleteDays.IsEnabled = _leaveOnServer.IsChecked == true && _deleteAfter.IsChecked == true;

    private Control Layout()
    {
        var save = new Button { Content = "Save", IsDefault = true, Classes = { "sysbutton" } };
        save.Click += (_, _) => Save();

        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "sysbutton" } };
        cancel.Click += (_, _) => Close();

        Bind(_status, TextBlock.ForegroundProperty, "status.danger.brush");

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Section("Account"),
                Field("Account name", _displayName),

                Section("Incoming mail"),
                Field("Server", _incomingHost, "Port", _incomingPort),
                Field("Username", _incomingUser),
                Field("Encryption", _incomingSecurity),

                Section("Outgoing mail"),
                Field("Server", _outgoingHost, "Port", _outgoingPort),
                Field("Username", _outgoingUser),
                Field("Encryption", _outgoingSecurity),

                DeliverySection(),
                _status,
            },
        };

        return new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                body,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0),
                    Children = { cancel, save },
                },
            },
        };
    }

    /// <summary>
    /// POP3 decides what to do with a copy on the server; IMAP decides how much of the mailbox
    /// to keep here. Different questions, so the section is different by protocol rather than
    /// showing a POP3 policy that an IMAP account cannot honour.
    /// </summary>
    private Control DeliverySection()
    {
        if (IsImap)
        {
            return new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Section("Offline"),
                    Field("Mail to keep offline", _offlineMonths, labelWidth: 140),
                    Section("Rules on the server"),
                    Field("Rules server", _sieveHost, "Port", _sievePort),
                    Note("Rules marked \"run on the mail server\" in the Rules Wizard are put here by ManageSieve."),
                },
            };
        }

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Section("Delivery"),
                _leaveOnServer,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _deleteAfter, _deleteDays, Caption("days", 40) },
                },
            },
        };
    }

    private static readonly int[] OfflineChoices = [1, 3, 12, 24, 0];

    private static int OfflineIndex(int months)
    {
        var found = Array.IndexOf(OfflineChoices, months);
        return found >= 0 ? found : 2;
    }

    private Control Section(string title)
    {
        var block = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 2),
        };
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        return block;
    }

    private Control Field(string label, Control control, string? second = null,
        Control? secondControl = null, double labelWidth = 100)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Caption(label, labelWidth), control },
        };

        if (second is not null && secondControl is not null)
        {
            row.Children.Add(Caption(second, 34));
            row.Children.Add(secondControl);
        }

        return row;
    }

    private TextBlock Note(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Left };
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");
        return block;
    }

    private TextBlock Caption(string text, double width)
    {
        var block = new TextBlock
        {
            Text = text,
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        return block;
    }

    private void Save()
    {
        var incomingHost = (_incomingHost.Text ?? string.Empty).Trim();
        if (incomingHost.Length == 0)
        {
            _status.Text = "An incoming server is needed before mail can be collected.";
            return;
        }

        // What this form does not show is carried across rather than left to its default. Built
        // from nothing, a save here turned an account that signs in through a browser back into a
        // password account — `auth OAuth2` to `auth Password`, the provider and the registration
        // both wiped — so the next send/receive failed as "authentication failed" against a
        // keyring that holds only a refresh token; and it put a POP3 account's delivery folder
        // back to the Inbox, undoing the Change Folder button on the tab this dialog opens from.
        var held = AccountSettings.Load(App.Settings, _account.Address);

        var settings = new AccountSettings(
            incomingHost,
            Port(_incomingPort.Text, 995),
            ToSecurity(_incomingSecurity.SelectedIndex),
            (_incomingUser.Text ?? string.Empty).Trim(),
            (_outgoingHost.Text ?? string.Empty).Trim(),
            Port(_outgoingPort.Text, 587),
            ToSecurity(_outgoingSecurity.SelectedIndex),
            (_outgoingUser.Text ?? string.Empty).Trim())
        {
            LeaveOnServer = _leaveOnServer.IsChecked == true,
            DeleteAfterDays = _leaveOnServer.IsChecked == true && _deleteAfter.IsChecked == true
                ? (int)(_deleteDays.Value ?? 14)
                : null,
            OfflineMonths = OfflineChoices[Math.Clamp(_offlineMonths.SelectedIndex, 0, OfflineChoices.Length - 1)],
            SieveHost = (_sieveHost.Text ?? string.Empty).Trim(),
            SievePort = Port(_sievePort.Text, 4190),

            Auth = held?.Auth ?? AuthKind.Password,
            OAuthProviderId = held?.OAuthProviderId ?? string.Empty,
            OAuthClientId = held?.OAuthClientId ?? string.Empty,
            DeliveryFolderId = held?.DeliveryFolderId,
        };

        settings.Save(App.Settings, _account.Address);
        if (App.Accounts.Find(_account.Address) is { } open)
        {
            open.Mail.RenameAccount(_account.Id, (_displayName.Text ?? string.Empty).Trim());
        }

        Log.Info($"Server settings saved for {_account.Address}.");
        Saved = true;
        Close();
    }

    private static int Port(string? text, int fallback)
        => int.TryParse(text, out var port) && port is > 0 and < 65536 ? port : fallback;

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
