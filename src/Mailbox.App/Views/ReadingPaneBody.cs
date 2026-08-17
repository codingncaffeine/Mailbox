using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Mailbox.Core.Diagnostics;
using Mailbox.Rendering;
using RenderOptions = Mailbox.Rendering.RenderOptions;
using Mailbox.Security;
using Mailbox.Security.Smime;
using Mailbox.Store;
using Mailbox.Theming;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Tokens;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The reading pane's body: the bars that say what was held back, and the message itself.
/// </summary>
/// <remarks>
/// The engine is handed a document that has already had every remote reference taken out of it,
/// so there is nothing here that decides what to allow at request time — by the time this runs,
/// the decision has been made and baked into the markup. See §11 for why that is the design
/// rather than a request veto.
/// <para>
/// The WebView is created defensively. The WPE backend is new, and a reading pane that throws on
/// a machine without it would take the application with it; the fallback renders the message as
/// text, which is what the pane did before this phase and is better than a crash.
/// </para>
/// </remarks>
public sealed class ReadingPaneBody : UserControl
{
    private readonly ThemeService _themes;
    private readonly Func<MailRepository?> _mail;

    private readonly StackPanel _bars = new();
    private readonly ContentControl _surface = new();
    private readonly TextBlock _fallback = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(20, 16),
    };

    /// <summary>
    /// The one scroller the fallback text lives in. It used to be made anew on every showing,
    /// and a control put into a second ScrollViewer while the first still lists it as its child
    /// throws "already has a visual parent" — the second empty folder selected in a row did it.
    /// </summary>
    private readonly ScrollViewer _fallbackHost;

    private NativeWebView? _web;
    private MimeMessage? _message;
    private DkimResult? _verified;
    private string _fallbackText = string.Empty;
    private RenderedMessage? _rendered;
    private RemoteImagePolicy _policy = RemoteImagePolicy.Block;
    private IReadOnlyDictionary<string, string> _inlined =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ReadingPaneBody(ThemeService themes, Func<MailRepository?> mail)
    {
        _themes = themes;
        _mail = mail;
        _fallbackHost = new ScrollViewer { Content = _fallback };

        var root = new DockPanel();
        DockPanel.SetDock(_bars, Dock.Top);
        root.Children.Add(_bars);
        root.Children.Add(_surface);

        Content = root;

        _surface.Content = BuildSurface();
        _themes.Changed += (_, _) => Refresh();
    }

    /// <summary>
    /// The type size the message renders at, following the status bar's zoom.
    /// </summary>
    /// <remarks>
    /// Not the control's own <c>FontSize</c>: that is the size of the bars around the message,
    /// and zooming the message must not resize the warning above it.
    /// </remarks>
    public double MessageFontSize
    {
        get;
        set
        {
            if (Math.Abs(field - value) < 0.01) return;
            field = value;
            Refresh();
        }
    } = 14.5;

    /// <summary>
    /// Shows a message.
    /// </summary>
    /// <param name="message">
    /// The message as it arrived, or null while the sample is on screen — there is no MIME
    /// behind a sample row, and inventing some would be a worse lie than rendering its text.
    /// </param>
    /// <param name="fallbackText">What to show when there is no message to parse.</param>
    /// <param name="verified">
    /// What checking this message's signature came to when it arrived, read from the store, or
    /// null for a message that was never checked. Passed in rather than looked up: verifying
    /// resolves a name the sender chose, and §19 does not allow that on the path that draws a
    /// message. Nothing here ever asks the network anything.
    /// </param>
    public void Show(MimeMessage? message, string fallbackText, DkimResult? verified = null,
        bool suspectedJunk = false)
    {
        _message = message;
        _fallbackText = fallbackText;
        _verified = verified;
        _suspectedJunk = suspectedJunk;

        // A decision belongs to the message it was made about. Carrying "show images" from one
        // message to the next would allow a sender the reader never agreed to.
        _policy = RemoteImagePolicy.Block;
        _inlined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Refresh();
    }

    /// <summary>
    /// Whether the message on show is in the Junk folder, so the Junk Options dialog's "disable
    /// links" applies to it: the reference draws a suspected message's links inert, on the
    /// grounds that the one thing junk wants is a click.
    /// </summary>
    private bool _suspectedJunk;

    /// <summary>The sender's address, for the safe-sender decision.</summary>
    private string SenderAddress => _message?.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;

    /// <summary>
    /// Which of our addresses this message came to, which is the one an invitation answers as.
    /// </summary>
    /// <remarks>
    /// Read off the message rather than taken from the default account: a reply from the wrong
    /// address is a reply the organizer's client cannot match to anybody it invited.
    /// </remarks>
    public string RecipientAddress
    {
        get
        {
            var ours = App.Accounts.All.Select(a => a.Account.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mine = (_message?.To.Mailboxes ?? []).Concat(_message?.Cc.Mailboxes ?? [])
                .FirstOrDefault(m => ours.Contains(m.Address));
            return mine?.Address ?? App.Accounts.All.FirstOrDefault()?.Account.Address ?? string.Empty;
        }
    }

    /// <summary>The invitation bar on show, for the harness — a capture cannot press a button.</summary>
    private InvitationBar? _invitation;

    internal InvitationBar? Invitation => _invitation;

    /// <summary>Raised when Accept, Tentative or Decline was pressed; the shell sends the reply.</summary>
    public event EventHandler<InvitationBar.Answer>? InvitationAnswered;

    private void Refresh()
    {
        _bars.Children.Clear();

        if (_message is null)
        {
            _rendered = null;
            ShowText(_fallbackText);
            return;
        }

        var trust = SenderTrust.Evaluate(_message, FamiliarDomains(), _verified);
        if (trust.Warnings.Count > 0) _bars.Children.Add(TrustBar(trust));

        // The signature, when the reader has asked for S/MIME at all. Crypto ships off (§14), and
        // a bar that says "signed" over a check nobody made would be worse than no bar.
        if (SignatureOf(_message) is { State: not SignatureState.None } signature)
        {
            _bars.Children.Add(SignatureBar(signature));
        }

        // An invitation is the one bar that goes above the trust strip in the reference: it is
        // what the message is, not a caveat about it.
        if (InvitationBar.Read(_message) is { } invitation)
        {
            var bar = new InvitationBar(
                invitation,
                RecipientAddress,
                App.Pim,
                _message.From.Mailboxes.FirstOrDefault()?.Name);
            bar.Answered += (_, answer) => InvitationAnswered?.Invoke(this, answer);
            _bars.Children.Insert(0, bar);
            _invitation = bar;
        }
        else
        {
            _invitation = null;
        }

        var disableLinks = _suspectedJunk && App.MailOptions.DisableLinksInJunk;

        // An encrypted message is opened before anything is rendered, and what comes out is
        // rendered *instead of* the message it arrived in — never inside it. See §19: the channel
        // CVE-2026-0818 used was the cascade, so a decrypted part spliced into the outer document
        // is readable by the outer document's own stylesheet.
        var opened = Decrypted(_message);
        if (opened.State != DecryptionState.None) _bars.Children.Add(EncryptionBar(opened));

        var options = new RenderOptions
        {
            Style = Style(),
            Inlined = _inlined,
            PrintHeader = Memo(_message),
            DisableLinks = disableLinks,
            Isolated = opened.Opened,
        };

        _rendered = opened.Opened
            ? MessageRenderer.Render(AsMessage(_message, opened.Content!), options)
            : MessageRenderer.Render(_message, options);

        if (disableLinks) _bars.Children.Add(JunkBar());
        if (_rendered.HasRemoteContent) _bars.Children.Add(RemoteImageBar(_rendered));

        Load(_rendered.Html);
    }

    /// <summary>
    /// The domains the lookalike check compares against — or none, when the Junk Options
    /// dialog's "warn me about suspicious domain names" is off, which is what turns that one
    /// warning off without touching the rest of the trust bar.
    /// </summary>
    private IEnumerable<string> FamiliarDomains()
        => App.MailOptions.WarnAboutSuspiciousDomains ? _mail()?.FamiliarDomains() ?? [] : [];

    /// <summary>The bar above a message in Junk whose links have been drawn inert.</summary>
    private Control JunkBar()
    {
        var bar = Bar("reading.infobar.background.brush");
        var row = Row();

        var glyph = Glyph("junk");
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        var text = new TextBlock
        {
            Text = "This message is in the Junk Email folder, so its links have been turned off. "
                   + "Not Junk moves it back to the Inbox.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        bar.Child = row;
        return bar;
    }

    /// <summary>
    /// Whether this sender's images load without asking, which is the per-sender allow list.
    /// </summary>
    private bool IsSafeSender()
        => SenderAddress.Length > 0 && (_mail()?.IsSafeSender(SenderAddress) ?? false);

    // ---- The surface -------------------------------------------------------------------------

    private Control BuildSurface()
    {
        try
        {
            _web = new NativeWebView();
            _web.EnvironmentRequested += OnEnvironmentRequested;
            _web.NavigationStarted += OnNavigationStarted;
            _web.NewWindowRequested += OnNewWindowRequested;
            Bind(_web, NativeWebView.BackgroundProperty, "reading.background.brush");

            // Which engine actually attached, and whether the document loaded. Both are worth
            // knowing from a log: the backend is chosen at runtime, and a blank pane looks the
            // same whether the engine is missing or the markup was rejected.
            _web.AdapterCreated += (_, _) => Log.Info($"Reading pane engine: {Describe()}");
            _web.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) Log.Debug("The reading pane loaded a message.");
                else Log.Warn("The reading pane could not load the message.");
            };

            return _web;
        }
        catch (Exception ex)
        {
            // No engine on this machine. Say so once, in the log, and render text.
            Log.Warn("No web engine is available; the reading pane will render text only.", ex);
            _web = null;
            return _fallbackHost;
        }
    }

    /// <summary>
    /// What the engine reports itself as, for the log.
    /// </summary>
    /// <remarks>
    /// The embedding scenario is the part worth recording. A pane embedded as a native child
    /// window has the airspace problem — Avalonia's own menus and flyouts cannot draw over it,
    /// which is disqualifying in a mail client where popups overlap the reading pane constantly.
    /// An offscreen renderer composites into the visual tree and does not. §2 chose this backend
    /// on that basis, and this is where the claim can be checked on a real machine.
    /// </remarks>
    private string Describe()
    {
        if (_web?.AdapterInfo is not { } info) return "unknown";

        var scenarios = WebViewAdapterInfo.GetAdapterInfo(info.Type)?.SupportedScenarios;
        return $"{info.Type} ({info.Engine} {info.Version}), embedding: {scenarios}";
    }

    /// <summary>
    /// Configures the engine before it starts.
    /// </summary>
    /// <remarks>
    /// WPE first, WebKitGTK behind an environment variable — the backend choice is a runtime
    /// flag rather than a redesign, which is what makes preferring the newer one affordable.
    /// Both are told to keep nothing: there is no session to persist and no cache worth keeping
    /// for documents that are already in the store.
    /// </remarks>
    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mailbox-webview");

        switch (e)
        {
            case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                wpe.PreferWebKitGtkInstead = string.Equals(
                    Environment.GetEnvironmentVariable("MAILBOX_WEBVIEW"),
                    "webkitgtk",
                    StringComparison.OrdinalIgnoreCase);

                wpe.CacheDirectory = Path.Combine(scratch, "cache");
                wpe.DataDirectory = Path.Combine(scratch, "data");
                break;

            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = true;
                gtk.DisableCache = true;
                break;
        }
    }

    /// <summary>
    /// The navigation policy, which is the one interception the Linux backend does bind.
    /// </summary>
    /// <remarks>
    /// A document loaded from a string reports its own load as a navigation, and that one has to
    /// be allowed or nothing ever renders. Everything else is a link the reader clicked, and a
    /// link opens in their browser rather than in the pane: a reading pane that navigates is a
    /// browser with no address bar, which is the thing a phishing message wants.
    /// </remarks>
    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is not { } uri) return;
        if (uri.Scheme is "about" or "data") return;

        e.Cancel = true;
        OpenExternally(uri);
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { } uri) OpenExternally(uri);
    }

    /// <summary>Hands a link to the desktop, which is the only thing that should follow it.</summary>
    private static void OpenExternally(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https" or "mailto" or "tel" or "ftp" or "ftps"))
        {
            Log.Warn($"Refused to open a link with the '{uri.Scheme}' scheme.");
            return;
        }

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { uri.ToString() },
                    UseShellExecute = false,
                },
            };

            process.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {uri.Host}.", ex);
        }
    }

    private void Load(string html)
    {
        if (_web is null)
        {
            ShowText(_message?.TextBody ?? _fallbackText);
            return;
        }

        try
        {
            // A base of about:blank, so a relative reference the sanitizer let through has
            // nowhere to resolve to.
            _web.NavigateToString(html, new Uri("about:blank"));
        }
        catch (Exception ex)
        {
            Log.Warn("The web engine would not load the message; showing text instead.", ex);
            _surface.Content = _fallbackHost;
            _web = null;
            ShowText(_message?.TextBody ?? _fallbackText);
        }
    }

    private void ShowText(string text)
    {
        _fallback.Text = text;
        _fallback.FontSize = MessageFontSize;
        Bind(_fallback, TextBlock.ForegroundProperty, "reading.infobar.text.brush");

        if (_web is not null && !ReferenceEquals(_surface.Content, _fallbackHost)) _surface.Content = _fallbackHost;
    }

    /// <summary>
    /// What a printed copy says above the message, in the reference's Memo style.
    /// </summary>
    /// <remarks>
    /// Built here because the engine cannot see the pane's own header — that is Avalonia chrome,
    /// and printing without this produces a page of body text with nothing saying who sent it.
    /// </remarks>
    private static PrintHeader Memo(MimeMessage message) => new(
        message.From.ToString(),
        message.Date.ToLocalTime().ToString("dddd, d MMMM yyyy HH:mm"),
        message.To.ToString(),
        message.Subject ?? string.Empty)
    {
        Cc = message.Cc.Count > 0 ? message.Cc.ToString() : null,
    };

    /// <summary>The document's colours and type, resolved from the active theme.</summary>
    private RenderStyle Style() => new(
        _themes.Tokens.GetString(TokenKeys.Reading.Background),
        _themes.Tokens.GetString(TokenKeys.Text.Primary),
        _themes.Tokens.GetString(TokenKeys.Text.Link),
        _themes.Tokens.GetString(TokenKeys.Text.Secondary),
        _themes.Tokens.GetString(TokenKeys.Typography.ContentFamily),
        MessageFontSize);

    // ---- The bars ----------------------------------------------------------------------------

    /// <summary>
    /// What checking this message's signature came to, or nothing when S/MIME is switched off.
    /// </summary>
    /// <remarks>
    /// The store is the machine's own: a certificate is trusted because this machine trusts it,
    /// never because the message carried it (§19). Nothing is imported here at all.
    /// </remarks>
    private static SignatureReport SignatureOf(MimeMessage message)
    {
        if (!App.Security.Smime || !SmimeVerification.IsSigned(message)) return SignatureReport.Unsigned;

        try
        {
            using var context = CertificateStore();
            return SmimeVerification.Verify(message, context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            // No certificate store to check against is not a verdict about the message.
            Log.Warn("The certificate store could not be opened.", ex);
            return new SignatureReport(SignatureState.Unknown, string.Empty, "The certificate store could not be opened.");
        }
    }

    /// <summary>
    /// Opens an encrypted message, when the reader has asked for S/MIME at all.
    /// </summary>
    private static DecryptionReport Decrypted(MimeMessage message)
    {
        if (!App.Security.Smime || !SmimeDecryption.IsEncrypted(message)) return DecryptionReport.Unencrypted;

        try
        {
            using var context = CertificateStore();
            return SmimeDecryption.Open(message, context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            Log.Warn("The certificate store could not be opened.", ex);
            return new DecryptionReport(DecryptionState.Failed, null, "The certificate store could not be opened.");
        }
    }

    /// <summary>
    /// The decrypted entity as a message of its own, which is what gets rendered.
    /// </summary>
    /// <remarks>
    /// A message rather than a bare entity because the renderer picks a body part out of a
    /// message — and a message of its own rather than the original with its body swapped, so
    /// nothing of the outer document can reach what was inside. The headers a reader sees are
    /// still the envelope's; only the body is the plaintext's.
    /// </remarks>
    private static MimeMessage AsMessage(MimeMessage envelope, MimeEntity content)
    {
        var message = new MimeMessage { Subject = envelope.Subject ?? string.Empty, Date = envelope.Date, Body = content };
        foreach (var from in envelope.From) message.From.Add(from);
        foreach (var to in envelope.To) message.To.Add(to);
        return message;
    }

    /// <summary>The bar over an encrypted message, in the three states opening one comes to.</summary>
    private Control EncryptionBar(DecryptionReport report)
    {
        var alarm = report.State is DecryptionState.Failed;
        var bar = Bar(alarm ? "reading.infobar.warning.background.brush" : "reading.infobar.background.brush");

        var text = new TextBlock
        {
            Text = report.State switch
            {
                DecryptionState.Opened => "This message was encrypted.",
                DecryptionState.Locked => "This message is encrypted to a key this computer has not got.",
                _ => "This message is encrypted and could not be opened.",
            },
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);

        var glyph = Glyph(report.State == DecryptionState.Opened ? "shield" : "warning");
        Grid.SetColumn(glyph, 0);

        var grid = Row();
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        bar.Child = grid;
        return bar;
    }

    /// <summary>
    /// The machine's certificate store — or a throwaway one under the harness.
    /// </summary>
    /// <remarks>
    /// Beside the application's own data rather than in the library's default home directory, for
    /// the reason the mail stores are: what this application keeps is in one place a reader can
    /// find, back up and delete. A capture run gets a temporary store instead, because a run that
    /// photographs the window has no business touching key material — the same rule that keeps it
    /// off the keyring.
    /// </remarks>
    private static MimeKit.Cryptography.SecureMimeContext CertificateStore()
    {
        if (Mailbox.App.Theming.WindowCapture.IsRequested)
        {
            return new MimeKit.Cryptography.TemporarySecureMimeContext();
        }

        var directory = Path.GetDirectoryName(Mailbox.Store.Pim.PimStore.DefaultPath())!;
        Directory.CreateDirectory(directory);
        return new MimeKit.Cryptography.DefaultSecureMimeContext(Path.Combine(directory, "certificates.db"));
    }

    /// <summary>
    /// The line the reference draws over a signed message, in the four states §19 asks for.
    /// </summary>
    /// <remarks>
    /// A mismatch is not folded into either of the other two: it is the attack, and it reads as
    /// its own sentence. Nothing here offers a way to see the content "anyway" — Thunderbird's
    /// wording is the model, and a button that dismisses the warning is the bug it warns about.
    /// </remarks>
    private Control SignatureBar(SignatureReport signature)
    {
        var alarm = signature.State is SignatureState.Invalid or SignatureState.Mismatched;
        var bar = Bar(alarm ? "reading.infobar.warning.background.brush" : "reading.infobar.background.brush");

        var headline = signature.State switch
        {
            SignatureState.Valid => $"Signed by {signature.Signer}.",
            SignatureState.Mismatched => "This message is signed by somebody other than its sender.",
            SignatureState.Invalid => "This message's signature does not hold.",
            _ => "This message is signed in a way Mailbox cannot check.",
        };

        var text = new TextBlock
        {
            Text = headline,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);

        var glyph = Glyph(alarm ? "warning" : signature.State == SignatureState.Valid ? "shield" : "info");
        Grid.SetColumn(glyph, 0);

        var grid = Row();
        grid.Children.Add(glyph);
        grid.Children.Add(text);

        if (signature.Detail.Length > 0)
        {
            var details = BarButton("Details");
            details.Click += async (_, _) => await ExplainAsync("About this signature", signature.Detail);
            Grid.SetColumn(details, 2);
            grid.Children.Add(details);
        }

        bar.Child = grid;
        return bar;
    }

    private Control TrustBar(SenderTrust trust)
    {
        var bar = Bar(trust.Level == TrustLevel.Alarm
            ? "reading.infobar.warning.background.brush"
            : "reading.infobar.background.brush");

        var text = new TextBlock
        {
            Text = trust.Headline,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);

        var glyph = Glyph(trust.Level == TrustLevel.Alarm ? "warning" : "info");
        Grid.SetColumn(glyph, 0);

        var details = BarButton("Details");
        details.Click += async (_, _) => await ExplainAsync(
            "About this sender",
            string.Join("\n\n", trust.Warnings.Select(w => $"{w.Headline}\n{w.Detail}")));
        Grid.SetColumn(details, 2);

        var grid = Row();
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        grid.Children.Add(details);

        bar.Child = grid;
        return bar;
    }

    /// <summary>
    /// The three-way bar: block, allow once, always allow this sender.
    /// </summary>
    /// <remarks>
    /// Blocking is not one of the buttons because it is what has already happened. The bar
    /// exists to offer the two ways out of it, and to say what was held back.
    /// </remarks>
    private Control RemoteImageBar(RenderedMessage rendered)
    {
        var bar = Bar("reading.infobar.background.brush");

        var count = rendered.BlockedImages;
        var hosts = rendered.Hosts.Count;

        var text = new TextBlock
        {
            Text = count > 0
                ? $"{Images(count)} in this message {(count == 1 ? "was" : "were")} blocked, "
                  + $"from {Hosts(hosts)}."
                : $"This message asked for content from {Hosts(hosts)}, which was blocked.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);

        var glyph = Glyph("tracker");
        Grid.SetColumn(glyph, 0);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var once = BarButton("Show images");
        once.Click += async (_, _) => await AllowOnceAsync();
        actions.Children.Add(once);

        if (SenderAddress.Length > 0)
        {
            var always = BarButton("Always from this sender");
            always.Click += async (_, _) => await AlwaysAllowAsync();
            actions.Children.Add(always);
        }

        var report = BarButton("Details");
        report.Click += async (_, _) => await ExplainAsync(
            "Blocked content",
            "This message tried to load content from:\n\n"
            + string.Join("\n", rendered.Hosts.Select(h => "  • " + h))
            + "\n\nA remote image is how a sender finds out that a message was opened, when, "
            + "and roughly from where.");
        actions.Children.Add(report);

        Grid.SetColumn(actions, 2);

        var grid = Row();
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        grid.Children.Add(actions);

        bar.Child = grid;
        return bar;
    }

    private static string Images(int count) => count == 1 ? "One image" : $"{count} images";

    private static string Hosts(int count) => count == 1 ? "one host" : $"{count} hosts";

    private async Task AllowOnceAsync()
    {
        if (_rendered is null) return;

        _policy = RemoteImagePolicy.AllowOnce;
        _inlined = await RemoteImages.FetchAsync(_rendered.Blocked);
        Refresh();
    }

    private async Task AlwaysAllowAsync()
    {
        if (SenderAddress.Length == 0) return;

        _mail()?.AddSafeSender(SenderAddress, DateTimeOffset.UtcNow);
        await AllowOnceAsync();
    }

    /// <summary>Loads images without asking, for a sender already on the list.</summary>
    public async Task ApplySenderPolicyAsync()
    {
        if (_rendered is { HasRemoteContent: true } && _policy == RemoteImagePolicy.Block
            && IsSafeSender())
        {
            await AllowOnceAsync();
        }
    }

    /// <summary>
    /// The tracker report, from the ribbon rather than from the bar.
    /// </summary>
    /// <remarks>
    /// The same detail either way. A reader who has put the command on their ribbon should not
    /// have to find the bar to reach what it says, and a message with nothing blocked should
    /// say so rather than opening an empty list.
    /// </remarks>
    public async Task ShowTrackerReportAsync()
    {
        if (_rendered is not { HasRemoteContent: true } rendered)
        {
            await ExplainAsync("Blocked content", "This message asked for nothing from the network.");
            return;
        }

        await ExplainAsync(
            "Blocked content",
            "This message tried to load content from:\n\n"
            + string.Join("\n", rendered.Hosts.Select(h => "  • " + h)));
    }

    /// <summary>What the sending domain's own checks said, from the ribbon.</summary>
    public async Task ShowAuthenticationAsync()
    {
        if (_message is null) return;

        var trust = SenderTrust.Evaluate(_message, _mail()?.FamiliarDomains() ?? [], _verified);
        var results = trust.Authentication;

        // Two kinds of evidence, and which is which is the useful part. The signature was
        // checked here, against the bytes in the store. Everything else is a server's word.
        var detail = trust.Verified is { WasChecked: true } local
            ? "Checked here, against the message as it was received:\n"
              + $"  Signature (DKIM): {Says(local.Verdict)}"
              + (local.SigningDomain is { Length: > 0 } signer ? $"\n  Signed by: {signer}" : string.Empty)
            : "This message's signature has not been checked here. That is the case for mail "
              + "received before the check existed, mail that carries no signature, and mail "
              + "collected with no resolver to hand.";

        detail += "\n\n" + (results.WasChecked
            ? "Reported by the server that delivered it:\n"
              + $"  DKIM: {Says(results.Dkim)}\n  SPF: {Says(results.Spf)}\n"
              + $"  DMARC: {Says(results.Dmarc)}"
              + (results.SigningDomain is { Length: > 0 } d ? $"\n  Signed by: {d}" : string.Empty)
            : "The server that delivered this message recorded no authentication results, "
              + "which is ordinary for mail sent directly rather than through a provider.");

        if (trust.Warnings.Count > 0)
        {
            detail += "\n\n" + string.Join("\n\n",
                trust.Warnings.Select(w => $"{w.Headline}\n{w.Detail}"));
        }

        await ExplainAsync("Message authentication", detail);
    }

    /// <summary>A verdict in words rather than in the vocabulary of a specification.</summary>
    private static string Says(AuthVerdict verdict) => verdict switch
    {
        AuthVerdict.Pass => "passed",
        AuthVerdict.Fail => "failed",
        AuthVerdict.SoftFail => "failed, but the domain does not insist",
        AuthVerdict.Neutral => "neither passed nor failed",
        AuthVerdict.Error => "could not be checked",
        _ => "not checked",
    };

    /// <summary>
    /// Prints through the engine, which is the only thing that knows how the message is laid
    /// out. Nothing to do when the message is rendering as text.
    /// </summary>
    public bool Print()
    {
        if (_web is null) return false;

        _web.ShowPrintUI();
        return true;
    }

    /// <summary>
    /// Writes the message to a PDF the reader chooses, through the engine's own printer.
    /// </summary>
    /// <remarks>
    /// The same Memo layout a printed copy gets: the print stylesheet is part of the document,
    /// so paper and PDF cannot drift apart.
    /// </remarks>
    public async Task<bool> PrintToPdfAsync()
    {
        if (_web is null) return false;
        if (TopLevel.GetTopLevel(this) is not { } top) return false;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as PDF",
            SuggestedFileName = Suggested(),
            DefaultExtension = "pdf",
        });

        if (file?.TryGetLocalPath() is not { } path) return false;

        try
        {
            await using var pdf = await _web.PrintToPdfStreamAsync();
            await using var destination = File.Create(path);
            await pdf.CopyToAsync(destination);

            Log.Info("Wrote a message to PDF.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write the message to PDF.", ex);
            return false;
        }
    }

    /// <summary>A file name from the subject, with what a file system will not take removed.</summary>
    private string Suggested()
    {
        var subject = _message?.Subject;
        if (string.IsNullOrWhiteSpace(subject)) return "message.pdf";

        var clean = new string([.. subject.Where(c => !Path.GetInvalidFileNameChars().Contains(c))]);
        return (clean.Length > 60 ? clean[..60] : clean).Trim() + ".pdf";
    }

    private async Task ExplainAsync(string title, string detail)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            await Confirm.AskAsync(window, title, detail, "Close", destructive: false);
        }
    }

    // ---- Building blocks ---------------------------------------------------------------------

    private Border Bar(string background)
    {
        var bar = new Border
        {
            Padding = new Thickness(14, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Bind(bar, Border.BackgroundProperty, background);
        Bind(bar, Border.BorderBrushProperty, "border.subtle.brush");
        return bar;
    }

    private static Grid Row()
        => new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

    private Control Glyph(string icon)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        return glyph;
    }

    private Button BarButton(string label)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");

        var button = new Button
        {
            Content = text,
            Height = 22,
            Padding = new Thickness(9, 0),
            Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(1),
        };
        Bind(button, BorderBrushProperty, "border.strong.brush");
        Bind(button, BackgroundProperty, "surface.raised.brush");
        return button;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
