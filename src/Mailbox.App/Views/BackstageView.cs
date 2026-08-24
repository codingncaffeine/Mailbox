using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The File tab — the reference's Backstage.
/// </summary>
/// <remarks>
/// Not a ribbon tab but a full-window takeover: a back arrow returns to the mailbox, a dark
/// rail down the left lists the pages, and the right-hand pane shows the selected one. Account
/// Information is the landing page.
/// <para>
/// Each section on that page is a large square button paired with a heading and a sentence of
/// explanation to its right, which is the layout the reference application uses throughout Backstage.
/// </para>
/// </remarks>
public sealed class BackstageView : Border
{
    private const double RailWidth = 165;

    private readonly StackPanel _rail = new();
    private readonly ContentControl _page = new();
    private string _selected = "info";

    public BackstageView()
    {
        Bind(this, BackgroundProperty, "surface.ground.brush");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{RailWidth},*"),
            RowDefinitions = new RowDefinitions("Auto,*"),
        };

        var back = BuildBackButton();
        Grid.SetRow(back, 0);
        Grid.SetColumn(back, 0);
        grid.Children.Add(back);

        var railHost = new Border { Child = _rail, Padding = new Thickness(0, 6, 0, 12) };
        Bind(railHost, BackgroundProperty, "backstage.rail.brush");
        Grid.SetRow(railHost, 1);
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);

        _page.Margin = new Thickness(40, 24, 24, 24);
        Grid.SetRow(_page, 1);
        Grid.SetColumn(_page, 1);
        grid.Children.Add(_page);

        // The rail's background continues up behind the back arrow.
        var railTop = new Border();
        Bind(railTop, BackgroundProperty, "backstage.rail.brush");
        Grid.SetRow(railTop, 0);
        Grid.SetColumn(railTop, 0);
        grid.Children.Insert(0, railTop);
        grid.Children.Remove(back);
        Grid.SetRow(back, 0);
        Grid.SetColumn(back, 0);
        grid.Children.Add(back);

        Child = grid;

        BuildRail();
        ShowPage(_selected);
    }

    public event EventHandler? CloseRequested;

    /// <summary>Raised by Exit. The host quits the application, as the reference's Exit does.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised by the Options page entry. The shell opens the dialog.</summary>
    public event EventHandler? OptionsRequested;

    /// <summary>Raised by Add Account. The shell owns the window, and the reload after it.</summary>
    public event EventHandler? AddAccountRequested;

    private Control BuildBackButton()
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("chevron-left", 20),
            FontFamily = IconFont.Family,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "backstage.rail.text.brush");

        var circle = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = glyph,
        };
        Bind(circle, BorderBrushProperty, "backstage.rail.text.brush");

        var button = new Button
        {
            Content = circle,
            Margin = new Thickness(14, 14, 0, 10),
            Padding = default,
            Background = Brushes.Transparent,
            BorderThickness = default,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(button, "Back");
        button.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    /// <summary>
    /// the reference's page list, with the two rules that break it into groups and the account,
    /// options and exit entries pinned to the bottom.
    /// </summary>
    private void BuildRail()
    {
        _rail.Children.Clear();

        _rail.Children.Add(RailItem("info", "Info", "mail"));
        _rail.Children.Add(RailItem("openexport", "Open & Export", "folder-open"));
        _rail.Children.Add(RailRule());
        _rail.Children.Add(RailItem("saveas", "Save As", "archive"));
        _rail.Children.Add(RailItem("saveattachments", "Save Attachments", "attach", enabled: false));
        _rail.Children.Add(RailItem("print", "Print", "print"));

        _rail.Children.Add(new Panel { Height = 320 });

        _rail.Children.Add(RailRule());
        _rail.Children.Add(RailItem("account", "Mailbox Account", "people"));
        _rail.Children.Add(RailItem("options", "Options", "settings"));
        _rail.Children.Add(RailItem("exit", "Exit", "dismiss"));
    }

    private Control RailRule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(14, 6) };
        Bind(rule, BackgroundProperty, "backstage.rail.rule.brush");
        return rule;
    }

    private Control RailItem(string id, string label, string icon, bool enabled = true)
    {
        var selected = string.Equals(id, _selected, StringComparison.Ordinal);

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 22,
        };

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };

        var key = !enabled ? "backstage.rail.disabled.brush"
            : selected ? "accent.rest.brush"
            : "backstage.rail.text.brush";
        Bind(glyph, TextBlock.ForegroundProperty, key);
        Bind(text, TextBlock.ForegroundProperty, key);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(14, 0),
        };
        row.Children.Add(glyph);
        row.Children.Add(text);

        var button = new Button
        {
            Content = row,
            Height = 32,
            Padding = default,
            IsEnabled = enabled,
            BorderThickness = new Thickness(selected ? 1 : 0),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

        // The selected page is outlined rather than filled, as the reference application draws it.
        if (selected) Bind(button, BorderBrushProperty, "accent.rest.brush");

        button.Click += (_, _) =>
        {
            if (id == "exit") { ExitRequested?.Invoke(this, EventArgs.Empty); return; }
            if (id == "options") { OptionsRequested?.Invoke(this, EventArgs.Empty); return; }
            _selected = id;
            BuildRail();
            ShowPage(id);
        };
        return button;
    }

    /// <summary>Opens a page by rail id — what a click does, for the harness, which cannot click.</summary>
    internal void Open(string id)
    {
        _selected = id;
        ShowPage(id);
    }

    private void ShowPage(string id)
        => _page.Content = id switch
        {
            "info" => BuildAccountInformation(),
            "openexport" => BuildOpenExport(),
            "saveas" => BuildSaveAs(),
            _ => Placeholder(id),
        };

    // ------------------------------------------------------------------------------------
    // Save As — what leaves the store leaves it verbatim (§7.6a)
    // ------------------------------------------------------------------------------------

    private Control BuildSaveAs()
    {
        var stack = new StackPanel { Spacing = 0, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };

        var heading = new TextBlock
        {
            Text = "Save As",
            FontSize = 21,
            Margin = new Thickness(0, 0, 0, 14),
        };
        Bind(heading, TextBlock.ForegroundProperty, "text.primary.brush");
        stack.Children.Add(heading);

        stack.Children.Add(BuildSection(
            "mail", "Save Message", hasDropdown: false,
            "The selected message as .eml",
            "Its stored bytes, verbatim — headers nothing here ever parsed included. That is " +
            "the promise the store makes: you can always leave with everything.",
            action: "export.eml"));

        stack.Children.Add(BuildSection(
            "folder", "Save Folder", hasDropdown: false,
            "The open folder as mbox",
            "Every message in the folder, byte-exact inside the one file everything since " +
            "Unix reads.",
            action: "export.mbox"));

        stack.Children.Add(BuildSection(
            "open-calendar", "Save Calendar", hasDropdown: false,
            "The default calendar as .ics",
            "Every appointment as the iCalendar text the store already keeps verbatim.",
            action: "export.ics"));

        stack.Children.Add(BuildSection(
            "people", "Save Contacts", hasDropdown: false,
            "The address book as .vcf",
            "Every card as the vCard text the store already keeps verbatim.",
            action: "export.vcf"));

        return stack;
    }

    // ------------------------------------------------------------------------------------
    // Open & Export — the importers, §16 arriving one format at a time
    // ------------------------------------------------------------------------------------

    private Control BuildOpenExport()
    {
        var stack = new StackPanel { Spacing = 0, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };

        var heading = new TextBlock
        {
            Text = "Open & Export",
            FontSize = 21,
            Margin = new Thickness(0, 0, 0, 14),
        };
        Bind(heading, TextBlock.ForegroundProperty, "text.primary.brush");
        stack.Children.Add(heading);

        stack.Children.Add(BuildSection(
            "folder-open", "Import Maildir", hasDropdown: false,
            "Import a Maildir",
            "Bring mail in from a Maildir tree — what Dovecot, Evolution, KMail, mutt and " +
            "offlineimap keep. The source is only read, never changed.",
            action: "import.maildir"));

        stack.Children.Add(BuildSection(
            "mail", "Import Thunderbird", hasDropdown: false,
            "Import a Thunderbird profile",
            "The mail tree, the address books, and the filters that translate — a filter " +
            "whose meaning would change is skipped and named instead.",
            action: "import.thunderbird"));

        stack.Children.Add(BuildSection(
            "attach", "Import Files", hasDropdown: false,
            "Import files",
            "One or more files: a whole .pst data file — mail, calendar, contacts, tasks, " +
            "notes and journal — a saved .msg routed by what it is, an mbox into a folder, " +
            ".eml messages, .ics appointments and tasks, .vcf contacts.",
            action: "import.files"));

        return stack;
    }

    private Control Placeholder(string id)
    {
        var text = new TextBlock { Text = $"{id} — not built yet.", FontSize = 15 };
        Bind(text, TextBlock.ForegroundProperty, "text.secondary.brush");
        return text;
    }

    // ------------------------------------------------------------------------------------
    // Account Information — the landing page
    // ------------------------------------------------------------------------------------

    private Control BuildAccountInformation()
    {
        var stack = new StackPanel { Spacing = 0, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };

        var heading = new TextBlock
        {
            Text = "Account Information",
            FontSize = 21,
            Margin = new Thickness(0, 0, 0, 14),
        };
        Bind(heading, TextBlock.ForegroundProperty, "text.primary.brush");
        stack.Children.Add(heading);

        stack.Children.Add(BuildAccountPicker());
        stack.Children.Add(BuildAddAccount());

        stack.Children.Add(BuildSection(
            "settings", "Account\nSettings", true,
            "Account Settings",
            "Change settings for this account or set up more connections.",
            AccountSettingsMenu()));

        stack.Children.Add(BuildSection(
            "archive", "Tools", true,
            "Mailbox Settings",
            "Manage the size of your mailbox by emptying Deleted Items and archiving.",
            ToolsMenu()));

        stack.Children.Add(BuildSection(
            "rules", "Manage Rules\n& Alerts", false,
            "Rules and Alerts",
            "Use Rules and Alerts to help organize your incoming email messages, and receive " +
            "updates when items are added, changed, or removed.",
            action: "rules"));

        stack.Children.Add(BuildSection(
            "refresh", "Check for\nUpdates", false,
            "Mailbox Update",
            "Ask the release page whether a newer version exists. Asks only when pressed — " +
            "or at startup, if the Options page's own switch says so; nothing else here " +
            "touches the network.",
            action: "update.check"));

        return new ScrollViewer { Content = stack };
    }

    private Control BuildAccountPicker()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var icon = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("mail", 20),
            FontFamily = IconFont.Family,
            FontSize = 17,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(icon, TextBlock.ForegroundProperty, "text.primary.brush");
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var lines = new StackPanel { Margin = new Thickness(0, 6), VerticalAlignment = VerticalAlignment.Center };
        var address = new TextBlock { Text = "you@example.com" };
        Bind(address, TextBlock.ForegroundProperty, "text.primary.brush");
        var kind = new TextBlock { Text = "POP/SMTP", FontSize = 11 };
        Bind(kind, TextBlock.ForegroundProperty, "text.secondary.brush");
        lines.Children.Add(address);
        lines.Children.Add(kind);
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);

        var chevron = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
            FontFamily = IconFont.Family,
            FontSize = 11,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(chevron, TextBlock.ForegroundProperty, "text.secondary.brush");
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var box = new Border
        {
            Child = grid,
            Width = 560,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(2),
        };
        Bind(box, BackgroundProperty, "backstage.field.brush");
        return box;
    }

    private Control BuildAddAccount()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8, 3),
        };

        var plus = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("add", 16),
            FontFamily = IconFont.Family,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(plus, TextBlock.ForegroundProperty, "status.success.brush");
        row.Children.Add(plus);

        var label = new TextBlock { Text = "Add Account", VerticalAlignment = VerticalAlignment.Center };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
        row.Children.Add(label);

        var button = new Button
        {
            Content = row,
            Margin = new Thickness(0, 8, 0, 18),
            Padding = default,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
        };
        Bind(button, BorderBrushProperty, "border.strong.brush");
        button.Click += (_, _) => AddAccountRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    /// <summary>
    /// A large square button on the left with its heading and explanation to the right — the
    /// shape every Backstage section uses.
    /// </summary>
    /// <summary>Raised by every Backstage action, with the name of what was asked for.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>
    /// A menu item with a title and a sentence beneath it, which is the shape every item in
    /// these two menus takes. Built rather than templated so both lines take their own token —
    /// a detail line in the primary colour reads as a second title.
    /// </summary>
    private MenuItem MenuEntry(string icon, string title, string detail, string action,
        bool enabled = true)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 16,
            Margin = new Thickness(0, 2, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Bind(glyph, TextBlock.ForegroundProperty,
            enabled ? "text.primary.brush" : "text.disabled.brush");

        var lines = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, Classes = { "menu-title" } },
                new TextBlock { Text = detail, Classes = { "menu-detail" } },
            },
        };

        var item = new MenuItem
        {
            IsEnabled = enabled,
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { glyph, lines },
            },
        };

        if (enabled) item.Click += (_, _) => ActionRequested?.Invoke(this, action);
        return item;
    }

    private MenuFlyout AccountSettingsMenu() => new()
    {
        Placement = PlacementMode.BottomEdgeAlignedLeft,
        ItemsSource = new[]
        {
            MenuEntry("settings", "Account Settings…",
                "Add and remove accounts, or change existing connection settings.",
                "account.settings"),
            MenuEntry("shield", "Update Password",
                "Update the account password saved in Mailbox.", "account.password"),
            MenuEntry("people", "Account Name and Sync Settings",
                "Change the account name and what is downloaded.", "account.server"),
            MenuEntry("settings", "Server Settings",
                "Change the server name, port and encryption.", "account.server"),
        },
    };

    private MenuFlyout ToolsMenu() => new()
    {
        Placement = PlacementMode.BottomEdgeAlignedLeft,
        ItemsSource = new[]
        {
            MenuEntry("cleanup", "Mailbox Cleanup…",
                "See what is taking up room and clear it.", "tools.cleanup"),
            MenuEntry("archive", "Clean Up Old Items…",
                "Move old items to the Archive folder now, by folder or by the AutoArchive settings.", "tools.archive"),
            MenuEntry("delete", "Empty Deleted Items Folder",
                "Permanently delete everything in Deleted Items.", "tools.emptydeleted"),
            MenuEntry("undo", "Recover Deleted Items…",
                "Bring back mail that was permanently deleted recently.", "tools.recover"),
        },
    };

    private Control BuildSection(
        string icon, string buttonLabel, bool hasDropdown, string heading, string description,
        MenuFlyout? menu = null, string? action = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 0, 18),
        };

        var tile = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 32),
            FontFamily = IconFont.Family,
            FontSize = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        tile.Children.Add(glyph);

        var caption = new TextBlock
        {
            Text = buttonLabel,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 11.5,
        };
        Bind(caption, TextBlock.ForegroundProperty, "text.primary.brush");
        tile.Children.Add(caption);

        if (hasDropdown)
        {
            var chevron = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
                FontFamily = IconFont.Family,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Bind(chevron, TextBlock.ForegroundProperty, "text.secondary.brush");
            tile.Children.Add(chevron);
        }

        var button = new Button
        {
            Content = tile,
            Width = 96,
            Height = 86,
            Padding = new Thickness(4),
            BorderThickness = default,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Bind(button, BackgroundProperty, "backstage.field.brush");
        if (menu is not null) button.Flyout = menu;
        else if (action is not null)
        {
            button.Click += (_, _) => ActionRequested?.Invoke(this, action);
        }

        Grid.SetColumn(button, 0);
        grid.Children.Add(button);

        var text = new StackPanel { Margin = new Thickness(16, 2, 0, 0), Spacing = 4 };

        var title = new TextBlock { Text = heading, FontSize = 15 };
        Bind(title, TextBlock.ForegroundProperty, "text.primary.brush");
        text.Children.Add(title);

        var body = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        Bind(body, TextBlock.ForegroundProperty, "text.primary.brush");
        text.Children.Add(body);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
