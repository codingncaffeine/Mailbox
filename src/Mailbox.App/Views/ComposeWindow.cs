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
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Protocols;
using Mailbox.Store;
using Mailbox.Theming.Icons;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The new message window: its own ribbon, its own address fields, its own Send.
/// </summary>
/// <remarks>
/// A separate window with its own tab collection rather than a pane in the shell, because that
/// is what the reference does and because the ribbon model was built to swap the whole tab set
/// per host.
/// <para>
/// The body is a plain text box. The rich text editor is Phase 5 and is the largest single work
/// item in the project; until it exists this window composes plain text and says so, rather than
/// offering formatting buttons that quietly do nothing. What each button does today is recorded
/// once in <see cref="ComposeAvailability"/> and read from there, so a blocked button reports
/// what it is waiting for instead of "not wired yet".
/// </para>
/// </remarks>
public sealed class ComposeWindow : Window
{
    private const string PlainTextNotice =
        "Plain text. Formatting arrives with the editor in Phase 5.";

    private readonly CommandCatalog _catalog;
    private readonly AccountStores? _accounts;
    private readonly RibbonView _ribbon;

    private readonly TextBox _to = Field();
    private readonly TextBox _cc = Field();
    private readonly TextBox _bcc = Field();
    private readonly TextBox _subject = Field();
    private readonly TextBox _body;
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

        Title = "Untitled - Message (Plain Text)";
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

        _body = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = default,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        // The body is a document page, not a pane — white even in Dark Gray, where the reading
        // pane is not. See the compose tokens in each theme.
        Bind(_body, TemplatedControl.BackgroundProperty, "compose.body.background.brush");
        Bind(_body, TemplatedControl.ForegroundProperty, "compose.body.text.brush");

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

        return !string.IsNullOrEmpty(_body.Text);
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
        _body.Text = text;
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

        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        var body = new Border { Child = _body, Padding = new Thickness(12, 8) };
        Bind(body, Border.BackgroundProperty, "compose.body.background.brush");
        root.Children.Add(body);

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
                ? "Untitled - Message (Plain Text)"
                : $"{_subject.Text} - Message (Plain Text)";
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
        var rows = new StackPanel { Spacing = 2 };

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
            Height = RowHeight * 2 + RowGap,
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
            Margin = new Thickness(0, 10, 14, 10),
        };
        Grid.SetColumn(send, 0);
        grid.Children.Add(send);
        Grid.SetColumn(rows, 1);
        grid.Children.Add(rows);

        var host = new Border { Child = grid };
        Bind(host, Border.BackgroundProperty, "compose.header.background.brush");
        return host;
    }

    // Measured off the compose capture. The header's own background runs from x=13; Send is a
    // filled button at x=34–93; the From/To/Cc buttons start at x=110; and the field rules begin
    // at x=202. Rows sit 40px apart with 31px of button in each.
    private const double SendInset = 21;
    private const double SendWidth = 58;
    private const double LabelInset = 17;
    private const double LabelWidth = 80;
    private const double FieldInset = 12;
    private const double RowHeight = 31;
    private const double RowGap = 9;

    /// <summary>
    /// One address row — its label and its field as a single control, so the pair is shown and
    /// hidden together and cannot get out of step.
    /// </summary>
    private Control AddressRow(
        StackPanel rows, string label, Control field,
        bool opensAddressBook = true, bool picksAccount = false)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Height = RowHeight,
            Margin = new Thickness(0, 0, 0, RowGap),
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
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            Bind(text, TextBlock.ForegroundProperty, "text.secondary.brush");
            caption = text;
        }

        Grid.SetColumn(caption, 0);
        row.Children.Add(caption);

        // The fields carry no box. The reference draws a single rule under each one and nothing
        // else, so the address block reads as writing lines rather than as a form.
        if (field is TextBlock plain) Bind(plain, TextBlock.ForegroundProperty, "text.primary.brush");

        if (field is TextBox box)
        {
            box.BorderThickness = new Thickness(0, 0, 0, 1);
            box.CornerRadius = default;
            box.Background = Brushes.Transparent;
            Bind(box, TemplatedControl.BorderBrushProperty, "compose.field.rule.brush");
            Bind(box, TemplatedControl.ForegroundProperty, "text.primary.brush");
        }

        field.Margin = new Thickness(FieldInset, 0, 0, 0);
        field.VerticalAlignment = VerticalAlignment.Stretch;

        Grid.SetColumn(field, 1);
        row.Children.Add(field);

        rows.Children.Add(row);
        return row;
    }

    private Control BuildStatusBar()
    {
        Bind(_status, TextBlock.ForegroundProperty, "statusbar.foreground.brush");
        Bind(_status, TextBlock.FontSizeProperty, "type.ui.size.small.value");
        _status.VerticalAlignment = VerticalAlignment.Center;

        var host = new Border
        {
            Child = _status,
            Height = 26,
            Padding = new Thickness(12, 0),
        };
        Bind(host, Border.BackgroundProperty, "statusbar.background.brush");
        return host;
    }

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

        if (id == ComposeCommands.Cut.Id) { _body.Cut(); return true; }
        if (id == ComposeCommands.Copy.Id) { _body.Copy(); return true; }
        if (id == ComposeCommands.Paste.Id) { _body.Paste(); return true; }
        if (id == ComposeCommands.SelectAll.Id) { _body.SelectAll(); _body.Focus(); return true; }

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
        var next = _body.FontSize + 2;
        _body.FontSize = next > 28 ? 12 : next;
        Report($"Zoom {_body.FontSize:0}pt.");
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

    private void UpdateStatus()
    {
        var bits = new List<string> { PlainTextNotice };

        if (_importance != MessageImportance.Normal) bits.Add($"{_importance} importance");
        if (_attachments.Count > 0) bits.Add($"{_attachments.Count} attached");
        if (_notBefore is { } when) bits.Add($"held until {when.LocalDateTime:g}");

        _status.Text = string.Join("  ·  ", bits);
    }

    private void Report(string message) => _status.Text = message;

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
        var text = _body.Text ?? string.Empty;
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

        var text = _body.Text ?? string.Empty;

        if (!replace)
        {
            var at = text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) { Report($"'{needle}' is not in the message."); return; }

            _body.SelectionStart = at;
            _body.SelectionEnd = at + needle.Length;
            _body.Focus();
            Report($"Found '{needle}'.");
            return;
        }

        var with = await Prompt("Replace", "Replace with:") ?? string.Empty;
        var replaced = text.Replace(needle, with, StringComparison.CurrentCultureIgnoreCase);
        var count = (text.Length - replaced.Length) / Math.Max(1, needle.Length - with.Length);

        _body.Text = replaced;
        Report(text == replaced
            ? $"'{needle}' is not in the message."
            : $"Replaced {Math.Max(1, Math.Abs(count))}.");
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

        Insert(address);
        Report("Inserted as text. A real hyperlink needs the editor in Phase 5.");
    }

    private void Insert(string text)
    {
        var at = Math.Clamp(_body.CaretIndex, 0, (_body.Text ?? string.Empty).Length);
        _body.Text = (_body.Text ?? string.Empty).Insert(at, text);
        _body.CaretIndex = at + text.Length;
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

        var builder = new BodyBuilder { TextBody = _body.Text ?? string.Empty };

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
           || !string.IsNullOrWhiteSpace(_body.Text)
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
