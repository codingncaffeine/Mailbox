using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mailbox.App.Theming;
using Mailbox.Controls.Ribbon;
using Mailbox.Editor;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Protocols;
using Mailbox.Store;
using Mailbox.Theming.Icons;
using MimeKit;
using MimeKit.Utils;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;

namespace Mailbox.App.Views;

/// <summary>
/// The new message window: its own ribbon, its own address fields, its own Send.
/// </summary>
/// <remarks>
/// A separate window with its own tab collection rather than a pane in the shell, because that
/// is what the reference does and because the ribbon model was built to swap the whole tab set
/// per host.
/// <para>
/// The body is a real document. §7.3's survey found a GPL-3-compatible editor that carries the
/// document model that section planned to build, so what is in-house is the serializer — and
/// that is the half mail fidelity rests on. Send writes both an HTML body, through
/// <see cref="EmailHtml"/>, and the plain text alternative, off the same document so the two
/// cannot disagree.
/// <para>
/// The editor rather than its packaged view: the view brings a toolbar and a status bar of its
/// own, and this window already has a ribbon. What each ribbon button does today is recorded
/// once in <see cref="ComposeAvailability"/> and read from there, so a button still waiting on
/// something says what, rather than "not wired yet".
/// </para>
/// </remarks>
public sealed class ComposeWindow : Window
{
    /// <summary>
    /// What new mail is written in. The reference's own default, and the one name §6 cares most
    /// about getting onto the wire correctly.
    /// </summary>
    private const string ComposeFontFamily = "Calibri";

    private const double ComposeFontPoints = 11;

    /// <summary>The document measures in device-independent pixels; mail talks in points.</summary>
    private const double PointsPerPixel = 0.75;

    private const string PlainTextNotice =
        "This message is being composed as HTML. Plain-text-only composing is Phase 6.";

    private readonly CommandCatalog _catalog;
    private readonly AccountStores? _accounts;
    private readonly RibbonView _ribbon;

    private readonly TextBox _to = Field();
    private readonly TextBox _cc = Field();
    private readonly TextBox _bcc = Field();
    private readonly TextBox _subject = Field();
    private readonly RichEditor _body;
    /// <summary>
    /// The sending account, shown as plain text beside the From button.
    /// </summary>
    /// <remarks>
    /// Not a combo. The reference puts a <c>From ⌄</c> button in the label column and the
    /// address as text in the field column, which is why a full-width picker looked wrong: the
    /// choosing happens in the button's menu, and the field only reports.
    /// </remarks>
    private readonly TextBlock _fromAddress = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
    };

    private string? _sendingAddress;
    private readonly TextBlock _status = new();
    private readonly TextBlock _attachmentStrip = new() { TextWrapping = TextWrapping.Wrap };

    // Each address row is one control, so showing or hiding it is one visibility change on one
    // thing rather than two halves that have to agree.
    private Control _bccRow = null!;
    private Control _fromRow = null!;
    private Border _attachmentRow = null!;

    private readonly List<IStorageFile> _attachments = [];

    private MessageImportance _importance = MessageImportance.Normal;
    private bool _wantsReadReceipt;
    private bool _wantsDeliveryReceipt;
    private DateTimeOffset? _notBefore;
    private string? _replyTo;
    private bool _sent;

    public ComposeWindow(CommandCatalog catalog, AccountStores? accounts)
    {
        _catalog = catalog;
        _accounts = accounts;

        Title = "Untitled - Message (HTML)";
        Width = 1000;
        Height = 760;
        MinWidth = 620;
        MinHeight = 420;
        Icon = new WindowIcon(AssetLoaderIcon());

        // Without this the compositor keeps drawing its own frame, so the window carried two
        // sets of caption buttons and two titles — the system's centred above ours. It also
        // makes the window transparent, which is what lets WindowFrame.Rounded draw the shape.
        WindowFrame.Apply(this);
        FontFamily = (FontFamily)(Application.Current!.FindResource("ui.fontfamily") ?? FontFamily.Default);

        TextRendering.Apply(this);

        // The editor itself rather than its own view: that wrapper brings a toolbar and a
        // status bar of its own, and this window already has a ribbon. Two bars disagreeing
        // about the same document is the mistake the compose window made once already with
        // its caption buttons.
        _body = new RichEditor
        {
            DefaultFontFamily = new FontFamily(App.Fonts.Resolve(ComposeFontFamily).Rendered),
            DefaultFontSize = ComposeFontPoints / PointsPerPixel,
            AllowRemoteImagesOnPaste = false,
            AllowLocalFileImages = false,
            AutoLinkOnType = true,
        };

        // The body is a document page, not a pane — white even in Dark Gray, where the reading
        // pane is not. The page is painted by the border around it; these are the marks drawn
        // on top of it, which have to come from tokens like everything else.
        Bind(_body, RichEditor.SelectionBrushProperty, "state.selected.brush");
        Bind(_body, RichEditor.CaretBrushProperty, "compose.body.text.brush");

        _ribbon = new RibbonView(catalog, DefaultRibbonLayouts.Compose)
        {
            CommandEnabled = IsUsable,
        };

        // The left of the Message tab is pale on an empty message and darkens as soon as there
        // is something to format. That is enablement, and it has to track every keystroke.
        _body.TextChanged += (_, _) => _ribbon.RefreshEnablement();

        _ribbon.CommandInvoked += (_, e) => Run(e.Command);
        _ribbon.BackstageRequested += (_, _) => ShowBackstage();

        Content = BuildRoot();
        UpdateStatus();

        _to.AttachedToVisualTree += (_, _) => _to.Focus();
    }

    /// <summary>
    /// Commands that need something in the body before they mean anything — either because they
    /// format text that is not there, or because they insert into a document that is empty.
    /// </summary>
    private static readonly HashSet<CommandId> InsertsIntoBody =
    [
        ComposeCommands.Table.Id, ComposeCommands.Pictures.Id, ComposeCommands.StockImages.Id,
        ComposeCommands.OnlinePictures.Id, ComposeCommands.Shapes.Id, ComposeCommands.Icons.Id,
        ComposeCommands.Models3D.Id, ComposeCommands.SmartArt.Id, ComposeCommands.Chart.Id,
        ComposeCommands.Equation.Id, ComposeCommands.Symbol.Id, ComposeCommands.Link.Id,
        ComposeCommands.Styles.Id, ComposeCommands.ChangeStyles.Id, ComposeCommands.PageColor.Id,
    ];

    /// <summary>
    /// Whether a command is usable right now.
    /// </summary>
    /// <remarks>
    /// Paste is the exception among the formatting commands: there is always somewhere to paste
    /// to, even in an empty message. Everything else in that run needs text to act on.
    /// </remarks>
    private bool IsUsable(CommandId id)
    {
        if (id == ComposeCommands.Paste.Id) return true;
        if (!_catalog.TryGet(id, out var command)) return true;
        if (!command.NeutralIcon && !InsertsIntoBody.Contains(id)) return true;

        return !string.IsNullOrEmpty(_body.GetPlainText());
    }

    /// <summary>Selects a ribbon tab by id. Used by the fidelity harness, which cannot click.</summary>
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
    public void PoseBodyText(string text)
    {
        _body.Clear();
        _body.InsertText(text);
        _ribbon.RefreshEnablement();
    }

    /// <summary>Poses the optional address fields, so a capture can show them.</summary>
    public void ShowOptionalFields()
    {
        Toggle(_bccRow);
        Toggle(_fromRow);
    }

    // ----------------------------------------------------------------------------------
    // Composition
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Title bar, ribbon and address header stacked from the top, status bar pinned to the
    /// bottom, and the body taking what is left.
    /// </summary>
    /// <remarks>
    /// A DockPanel rather than a Grid of auto rows, deliberately. Showing Bcc or From after the
    /// first layout pass did not grow an auto row — the field appeared and Subject was clipped
    /// off the bottom — and invalidating the grid and its border did not fix it. Docking sizes
    /// each band to its content every pass, so a field that appears simply makes the band taller.
    /// </remarks>
    private Control BuildRoot()
    {
        // The Backstage takes the whole window over, so the content sits under an overlay
        // rather than beside it. File does the same thing here as in the shell — it just
        // happens in this window.
        var layered = new Grid();
        var root = new DockPanel { LastChildFill = true };

        var title = BuildTitleBar();
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        // The reference's compose ribbon panel starts 8px in from the window's content edge.
        // The shell's needs no such inset because the app rail provides it; this window has no
        // rail, so without it the whole bar sits 8px left of where the capture puts it.
        var ribbonHost = new Border
        {
            Child = _ribbon,
            ZIndex = 2,
            Padding = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(ribbonHost, Dock.Top);
        root.Children.Add(ribbonHost);

        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var info = BuildInfoBar();
        DockPanel.SetDock(info, Dock.Top);
        root.Children.Add(info);

        // The body is inset 5px left and right, and the chrome shows through as a gutter —
        // measured: the white starts at x=18 in a window whose content starts at 13, and ends
        // at 1647 with five more before the frame. It runs to the bottom; there is no status
        // bar in the reference, and the format the window is in is already in its title.
        var body = new Border { Child = _body, Padding = new Thickness(12, 8) };
        Bind(body, Border.BackgroundProperty, "compose.body.background.brush");

        var bodyHost = new Border
        {
            Child = body,
            Padding = new Thickness(BodyGutter, 0, BodyGutter, 0),
        };
        Bind(bodyHost, Border.BackgroundProperty, "compose.header.background.brush");
        root.Children.Add(bodyHost);

        layered.Children.Add(root);

        _backstage = new ContentControl { ZIndex = 10, IsVisible = false };
        layered.Children.Add(_backstage);

        return WindowFrame.Rounded(layered);
    }

    private ContentControl _backstage = null!;

    /// <summary>
    /// Opens the Backstage over this window. Same pages and same behaviour as the shell's —
    /// <see cref="BackstageActions"/> is the one implementation — it simply takes over the
    /// compose window rather than the main one.
    /// </summary>
    private void ShowBackstage()
    {
        var host = new BackstageHost(this, Report, PopulateAccounts, CloseBackstage);

        var backstage = new BackstageView();
        backstage.OptionsRequested += async (_, _) =>
            await new OptionsWindow(App.Themes).ShowDialog<bool>(this);
        backstage.AddAccountRequested += async (_, _) =>
            await BackstageActions.AddAccountAsync(host);
        backstage.ActionRequested += async (_, action) =>
            await BackstageActions.RunAsync(host, action);
        backstage.CloseRequested += (_, _) => CloseBackstage();

        _backstage.Content = backstage;
        _backstage.IsVisible = true;
    }

    private void CloseBackstage()
    {
        _backstage.IsVisible = false;
        _backstage.Content = null;
    }

    /// <summary>
    /// The title bar, in the reference's order: application icon, the Quick Access Toolbar and
    /// its customize chevron, then the title — <em>left aligned, immediately after them</em>,
    /// not centred — and the caption buttons at the far right.
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

        // The application icon belongs on this bar. With the system frame still on it was drawn
        // by the compositor on a strip of its own above ours, which is why it looked like it was
        // sitting on the wrong bar.
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

        var caption = new TextBlock
        {
            Text = Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Bind(caption, TextBlock.ForegroundProperty, "titlebar.foreground.brush");
        Bind(caption, TextBlock.FontSizeProperty, "type.ui.size.value");
        leading.Children.Add(caption);

        Grid.SetColumn(leading, 0);
        grid.Children.Add(leading);

        var buttons = new CaptionButtons(this) { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        var host = new Border { Child = grid, Height = 40 };
        Bind(host, Border.BackgroundProperty, "titlebar.background.brush");
        WindowFrame.Drags(this, host);

        // Keeps the caption text following the window title as the subject is typed.
        _subject.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            Title = string.IsNullOrWhiteSpace(_subject.Text)
                ? "Untitled - Message (HTML)"
                : $"{_subject.Text} - Message (HTML)";
            caption.Text = Title;
        };

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
        button.Click += (_, _) => Run(command.Id);
        return button;
    }

    /// <summary>
    /// Send beside the address fields, as the reference has it: the button that finishes the
    /// job is not on the ribbon, because it is not a formatting choice.
    /// </summary>
    private Control BuildHeader()
    {
        // A stack, not a grid of auto rows. A row becoming visible after the first layout pass
        // has to make the header taller, and an auto row did not do that: Bcc appeared, the
        // header kept its old height, and Subject was painted over by the body below. A stack's
        // height is the sum of what is visible, recomputed every pass.
        // No spacing: each row already carries its own gap as a bottom margin, and adding
        // both put the rows on a 42px pitch where the reference measures 40.
        var rows = new StackPanel();

        _fromRow = AddressRow(rows, "From", _fromAddress, opensAddressBook: false, picksAccount: true);
        AddressRow(rows, "To", _to);
        AddressRow(rows, "Cc", _cc);
        _bccRow = AddressRow(rows, "Bcc", _bcc);
        AddressRow(rows, "Subject", _subject, opensAddressBook: false);

        _attachmentStrip.Text = string.Empty;
        Bind(_attachmentStrip, TextBlock.ForegroundProperty, "text.secondary.brush");
        _attachmentRow = new Border
        {
            Child = _attachmentStrip,
            Padding = new Thickness(76, 6, 0, 0),
            IsVisible = false,
        };
        rows.Children.Add(_attachmentRow);

        var send = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Children =
                {
                    IconBlock("send", 22),
                    new TextBlock { Text = "Send", HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
            Width = SendWidth,
            Height = SendHeight,
            Margin = new Thickness(SendInset, 0, LabelInset, 0),
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1),
        };
        Bind(send, TemplatedControl.BackgroundProperty, "surface.raised.brush");
        Bind(send, TemplatedControl.BorderBrushProperty, "border.strong.brush");
        ToolTip.SetTip(send, "Send this message  (Ctrl+Enter)");
        send.Click += (_, _) => Run(ComposeCommands.Send.Id);

        // Bcc and From are off until asked for, which is what the Options tab's two toggles do.
        _bccRow.IsVisible = false;
        _fromRow.IsVisible = false;
        PopulateAccounts();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, HeaderTopInset, 14, HeaderTopInset),
        };
        Grid.SetColumn(send, 0);
        grid.Children.Add(send);
        Grid.SetColumn(rows, 1);
        grid.Children.Add(rows);

        var host = new Border { Child = grid };
        Bind(host, Border.BackgroundProperty, "compose.header.background.brush");
        return host;
    }

    // Measured off the compose capture, all of it. Horizontally: the header's background runs
    // from x=13, Send is a filled button at x=34–93, the From/To/Cc buttons start at x=110, and
    // the field rules begin at x=202. Vertically: the header background starts at y=136 and the
    // first button at y=161, so the block is inset 25; buttons are 31 tall on a 40 pitch; and
    // the rule under a field sits at 197 — five below its button, not level with it, which is
    // why the field is taller than the button beside it. Send runs 161–236.
    private const double SendInset = 21;
    private const double SendWidth = 58;
    private const double SendHeight = 75;
    private const double LabelInset = 17;
    private const double LabelWidth = 80;
    private const double FieldInset = 12;
    private const double HeaderTopInset = 25;
    private const double RowPitch = 40;
    private const double RowHeight = 31;
    private const double FieldHeight = 36;

    /// <summary>
    /// One address row — its label and its field as a single control, so the pair is shown and
    /// hidden together and cannot get out of step.
    /// </summary>
    private Control AddressRow(
        StackPanel rows, string label, Control field,
        bool opensAddressBook = true, bool picksAccount = false)
    {
        // The row is the pitch; the button and the field are different heights within it, which
        // is what puts the rule below the button rather than level with it.
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Height = RowPitch,
        };

        Control caption;
        if (picksAccount)
        {
            // From carries a chevron and opens the account list, rather than acting on the
            // address book like the recipient rows.
            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock
                        {
                            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
                            FontFamily = IconFont.Family,
                            FontSize = 9,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                Width = LabelWidth,
                Height = RowHeight,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                Flyout = AccountMenu(),
            };
            Bind(button, TemplatedControl.BackgroundProperty, "surface.raised.brush");
            Bind(button, TemplatedControl.BorderBrushProperty, "border.strong.brush");
            ToolTip.SetTip(button, "Send this message from a different account");
            caption = button;
        }
        else if (opensAddressBook)
        {
            var button = new Button
            {
                Content = label,
                Width = LabelWidth,
                Height = RowHeight,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
            };
            Bind(button, TemplatedControl.BackgroundProperty, "surface.raised.brush");
            Bind(button, TemplatedControl.BorderBrushProperty, "border.strong.brush");
            ToolTip.SetTip(button, $"Choose {label} recipients from the address book");
            button.Click += (_, _) => Run(MailCommands.AddressBook.Id);
            caption = button;
        }
        else
        {
            // Subject is a plain label rather than a button: it opens nothing.
            var text = new TextBlock
            {
                Text = label,
                Width = LabelWidth,
                Height = RowHeight,
                VerticalAlignment = VerticalAlignment.Top,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            };
            Bind(text, TextBlock.ForegroundProperty, "text.secondary.brush");
            caption = text;
        }

        Grid.SetColumn(caption, 0);
        row.Children.Add(caption);

        // The fields carry no box. The reference draws a single rule under each one and nothing
        // else, so the address block reads as writing lines rather than as a form.
        // The rule belongs to the row, not to whatever control sits in it. From shows an
        // address as text rather than an input, and it is still underlined in the reference —
        // hanging the rule off the TextBox left that one row without one.
        if (field is TextBox box)
        {
            box.BorderThickness = default;
            box.CornerRadius = default;
            box.Background = Brushes.Transparent;
            box.Padding = default;
            Bind(box, TemplatedControl.ForegroundProperty, "text.primary.brush");
        }

        if (field is TextBlock plain) Bind(plain, TextBlock.ForegroundProperty, "text.primary.brush");

        var underlined = new Border
        {
            Child = field,
            Height = FieldHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(FieldInset, 0, 0, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 4),
        };
        Bind(underlined, Border.BorderBrushProperty, "compose.field.rule.brush");

        Grid.SetColumn(underlined, 1);
        row.Children.Add(underlined);

        rows.Children.Add(row);
        return row;
    }

    /// <summary>
    /// The strip that carries a message, between the header and the body.
    /// </summary>
    /// <remarks>
    /// Where the reference puts an InfoBar, and hidden until there is something to say — the
    /// compose window has no status bar, so a permanent strip would be a band the reference
    /// does not have. It carries two things: what a button that cannot act yet is waiting for,
    /// and the state worth stating, which is importance and a delayed send.
    /// </remarks>
    private Control BuildInfoBar()
    {
        Bind(_status, TextBlock.ForegroundProperty, "reading.infobar.text.brush");
        Bind(_status, TextBlock.FontSizeProperty, "type.ui.size.value");
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;

        _infoBar = new Border
        {
            Child = _status,
            Padding = new Thickness(BodyGutter + 12, 7),
            IsVisible = false,
        };
        Bind(_infoBar, Border.BackgroundProperty, "reading.infobar.background.brush");
        return _infoBar;
    }

    private Border _infoBar = null!;

    /// <summary>The body's inset from the window edge, measured at 5px either side.</summary>
    private const double BodyGutter = 5;

    private static TextBox Field() => new() { MinWidth = 200 };

    private static Control IconBlock(string icon, double size)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 24),
            FontFamily = IconFont.Family,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        glyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("accent.rest.brush");
        return glyph;
    }

    /// <summary>
    /// The assembly is named <c>mailbox</c>, not <c>Mailbox.App</c>, and an avares URI keys off
    /// the assembly name rather than the namespace.
    /// </summary>
    private static Stream AssetLoaderIcon()
        => Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://mailbox/Assets/Icons/mailbox-256.png"));

    private void PopulateAccounts()
    {
        _sendingAddress = _accounts?.Default?.Account.Address;
        _fromAddress.Text = _sendingAddress ?? string.Empty;
    }

    /// <summary>The From button's menu: one entry per account, ticked for the sending one.</summary>
    private MenuFlyout AccountMenu()
    {
        var flyout = new MenuFlyout();
        var accounts = _accounts?.All ?? [];

        if (accounts.Count == 0)
        {
            flyout.ItemsSource = new[]
            {
                new MenuItem { Header = "No account is set up yet", IsEnabled = false },
            };
            return flyout;
        }

        flyout.ItemsSource = accounts
            .Select(account =>
            {
                var address = account.Account.Address;
                var item = new MenuItem { Header = address };
                item.Click += (_, _) =>
                {
                    _sendingAddress = address;
                    _fromAddress.Text = address;
                };
                return item;
            })
            .ToList();

        return flyout;
    }

    // ----------------------------------------------------------------------------------
    // Commands
    // ----------------------------------------------------------------------------------

    private void Run(CommandId id)
    {
        _ribbon.CloseFloatingBody();

        if (!_catalog.TryGet(id, out var command))
        {
            Report($"No command with id '{id}'.");
            return;
        }

        // Everything that works is handled here. Everything else falls through to the status
        // line, which says what it is waiting for rather than that it is not wired.
        if (Handle(id)) return;

        var status = ComposeAvailability.For(id);
        Report(status is null
            ? $"{command.Label} — no recorded status. That is a bug in ComposeAvailability."
            : $"{command.Label} — {status.Note}");
    }

    private bool Handle(CommandId id)
    {
        if (id == ComposeCommands.Send.Id) { _ = SendAsync(); return true; }
        if (id == ComposeCommands.SaveDraft.Id) { SaveDraft(); return true; }
        if (id == ComposeCommands.Discard.Id) { Close(); return true; }

        // Paste goes through the editor, which reads the clipboard's HTML flavour and keeps
        // the formatting. Cut, Copy and Select All are the editor's own key handling and it
        // exposes no method for them, so the buttons say where they are rather than pretending.
        if (id == ComposeCommands.Paste.Id) { _ = _body.PasteFromClipboardAsync(); return true; }

        if (id == ComposeCommands.Cut.Id || id == ComposeCommands.Copy.Id
            || id == ComposeCommands.SelectAll.Id)
        {
            _body.Focus();
            Report("Cut, Copy and Select All are on Ctrl+X, Ctrl+C and Ctrl+A.");
            return true;
        }

        if (id == ComposeCommands.ShowBcc.Id) { Toggle(_bccRow); return true; }
        if (id == ComposeCommands.ShowFrom.Id) { Toggle(_fromRow); return true; }

        if (id == ComposeCommands.HighImportance.Id) { SetImportance(MessageImportance.High); return true; }
        if (id == ComposeCommands.LowImportance.Id) { SetImportance(MessageImportance.Low); return true; }

        if (id == ComposeCommands.DeliveryReceipt.Id)
        {
            _wantsDeliveryReceipt = !_wantsDeliveryReceipt;
            Report($"Delivery receipt {(_wantsDeliveryReceipt ? "requested" : "not requested")}.");
            return true;
        }

        if (id == ComposeCommands.ReadReceipt.Id)
        {
            _wantsReadReceipt = !_wantsReadReceipt;
            Report($"Read receipt {(_wantsReadReceipt ? "requested" : "not requested")}.");
            return true;
        }

        if (id == ComposeCommands.WordCount.Id) { _ = ShowWordCountAsync(); return true; }
        if (id == ComposeCommands.Zoom.Id) { StepZoom(); return true; }
        if (id == ComposeCommands.AttachFile.Id) { _ = AttachAsync(); return true; }
        if (id == ComposeCommands.CheckNames.Id) { CheckNames(); return true; }
        if (id == ComposeCommands.FormatPlainText.Id)
        {
            Report(PlainTextNotice);
            return true;
        }

        if (id == ComposeCommands.Find.Id || id == ComposeCommands.Replace.Id)
        {
            _ = FindAsync(replace: id == ComposeCommands.Replace.Id);
            return true;
        }

        if (id == ComposeCommands.Symbol.Id) { _ = InsertSymbolAsync(); return true; }
        if (id == ComposeCommands.Link.Id) { _ = InsertLinkAsync(); return true; }
        if (id == ComposeCommands.DelayDelivery.Id) { _ = DelayAsync(); return true; }
        if (id == ComposeCommands.DirectRepliesTo.Id) { _ = DirectRepliesAsync(); return true; }

        return false;
    }

    /// <summary>
    /// Shows or hides an address row, and settles the layout before returning.
    /// </summary>
    /// <remarks>
    /// The forced pass is load-bearing. Making a row visible invalidates measure, but the band
    /// it sits in keeps its old height until a layout pass runs — and the header is docked, so
    /// the body below simply paints over whatever overflows. Bcc appeared and Subject vanished
    /// underneath it.
    /// </remarks>
    private void Toggle(Control row)
    {
        row.IsVisible = !row.IsVisible;
        UpdateLayout();
    }

    private void SetImportance(MessageImportance wanted)
    {
        _importance = _importance == wanted ? MessageImportance.Normal : wanted;
        UpdateStatus();
    }

    private void StepZoom()
    {
        var next = _body.DefaultFontSize + 2;
        _body.DefaultFontSize = next > 28 ? 12 : next;
        Report($"Zoom {_body.DefaultFontSize * PointsPerPixel:0}pt.");
    }

    private void CheckNames()
    {
        var bad = new List<string>();

        foreach (var (label, box) in new[] { ("To", _to), ("Cc", _cc), ("Bcc", _bcc) })
        {
            foreach (var entry in Split(box.Text))
            {
                if (!MailboxAddress.TryParse(entry, out _)) bad.Add($"{label}: {entry}");
            }
        }

        Report(bad.Count == 0
            ? "Every address parses. Resolving a bare name against contacts is Phase 12."
            : "Could not read: " + string.Join("; ", bad));
    }

    private static IEnumerable<string> Split(string? value)
        => (value ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Restates whatever is worth stating about the message, or hides the bar when nothing is.
    /// </summary>
    /// <remarks>
    /// The format is not among them: the title bar already reads "Message (HTML)", which is
    /// exactly where the reference says it.
    /// </remarks>
    private void UpdateStatus()
    {
        var bits = new List<string>();

        if (_importance != MessageImportance.Normal)
        {
            bits.Add($"This message will be sent with {_importance} importance.");
        }

        if (_notBefore is { } when)
        {
            bits.Add($"It will not be delivered before {when.LocalDateTime:g}.");
        }

        if (bits.Count == 0)
        {
            _status.Text = string.Empty;
            _infoBar.IsVisible = false;
            return;
        }

        Report(string.Join("  ", bits));
    }

    private void Report(string message)
    {
        _status.Text = message;
        _infoBar.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    // ----------------------------------------------------------------------------------
    // Dialogs and pickers
    // ----------------------------------------------------------------------------------

    private async Task AttachAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true,
        });

        if (picked.Count == 0) return;

        _attachments.AddRange(picked);
        _attachmentStrip.Text = "Attached: " +
            string.Join(", ", _attachments.Select(f => f.Name));
        _attachmentRow.IsVisible = true;
        UpdateStatus();
    }

    private async Task ShowWordCountAsync()
    {
        var text = _body.GetPlainText();
        var words = text.Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        await Message("Word Count",
            $"Words: {words}\n" +
            $"Characters (with spaces): {text.Length}\n" +
            $"Characters (no spaces): {text.Count(c => !char.IsWhiteSpace(c))}\n" +
            $"Paragraphs: {paragraphs}");
    }

    private async Task FindAsync(bool replace)
    {
        var needle = await Prompt(replace ? "Replace" : "Find", "Find what:");
        if (string.IsNullOrEmpty(needle)) return;

        // The editor's own, which searches the document rather than a flattened copy of it —
        // so a match spanning two differently-formatted runs is still a match, and replacing
        // one keeps the formatting around it.
        if (!replace)
        {
            _body.Focus();

            Report(_body.FindNext(needle, matchCase: false)
                ? $"Found '{needle}'."
                : $"'{needle}' is not in the message.");

            return;
        }

        var with = await Prompt("Replace", "Replace with:") ?? string.Empty;
        var replaced = _body.ReplaceAll(needle, with, matchCase: false);

        Report(replaced == 0
            ? $"'{needle}' is not in the message."
            : $"Replaced {replaced}.");
    }

    private async Task InsertSymbolAsync()
    {
        var symbol = await Prompt("Symbol", "Character to insert:");
        if (string.IsNullOrEmpty(symbol)) return;

        Insert(symbol);
    }

    private async Task InsertLinkAsync()
    {
        var address = await Prompt("Link", "Address:");
        if (string.IsNullOrWhiteSpace(address)) return;

        // A real one, now that there is a document to put it in.
        var escaped = address.Trim()
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

        _body.InsertHtml($"<a href=\"{escaped}\">{escaped}</a>");
        _body.Focus();
    }

    private void Insert(string text)
    {
        _body.InsertText(text);
        _body.Focus();
    }

    private async Task DelayAsync()
    {
        var entered = await Prompt("Delay Delivery",
            "Do not deliver before (yyyy-MM-dd HH:mm), or blank to clear:");

        if (entered is null) return;

        if (string.IsNullOrWhiteSpace(entered))
        {
            _notBefore = null;
            UpdateStatus();
            return;
        }

        if (!DateTime.TryParse(entered, CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal, out var when))
        {
            Report($"Could not read '{entered}' as a date and time.");
            return;
        }

        _notBefore = new DateTimeOffset(when);
        UpdateStatus();
    }

    private async Task DirectRepliesAsync()
    {
        var entered = await Prompt("Direct Replies To", "Send replies to:");
        if (entered is null) return;

        if (string.IsNullOrWhiteSpace(entered))
        {
            _replyTo = null;
            Report("Replies go to the sending account.");
            return;
        }

        if (!MailboxAddress.TryParse(entered, out _))
        {
            Report($"Could not read '{entered}' as an address.");
            return;
        }

        _replyTo = entered;
        Report($"Replies will go to {entered}.");
    }

    // ----------------------------------------------------------------------------------
    // Send and save
    // ----------------------------------------------------------------------------------

    private OpenAccount? SendingAccount()
    {
        if (_accounts is null) return null;

        return _sendingAddress is null
            ? _accounts.Default
            : _accounts.Find(_sendingAddress) ?? _accounts.Default;
    }

    private async Task SendAsync()
    {
        if (SendingAccount() is not { } account)
        {
            Report("No account is set up yet. Add one under File in the main window.");
            return;
        }

        if (Split(_to.Text).Concat(Split(_cc.Text)).Concat(Split(_bcc.Text)).Any() is false)
        {
            Report("Add at least one recipient.");
            return;
        }

        MimeMessage message;
        try
        {
            message = await BuildMessageAsync(account);
        }
        catch (Exception ex)
        {
            Report($"Could not build the message: {ex.Message}");
            return;
        }

        try
        {
            var sender = new SmtpSender(account.Mail);
            var outboxId = sender.Queue(account.Account.Id, message);

            if (_notBefore is { } when) account.Mail.ScheduleOutbox(outboxId, when);

            _sent = true;
            Report(_notBefore is { } held
                ? $"Queued, held until {held.LocalDateTime:g}."
                : "Queued in the Outbox. It goes out on the next send/receive.");

            Close();
        }
        catch (Exception ex)
        {
            Report($"Could not queue the message: {ex.Message}");
        }
    }

    private async Task<MimeMessage> BuildMessageAsync(OpenAccount account)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.Account.DisplayName, account.Account.Address));

        foreach (var entry in Split(_to.Text)) message.To.Add(MailboxAddress.Parse(entry));
        foreach (var entry in Split(_cc.Text)) message.Cc.Add(MailboxAddress.Parse(entry));
        foreach (var entry in Split(_bcc.Text)) message.Bcc.Add(MailboxAddress.Parse(entry));

        if (_replyTo is { } reply) message.ReplyTo.Add(MailboxAddress.Parse(reply));

        message.Subject = _subject.Text ?? string.Empty;
        message.Importance = _importance;
        message.Priority = _importance switch
        {
            MessageImportance.High => MessagePriority.Urgent,
            MessageImportance.Low => MessagePriority.NonUrgent,
            _ => MessagePriority.Normal,
        };

        if (_wantsReadReceipt)
        {
            message.Headers.Add("Disposition-Notification-To", account.Account.Address);
        }

        if (_wantsDeliveryReceipt)
        {
            message.Headers.Add("Return-Receipt-To", account.Account.Address);
        }

        // Both halves, always. A recipient whose client shows plain text — or who has told it
        // to — gets a readable message rather than a page of markup, and the two are the same
        // message rather than two that can disagree, because both come off one document.
        var builder = new BodyBuilder { TextBody = _body.GetPlainText() };

        // Ours, not the editor's: §6's wire/render split and the narrow set of elements mail
        // clients actually render. See Mailbox.Editor.EmailHtml for why that is the half worth
        // keeping in-house.
        builder.HtmlBody = EmailHtml.Serialize(_body.Document ?? new FlowDocument(), new EmailHtmlOptions
        {
            BaseFontFamily = ComposeFontFamily,
            BaseFontPoints = ComposeFontPoints,

            // An image the writer put in the body becomes a related part and a cid: reference,
            // which is how mail carries one. Several large clients drop a data: image outright.
            RegisterImage = (bytes, type) =>
            {
                var extension = type.Split('/').ElementAtOrDefault(1) ?? "png";
                var part = builder.LinkedResources.Add(
                    $"image-{builder.LinkedResources.Count + 1}.{extension}", bytes);

                part.ContentId = MimeUtils.GenerateMessageId();
                return $"cid:{part.ContentId}";
            },
        });

        foreach (var file in _attachments)
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            builder.Attachments.Add(file.Name, buffer.ToArray());
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private void SaveDraft()
    {
        if (SendingAccount() is not { } account)
        {
            Report("No account is set up yet, so there is no Drafts folder to save into.");
            return;
        }

        try
        {
            var drafts = account.Mail.Folders(account.Account.Id)
                .FirstOrDefault(f => f.Role == FolderRole.Drafts);

            if (drafts is null)
            {
                Report("This account has no Drafts folder.");
                return;
            }

            var message = BuildMessageAsync(account).GetAwaiter().GetResult();

            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();

            var summary = MessageMapper.ToSummary(message, null, raw.Length, DateTimeOffset.UtcNow);
            account.Mail.AddMessage(drafts.Id, summary, raw);

            _sent = true;
            Report("Saved to Drafts.");
        }
        catch (Exception ex)
        {
            Report($"Could not save the draft: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------------------------
    // Window behaviour
    // ----------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Enter when control:
                Run(ComposeCommands.Send.Id);
                break;

            case Key.S when control:
                Run(ComposeCommands.SaveDraft.Id);
                break;

            case Key.Escape:
                Close();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // Nothing typed, or already dealt with: close without asking.
        if (_sent || !HasContent())
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        var keep = await Confirm.AskAsync(this, "Save this message?",
            "This message has not been sent. Save it to Drafts?", "Save", destructive: false);

        _sent = true;
        if (keep) SaveDraft();
        Close();
    }

    private bool HasContent()
        => !string.IsNullOrWhiteSpace(_to.Text)
           || !string.IsNullOrWhiteSpace(_cc.Text)
           || !string.IsNullOrWhiteSpace(_bcc.Text)
           || !string.IsNullOrWhiteSpace(_subject.Text)
           || !string.IsNullOrWhiteSpace(_body.GetPlainText())
           || _attachments.Count > 0;

    // ----------------------------------------------------------------------------------
    // Small dialogs
    // ----------------------------------------------------------------------------------

    private async Task Message(string title, string body)
    {
        var dialog = SmallDialog(title, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            out var panel);

        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        panel.Children.Add(ok);

        await dialog.ShowDialog(this);
    }

    private async Task<string?> Prompt(string title, string label)
    {
        var input = new TextBox { MinWidth = 320 };
        var content = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = label }, input },
        };

        var dialog = SmallDialog(title, content, out var panel);
        string? answer = null;

        var ok = new Button { Content = "OK" };
        ok.Click += (_, _) => { answer = input.Text ?? string.Empty; dialog.Close(); };

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => dialog.Close();

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        });

        input.AttachedToVisualTree += (_, _) => input.Focus();
        await dialog.ShowDialog(this);
        return answer;
    }

    private static Window SmallDialog(string title, Control content, out StackPanel panel)
    {
        panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(content);

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = panel,
        };
        Bind(dialog, BackgroundProperty, "surface.ground.brush");
        return dialog;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
