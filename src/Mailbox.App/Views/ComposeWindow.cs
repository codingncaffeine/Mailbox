using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Rendering;
using Mailbox.Store;
using Mailbox.Theming.Icons;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The new-message window: a title bar, the compose ribbon, and a <see cref="ComposeSurface"/>
/// for everything below them.
/// </summary>
/// <remarks>
/// A thin shell. Everything that composes a message — the address fields, Send, the editor and
/// the sixty-odd command handlers — is <see cref="ComposeSurface"/>, so the reading pane can host
/// the same control as an inline reply strip. This window keeps only what a window owns: the
/// frame, the caption buttons, its own ribbon, the Backstage overlay, and the save-on-close
/// prompt. Its public surface is unchanged, so the shell opens it exactly as before.
/// </remarks>
public sealed class ComposeWindow : Window
{
    private readonly CommandCatalog _catalog;
    private readonly ComposeSurface _surface;
    private readonly RibbonView _ribbon;
    private TextBlock _caption = null!;
    private ContentControl _backstage = null!;

    /// <summary>Where a collapsed ribbon's body floats over the surface, and the body floating there.</summary>
    private Canvas _floatLayer = null!;
    private Control? _floatingRibbon;

    /// <summary>Set once the save-on-close prompt has been answered, so the second close does not re-ask.</summary>
    private bool _closing;

    /// <summary>Re-raised from the surface: a message queued and meant to go under Undo Send's hold.</summary>
    public event EventHandler<QueuedMessageEventArgs>? Queued;

    public ComposeWindow(CommandCatalog catalog, AccountStores? accounts, Mailbox.Contacts.ContactBook? contacts = null)
        : this(catalog, new ComposeSurface(catalog, accounts, contacts))
    {
    }

    /// <summary>
    /// Adopts an existing surface into a window — for popping an inline reply out of the reading
    /// pane. The surface is host-neutral and resolves its owner from the tree, so moving it here
    /// keeps every bit of its state: the recipients, the body, the threading headers of a reply.
    /// </summary>
    internal ComposeWindow(CommandCatalog catalog, ComposeSurface surface)
    {
        _catalog = catalog;
        _surface = surface;

        Title = _surface.Title;
        Width = 1000;
        Height = 760;
        MinWidth = 620;
        MinHeight = 420;
        Icon = new WindowIcon(AssetLoaderIcon());

        // Without this the compositor keeps drawing its own frame, so the window carried two sets
        // of caption buttons and two titles. It also makes the window transparent, which is what
        // lets WindowFrame.Rounded draw the shape.
        WindowFrame.Apply(this);
        FontFamily = (FontFamily)(Application.Current!.FindResource("ui.fontfamily") ?? FontFamily.Default);
        TextRendering.Apply(this);

        _ribbon = new RibbonView(catalog, DefaultRibbonLayouts.Compose)
        {
            CommandEnabled = _surface.IsCommandEnabled,
        };

        // This window's ribbon opens as it was last left, remembered apart from the shell's: the
        // reference keeps a message window's layout separately from the main window's. The
        // harness's pose reaches both windows through one variable.
        RibbonDisplayMemory.Wire(_ribbon, RibbonWindow.Compose, Environment.GetEnvironmentVariable("MAILBOX_RIBBON"));

        // The ribbon is the window's; the surface is what a command acts on. Close any floating
        // group body first — that is a ribbon affordance — then route the command in.
        _ribbon.CommandInvoked += (_, e) =>
        {
            _ribbon.CloseFloatingBody();
            _surface.Invoke(e.Command);
        };
        _ribbon.BackstageRequested += (_, _) => ShowBackstage();

        // The bar's "…" and what it would list, for the harness — a flyout is a separate
        // surface and never appears in a capture.
        OverflowMenu = () => _ribbon.OpenOverflowMenu();

        // "Show tabs only" collapses the ribbon to its strip and floats the body over the
        // surface on a tab click; without a host for that body the tabs did nothing here, and
        // with the chevron gone there was no menu to undo the choice from.
        _ribbon.FloatingBodyChanged += (_, e) => ShowFloatingRibbon(e.Body);
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // The surface says when a command's enabled state may have changed; the ribbon repaints.
        _surface.EnablementChanged += (_, _) => _ribbon.RefreshEnablement();

        // The window's caption follows the surface's title as the subject is typed.
        _surface.TitleChanged += (_, _) =>
        {
            Title = _surface.Title;
            _caption.Text = _surface.Title;
        };

        // The surface asks to be dismissed (sent, discarded, Escape); the window closes.
        _surface.CloseRequested += (_, _) => Close();

        // A queued message has to outlive the window that wrote it, so the shell owns the toast.
        _surface.Queued += (_, e) => Queued?.Invoke(this, e);

        Content = BuildRoot();
    }

    // ----------------------------------------------------------------------------------
    // Forwarders — the window's public surface, unchanged, delegated to the compose surface
    // ----------------------------------------------------------------------------------

    /// <summary>Fills the window from a message pulled back out of the outbox (Undo Send).</summary>
    public void Restore(MimeMessage message) => _surface.Restore(message);

    /// <summary>Opens on a reply or a forward.</summary>
    public void Prefill(ReplyDraft draft, ReplyKind kind) => _surface.Prefill(draft, kind);

    /// <summary>Opens a draft for more writing.</summary>
    public void EditDraft(long messageId, MimeMessage message) => _surface.EditDraft(messageId, message);

    /// <summary>Fills a new message from a mailto: link — Mailbox as the system mail client.</summary>
    public void ComposeFromMailto(Mailbox.Core.Compose.MailtoLink link) => _surface.FillFromMailto(link);

    /// <summary>Starts the message from this account.</summary>
    public void SendFromAccount(string address) => _surface.SendFromAccount(address);

    /// <summary>Selects a ribbon tab by id. Used by the fidelity harness, which cannot click.</summary>
    /// <summary>Opens the bar's "…" and hands back what it holds. Harness only.</summary>
    public Func<IReadOnlyList<string>> OverflowMenu { get; private set; } = () => [];

    public void SelectTab(string tabId)
    {
        // "file" is not a tab with a body — it opens the Backstage over the window.
        if (string.Equals(tabId, "file", StringComparison.OrdinalIgnoreCase))
        {
            ShowBackstage();
            return;
        }

        _ribbon.ActiveTabId = tabId;
    }

    /// <summary>Puts text in the body, so a capture can show the ribbon in its enabled state.</summary>
    public void PoseBodyText(string text) => _surface.PoseBodyText(text);

    /// <summary>A body with one of everything the serializer handles, for the harness to send.</summary>
    public void PoseRichBody() => _surface.PoseRichBody();

    /// <summary>Presses Send, for the harness.</summary>
    public void PressSend() => _surface.PressSend();

    /// <summary>Poses the optional address fields, so a capture can show them.</summary>
    public void ShowOptionalFields() => _surface.ShowOptionalFields();

    /// <summary>Fills the header, so a capture can be measured against the reference.</summary>
    public void PoseHeader(string to, string cc, string subject) => _surface.PoseHeader(to, cc, subject);

    /// <summary>Types into the To line as a person would, for the harness.</summary>
    public void PoseTyping(string text) => _surface.PoseTyping(text);

    /// <summary>What the Auto-Complete List last offered on the To line, for the harness.</summary>
    public (bool IsOpen, int Offered) ToLineCompletion => _surface.ToLineCompletion;

    /// <summary>What the To line is offering, one line each, for the harness.</summary>
    public IReadOnlyList<string> ToLineSuggestions => _surface.ToLineSuggestions;

    // ----------------------------------------------------------------------------------
    // Window chrome
    // ----------------------------------------------------------------------------------

    private Control BuildRoot()
    {
        // The Backstage takes the whole window over, so the content sits under an overlay
        // rather than beside it. File does the same thing here as in the shell.
        var layered = new Grid();
        var root = new DockPanel { LastChildFill = true };

        var title = BuildTitleBar();
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        // The reference's compose ribbon panel starts 8px in from the window's content edge. The
        // shell's needs no such inset because the app rail provides it; this window has no rail.
        var ribbonHost = new Border
        {
            Child = _ribbon,
            ZIndex = 2,
            Padding = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(ribbonHost, Dock.Top);
        root.Children.Add(ribbonHost);

        // The surface fills the rest, with a layer over it for a collapsed ribbon's body: the
        // body floats over the content, as it does in the shell, rather than pushing it down.
        var workspace = new Grid();
        workspace.Children.Add(_surface);
        _floatLayer = new Canvas { IsHitTestVisible = true, ZIndex = 1 };
        workspace.Children.Add(_floatLayer);
        root.Children.Add(workspace);

        layered.Children.Add(root);

        _backstage = new ContentControl { ZIndex = 10, IsVisible = false };
        layered.Children.Add(_backstage);

        return WindowFrame.Rounded(layered);
    }

    /// <summary>
    /// The title bar, in the reference's order: application icon, the Quick Access Toolbar and
    /// its customize chevron, then the title — <em>left aligned</em>, not centred — and the
    /// caption buttons at the far right.
    /// </summary>
    private Control BuildTitleBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var leading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        leading.Children.Add(new Image
        {
            Source = new Avalonia.Media.Imaging.Bitmap(AssetLoaderIcon()),
            Width = 16,
            Height = 16,
            Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        foreach (var id in DefaultRibbonLayouts.Compose.QuickAccess)
        {
            if (!_catalog.TryGet(id, out var command)) continue;
            leading.Children.Add(QuickAccessButton(command));
        }

        // The customize chevron closes the toolbar, as it does in the shell.
        var chevron = new Button
        {
            Content = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
                FontFamily = IconFont.Family,
                FontSize = 11,
                [!TextBlock.ForegroundProperty] =
                    new DynamicResourceExtension("titlebar.foreground.brush"),
            },
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderThickness = default,
        };
        ToolTip.SetTip(chevron, "Customize Quick Access Toolbar");
        leading.Children.Add(chevron);

        _caption = new TextBlock
        {
            Text = Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Bind(_caption, TextBlock.ForegroundProperty, "titlebar.foreground.brush");
        Bind(_caption, TextBlock.FontSizeProperty, "type.ui.size.value");
        leading.Children.Add(_caption);

        Grid.SetColumn(leading, 0);
        grid.Children.Add(leading);

        var buttons = new CaptionButtons(this) { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        var host = new Border { Child = grid, Height = 40 };
        Bind(host, Border.BackgroundProperty, "titlebar.background.brush");
        WindowFrame.Drags(this, host);
        return host;
    }

    private Button QuickAccessButton(MailboxCommand command)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(command.Icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "titlebar.foreground.brush");

        var button = new Button
        {
            Content = glyph,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderThickness = default,
        };
        ToolTip.SetTip(button, command.Label);
        button.Click += (_, _) => _surface.Invoke(command.Id);
        return button;
    }

    /// <summary>
    /// Hosts the body a collapsed ribbon floats on a tab click, or takes it down when null.
    /// In the layer over the surface rather than a popup, for the shell's reasons: it clips and
    /// z-orders with the window, and it appears in a capture.
    /// </summary>
    private void ShowFloatingRibbon(Control? body)
    {
        if (_floatingRibbon is not null)
        {
            _floatLayer.Children.Remove(_floatingRibbon);
            _floatingRibbon = null;
        }

        if (body is null) return;

        body.Width = _floatLayer.Bounds.Width > 0 ? _floatLayer.Bounds.Width : Width;
        Canvas.SetLeft(body, 0);
        Canvas.SetTop(body, 0);

        _floatLayer.Children.Add(body);
        _floatingRibbon = body;
    }

    /// <summary>A click anywhere but the ribbon or its floating body rolls the body up.</summary>
    private void OnWindowPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_floatingRibbon is null || e.Source is not Visual source) return;
        if (IsWithin(source, _floatingRibbon) || IsWithin(source, _ribbon)) return;

        _ribbon.CloseFloatingBody();
    }

    private static bool IsWithin(Visual node, Visual? ancestor)
        => ancestor is not null
           && (ReferenceEquals(node, ancestor) || node.GetVisualAncestors().Contains(ancestor));

    /// <summary>
    /// Opens the Backstage over this window. Same pages and behaviour as the shell's —
    /// <see cref="BackstageActions"/> is the one implementation — it just takes over this window.
    /// </summary>
    private void ShowBackstage()
    {
        var host = new BackstageHost(this, _ => { }, () => { }, CloseBackstage);

        var backstage = new BackstageView();
        backstage.OptionsRequested += async (_, _) =>
            await new OptionsWindow(App.Themes).ShowDialog<bool>(this);
        backstage.AddAccountRequested += async (_, _) =>
            await BackstageActions.AddAccountAsync(host);
        backstage.ActionRequested += async (_, action) =>
            await BackstageActions.RunAsync(host, action);
        backstage.CloseRequested += (_, _) => CloseBackstage();

        // Exit from a message window quits the application, as the reference does. Every window
        // is asked to close on the way, so a message with unsaved content still gets its prompt
        // and can hold the exit up.
        backstage.ExitRequested += (_, _) =>
        {
            CloseBackstage();
            (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };

        _backstage.Content = backstage;
        _backstage.IsVisible = true;
    }

    private void CloseBackstage()
    {
        _backstage.IsVisible = false;
        _backstage.Content = null;
    }

    // ----------------------------------------------------------------------------------
    // Closing
    // ----------------------------------------------------------------------------------

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // Nothing typed, or already dealt with: close without asking. Otherwise offer to keep
        // it — which is the affordance for the X and for Discard alike, both of which arrive
        // here through Close().
        if (_closing || _surface.IsSent || !_surface.HasContent())
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        var keep = await Confirm.AskAsync(this, "Save this message?",
            "This message has not been sent. Save it to Drafts?", "Save", destructive: false);

        if (keep) _surface.SaveDraft();

        // Answered either way: let the next close through, whether or not a draft was kept.
        _closing = true;
        Close();
    }

    /// <summary>
    /// The assembly is named <c>mailbox</c>, not <c>Mailbox.App</c>, and an avares URI keys off
    /// the assembly name rather than the namespace.
    /// </summary>
    private static Stream AssetLoaderIcon()
        => Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://mailbox/Assets/Icons/mailbox-256.png"));

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
