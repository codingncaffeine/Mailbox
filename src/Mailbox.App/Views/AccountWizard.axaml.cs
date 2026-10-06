using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
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
public sealed partial class AccountWizard : Window
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
    private readonly Button _signIn = new() { Content = "Sign in…", Classes = { "sysbutton" } };
    private readonly TextBox _clientId = new() { Classes = { "sysfield" }, Width = 320 };
    private readonly TextBlock _signedIn = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
    private Control _passwordRow = null!;
    private Control _signInRow = null!;
    private Control _clientIdRow = null!;

    private AutoconfigResult? _found;
    private CancellationTokenSource? _discovering;
    private Task? _discovery;

    /// <summary>
    /// True from the press of Add until it has finished. Anything that re-reads the form in the
    /// meantime — an MX answer moving the account type re-runs the address handler — would
    /// otherwise turn the button back on, and a second press adds the account twice.
    /// </summary>
    private bool _adding;

    /// <summary>How long typing has to rest before the domain is looked up.</summary>
    private static readonly TimeSpan DiscoveryPause = TimeSpan.FromMilliseconds(400);
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

        // The control theme fills the Server settings arrow from its own palette, which is the
        // application's ink — near-white on the Black theme, and so all but invisible on this
        // light page beside a heading in the dialog's ink. Its template sets the fill, which no
        // style outranks, so the arrow is bound here, once the template exists, to the ink every
        // other mark on the page uses.
        _advanced.Loaded += (_, _) =>
        {
            foreach (var arrow in _advanced.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                         .Where(p => p.TemplatedParent is ToggleButton))
            {
                Bind(arrow, Avalonia.Controls.Shapes.Shape.FillProperty, "systemdialog.foreground.brush");
            }
        };

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

        // Kept for the calendar-and-contacts lane, which swaps the whole page: same window,
        // same heading block, a different account being added.
        _heading = heading;
        _subheading = subheading;
        _mailFields = fields;
        _mailButtons = buttons;

        return new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Children = { heading, subheading, fields, DavSwapRow(), buttons, DavPane() },
            },
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
        var prefer = wantsPop ? MailProtocolKind.Pop3 : MailProtocolKind.Imap;

        // A lookup for the address as it was a keystroke ago is not wanted any more.
        _discovering?.Cancel();
        _discovering = null;

        // Asked once: a plugin answers over everything, and a domain nothing else knows is the
        // one whose MX records are worth a question.
        var recognised = App.Plugins.RecognizeAccount(address);
        var asking = recognised is null && !Autoconfig.IsListed(address);
        if (!Show(Autoconfig.ForAddress(address, prefer), address, asking)) return;

        // A plugin's account provider answers over the guess — the design's "register account
        // providers": what the built-in autoconfiguration is for the well-known services, a
        // plugin is for whatever it knows. The reader's boxes stay the reader's; the sign-in
        // stays the ordinary password path, which the API says in as many words.
        if (recognised is { } plugin)
        {
            _incomingHost.Text = plugin.Settings.IncomingHost;
            _incomingPort.Text = plugin.Settings.IncomingPort.ToString();
            _outgoingHost.Text = plugin.Settings.OutgoingHost;
            _outgoingPort.Text = plugin.Settings.OutgoingPort.ToString();
            _protocol.SelectedIndex = string.Equals(plugin.Settings.Protocol, "pop3", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

            _guidance.Text = plugin.Settings.Guidance is { Length: > 0 } line
                ? $"{plugin.ProviderName}: {line}"
                : $"Recognised by {plugin.ProviderName} ({plugin.PluginName}). Settings filled in.";
            _advanced.IsExpanded = false;

            Log.Info($"Wizard: {address} recognised by plugin provider “{plugin.ProviderName}” "
                     + $"({plugin.PluginName}) — {plugin.Settings.IncomingHost}:{plugin.Settings.IncomingPort} "
                     + $"/ {plugin.Settings.OutgoingHost}:{plugin.Settings.OutgoingPort}.");

            ShowTheRightCredential(address);
            return;
        }

        // A domain of the reader's own: its MX records say whether a provider in the table hosts
        // it, so the guess is in the boxes now and replaced if they do.
        if (asking)
        {
            _discovering = new CancellationTokenSource();
            _discovery = DiscoverAsync(address, prefer, _discovering.Token);
        }
    }

    /// <summary>
    /// Fills the form from an answer. False when the answer moved the account type instead, which
    /// runs <see cref="AddressChanged"/> again for the type it now shows.
    /// </summary>
    /// <param name="answer">What setup found.</param>
    /// <param name="address">The address it was found for.</param>
    /// <param name="asking">
    /// True while the domain's MX records are still being asked for. The server settings are
    /// left as they are rather than opened for the guess and closed again half a second later
    /// when the lookup recognises the provider — which read as the dialog jumping.
    /// </param>
    private bool Show(AutoconfigResult answer, string address, bool asking = false)
    {
        _found = answer;

        // A provider with only one protocol answered with it — Proton's Bridge has no POP — so
        // the account type follows. Changing the selection runs this again for the type it now
        // shows, which fills the boxes; the answer is the same, so it settles in one pass.
        var found = _found.Protocol == MailProtocolKind.Pop3 ? 0 : 1;
        if (_protocol.SelectedIndex != found)
        {
            _protocol.SelectedIndex = found;
            return false;
        }

        _incomingHost.Text = _found.Incoming.Host;
        _incomingPort.Text = _found.Incoming.Port.ToString();
        _outgoingHost.Text = _found.Outgoing.Host;
        _outgoingPort.Text = _found.Outgoing.Port.ToString();

        if (asking)
        {
            _guidance.Text = $"Finding the mail servers for {Autoconfig.DomainOf(address)}…";
            ShowTheRightCredential(address);
            return true;
        }

        _guidance.Text = _found.Guidance ?? (_found.IsKnownProvider
            ? $"Recognised {Autoconfig.DomainOf(address)}. Settings filled in."
            : "These server names are a guess. Open Server settings if they are wrong.");

        // A guess is worth looking at; a known provider is not.
        _advanced.IsExpanded = !_found.IsKnownProvider;

        ShowTheRightCredential(address);
        return true;
    }

    /// <summary>
    /// Asks the domain's MX records who hosts it, and shows that provider's settings if one in
    /// the table does.
    /// </summary>
    /// <remarks>
    /// A pause first, so typing a domain asks about the domain and not about each prefix of it on
    /// the way. The lookup itself runs off the interface's thread; only showing the answer comes
    /// back to it, and only while the address is still the one that was asked about.
    /// </remarks>
    private async Task DiscoverAsync(string address, MailProtocolKind prefer, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(DiscoveryPause, cancellation);

            var lookup = MxLookup();
            var answer = await Task.Run(
                () => Autoconfig.DiscoverAsync(address, prefer, lookup, cancellation), cancellation);

            if (cancellation.IsCancellationRequested
                || !string.Equals(_address.Text, address, StringComparison.Ordinal))
            {
                return;
            }

            // Recognised or not, the lookup is over: either the provider's settings replace the
            // guess, or the guess is now the answer and is shown as one.
            Log.Info(answer.HostedBy is { } host
                ? $"Wizard: {Autoconfig.DomainOf(address)} is hosted by {host} — "
                  + $"{answer.Incoming.Host}:{answer.Incoming.Port} / {answer.Outgoing.Host}:{answer.Outgoing.Port}."
                : $"Wizard: nothing recognisable handles {Autoconfig.DomainOf(address)}'s mail; the guess stands.");
            Show(answer, address);
        }
        catch (OperationCanceledException)
        {
            // The address changed while this was asking, and the next keystroke asks again.
        }
        catch (Exception ex)
        {
            // Not finding out leaves the guess as the answer, shown as one rather than as a
            // lookup still going.
            Log.Warn($"Wizard: looking up who hosts {Autoconfig.DomainOf(address)} failed.", ex);
            if (string.Equals(_address.Text, address, StringComparison.Ordinal))
            {
                Show(Autoconfig.ForAddress(address, prefer), address);
            }
        }
    }

    /// <summary>
    /// Where MX records come from: the machine's own resolver, or — in a capture run, which must
    /// not depend on the network — the answers <c>MAILBOX_MX</c> poses.
    /// </summary>
    private static Mailbox.Security.Dns.IMxLookup MxLookup()
        => Theming.WindowCapture.IsRequested ? PosedMx.FromEnvironment() : App.Resolver;

    /// <summary>
    /// A password box or a sign-in button, according to what the provider still accepts.
    /// </summary>
    /// <remarks>
    /// Deciding this from the address is the point of the guidance in the first place: a
    /// Microsoft account rejecting a password with "authentication failed" sends the user off to
    /// check a password that was never wrong, and this is the wizard saying so before the attempt
    /// rather than after.
    /// </remarks>
    private void ShowTheRightCredential(string address)
    {
        var provider = _found is { } found ? OAuthProviders.For(found) : null;

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
        _add.IsEnabled = !_adding && addressed && (SignsIn
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
            using var flow = new OAuthFlow(PosedAuthorizationServer.HandlerOrNull(), OpenBrowser);
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
            != Mailbox.Core.Platform.DesktopOpenResult.Posed)
        {
            return;
        }

        Log.Info($"Harness: sign-in — would open {url.AbsoluteUri}");

        // MAILBOX_OAUTH_FAKE answers instead of giving up: the redirect the browser would have
        // been sent back to is knocked on from here, so the wait ends the way a real sign-in ends
        // it and the rest of the flow — the state check, the exchange, the token parse, the
        // refresh token going to the keyring — runs for real against an authorization server the
        // run controls. Without it there is nobody to answer, so the wait is stopped instead.
        if (PosedAuthorizationServer.IsRequested)
        {
            _ = PosedAuthorizationServer.AnswerAsync(url);
            return;
        }

        _signingIn?.Cancel();
    }

    private async Task AddAsync()
    {
        _adding = true;
        try
        {
            await AddingAsync();
        }
        finally
        {
            _adding = false;
        }
    }

    private async Task AddingAsync()
    {
        var address = (_address.Text ?? string.Empty).Trim();
        var password = _password.Text ?? string.Empty;

        _add.IsEnabled = false;

        // A lookup still in flight decides which servers are being saved, so it finishes first;
        // the resolver's own timeout bounds the wait.
        if (_discovery is { IsCompleted: false })
        {
            _status.Text = $"Finding the mail servers for {Autoconfig.DomainOf(address)}…";
            // A loop, because an answer that moves the account type asks once more for the
            // type it moved to.
            while (_discovery is { IsCompleted: false } pending) await pending;

            // What it found may sign in through a browser, which the password typed meanwhile
            // cannot stand in for: the reader is asked for the sign-in rather than an account
            // saved that could never collect mail.
            if (SignsIn && _tokens is null)
            {
                _status.Text = $"{_found?.HostedBy ?? _provider!.Name} handles this address's mail. "
                               + $"Sign in with {_provider!.Name} to add it.";
                _adding = false;
                UpdateAddButton();
                return;
            }
        }

        _status.Text = "Saving…";

        try
        {
            var protocol = _protocol.SelectedIndex == 0 ? MailProtocol.Pop3 : MailProtocol.Imap;

            // Creates the account's own store file, named after the address.
            var opened = App.Accounts.Add(address, address, protocol);
            var account = opened.Account;

            var incomingPort = Port(_incomingPort.Text, 995);
            var outgoingPort = Port(_outgoingPort.Text, 587);

            var settings = (_found is null
                ? AccountSettings.From(Autoconfig.ForAddress(address))
                : AccountSettings.From(_found)) with
            {
                IncomingHost = (_incomingHost.Text ?? string.Empty).Trim(),
                IncomingPort = incomingPort,
                OutgoingHost = (_outgoingHost.Text ?? string.Empty).Trim(),
                OutgoingPort = outgoingPort,

                // From the port that is actually there rather than the one the guess put in the
                // box. This panel offers a port and no encryption, so the port is the only thing
                // in it that can answer the question — and correcting 995 to 110 while implicit
                // TLS stayed behind produced an account that could never connect and said only
                // that the server "could not be reached". Every guess in Autoconfig is built from
                // this same method, so a port nobody touched still gets the answer it had.
                IncomingSecurity = Autoconfig.Security(incomingPort),
                OutgoingSecurity = Autoconfig.Security(outgoingPort),

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
            // With the account's own encryption, not the record's default of Automatic: the point
            // of the check is whether the connection this account is about to make works, and a
            // probe that negotiates differently answers about a connection nobody will make.
            var outgoing = new ServerSettings(
                settings.OutgoingHost, settings.OutgoingPort, settings.OutgoingSecurity)
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

            if (!inbound.Reached && inbound.NothingListening && _found?.LocalService is { } service
                && string.Equals(settings.IncomingHost, _found.Incoming.Host, StringComparison.OrdinalIgnoreCase))
            {
                // The settings are right and the program behind them is not running. Sending the
                // reader to correct them would have them change the one thing that was not wrong.
                _status.Text = $"{service} is not running: nothing on this machine answered on port "
                               + $"{settings.IncomingPort}. The account was added and will collect "
                               + $"mail once {service} is started and signed in.";
            }
            else if (!inbound.Reached)
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
        // Held across the whole list: the capture's timer photographs at first idle, and an
        // awaited action — sign-in's settle, the DAV lane's discovery — hands it several.
        using var hold = WindowCapture.Hold();

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

                // Waits out the MX lookup the address started, so what follows acts on the
                // provider it found rather than on the guess shown while it was asking.
                case "settle":
                    while (_discovery is { IsCompleted: false } discovery) await discovery;
                    Log.Info($"Harness: settled — hosted by {_found?.HostedBy ?? "nobody recognised"}; "
                             + $"guidance “{_guidance.Text}”.");
                    break;

                case "add":
                    if (!_add.IsEnabled)
                    {
                        Log.Info("Harness: add account — the button is off, so nothing was saved.");
                        break;
                    }

                    _add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                    // A lookup still in flight and the probes after the save both outlive this
                    // pass, and what they conclude is the status line the reader is left with —
                    // so wait for the button to come back, or the window to close on a clean add.
                    for (var waited = 0; waited < 12000 && !_add.IsEnabled && IsVisible; waited += 100)
                    {
                        await Task.Delay(100);
                    }

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

                    // The press starts an exchange that outlives this pass. The outcome is what
                    // the next action needs: an `add` sent while the flow is still running
                    // presses a button that is honestly off, and reads as a sign-in that saved
                    // nothing. Done means tokens arrived, Add came on, or the status stopped
                    // being the waiting line and became an answer.
                    for (var waited = 0; waited < 8000; waited += 100)
                    {
                        if (_tokens is not null || _add.IsEnabled) break;
                        if (_status.Text is { Length: > 0 } text
                            && !text.Contains("Waiting", StringComparison.Ordinal))
                        {
                            break;
                        }

                        await Task.Delay(100);
                    }

                    Log.Info($"Harness: sign-in settled — add {(_add.IsEnabled ? "on" : "off")}, "
                             + $"status “{_status.Text}”.");
                    break;

                // The calendar-and-contacts lane's own verbs, answered in the partial that
                // builds the lane: swapping to it, filling its three fields, pressing Find and
                // Add through the real handlers, and unticking a found collection by name.
                case "dav" or "server" or "user" or "davpassword" or "find" or "untick" or "davadd":
                    await HarnessDavAsync(action, argument);
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
            + $"Add is {(_add.IsEnabled ? "on" : "off")}; "
            + $"type {_protocol.SelectedItem}; incoming {_incomingHost.Text}:{_incomingPort.Text}; "
            + $"outgoing {_outgoingHost.Text}:{_outgoingPort.Text}; "
            + $"status “{_status.Text}”.");
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
