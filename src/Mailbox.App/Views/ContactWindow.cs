using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.Theming;
using Mailbox.Contacts;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Store.Pim;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>What a contact window came back with.</summary>
/// <param name="Deleted">True when the reader deleted it rather than saving it.</param>
public sealed record ContactResult(Contact Contact, long CollectionId, bool Deleted);

/// <summary>
/// The Contact window: one person or one group, in a window of its own with its own ribbon.
/// </summary>
/// <remarks>
/// A thin host, as <see cref="AppointmentWindow"/> and <see cref="ComposeWindow"/> are: the frame,
/// the caption, the ribbon and the pages belong here, and everything about the contact is
/// <see cref="ContactSurface"/> — which is measured off the reference's own Contact window.
/// <para>
/// The Show group's four buttons are pages rather than more buttons, which is what the reference
/// means by them: General is the form, and Details, Certificates and All Fields replace it. Two of
/// those three are built here — Details from the fields the card already carries, All Fields from
/// every property in it — and Certificates says what it waits for: S/MIME itself is built, but a
/// contact carries no certificate column for the page to list.
/// </para>
/// </remarks>
public sealed class ContactWindow : Window
{
    private readonly ContactSurface _surface;
    private readonly RibbonView _ribbon;
    private readonly Grid _workspace = new();
    private readonly Contact _contact;

    private TextBlock _caption = null!;
    private Canvas _floatLayer = null!;
    private Control? _floatingRibbon;

    public ContactWindow(CommandCatalog commands, Contact contact, IReadOnlyList<Collection> books, long collectionId)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _contact = contact ?? throw new ArgumentNullException(nameof(contact));
        _surface = new ContactSurface(contact, books, collectionId);

        Title = _surface.Title;
        // The size the reference's own contact window is in the captures, which is the width its
        // Insert tab needs before the Illustrations group degrades to a stack.
        Width = 1400;
        Height = 846;
        MinWidth = 720;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        WindowFrame.Apply(this);
        FontFamily = (FontFamily)(Application.Current!.FindResource("ui.fontfamily") ?? FontFamily.Default);
        TextRendering.Apply(this);

        _ribbon = new RibbonView(commands, ContactRibbonLayout.Contact);
        RibbonDisplayMemory.Wire(_ribbon, RibbonWindow.Compose, Environment.GetEnvironmentVariable("MAILBOX_RIBBON"));

        // A width the harness chose, since the ribbon's collapse ladder has a different answer
        // every hundred pixels and this window has two tabs' worth of it.
        Mailbox.App.Theming.WindowCapture.ApplyRequestedSize(this);

        // MAILBOX_TAB opens the strip on one tab, as it does on the shell — a capture of the
        // Insert tab is otherwise a capture of whichever tab the window remembers.
        if (Environment.GetEnvironmentVariable("MAILBOX_TAB") is { Length: > 0 } posedTab)
        {
            _ribbon.ActiveTabId = posedTab.Trim();
        }

        _ribbon.CommandInvoked += (_, e) =>
        {
            _ribbon.CloseFloatingBody();
            _surface.Invoke(e.Command);
        };

        _ribbon.FloatingBodyChanged += (_, e) => ShowFloatingRibbon(e.Body);

        _surface.TitleChanged += (_, _) =>
        {
            Title = _surface.Title;
            _caption.Text = _surface.Title;
        };

        _surface.Finished += (_, result) =>
        {
            Result = result;
            Close();
        };

        _surface.Cancelled += (_, _) => Close();
        _surface.AnotherRequested += (_, _) => Another = true;
        _surface.ShellCommandRequested += (_, id) => ShellCommandRequested?.Invoke(this, id);
        _surface.MapRequested += (_, address) => MapRequested?.Invoke(this, address);
        _surface.PageRequested += (_, page) => ShowPage(page);

        Content = BuildRoot();

        // The harness cannot press a page button, and three of them replace the whole form.
        if (Environment.GetEnvironmentVariable("MAILBOX_CONTACT_PAGE") is { Length: > 0 } posed)
        {
            Opened += (_, _) => ShowPage("contact.show." + posed.Trim().ToLowerInvariant());
        }

        // Presses this window's own commands, as MAILBOX_RUN does the shell's:
        // MAILBOX_CONTACT_RUN=format.bold,insert.datetime — with the settled read-back the
        // press sweep classifies from. The bar carries the surface's typed refusal (the
        // recorded reason an Insert entry is absent here), which is this window's own honest
        // "not wired yet"; a refusal also opens its explaining dialog, which the windows list
        // shows.
        if (Environment.GetEnvironmentVariable("MAILBOX_CONTACT_RUN") is { Length: > 0 } run)
        {
            Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
                async () =>
                {
                    // Held across the presses: the settled read awaits, and the capture's exit
                    // must not land between a press and its read-back.
                    using var hold = WindowCapture.Hold();

                    foreach (var id in run.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var known = false;
                        try
                        {
                            known = App.Commands.TryGet(new CommandId(id), out _);
                        }
                        catch (ArgumentException)
                        {
                            // A malformed id is as unknown as an unregistered one.
                        }

                        var fieldsBefore = _surface.DescribeForm();
                        var noteBefore = _surface.NoteText;
                        var markupBefore = _surface.DescribeNote();
                        var pageBefore = _page;

                        Log.Info($"Harness: contact running {id}.");
                        var refused = known ? _surface.Invoke(new CommandId(id)) : null;

                        await Task.Delay(600);

                        Log.Info(
                            $"Harness: ran {id} — {(known ? "known" : "UNKNOWN to the catalogue")}"
                            + $"{(refused is { Length: > 0 } ? ", fallback" : string.Empty)}, "
                            + $"bar “”→“{refused ?? string.Empty}”, "
                            + $"fields {(_surface.DescribeForm() == fieldsBefore ? "unchanged" : "changed")}, "
                            + $"body {noteBefore.Length}→{_surface.NoteText.Length}, "
                            + $"markup {(_surface.DescribeNote() == markupBefore ? "unchanged" : "changed")}, "
                            + $"state {(_page == pageBefore ? "unchanged" : $"“{pageBefore}”→“{_page}”")}, "
                            + $"window {(IsVisible ? "open" : "closed")}, "
                            + $"windows: {MainWindow.WindowsBeside(this)}");
                    }
                },
                Avalonia.Threading.DispatcherPriority.Background);
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _surface.Cancel();
                e.Handled = true;
                return;
            }

            if (Keystroke.Of(e) is not { } chord || Keystroke.IsTyping(chord)) return;
            if (App.Keys.CommandFor(chord, CommandSurface.Contact) is not { } id) return;

            _surface.Invoke(id);
            e.Handled = true;
        };
    }

    /// <summary>What the window came back with, or null when it was cancelled.</summary>
    public ContactResult? Result { get; private set; }

    /// <summary>True when Save &amp; New was pressed: the caller opens another after this one.</summary>
    public bool Another { get; private set; }

    /// <summary>A command the window cannot answer on its own — a message, the address book.</summary>
    public event EventHandler<CommandId>? ShellCommandRequested;

    /// <summary>Map It: the address, for the desktop to open however it opens a map.</summary>
    public event EventHandler<string>? MapRequested;

    /// <summary>The surface, for the harness, which poses the form and presses its buttons.</summary>
    internal ContactSurface Surface => _surface;

    /// <summary>
    /// Puts one of the Show group's pages under the bar.
    /// </summary>
    /// <remarks>
    /// The form is kept rather than rebuilt — it holds everything typed so far, and a reader who
    /// looks at All Fields and comes back has not lost the name they were in the middle of.
    /// </remarks>
    /// <summary>Which of the four pages the workspace is showing, for the door's read-back.</summary>
    private string _page = "contact.show.general";

    private void ShowPage(string page)
    {
        _page = page;
        Control body = page switch
        {
            "contact.show.details" => DetailsPage(),
            "contact.show.allfields" => AllFieldsPage(),
            "contact.show.certificates" => Waiting(
                "A contact's own S/MIME certificates are not kept on the card yet, so there is "
                + "nothing to list here."),
            _ => _surface,
        };

        _workspace.Children.Clear();
        _workspace.Children.Add(body);
        _workspace.Children.Add(_floatLayer);
    }

    /// <summary>Details: what the card holds that the General page has no room for.</summary>
    private Control DetailsPage()
    {
        var contact = _surface.Current();
        var rows = new StackPanel { Margin = new Thickness(29, 16, 16, 16), Spacing = 6 };

        void Line(string label, string value)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
            var name = new TextBlock { Text = label, FontSize = 12 };
            name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");
            grid.Children.Add(name);

            var text = new SelectableTextBlock { Text = value, FontSize = 12 };
            text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            rows.Children.Add(grid);
        }

        Line("Department", contact.Department);
        Line("Nickname", contact.NickName);
        Line("Birthday", contact.Birthday?.ToString("d MMMM yyyy") ?? string.Empty);
        Line("Anniversary", contact.Anniversary?.ToString("d MMMM yyyy") ?? string.Empty);
        Line("Categories", string.Join(", ", contact.Categories));
        Line("Private", contact.IsPrivate ? "Yes" : "No");
        Line("Follow up", contact.FollowUpDue is { } due ? due.LocalDateTime.ToString("d") : string.Empty);

        return Page(rows);
    }

    /// <summary>All Fields: every property the card carries, including what no page shows.</summary>
    private Control AllFieldsPage()
    {
        var contact = _surface.Current();
        var text = new SelectableTextBlock
        {
            Text = VCardCodec.Serialize(contact),
            FontSize = 12,
            Margin = new Thickness(29, 16, 16, 16),
        };

        // The theme's monospaced family, not a name in this file: asked for by a bare name a family
        // skips the metric-compatible substitution, and a bundled one is never found at all.
        text[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("mono.fontfamily");
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");
        return Page(new ScrollViewer { Content = text });
    }

    private Control Waiting(string words)
    {
        var text = new TextBlock { Text = words, Margin = new Thickness(29, 20, 16, 16), FontSize = 12 };
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("compose.header.text.brush");
        return Page(text);
    }

    private static Control Page(Control body)
    {
        var host = new Border { Child = body };
        host[!Border.BackgroundProperty] = new DynamicResourceExtension("list.background.brush");
        return host;
    }

    private Control BuildRoot()
    {
        var layered = new Grid();
        var root = new DockPanel { LastChildFill = true };

        var title = BuildTitleBar();
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var ribbonHost = new Border { Child = _ribbon, ZIndex = 2, Padding = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(ribbonHost, Dock.Top);
        root.Children.Add(ribbonHost);

        _floatLayer = new Canvas { IsHitTestVisible = true, ZIndex = 1 };
        _workspace.Children.Add(_surface);
        _workspace.Children.Add(_floatLayer);
        root.Children.Add(_workspace);

        layered.Children.Add(root);
        return WindowFrame.Rounded(layered);
    }

    private void ShowFloatingRibbon(Control? body)
    {
        if (_floatingRibbon is not null)
        {
            _floatLayer.Children.Remove(_floatingRibbon);
            _floatingRibbon = null;
        }

        if (body is null) return;
        Canvas.SetLeft(body, 0);
        Canvas.SetTop(body, 0);
        _floatLayer.Children.Add(body);
        _floatingRibbon = body;
    }

    private Control BuildTitleBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var leading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var icon = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(_contact.IsGroup ? "contact-group" : "contact-card", 16),
            FontFamily = IconFont.Family,
            FontSize = 15,
            Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Bind(icon, TextBlock.ForegroundProperty, "titlebar.foreground.brush");
        leading.Children.Add(icon);

        _caption = new TextBlock { Text = Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Bind(_caption, TextBlock.ForegroundProperty, "titlebar.foreground.brush");
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

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
