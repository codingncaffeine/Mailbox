using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Mailbox.Core.Diagnostics;
using Mailbox.Rendering;
using RenderOptions = Mailbox.Rendering.RenderOptions;
using Mailbox.Security;
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

    private NativeWebView? _web;
    private MimeMessage? _message;
    private string _fallbackText = string.Empty;
    private RenderedMessage? _rendered;
    private RemoteImagePolicy _policy = RemoteImagePolicy.Block;
    private IReadOnlyDictionary<string, string> _inlined =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ReadingPaneBody(ThemeService themes, Func<MailRepository?> mail)
    {
        _themes = themes;
        _mail = mail;

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
    public void Show(MimeMessage? message, string fallbackText)
    {
        _message = message;
        _fallbackText = fallbackText;

        // A decision belongs to the message it was made about. Carrying "show images" from one
        // message to the next would allow a sender the reader never agreed to.
        _policy = RemoteImagePolicy.Block;
        _inlined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Refresh();
    }

    /// <summary>The sender's address, for the safe-sender decision.</summary>
    private string SenderAddress => _message?.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;

    private void Refresh()
    {
        _bars.Children.Clear();

        if (_message is null)
        {
            _rendered = null;
            ShowText(_fallbackText);
            return;
        }

        var trust = SenderTrust.Evaluate(_message, _mail()?.FamiliarDomains() ?? []);
        if (trust.Warnings.Count > 0) _bars.Children.Add(TrustBar(trust));

        var options = new RenderOptions { Style = Style(), Inlined = _inlined };
        _rendered = MessageRenderer.Render(_message, options);

        if (_rendered.HasRemoteContent) _bars.Children.Add(RemoteImageBar(_rendered));

        Load(_rendered.Html);
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
            return new ScrollViewer { Content = _fallback };
        }
    }

    /// <summary>What the engine reports itself as, for the log.</summary>
    private string Describe()
        => _web?.AdapterInfo is { } info
            ? $"{info.Type} ({info.Engine} {info.Version})"
            : "unknown";

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
            _surface.Content = new ScrollViewer { Content = _fallback };
            _web = null;
            ShowText(_message?.TextBody ?? _fallbackText);
        }
    }

    private void ShowText(string text)
    {
        _fallback.Text = text;
        _fallback.FontSize = MessageFontSize;
        Bind(_fallback, TextBlock.ForegroundProperty, "reading.infobar.text.brush");

        if (_web is not null) _surface.Content = new ScrollViewer { Content = _fallback };
    }

    /// <summary>The document's colours and type, resolved from the active theme.</summary>
    private RenderStyle Style() => new(
        _themes.Tokens.GetString(TokenKeys.Reading.Background),
        _themes.Tokens.GetString(TokenKeys.Text.Primary),
        _themes.Tokens.GetString(TokenKeys.Text.Link),
        _themes.Tokens.GetString(TokenKeys.Text.Secondary),
        _themes.Tokens.GetString(TokenKeys.Typography.ContentFamily),
        MessageFontSize);

    // ---- The bars ----------------------------------------------------------------------------

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

        var trust = SenderTrust.Evaluate(_message, _mail()?.FamiliarDomains() ?? []);
        var results = trust.Authentication;

        var detail = results.WasChecked
            ? $"DKIM: {results.Dkim}\nSPF: {results.Spf}\nDMARC: {results.Dmarc}"
              + (results.SigningDomain is { Length: > 0 } d ? $"\nSigned by: {d}" : string.Empty)
            : "The server that delivered this message recorded no authentication results, "
              + "which is ordinary for mail sent directly rather than through a provider.";

        if (trust.Warnings.Count > 0)
        {
            detail += "\n\n" + string.Join("\n\n",
                trust.Warnings.Select(w => $"{w.Headline}\n{w.Detail}"));
        }

        await ExplainAsync("Message authentication", detail);
    }

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
