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

    private readonly TextBox _displayName = new() { Width = 260 };
    private readonly TextBox _incomingHost = new() { Width = 260 };
    private readonly TextBox _incomingPort = new() { Width = 80 };
    private readonly TextBox _incomingUser = new() { Width = 260 };
    private readonly TextBox _outgoingHost = new() { Width = 260 };
    private readonly TextBox _outgoingPort = new() { Width = 80 };
    private readonly TextBox _outgoingUser = new() { Width = 260 };
    private readonly ComboBox _incomingSecurity = SecurityCombo();
    private readonly ComboBox _outgoingSecurity = SecurityCombo();
    private readonly CheckBox _leaveOnServer = new() { Content = "Leave a copy of messages on the server" };
    private readonly CheckBox _deleteAfter = new() { Content = "Remove from the server after" };
    private readonly NumericUpDown _deleteDays = new()
    {
        Minimum = 1, Maximum = 3650, Value = 14, Width = 80,
    };

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

        Content = Layout();
        Bind(this, BackgroundProperty, "surface.ground.brush");
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

        var settings = AccountSettings.Load(App.Settings, _account.Address)
                       ?? AccountSettings.From(Autoconfig.ForAddress(_account.Address));

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
        var save = new Button { Content = "Save", IsDefault = true };
        save.Click += (_, _) => Save();

        var cancel = new Button { Content = "Cancel", IsCancel = true };
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

                Section("Delivery"),
                _leaveOnServer,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _deleteAfter, _deleteDays, Caption("days", 40) },
                },
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

    private Control Section(string title)
    {
        var block = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 2),
        };
        Bind(block, TextBlock.ForegroundProperty, "text.primary.brush");
        return block;
    }

    private Control Field(string label, Control control, string? second = null,
        Control? secondControl = null)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Caption(label, 100), control },
        };

        if (second is not null && secondControl is not null)
        {
            row.Children.Add(Caption(second, 34));
            row.Children.Add(secondControl);
        }

        return row;
    }

    private TextBlock Caption(string text, double width)
    {
        var block = new TextBlock
        {
            Text = text,
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(block, TextBlock.ForegroundProperty, "text.primary.brush");
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
