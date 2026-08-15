using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Adding an account: type an address, type a password, done.
/// </summary>
/// <remarks>
/// One page, not a sequence. The reference asks for an address and tries to work the rest out,
/// and every extra field before the first attempt is a place to give up. Server details are
/// present but folded away, filled in by autoconfig, and only worth opening when the guess is
/// wrong.
/// <para>
/// The provider guidance matters more than any of it. Gmail rejecting an ordinary password with
/// "authentication failed" is the single most common reason setting up a Linux mail client
/// fails, and the wizard says so before the attempt rather than after.
/// </para>
/// </remarks>
public sealed class AccountWizard : Window
{
    private readonly TextBox _address = new() { PlaceholderText = "you@example.com", Width = 320 };
    private readonly TextBox _password = new() { PasswordChar = '•', Width = 320 };
    private readonly TextBlock _guidance = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
    private readonly Expander _advanced;
    private readonly TextBox _incomingHost = new() { Width = 220 };
    private readonly TextBox _incomingPort = new() { Width = 70 };
    private readonly TextBox _outgoingHost = new() { Width = 220 };
    private readonly TextBox _outgoingPort = new() { Width = 70 };
    private readonly ComboBox _protocol = new()
    {
        ItemsSource = new[] { "POP3", "IMAP" },
        SelectedIndex = 0,
        Width = 100,
    };

    private readonly Button _add;
    private AutoconfigResult? _found;

    /// <summary>The account created, or null when the window was dismissed.</summary>
    public Account? Created { get; private set; }

    public AccountWizard()
    {
        Title = "Add Account";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ExtendClientAreaToDecorationsHint = false;

        _add = new Button { Content = "Add Account", IsEnabled = false, IsDefault = true };
        _add.Click += async (_, _) => await AddAsync();

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        _advanced = new Expander
        {
            Header = "Server settings",
            IsExpanded = false,
            Content = AdvancedPanel(),
        };

        _address.TextChanged += (_, _) => AddressChanged();
        _password.TextChanged += (_, _) => UpdateAddButton();
        _protocol.SelectionChanged += (_, _) => AddressChanged();

        Bind(_guidance, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        Bind(_status, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        DialogChrome.Apply(this, Layout(cancel));
        Bind(this, BackgroundProperty, "surface.ground.brush");
    }

    private Control Layout(Button cancel)
    {
        var heading = new TextBlock
        {
            Text = "Add an email account",
            FontSize = 20,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var subheading = new TextBlock
        {
            Text = "Mailbox will work out the server settings from your address.",
            Margin = new Thickness(0, 0, 0, 18),
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(subheading, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
            Children = { cancel, _add },
        };

        var fields = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Labelled("Email address", _address),
                Labelled("Password", _password),
                Labelled("Account type", _protocol),
                _guidance,
                _advanced,
                _status,
            },
        };

        return new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel { Children = { heading, subheading, fields, buttons } },
        };
    }

    private Control AdvancedPanel()
    {
        var grid = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        grid.Children.Add(Pair("Incoming server", _incomingHost, "Port", _incomingPort));
        grid.Children.Add(Pair("Outgoing server", _outgoingHost, "Port", _outgoingPort));
        return grid;
    }

    private Control Pair(string label, Control first, string secondLabel, Control second)
        => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                Caption(label, 110), first, Caption(secondLabel, 34), second,
            },
        };

    private Control Labelled(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children = { Caption(label, 110), control },
    };

    private TextBlock Caption(string text, double width)
    {
        var block = new TextBlock
        {
            Text = text,
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private void AddressChanged()
    {
        var address = _address.Text ?? string.Empty;
        UpdateAddButton();

        if (!Autoconfig.LooksLikeAnAddress(address))
        {
            _guidance.Text = string.Empty;
            return;
        }

        var wantsPop = _protocol.SelectedIndex == 0;
        _found = Autoconfig.ForAddress(
            address, wantsPop ? MailProtocolKind.Pop3 : MailProtocolKind.Imap);

        _incomingHost.Text = _found.Incoming.Host;
        _incomingPort.Text = _found.Incoming.Port.ToString();
        _outgoingHost.Text = _found.Outgoing.Host;
        _outgoingPort.Text = _found.Outgoing.Port.ToString();

        _guidance.Text = _found.Guidance ?? (_found.IsKnownProvider
            ? $"Recognised {Autoconfig.DomainOf(address)}. Settings filled in."
            : "These server names are a guess. Open Server settings if they are wrong.");

        // A guess is worth looking at; a known provider is not.
        _advanced.IsExpanded = !_found.IsKnownProvider;
    }

    private void UpdateAddButton()
        => _add.IsEnabled = Autoconfig.LooksLikeAnAddress(_address.Text ?? string.Empty)
                            && (_password.Text ?? string.Empty).Length > 0;

    private async Task AddAsync()
    {
        var address = (_address.Text ?? string.Empty).Trim();
        var password = _password.Text ?? string.Empty;

        _add.IsEnabled = false;
        _status.Text = "Saving…";

        try
        {
            var protocol = _protocol.SelectedIndex == 0 ? MailProtocol.Pop3 : MailProtocol.Imap;

            // Creates the account's own store file, named after the address.
            var opened = App.Accounts.Add(address, address, protocol);
            var account = opened.Account;

            var settings = (_found is null
                ? AccountSettings.From(Autoconfig.ForAddress(address))
                : AccountSettings.From(_found)) with
            {
                IncomingHost = (_incomingHost.Text ?? string.Empty).Trim(),
                IncomingPort = Port(_incomingPort.Text, 995),
                OutgoingHost = (_outgoingHost.Text ?? string.Empty).Trim(),
                OutgoingPort = Port(_outgoingPort.Text, 587),
            };
            settings.Save(App.Settings, address);

            var saved = await App.Secrets.SaveAsync(address, Credentials.Incoming, password);
            if (!saved)
            {
                _status.Text =
                    $"The account was added, but the password could only be kept for " +
                    $"{App.Secrets.Description}.";
            }

            Log.Info($"Account added: {address} ({protocol}) at {opened.Path}.");
            Created = account;
            Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Adding the account failed.", ex);
            _status.Text = $"The account could not be added: {ex.Message}";
            _add.IsEnabled = true;
        }
    }

    private static int Port(string? text, int fallback)
        => int.TryParse(text, out var port) && port is > 0 and < 65536 ? port : fallback;

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
