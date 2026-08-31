using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Mailbox.Core;
using Mailbox.Core.Diagnostics;
using Mailbox.Rendering;
using RenderOptions = Mailbox.Rendering.RenderOptions;
using Mailbox.Security;
using Mailbox.Security.OpenPgp;
using Mailbox.Security.Smime;
using Mailbox.Store;
using Mailbox.Theming;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Tokens;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// How a save-as-PDF ended, for a caller that has something to say about each.
/// </summary>
/// <remarks>
/// Three answers rather than a bool because two of them used to be one. Dismissing the file
/// picker and failing to write the file both returned false, and the caller told the reader the
/// message could not be written to PDF either way — an error for something the reader had just
/// chosen to do.
/// </remarks>
public enum PdfSaveResult
{
    /// <summary>The file was written.</summary>
    Saved,

    /// <summary>The reader dismissed the picker. Nothing to report.</summary>
    Cancelled,

    /// <summary>The write was attempted and did not work. Worth saying.</summary>
    Failed,
}

/// <summary>
/// The reading pane's body: the bars that say what was held back, and the message itself.
/// </summary>
/// <remarks>
/// The engine is handed a document that has already had every remote reference taken out of it,
/// so there is nothing here that decides what to allow at request time — by the time this runs,
/// the decision has been made and baked into the markup. The sanitizer's remarks say why that is the design
/// rather than a request veto.
/// <para>
/// The WebView is created defensively. The WPE backend is new, and a reading pane that throws on
/// a machine without it would take the application with it; the fallback renders the message as
/// text, which is what the pane did before the renderer arrived and is better than a crash.
/// </para>
/// </remarks>
public sealed partial class ReadingPaneBody : UserControl, IDisposable
{
    private readonly ThemeService _themes;
    private readonly Func<MailRepository?> _mail;

    private readonly StackPanel _bars = new();

    /// <summary>
    /// The message itself. Named, because what is inside it is a web engine or a block of text
    /// and neither says what it is: a reader landing here was told "custom" or nothing.
    /// </summary>
    private readonly ContentControl _surface = new()
    {
        [Avalonia.Automation.AutomationProperties.NameProperty] = "Message",
    };
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

    /// <summary>The sender as a header field writes it, once the pane has settled which one to draw.</summary>
    private string? _fromLine;

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
    /// resolves a name the sender chose, and no lookup is allowed on the path that draws a
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

    /// <summary>The unsubscribe bar's button, for the harness, on the same grounds.</summary>
    private Button? _unsubscribeButton;

    internal Button? UnsubscribeButton => _unsubscribeButton;

    /// <summary>Raised when Accept, Tentative or Decline was pressed; the shell sends the reply.</summary>
    public event EventHandler<InvitationBar.Answer>? InvitationAnswered;

    /// <summary>A cancelled meeting was removed from the calendar by the bar's own button.</summary>
    public event EventHandler? InvitationRemoved;

    /// <summary>
    /// The message whose parts are on screen — the decrypted one when there was one to open.
    /// </summary>
    /// <remarks>
    /// What the attachment strip reads, so the two cannot disagree: the strip used to list the
    /// envelope's parts, which for an encrypted message meant offering the ciphertext as a file to
    /// save and never offering what was actually attached inside it.
    /// </remarks>
    public MimeMessage? Carried { get; private set; }

    /// <summary>
    /// The header fields the message carried inside its own cryptography, or null when it carried none.
    /// </summary>
    /// <remarks>
    /// RFC 9788. What the shell draws its header from, and what a reply is addressed off — see
    /// <see cref="HeaderSubject"/> and <see cref="HeaderFrom"/>.
    /// </remarks>
    public ProtectedHeaders? Protected { get; private set; }

    /// <summary>The subject the pane's header should draw, or null to use the list row's.</summary>
    public string? HeaderSubject { get; private set; }

    /// <summary>The sender the pane's header should draw, or null to use the list row's.</summary>
    public string? HeaderFrom { get; private set; }

    /// <summary>Raised when the pane has settled what its header should say. The shell draws it.</summary>
    public event EventHandler? HeaderChanged;

    private void Refresh()
    {
        _bars.Children.Clear();
        _unsubscribeButton = null;

        if (_message is null)
        {
            _rendered = null;
            Carried = null;
            Protected = null;
            HeaderSubject = null;
            HeaderFrom = null;
            HeaderChanged?.Invoke(this, EventArgs.Empty);

            // A header with nothing under it is not an empty message: the server has one and it
            // has not been fetched. Say which, and offer to fetch it.
            if (HeaderOnly) _bars.Children.Add(HeaderOnlyBar());

            ShowText(_fallbackText);
            return;
        }

        var trust = SenderTrust.Evaluate(
            _message, FamiliarDomains(), _verified,
            reportAuthentication: App.Security.ShowAuthenticationResults,
            warnDisplayNameMismatch: App.Security.WarnDisplayNameMismatch);

        if (trust.Warnings.Count > 0) _bars.Children.Add(TrustBar(trust));

        // Every warning, not just the headline the bar draws. The bar shows the loudest one, so
        // two switches that each silence a different warning look identical from a capture — and
        // "the display-name warning went and the authentication warning stayed" is exactly the
        // claim the Trust Center's rows make.
        if (Mailbox.App.Theming.WindowCapture.IsRequested)
        {
            Log.Info($"Harness: reading trust — {trust.Level}, {trust.Warnings.Count} warning(s)"
                     + (trust.Warnings.Count == 0
                         ? string.Empty
                         : ": " + string.Join(" | ", trust.Warnings.Select(w => $"{w.Level} “{w.Headline}”")))
                     + $"; authentication results {(App.Security.ShowAuthenticationResults ? "shown" : "not shown")}"
                     + $", display-name mismatch {(App.Security.WarnDisplayNameMismatch ? "warned" : "not warned")}.");
        }

        // An encrypted message is opened before anything else is decided, because what gets checked
        // and what gets rendered both depend on what was inside. The channel CVE-2026-0818
        // used was the cascade, so a decrypted part spliced into the outer document is readable by
        // the outer document's own stylesheet — what comes out is rendered *instead of* the message
        // it arrived in, never inside it.
        var opened = Decrypted(_message);

        // The header fields a protected message carries a copy of inside itself (RFC 9788). Read
        // before anything is drawn, because what the pane's header says is one of the things they
        // decide — and what is rendered too, the payload being where the body is as well.
        Protected = Covered(_message, opened);

        // RFC 9788 §4.5.3, a MUST: the copy of the hidden fields that an encrypted message writes into its
        // own body, for a client that could not read them anywhere else, is not drawn by a client
        // that can. The HTML half's is dropped by the sanitizer; this is the text half's.
        if (opened.Opened && Protected is not null) HeaderProtection.HideLegacyDisplay(Protected.Rendered);

        var carrier = opened.Opened
            ? AsMessage(_message, Protected?.Rendered ?? opened.Content!, Protected)
            : _message;

        Carried = carrier;

        // The signature, when the reader has asked for either kind of crypto at all. Crypto ships
        // off, and a bar that says "signed" over a check nobody made would be worse than no
        // bar. A signature carried *inside* an encrypted packet is the packet's own — OpenPGP's
        // ordinary shape — and one carried as a MIME layer is read off whichever message the reader
        // is actually being shown.
        var signature = opened.Signature is { State: not SignatureState.None } enclosed
            ? enclosed
            : SignatureOf(carrier);

        if (signature.State != SignatureState.None) _bars.Children.Add(SignatureBar(signature));

        // Which From the header draws, and whether the two of them disagree — a question that cannot
        // be answered until the signature has been, because a signature bound to the address inside
        // is what makes it worth believing over the one the transport checked.
        var spoofed = Protected is { } covered && covered.FromMismatch(_message) && !Bound(signature, covered);
        Settle(spoofed);

        // What the crypto came to, for a harness run to read back. A bar can be photographed, but
        // what it says is a claim about the store and the keyring rather than about the picture —
        // and a refusal draws no content at all, which a capture cannot tell from an empty message.
        if (Mailbox.App.Theming.WindowCapture.IsRequested
            && (opened.State != DecryptionState.None || signature.State != SignatureState.None))
        {
            Log.Info($"Harness: crypto — encryption {opened.State}, signature {signature.State}"
                + (signature.Signer.Length > 0 ? $" by {signature.Signer}" : string.Empty)
                + $"; renders {(opened.Opened ? "the decrypted content, isolated" : "the message as it arrived")}."
                + string.Concat(new[] { opened.Detail, signature.Detail }.Where(d => d.Length > 0).Select(d => " " + d)));

            LogHeaders(signature, spoofed);
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
            bar.Removed += (_, _) => InvitationRemoved?.Invoke(this, EventArgs.Empty);
            _bars.Children.Insert(0, bar);
            _invitation = bar;
        }
        else
        {
            _invitation = null;
        }

        // Above everything, invitation included: this is the one warning that says the message may
        // not be from who it says it is, and RFC 9788 §4.4.2 asks for it to read like a phishing warning.
        if (spoofed) _bars.Children.Insert(0, MismatchBar());

        var disableLinks = _suspectedJunk && App.MailOptions.DisableLinksInJunk;

        if (opened.State != DecryptionState.None) _bars.Children.Add(EncryptionBar(opened));

        var options = new RenderOptions
        {
            Style = Style(),
            Inlined = _inlined,
            PrintHeader = Memo(carrier),
            DisableLinks = disableLinks,
            Isolated = opened.Opened,
            HideLegacyDisplay = opened.Opened && Protected is not null,
        };

        // `carrier` is the decrypted message when there was one, and the message itself otherwise —
        // so a refusal renders the envelope and nothing that was inside it.
        _rendered = MessageRenderer.Render(carrier, options);

        if (disableLinks) _bars.Children.Add(JunkBar());
        if (_rendered.HasRemoteContent) _bars.Children.Add(RemoteImageBar(_rendered));

        // The way out of a mailing list, surfaced — but never on suspected junk, where
        // "unsubscribe" is exactly the button that confirms the address is read.
        if (!_suspectedJunk
            && UnsubscribeOffer.Parse(
                _message.Headers["List-Unsubscribe"],
                _message.Headers["List-Unsubscribe-Post"]) is { } offer)
        {
            _bars.Children.Add(UnsubscribeBar(offer));
        }

        // The plugins' bars come after the application's own, so what the application says about
        // a message always outranks an add-in. Providers answer from what they already know —
        // this is the render path, where nothing may block or ask the network — and the
        // host charges a provider that throws to its plugin rather than to the pane.
        foreach (var (plugin, contributed) in App.Plugins.InfoBarsFor(PluginSummaryNow()))
        {
            _bars.Children.Add(PluginBar(contributed));
            Log.Info($"Harness: plugin bar — {plugin}: {contributed.Text}");
        }

        Load(_rendered.Html);

        // Last, so what it reports is the pane as it finally stands rather than part-way through
        // being built. MAILBOX_READING=dump gates it.
        LogForHarness();
    }

    /// <summary>
    /// The domains the lookalike check compares against — or none, when the Junk Options
    /// dialog's "warn me about suspicious domain names" is off, which is what turns that one
    /// warning off without touching the rest of the trust bar.
    /// </summary>
    private IEnumerable<string> FamiliarDomains()
        => App.MailOptions.WarnAboutSuspiciousDomains ? _mail()?.FamiliarDomains() ?? [] : [];

    /// <summary>
    /// The row a plugin's info-bar provider is asked about. The shell fills this beside the
    /// message when a list row is behind it; a pop-out window has only the envelope, and an
    /// empty account with zeroed ids is what "no row" honestly looks like to a plugin.
    /// </summary>
    public Mailbox.Plugins.Api.PluginMessageSummary? PluginSummary { get; set; }

    private Mailbox.Plugins.Api.PluginMessageSummary PluginSummaryNow()
        => PluginSummary ?? new(
            string.Empty, 0, 0,
            _message?.Subject ?? string.Empty,
            SenderAddress,
            _message?.Date ?? DateTimeOffset.MinValue,
            IsRead: true);

    /// <summary>One plugin's bar: its text, and the one button it may carry.</summary>
    private Control PluginBar(Mailbox.Plugins.Api.PluginInfoBar contributed)
    {
        var bar = Bar("reading.infobar.background.brush");
        var row = Row();

        var glyph = Glyph("apps");
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        var text = new TextBlock
        {
            Text = contributed.Text,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (contributed is { ButtonLabel.Length: > 0, ButtonPressed: { } pressed })
        {
            var button = BarButton(contributed.ButtonLabel);
            button.Click += (_, _) => pressed();
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }

        bar.Child = row;
        return bar;
    }

    /// <summary>The bar above a message in Junk whose links have been drawn inert.</summary>
    /// <summary>
    /// True when the row behind the pane is a header whose message has not been downloaded —
    /// Send/Receive's Download Headers wrote it, and only the reader can say it is wanted.
    /// </summary>
    public bool HeaderOnly { get; set; }

    /// <summary>Raised by the header bar's own button: fetch this one now.</summary>
    public event EventHandler? DownloadRequested;

    private Control HeaderOnlyBar()
    {
        var bar = Bar("reading.infobar.background.brush");
        var row = Row();

        var glyph = Glyph("download-headers");
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        var text = new TextBlock
        {
            Text = "Only this message's header has been downloaded. "
                   + "The message itself is still on the server.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var download = BarButton("Download");
        download.Click += (_, _) => DownloadRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(download, 2);
        row.Children.Add(download);

        bar.Child = row;
        return bar;
    }

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
            _web.AdapterCreated += (_, e) =>
            {
                Log.Info($"Reading pane engine: {Describe()}");
                HookDrawRequested(e.TryGetPlatformHandle());
            };
            _web.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) Log.Debug("The reading pane loaded a message.");
                else Log.Warn("The reading pane could not load the message.");

                if (e.IsSuccess) _ = NudgeFrameOutAsync(_web);

                // Under the dump gate only: the engine has finished loading, so it can be asked
                // what it drew — which a capture cannot answer, racing the offscreen frame.
                _ = ReportEngineWordsAsync(e.IsSuccess);
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
    /// Makes the offscreen engine export the frame it has already painted.
    /// </summary>
    /// <remarks>
    /// The offscreen embedding delivers a frame only on damage, and a static message stops
    /// causing damage the moment its last paint lands — which can be before the text was
    /// rasterised, leaving the pane holding an earlier, bare frame: an empty body over a
    /// perfectly healthy engine, with the words readable in the document and nothing on the
    /// screen. Proven by damaging the page on a timer and photographing the window from
    /// outside: exports resumed at once and the text appeared. So after every successful load,
    /// an invisible style flick runs through two animation frames — real damage with no visible
    /// effect — and the export it forces carries the finished paint. A handful of them on a
    /// short cadence rather than two far apart: the reader is watching this gap, and the paint
    /// becomes visible at the first flick after the text is rasterised, so the cadence is the
    /// worst case a small message waits.
    /// </remarks>
    private static async Task NudgeFrameOutAsync(NativeWebView web)
    {
        const string nudge =
            "requestAnimationFrame(function(){"
            + " document.documentElement.style.opacity='0.9999';"
            + " requestAnimationFrame(function(){ document.documentElement.style.opacity=''; });"
            + " });";
        try
        {
            for (var i = 0; i < 5; i++)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    async () => await web.InvokeScript(nudge));
                await Task.Delay(120);
            }
        }
        catch
        {
            // A pane torn down mid-nudge is a pane that no longer needs one.
        }
    }

    /// <summary>
    /// What the engine reports itself as, for the log.
    /// </summary>
    /// <remarks>
    /// The embedding scenario is the part worth recording. A pane embedded as a native child
    /// window has the airspace problem — Avalonia's own menus and flyouts cannot draw over it,
    /// which is disqualifying in a mail client where popups overlap the reading pane constantly.
    /// An offscreen renderer composites into the visual tree and does not. This backend was chosen
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

                // The offscreen embedding, same as the WPE default — without it the GTK
                // fallback puts the message in a native child window, which the flyout-airspace
                // rule rules out. Offscreen here is a snapshot into CPU memory per drawn frame,
                // so it does not depend on the GPU buffer-export path at all.
                gtk.ExperimentalOffscreen = true;
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

        // The address here comes out of the message, which is what makes this the one that had to
        // be guarded: a link in a seeded corpus, pressed under a pose, would hand an arbitrary URL
        // to the browser of whoever was running the sweep.
        Mailbox.Core.Platform.DesktopOpen.Open(uri.ToString());
    }

    /// <summary>
    /// Lets the web engine go.
    /// </summary>
    /// <remarks>
    /// The pane in the shell lives as long as the window does and never needs this. A message
    /// opened in its own window is the case that matters: double-clicking is the ordinary way to
    /// read mail, and each window builds a pane of its own with an engine behind it. The engine
    /// is torn down when its view leaves the visual tree, so this stops the load, takes the view
    /// out of the tree and drops the last reference to it — at the moment the window closes,
    /// rather than whenever a collection happens to notice.
    /// </remarks>
    public void Dispose()
    {
        if (_web is not { } web) return;

        _web = null;

        try
        {
            web.Stop();
            web.EnvironmentRequested -= OnEnvironmentRequested;
            web.NavigationStarted -= OnNavigationStarted;
            web.NewWindowRequested -= OnNewWindowRequested;
        }
        catch (Exception ex)
        {
            // A window is closing; there is nobody left to tell.
            Log.Debug($"The reading pane's engine did not stop cleanly: {ex.Message}");
        }

        // Out of the tree, which is what the engine's own teardown waits for.
        if (ReferenceEquals(_surface.Content, web)) _surface.Content = null;
    }

    private void Load(string html)
    {
        if (_web is null)
        {
            ShowText(_message?.TextBody ?? _fallbackText);
            return;
        }

        // ShowText hands the surface to the text fallback — an empty selection does it, and a
        // selection cleared by a folder switch does it again — and this is the only path that
        // hands it back. A web view out of the tree has no engine behind it: a navigate into
        // one attaches nothing, logs nothing and draws nothing, which reads as an empty pane
        // over a perfectly healthy application.
        if (!ReferenceEquals(_surface.Content, _web))
        {
            _surface.Content = _web;
            if (Mailbox.App.Theming.WindowCapture.IsRequested)
                Log.Info("Harness: reading surface — the web view takes the pane back.");
        }

        try
        {
            // The dump run's hold, taken before the navigation so the capture cannot fire
            // between the two; released when the engine answers for what it drew.
            HoldForEngine();

            // A base of about:blank, so a relative reference the sanitizer let through has
            // nowhere to resolve to.
            _web.NavigateToString(html, new Uri("about:blank"));
        }
        catch (Exception ex)
        {
            Log.Warn("The web engine would not load the message; showing text instead.", ex);
            _engineHold?.Dispose();
            _engineHold = null;
            _surface.Content = _fallbackHost;

            // Dispose rather than just forgetting it: an engine dropped while still in the tree
            // is one nothing will ever take out of it again.
            Dispose();
            ShowText(_message?.TextBody ?? _fallbackText);
        }
    }

    /// <summary>
    /// Scrolls the message down a screen, and says whether there was anywhere to go.
    /// </summary>
    /// <remarks>
    /// What makes Space one key rather than two in the feed reader: it means "carry on", and
    /// carrying on is more of this article until there is no more of it, then the next one. So
    /// the answer matters — a key that silently does nothing at the foot of an article is worse
    /// than no key at all.
    /// <para>
    /// Two surfaces to handle, because the pane is either a web engine or a block of text
    /// depending on what this machine has. The engine is asked in its own language and its
    /// answer awaited; the text is a ScrollViewer and is asked directly.
    /// </para>
    /// </remarks>
    public Task<bool> ScrollDownAsync() => ScrollAsync(down: true);

    /// <summary>Scrolls the message back up a screen.</summary>
    public Task<bool> ScrollUpAsync() => ScrollAsync(down: false);

    private async Task<bool> ScrollAsync(bool down)
    {
        if (ReferenceEquals(_surface.Content, _fallbackHost))
        {
            var before = _fallbackHost.Offset.Y;

            if (down) _fallbackHost.PageDown();
            else _fallbackHost.PageUp();

            return Math.Abs(_fallbackHost.Offset.Y - before) > 0.5;
        }

        if (_web is null) return false;

        try
        {
            // Scrolls, and reports whether it actually moved. Written to work on a document whose
            // scrolling element is the body and on one where it is the html element, which differ
            // by quirks mode and are both common.
            var by = down ? "d" : "-d";
            var answer = await _web.InvokeScript(
                "(function(){var e=document.scrollingElement||document.documentElement||document.body;"
                + "var b=e.scrollTop;var d=window.innerHeight*0.9;"
                + $"e.scrollTop=b+({by});return String(Math.abs(e.scrollTop-b)>1);}})()");

            return answer?.ToString()?.Contains("true", StringComparison.OrdinalIgnoreCase) ?? false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TaskCanceledException)
        {
            Log.Debug($"The reading pane would not scroll: {ex.Message}");
            return false;
        }
    }

    private void ShowText(string text)
    {
        _fallback.Text = text;
        _fallback.FontSize = MessageFontSize;
        Bind(_fallback, TextBlock.ForegroundProperty, "reading.infobar.text.brush");

        if (_web is not null && !ReferenceEquals(_surface.Content, _fallbackHost))
        {
            _surface.Content = _fallbackHost;
            if (Mailbox.App.Theming.WindowCapture.IsRequested)
                Log.Info("Harness: reading surface — the text fallback takes the pane.");
        }
    }

    /// <summary>
    /// What a printed copy says above the message, in the reference's Memo style.
    /// </summary>
    /// <remarks>
    /// Built here because the engine cannot see the pane's own header — that is Avalonia chrome,
    /// and printing without this produces a page of body text with nothing saying who sent it.
    /// <para>
    /// The sender and the subject are whatever the pane itself settled on, protected header fields
    /// included: a printed copy that disagreed with the window it was printed from would be the
    /// worse of the two to have on paper.
    /// </para>
    /// </remarks>
    private PrintHeader Memo(MimeMessage message) => new(
        _fromLine ?? message.From.ToString(),
        message.Date.ToLocalTime().ToString("dddd, d MMMM yyyy HH:mm"),
        message.To.ToString(),
        HeaderSubject ?? message.Subject ?? string.Empty)
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
    /// What checking this message's signature came to, or nothing when both switches are off.
    /// </summary>
    /// <remarks>
    /// The store is the machine's own, whichever kind: a key is trusted because this machine trusts
    /// it, never because the message carried it. Nothing is imported here at all.
    /// <para>
    /// The two are asked in turn rather than chosen between, because which one a message wants is
    /// the message's own business — and each answers only for the shape it recognises, so a
    /// <c>multipart/signed</c> naming the other's protocol falls straight through.
    /// </para>
    /// <para>
    /// <b>On the dispatcher on purpose.</b> This is disk plus crypto per signed message, and it
    /// stays synchronous because the verdict gates what the header says before anything is
    /// drawn: a signature bound to the inner From is what decides whether the display-name
    /// warning fires at all. Rendered first and settled later, a spoofed From would stand
    /// unwarned for the length of the check — a worse trade than the stall, which is bounded,
    /// local and paid only when a crypto switch is on and the message is actually signed.
    /// </para>
    /// </remarks>
    private static SignatureReport SignatureOf(MimeMessage message)
    {
        if (App.Security.Smime && SmimeVerification.IsSigned(message))
        {
            try
            {
                using var context = CryptoStores.Certificates();
                return SmimeVerification.Verify(message, context);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
            {
                // No certificate store to check against is not a verdict about the message.
                Log.Warn("The certificate store could not be opened.", ex);
                return new SignatureReport(
                    SignatureState.Unknown, string.Empty, "The certificate store could not be opened.");
            }
        }

        if (App.Security.OpenPgp && PgpVerification.IsSigned(message))
        {
            try
            {
                using var context = CryptoStores.KeyRing();
                return PgpVerification.Verify(message, context);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("The OpenPGP keyring could not be opened.", ex);
                return new SignatureReport(
                    SignatureState.Unknown, string.Empty, "The OpenPGP keyring could not be opened.");
            }
        }

        return SignatureReport.Unsigned;
    }

    /// <summary>
    /// Opens an encrypted message, when the reader has asked for that kind of crypto at all.
    /// </summary>
    private static DecryptionReport Decrypted(MimeMessage message)
    {
        if (App.Security.Smime && SmimeDecryption.IsEncrypted(message))
        {
            try
            {
                using var context = CryptoStores.Certificates();
                return SmimeDecryption.Open(message, context);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
            {
                Log.Warn("The certificate store could not be opened.", ex);
                return new DecryptionReport(
                    DecryptionState.Failed, null, "The certificate store could not be opened.");
            }
        }

        if (App.Security.OpenPgp && PgpDecryption.IsEncrypted(message))
        {
            try
            {
                // So whatever comes back wanting a passphrase is this message's own, and the bar
                // does not offer to unlock a key the message before it needed.
                CryptoStores.Passphrases.Clear();

                using var context = CryptoStores.KeyRing();
                return PgpDecryption.Open(message, context);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("The OpenPGP keyring could not be opened.", ex);
                return new DecryptionReport(
                    DecryptionState.Failed, null, "The OpenPGP keyring could not be opened.");
            }
        }

        return DecryptionReport.Unencrypted;
    }

    /// <summary>
    /// The decrypted entity as a message of its own, which is what gets rendered.
    /// </summary>
    /// <remarks>
    /// A message rather than a bare entity because the renderer picks a body part out of a
    /// message — and a message of its own rather than the original with its body swapped, so
    /// nothing of the outer document can reach what was inside.
    /// <para>
    /// The header fields are the protected ones where the message carried its own and the envelope's
    /// where it did not, which is what RFC 9788 §4 asks for and what makes a reply to one of these
    /// go to the right people. A reply is built from this message, so this is the one place that
    /// decision has to be made.
    /// </para>
    /// </remarks>
    private static MimeMessage AsMessage(
        MimeMessage envelope, MimeEntity content, ProtectedHeaders? covered)
        => HeaderProtection.Addressed(envelope, covered, content);

    /// <summary>
    /// The header fields inside the cryptography, when the reader has asked for cryptography at all.
    /// </summary>
    /// <remarks>
    /// Switched off means switched off: nothing has checked a signature, so a copy of a subject
    /// taken out of a body nobody verified is worth less than the one the list already shows.
    /// </remarks>
    private static ProtectedHeaders? Covered(MimeMessage message, DecryptionReport opened)
    {
        if (!App.Security.Smime && !App.Security.OpenPgp) return null;

        var content = opened.Opened ? opened.Content : message.Body;
        return content is null ? null : HeaderProtection.Read(message, content, opened.Opened);
    }

    /// <summary>Settles what the pane's own header should say, and tells the shell to draw it.</summary>
    private void Settle(bool spoofed)
    {
        if (Protected is not { } covered || _message is null)
        {
            HeaderSubject = null;
            HeaderFrom = null;
            _fromLine = null;
            HeaderChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        HeaderSubject = covered.Value("Subject");

        // RFC 9788 §4.4.3: where the two disagree and nothing binds a signature to the address inside, what
        // gets drawn is the address the transport authenticated. The protected one is the attacker's
        // half in that case, and drawing it would make header protection a way to dress up a spoof.
        _fromLine = (spoofed ? null : covered.Value("From")) ?? _message.From.ToString();
        HeaderFrom = Display(_fromLine);

        HeaderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A sender as the pane's header writes it: the name they chose, or their address.</summary>
    private static string Display(string value)
        => InternetAddressList.TryParse(value, out var list)
            && list.Mailboxes.FirstOrDefault() is { } who
            ? (who.Name is { Length: > 0 } name ? name : who.Address)
            : value;

    /// <summary>
    /// Whether a signature vouches for the address inside the message.
    /// </summary>
    /// <remarks>
    /// RFC 9788 §4.4.1.2 defines the opposite — "no valid and correctly bound signature" — as no signature, a
    /// broken one, or a valid one the reader sees no binding between and the protected From. This
    /// application's verifiers only ever report <see cref="SignatureState.Valid"/> for a signature
    /// whose certificate or key names the address it claims, so the binding is a comparison of two
    /// addresses rather than a second trust decision.
    /// </remarks>
    private static bool Bound(SignatureReport signature, ProtectedHeaders covered)
    {
        if (signature.State != SignatureState.Valid) return false;
        if (covered.Value("From") is not { } inner) return false;

        return InternetAddressList.TryParse(inner, out var list)
            && list.Mailboxes.Any(
                m => string.Equals(m.Address, signature.Signer, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a harness run reads back about the header fields, there being no bar for most of it.</summary>
    private void LogHeaders(SignatureReport signature, bool spoofed)
    {
        if (Protected is not { } covered) return;

        var held = signature.State == SignatureState.Valid;
        var confidential = covered.ConfidentialFields;

        Log.Info(
            $"Harness: header protection — {covered.Intent}"
            + (covered.Stated ? " (stated)" : " (inferred from the message's shape)")
            + $", subject \"{covered.Value("Subject")}\" is {covered.ProtectionOf("Subject", held)}"
            + $", {covered.Fields.Count} fields carried, "
            + (confidential.Count > 0 ? "kept back: " + string.Join(", ", confidential) : "none kept back")
            + (spoofed ? "; the From inside does not match the envelope's and nothing binds it." : "."));
    }

    /// <summary>The bar over an encrypted message, in the four states opening one comes to.</summary>
    /// <remarks>
    /// <b>Unprotected has no way past it</b>, which is the point of the state. The content exists —
    /// it decrypted — and it is not behind this bar, not behind a Details button and not behind a
    /// warning a reader can dismiss: a "show it anyway" is the bug the warning is about, and
    /// Thunderbird's wording is the model for saying so plainly.
    /// </remarks>
    private Control EncryptionBar(DecryptionReport report)
    {
        var alarm = report.State is DecryptionState.Failed or DecryptionState.Unprotected;
        var bar = Bar(alarm ? "reading.infobar.warning.background.brush" : "reading.infobar.background.brush");

        // Locked is two situations wearing one state: the key is not here, or it is here and shut.
        // Only the second is something the reader can do anything about, and the vault knows which —
        // it records every key it was asked for and had no answer to.
        var shut = report.State == DecryptionState.Locked && CryptoStores.Passphrases.Wanted.Count > 0;

        var text = new TextBlock
        {
            Text = report.State switch
            {
                DecryptionState.Opened => "This message was encrypted.",
                DecryptionState.Locked when shut =>
                    "This message is encrypted to a key on this computer that is locked.",
                DecryptionState.Locked => "This message is encrypted to a key this computer has not got.",
                DecryptionState.Unprotected => "Mailbox will not show this message: nothing proves it "
                                               + "arrived the way it was sent.",
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

        // The one action a bar over an encrypted message offers, and only for the one state where
        // there is anything to do. Nothing here ever offers to show content that did not open —
        // Unprotected in particular has no way past it, that being the point of the state.
        if (shut)
        {
            var unlock = BarButton("Unlock");
            unlock.Click += async (_, _) => await UnlockAsync();
            Grid.SetColumn(unlock, 2);
            grid.Children.Add(unlock);
        }
        else if (report.Detail.Length > 0)
        {
            var details = BarButton("Details");
            details.Click += async (_, _) => await ExplainAsync("About this message", report.Detail);
            Grid.SetColumn(details, 2);
            grid.Children.Add(details);
        }

        bar.Child = grid;
        return bar;
    }

    /// <summary>
    /// Asks for the passphrase the last attempt wanted, and draws the message again if it is given.
    /// </summary>
    /// <remarks>
    /// Drawn again rather than patched: the decision about what to render depends on what came out
    /// of the packet, so the whole pass runs over the now-openable message rather than a decrypted
    /// part being pushed into a view built for a refusal.
    /// </remarks>
    private async Task UnlockAsync()
    {
        var wanted = CryptoStores.Passphrases.Wanted;
        if (wanted.Count == 0 || TopLevel.GetTopLevel(this) is not Window owner) return;

        using var keys = CryptoStores.KeyRing();
        if (await PassphraseDialog.UnlockAsync(owner, keys, CryptoStores.Passphrases, wanted)) Refresh();
    }


    /// <summary>
    /// The bar over a message whose two From fields disagree.
    /// </summary>
    /// <remarks>
    /// RFC 9788 §4.4.2 asks for a warning comparable to the one a client gives about phishing, and
    /// for both addresses to be shown, which is why this names them rather than saying that
    /// something is wrong. RFC 9788 §10.1 is the attack it is about: a message whose protected From says one
    /// person and whose envelope — the part the transport authenticated with DKIM or SPF — says
    /// another. Without this, header protection would be a way to make a spoof look better than an
    /// ordinary one, and the address drawn above is deliberately the transport's for the same reason.
    /// </remarks>
    private Control MismatchBar()
    {
        var bar = Bar("reading.infobar.warning.background.brush");

        var inside = Protected?.Value("From") ?? string.Empty;
        var outside = _message?.From.ToString() ?? string.Empty;

        var text = new TextBlock
        {
            Text = "This message says inside itself that it is from " + inside
                   + ", and arrived saying it is from " + outside
                   + ". Nothing proves which is true, so Mailbox shows the second.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);

        var glyph = Glyph("warning");
        Grid.SetColumn(glyph, 0);

        var grid = Row();
        grid.Children.Add(glyph);
        grid.Children.Add(text);

        bar.Child = grid;
        return bar;
    }

    /// <summary>
    /// The line the reference draws over a signed message, in the four states the security design asks for.
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

        // "Report the hosts a message tried to contact." Off leaves the blocking exactly where it
        // was and stops naming what was blocked — so the bar still says content was held back,
        // without the host count, and offers no list to open.
        var naming = App.Security.ReportTrackerHosts;

        var text = new TextBlock
        {
            Text = count > 0
                ? $"{Images(count)} in this message {(count == 1 ? "was" : "were")} blocked"
                  + (naming ? $", from {Hosts(hosts)}." : ".")
                : naming
                    ? $"This message asked for content from {Hosts(hosts)}, which was blocked."
                    : "This message asked for content from the network, which was blocked.",
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

        if (naming)
        {
            var report = BarButton("Details");
            report.Click += async (_, _) => await ExplainAsync(
                "Blocked content",
                "This message tried to load content from:\n\n"
                + string.Join("\n", rendered.Hosts.Select(h => "  • " + h))
                + "\n\nA remote image is how a sender finds out that a message was opened, when, "
                + "and roughly from where.");
            actions.Children.Add(report);
        }

        Grid.SetColumn(actions, 2);

        var grid = Row();
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        grid.Children.Add(actions);

        bar.Child = grid;
        return bar;
    }

    /// <summary>The mailing-list bar: one sentence, one button, and the answer said in place.</summary>
    private Control UnsubscribeBar(UnsubscribeOffer offer)
    {
        var bar = Bar("reading.infobar.background.brush");

        var text = new TextBlock
        {
            Text = "This message came from a mailing list.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Grid.SetColumn(text, 1);

        var glyph = Glyph("mail");
        Grid.SetColumn(glyph, 0);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var unsubscribe = BarButton("Unsubscribe");
        unsubscribe.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            unsubscribe.IsEnabled = false;
            var said = await Unsubscriber.ActAsync(owner, offer, SenderAddress);
            unsubscribe.IsEnabled = said is null;

            // The bar's own sentence carries the outcome — the pane may be in a window whose
            // status bar is the shell's, three metres of screen away.
            if (said is { } outcome)
            {
                text.Text = outcome;
                Log.Info($"Harness: unsubscribe bar — “{outcome}”");
            }
        };
        actions.Children.Add(unsubscribe);
        _unsubscribeButton = unsubscribe;
        Grid.SetColumn(actions, 2);

        var grid = Row();
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        grid.Children.Add(actions);

        bar.Child = grid;
        Log.Info($"Harness: unsubscribe bar shown — mailto {offer.Mailto.Count}, web {offer.Web.Count}, "
                 + $"one-click {(offer.OneClick is null ? "no" : "yes")}.");
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

    /// <summary>
    /// Loads images without asking: for a sender already on the safe list, and for everyone when
    /// the Trust Center's "Don't download pictures automatically in messages" is off.
    /// </summary>
    /// <remarks>
    /// Here rather than on the render path, because the network is forbidden anywhere a message is
    /// drawn — the render blocks everything, and this is the pass afterwards that asks for what
    /// the reader has agreed to. The fetch is Mailbox's own client either way; what the switch
    /// decides is whether it runs unprompted.
    /// </remarks>
    public async Task ApplySenderPolicyAsync()
    {
        if (_rendered is not { HasRemoteContent: true } || _policy != RemoteImagePolicy.Block) return;

        var automatic = !App.Security.BlockRemotePictures;
        if (!automatic && !IsSafeSender()) return;

        if (Mailbox.App.Theming.WindowCapture.IsRequested)
        {
            Log.Info("Harness: reading images — loading without asking "
                     + $"({(automatic ? "pictures are not held back" : "the sender is on the safe list")}).");
        }

        await AllowOnceAsync();
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
        // The switch this command is the whole point of. Said rather than shown empty: a report
        // that has been turned off and a message that reached for nothing are different answers.
        if (!App.Security.ReportTrackerHosts)
        {
            await ExplainAsync(
                "Blocked content",
                "Reporting the hosts a message tried to contact is switched off.\n\n"
                + "Turn “Report the hosts a message tried to contact” back on under "
                + "File › Options › Trust Center to see them. Blocking is unaffected "
                + "either way.");
            return;
        }

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

        // Everything reported, whatever the Trust Center's reading-pane switch says: that one
        // governs what the pane volunteers, and this was asked for by the press.
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
    public async Task<PdfSaveResult> PrintToPdfAsync()
    {
        if (_web is null) return PdfSaveResult.Failed;
        if (TopLevel.GetTopLevel(this) is not { } top) return PdfSaveResult.Failed;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as PDF",
            SuggestedFileName = Suggested(),
            DefaultExtension = "pdf",
        });

        // Told apart from a failure: changing your mind is not an error, and reporting it as one
        // is how "This message could not be written to PDF." used to greet a plain Cancel. The
        // four Save As exports beside this one already say nothing when their picker is dismissed.
        if (file?.TryGetLocalPath() is not { } path) return PdfSaveResult.Cancelled;

        try
        {
            await using var pdf = await _web.PrintToPdfStreamAsync();
            await using var destination = File.Create(path);
            await pdf.CopyToAsync(destination);

            Log.Info("Wrote a message to PDF.");
            return PdfSaveResult.Saved;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write the message to PDF.", ex);
            return PdfSaveResult.Failed;
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
        // What the explanation says, before it is shown. Every Details button on every bar ends
        // here, and a modal a capture cannot answer meant the only read-back was a photograph of
        // a dialog — so "the tracker report named three hosts" and "it said the report is switched
        // off" were the same evidence.
        if (Mailbox.App.Theming.WindowCapture.IsRequested)
        {
            Log.Info($"Harness: reading explains — “{title}”: "
                     + detail.ReplaceLineEndings(" ").Replace("  ", " ", StringComparison.Ordinal).Trim());
        }

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
