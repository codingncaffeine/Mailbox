using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;
using Mailbox.Protocols.OAuth;
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
    private readonly TextBox _address = new() { Classes = { "sysfield" }, PlaceholderText = "you@example.com", Width = 320 };
    private readonly TextBox _password = new() { Classes = { "sysfield" }, PasswordChar = '•', Width = 320 };
    private readonly TextBlock _guidance = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
    private readonly Expander _advanced;
    private readonly TextBox _incomingHost = new() { Classes = { "sysfield" }, Width = 220 };
    private readonly TextBox _incomingPort = new() { Classes = { "sysfield" }, Width = 70 };
    private readonly TextBox _outgoingHost = new() { Classes = { "sysfield" }, Width = 220 };
    private readonly TextBox _outgoingPort = new() { Classes = { "sysfield" }, Width = 70 };
    private readonly ComboBox _protocol = new()
    {
        ItemsSource = new[] { "POP3", "IMAP" },
        SelectedIndex = 0,
        Width = 100,
    };

    private readonly Button _add;
    private readonly Button _signIn = new() { Content = "Sign in…" };
    private readonly TextBox _clientId = new() { Classes = { "sysfield" }, Width = 320 };
    private readonly TextBlock _signedIn = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
    private Control _passwordRow = null!;
    private Control _signInRow = null!;
    private Control _clientIdRow = null!;

    private AutoconfigResult? _found;
    private OAuthProvider? _provider;
    private OAuthTokens? _tokens;
    private CancellationTokenSource? _signingIn;

    /// <summary>The account created, or null when the window was dismissed.</summary>
    public Account? Created { get; private set; }

    /// <summary>True when this account signs in through a browser rather than holding a password.</summary>
    private bool SignsIn => _provider is not null;

    public AccountWizard()
    {
        Title = "Add Account";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _add = new Button { Content = "Add Account", IsEnabled = false, IsDefault = true, Classes = { "sysbutton" } };
        _add.Click += async (_, _) =>
        {
            // Once the account exists the button's job changes: pressing it again would add a
            // second copy of the same account.
            if (Created is not null) { Close(); return; }
            await AddAsync();
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "sysbutton" } };
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
        _clientId.TextChanged += (_, _) => UpdateAddButton();
        _signIn.Click += async (_, _) => await SignInAsync();

        Bind(_guidance, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");
        Bind(_status, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");
        Bind(_signedIn, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");

        // A member of the Account Settings family — drawn the way the desktop draws its own
        // dialogs and light in every theme, never DialogChrome's theme-following palette. And no
        // Background bind after it: WindowFrame sets the window transparent so its rounded,
        // clipping border is the only thing that paints.
        SystemDialogChrome.Apply(this, Layout(cancel));
    }

    private Control Layout(Button cancel)
    {
        var heading = new TextBlock
        {
            Text = "Add an email account",
            FontSize = 20,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Bind(heading, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

        var subheading = new TextBlock
        {
            Text = "Mailbox will work out the server settings from your address.",
            Margin = new Thickness(0, 0, 0, 18),
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(subheading, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
            Children = { cancel, _add },
        };

        _passwordRow = Labelled("Password", _password);
        _signInRow = Labelled(string.Empty, _signIn);
        _clientIdRow = Labelled("Client ID", _clientId);

        // Both hidden until an address says which of the two this account is. Showing a password
        // box beside a sign-in button asks the user to decide something the provider already has.
        _signInRow.IsVisible = false;
        _clientIdRow.IsVisible = false;
        _signedIn.IsVisible = false;

        var fields = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Labelled("Email address", _address),
                _passwordRow,
                _clientIdRow,
                _signInRow,
                _signedIn,
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
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
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

        // A plugin's account provider answers over the guess — §13's "register account
        // providers": what the built-in autoconfiguration is for the well-known services, a
        // plugin is for whatever it knows. The reader's boxes stay the reader's; the sign-in
        // stays the ordinary password path, which the API says in as many words.
        if (App.Plugins.RecognizeAccount(address) is { } recognised)
        {
            _incomingHost.Text = recognised.Settings.IncomingHost;
            _incomingPort.Text = recognised.Settings.IncomingPort.ToString();
            _outgoingHost.Text = recognised.Settings.OutgoingHost;
            _outgoingPort.Text = recognised.Settings.OutgoingPort.ToString();
            _protocol.SelectedIndex = string.Equals(recognised.Settings.Protocol, "pop3", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

            _guidance.Text = recognised.Settings.Guidance is { Length: > 0 } line
                ? $"{recognised.ProviderName}: {line}"
                : $"Recognised by {recognised.ProviderName} ({recognised.PluginName}). Settings filled in.";
            _advanced.IsExpanded = false;

            Log.Info($"Wizard: {address} recognised by plugin provider “{recognised.ProviderName}” "
                     + $"({recognised.PluginName}) — {recognised.Settings.IncomingHost}:{recognised.Settings.IncomingPort} "
                     + $"/ {recognised.Settings.OutgoingHost}:{recognised.Settings.OutgoingPort}.");
        }

        ShowTheRightCredential(address);
    }

    /// <summary>
    /// A password box or a sign-in button, according to what the provider still accepts.
    /// </summary>
    /// <remarks>
    /// Deciding this from the address is the point of the guidance in the first place: a
    /// Microsoft account rejecting a password with "authentication failed" sends the user off to
    /// check a password that was never wrong, and this is the wizard saying so before the attempt
    /// rather than after (§5).
    /// </remarks>
    private void ShowTheRightCredential(string address)
    {
        var provider = _found?.Auth == AuthKind.OAuth2 ? OAuthProviders.ForMail(address) : null;

        // Changing which account is being added throws away a sign-in for the previous one.
        if (provider?.Id != _provider?.Id || _tokens is not null && !SignedInAs(address))
        {
            _tokens = null;
            _signedIn.IsVisible = false;
        }

        _provider = provider;

        _passwordRow.IsVisible = !SignsIn;
        _signInRow.IsVisible = SignsIn;
        _clientIdRow.IsVisible = SignsIn && provider is { WorksOutOfTheBox: false };

        if (provider is not null)
        {
            _signIn.Content = $"Sign in with {provider.Name}…";

            // Where there is no registration to sign in with, the guidance is the instructions
            // for making one rather than the sentence about a browser opening.
            if (!provider.WorksOutOfTheBox && provider.OwnClientGuidance is { } instructions)
            {
                _guidance.Text = instructions;
            }
        }

        UpdateAddButton();
    }

    private bool SignedInAs(string address)
        => string.Equals(_signedIn.Tag as string, address, StringComparison.OrdinalIgnoreCase);

    private void UpdateAddButton()
    {
        var addressed = Autoconfig.LooksLikeAnAddress(_address.Text ?? string.Empty);

        // Nothing to save until the sign-in has happened: an account added first and signed in
        // afterwards would sit in the folder pane failing to collect, which is the state the
        // wizard exists to avoid.
        _add.IsEnabled = addressed && (SignsIn
            ? _tokens is not null
            : (_password.Text ?? string.Empty).Length > 0);

        _signIn.IsEnabled = addressed && _signingIn is null
                            && (_provider is not { WorksOutOfTheBox: false }
                                || (_clientId.Text ?? string.Empty).Trim().Length > 0);
    }

    /// <summary>Which registration this sign-in uses: the provider's own, or the pasted one.</summary>
    private string ClientIdInUse()
    {
        var typed = (_clientId.Text ?? string.Empty).Trim();
        return typed.Length > 0 ? typed : _provider?.ClientId ?? string.Empty;
    }

    private async Task SignInAsync()
    {
        if (_provider is not { } provider) return;

        var address = (_address.Text ?? string.Empty).Trim();
        _signingIn = new CancellationTokenSource();
        UpdateAddButton();
        _status.Text = "Waiting for the browser…";

        try
        {
            using var flow = new OAuthFlow(openBrowser: OpenBrowser);
            _tokens = await flow.SignInAsync(provider, ClientIdInUse(), address, _signingIn.Token);

            _signedIn.Tag = address;
            _signedIn.Text = $"Signed in to {provider.Name}. Mailbox will keep this sign-in in "
                             + $"{App.Secrets.Description}.";
            _signedIn.IsVisible = true;
            _status.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "The sign-in was stopped.";
        }
        catch (Exception ex)
        {
            Log.Warn("The sign-in failed.", ex);
            _status.Text = ex.Message;
        }
        finally
        {
            _signingIn?.Dispose();
            _signingIn = null;
            UpdateAddButton();
        }
    }

    /// <summary>
    /// Opens the browser — or, in a capture run, says where it would have sent one.
    /// </summary>
    /// <remarks>
    /// A harness run has no browser and nobody to answer one, so a real sign-in would wait on the
    /// loopback socket until it timed out. What can be checked without either is the request
    /// itself: the URL is built by the flow, printed here, and then the wait is stopped. That is
    /// the claim a capture run can make about this button — that pressing it produces a
    /// well-formed authorization request — rather than a claim about a provider it cannot reach.
    /// </remarks>
    private void OpenBrowser(Uri url)
    {
        // The helper carries the posed-run guard and logs the address it would have opened; what
        // is left here is this surface's own half of it, which is that a posed sign-in must also
        // stop waiting on a loopback socket nobody is going to answer.
        if (Mailbox.Core.Platform.DesktopOpen.Open(url.AbsoluteUri)
            == Mailbox.Core.Platform.DesktopOpenResult.Posed)
        {
            Log.Info($"Harness: sign-in — would open {url.AbsoluteUri}");
            _signingIn?.Cancel();
        }
    }

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
                Auth = SignsIn ? AuthKind.OAuth2 : _found?.Auth ?? AuthKind.Password,
                OAuthProviderId = SignsIn ? _provider!.Id : string.Empty,

                // Only when it is the user's own. Writing the shipped one down would freeze this
                // account on whichever registration the build it was added by happened to carry.
                OAuthClientId = SignsIn ? (_clientId.Text ?? string.Empty).Trim() : string.Empty,
            };
            settings.Save(App.Settings, address);

            // Asked before the account is saved, and only reported: an account whose sending is
            // switched off is still worth having for receiving, so this explains rather than
            // refuses. Without it the first send fails as "authentication failed" and sends the
            // user to check a password that was never wrong.
            //
            // It is also the first thing here that meets a certificate, so it is where a server
            // Mailbox cannot verify gets shown and asked about — the probe refuses, records what
            // it refused, and this asks and tries once more.
            var outgoing = new ServerSettings(settings.OutgoingHost, settings.OutgoingPort)
            {
                Trust = App.Trust,
            };

            // The incoming server first, because it is the one every send/receive afterwards
            // depends on: an account added without reaching it works until the moment somebody
            // presses the button. Both are asked about here so a certificate question is answered
            // once, at setup, rather than turning up later as a failure with no way to answer it.
            var incoming = new ServerSettings(
                settings.IncomingHost, settings.IncomingPort, settings.IncomingSecurity)
            {
                Trust = App.Trust,
            };

            var protocol2 = protocol == MailProtocol.Imap ? MailProtocolKind.Imap : MailProtocolKind.Pop3;
            var inbound = await new ServerProbe().CheckReceivingAsync(incoming, protocol2);

            if (!inbound.Reached && await AskAboutCertificateAsync(settings.IncomingHost, settings.IncomingPort))
            {
                inbound = await new ServerProbe().CheckReceivingAsync(incoming, protocol2);
            }

            var probe = await new ServerProbe().CheckSendingAsync(outgoing);

            if (!probe.Reached && await AskAboutCertificateAsync(settings.OutgoingHost, settings.OutgoingPort))
            {
                probe = await new ServerProbe().CheckSendingAsync(outgoing);
            }

            // An account whose incoming server cannot be reached is one that will fail every time
            // it is asked to collect mail, so it is reported here rather than saved quietly and
            // discovered later. It is still added — the settings may simply need correcting, and
            // throwing the typing away would be worse — but nobody is left thinking it worked.
            if (!inbound.Reached)
            {
                Log.Warn($"The incoming server for {address} could not be reached: {inbound.Explanation}");
            }

            if (SignsIn && _tokens is { } tokens)
            {
                // The refresh token goes to the keyring and the access token stays in the source,
                // which is the same one every send/receive will ask — so the account is usable
                // without a second round trip to the provider.
                await App.OAuth.For(address, _provider!, ClientIdInUse()).AdoptAsync(tokens);
            }
            else
            {
                var saved = await App.Secrets.SaveAsync(address, Credentials.Incoming, password);
                if (!saved)
                {
                    _status.Text =
                        $"The account was added, but the password could only be kept for " +
                        $"{App.Secrets.Description}.";
                }
            }

            if (!inbound.Reached)
            {
                _status.Text = inbound.Explanation
                               + " The account was added; correct the incoming server in Account "
                               + "Settings, or it will not be able to collect mail.";
            }
            else if (!probe.IsClear)
            {
                Log.Info($"Sending check for {address}: {probe.Explanation}");
                _status.Text = probe.Explanation;
            }

            Log.Info($"Account added: {address} ({protocol}) at {opened.Path}.");
            Created = account;

            // Kept open when there is something to say. Closing over the explanation would put
            // the account in exactly the state the check exists to warn about, silently.
            if (probe.IsClear && inbound.Reached)
            {
                Close();
                return;
            }

            _add.Content = "Close";
            _add.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Adding the account failed.", ex);
            _status.Text = $"The account could not be added: {ex.Message}";
            _add.IsEnabled = true;
        }
    }

    /// <summary>
    /// Poses and presses the wizard for a capture run.
    /// </summary>
    /// <remarks>
    /// <c>address:</c> types one, which is what decides whether this is a password account or a
    /// sign-in; <c>client:</c> pastes a registration; <c>signin</c> presses the button. What the
    /// press produces is in the log rather than the picture — the authorization request itself,
    /// or the refusal where there is nothing to sign in with.
    /// </remarks>
    internal async Task HarnessAsync(string actions)
    {
        foreach (var raw in actions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A pass of the dispatcher between actions, because typing is noticed on the next one:
            // setting Text raises TextChanged later, so pressing in the same call presses a button
            // whose state still belongs to the empty box.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => { }, Avalonia.Threading.DispatcherPriority.Background);

            var (action, argument) = raw.Split(':', 2) is [var a, var b] ? (a, b) : (raw, string.Empty);
            switch (action.ToLowerInvariant())
            {
                case "address":
                    _address.Text = argument;
                    break;

                case "password":
                    _password.Text = argument;
                    break;

                case "client":
                    _clientId.Text = argument;
                    break;

                case "add":
                    if (!_add.IsEnabled)
                    {
                        Log.Info("Harness: add account — the button is off, so nothing was saved.");
                        break;
                    }

                    _add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                    // Read back out of the settings rather than off the form: what matters is
                    // what a later run will load, and the three new keys are the ones that decide
                    // whether an account collects mail at all.
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => { }, Avalonia.Threading.DispatcherPriority.Background);

                    var address = (_address.Text ?? string.Empty).Trim();
                    if (AccountSettings.Load(App.Settings, address) is { } saved)
                    {
                        Log.Info(
                            $"Harness: saved {address} — auth {saved.Auth}; "
                            + $"provider {(saved.OAuthProviderId.Length > 0 ? saved.OAuthProviderId : "none")}; "
                            + $"client {(saved.OAuthClientId.Length > 0 ? "pasted" : "none")}; "
                            + $"incoming {saved.IncomingHost}:{saved.IncomingPort}; "
                            + $"token source {(saved.Authentication.Source(address, App.OAuth) is null ? "none" : "held")}.");
                    }
                    else
                    {
                        Log.Warn($"Harness: nothing was saved for {address}.");
                    }

                    break;

                case "signin":
                    if (!_signIn.IsEnabled)
                    {
                        Log.Info(
                            $"Harness: sign-in — the button is off. Provider: "
                            + $"{_provider?.Name ?? "none"}; client ID: "
                            + $"{(ClientIdInUse().Length > 0 ? "set" : "none")}.");
                        break;
                    }

                    _signIn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    break;

                default:
                    Log.Warn($"Harness: the account wizard has no action named {action}.");
                    break;
            }
        }

        Log.Info(
            $"Harness: add account — {_address.Text}; "
            + $"credential: {(SignsIn ? $"sign in with {_provider!.Name}" : "password")}; "
            + $"password box {(_passwordRow.IsVisible ? "shown" : "hidden")}, "
            + $"client ID box {(_clientIdRow.IsVisible ? "shown" : "hidden")}; "
            + $"Add is {(_add.IsEnabled ? "on" : "off")}.");
    }

    /// <summary>
    /// Offers a certificate the probe was refused, and says whether the reader agreed to it.
    /// </summary>
    /// <remarks>
    /// False when there was no certificate question — the server simply was not there — so the
    /// caller does not retry a connection that failed for some other reason.
    /// </remarks>
    private async Task<bool> AskAboutCertificateAsync(string host, int port)
    {
        if (App.Trust.RefusalFor(host, port) is not { } refusal) return false;
        if (!await CertificateDialog.AskAsync(this, refusal)) return false;

        App.Trust.Pin(refusal);
        return true;
    }

    private static int Port(string? text, int fallback)
        => int.TryParse(text, out var port) && port is > 0 and < 65536 ? port : fallback;

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
