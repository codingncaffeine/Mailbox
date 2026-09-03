using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
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

    /// <summary>
    /// Which module the shell was showing when this opened. The Print page is the only one that
    /// asks: a reader who presses File while looking at a week wants that week on paper, not the
    /// three mail styles.
    /// </summary>
    public Mailbox.Core.Commands.MailboxModule Module { get; init; }

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
    /// The reference's page list, with the two rules that break it into groups and the account,
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
        // The two rail entries that are not pages. A click on either raises its event and returns
        // before any page is shown; a posed open went straight to ShowPage and rendered
        // "exit — not built yet.", which is a photograph of something no reader can ever see.
        // A door that can pose an impossible state puts a lie in the evidence set.
        if (id is "exit") { ExitRequested?.Invoke(this, EventArgs.Empty); return; }
        if (id is "options") { OptionsRequested?.Invoke(this, EventArgs.Empty); return; }

        _selected = id;

        // The rail as well as the page: a click rebuilds it so the entry it landed on is the
        // one drawn selected, and a posed open that skipped that left the mark on Info while
        // another page was showing — which is what every capture of this window then showed.
        BuildRail();
        ShowPage(id);
    }

    private void ShowPage(string id)
        => _page.Content = id switch
        {
            "info" => BuildAccountInformation(),
            "openexport" => BuildOpenExport(),
            "saveas" => BuildSaveAs(),
            "print" => BuildPrint(),
            "account" => BuildAccount(),
            _ => Placeholder(id),
        };

    // ------------------------------------------------------------------------------------
    // Print — the reference's own page, translated to a desktop that owns the print dialog
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Print: the message, the folder as a list, or a PDF.
    /// </summary>
    /// <remarks>
    /// Both rail entries used to fall through to a placeholder that wrote "print — not built
    /// yet" into the page — in the shell, the compose window and the message window alike, on
    /// the most-used page the Backstage has. The commands behind it were all built; only the
    /// page was missing.
    /// <para>
    /// <b>A stated translation.</b> The reference's page carries a printer picker, a copies box
    /// and a preview, because on Windows the application owns the print dialog. Here the desktop
    /// owns it: the preview opens in its own window and its Print button hands over to the
    /// system's dialog, which is where the printer, the copies and the paper are chosen. A
    /// second picker in this page would be a second answer to a question the desktop has already
    /// asked.
    /// </para>
    /// </remarks>
    private Control BuildPrint()
    {
        var stack = new StackPanel { Spacing = 0, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(Heading("Print"));

        if (Module == Mailbox.Core.Commands.MailboxModule.Calendar)
        {
            stack.Children.Add(BuildSection(
                "calendar", "Daily", hasDropdown: false,
                "Daily style",
                "A day at a time, with its appointments under it — every day the view is showing.",
                action: "print.calendar.daily"));

            stack.Children.Add(BuildSection(
                "work-week", "Weekly", hasDropdown: false,
                "Weekly style",
                "The days across the page, a column each, which is the calendar somebody pins up.",
                action: "print.calendar.weekly"));

            stack.Children.Add(BuildSection(
                "month-view", "Monthly", hasDropdown: false,
                "Monthly style",
                "The month as its grid, one cell a day — the whole month, whatever run of days "
                + "the view happens to be showing.",
                action: "print.calendar.monthly"));

            stack.Children.Add(BuildSection(
                "document", "Details", hasDropdown: false,
                "Calendar Details style",
                "Every appointment in these days in time order, with where it is and whatever was "
                + "written in it — the one to take into a room.",
                action: "print.calendar.details"));

            return stack;
        }

        stack.Children.Add(BuildSection(
            "print", "Print", hasDropdown: false,
            "Print the message",
            "Opens the selected message as it will appear on paper, and hands over to the "
            + "desktop's own print dialog for the printer, the copies and the paper.",
            action: "print.message"));

        stack.Children.Add(BuildSection(
            "table", "Print List", hasDropdown: false,
            "Print the folder as a list",
            "The messages in this folder as a table — who from, subject, received — which is "
            + "the reference's other print style.",
            action: "print.list"));

        stack.Children.Add(BuildSection(
            "print", "Print to PDF", hasDropdown: false,
            "Write it to a PDF",
            "Straight to a file, without going through a print dialog. Not something the "
            + "reference offers: on Windows it prints to whatever the system provides, and here "
            + "the engine can write the PDF itself.",
            action: "print.pdf"));

        return stack;
    }

    // ------------------------------------------------------------------------------------
    // Mailbox Account — the reference's Office Account page
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Who is signed in, what the application is wearing, and what it is.
    /// </summary>
    /// <remarks>
    /// The reference's page shows a signed-in identity, a theme picker and an About panel. There
    /// is no identity to show — nothing here signs in to a vendor's service, by scope — so
    /// the accounts themselves stand in its place, which is what a reader would look for on a
    /// page called Mailbox Account anyway.
    /// </remarks>
    private Control BuildAccount()
    {
        var stack = new StackPanel { Spacing = 0, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(Heading("Mailbox Account"));

        var accounts = App.Accounts.All;

        stack.Children.Add(BuildSection(
            "people", "Accounts", hasDropdown: false,
            accounts.Count switch
            {
                0 => "No account yet",
                1 => accounts[0].Account.Address,
                _ => $"{accounts.Count} accounts",
            },
            accounts.Count == 0
                ? "Nothing is set up yet. Add Account walks through a server, or finds one from "
                  + "the address."
                : string.Join(", ", accounts.Select(a => a.Account.Address))
                  + ". Passwords are kept in the desktop's keyring and never in a file.",
            action: "account.settings"));

        stack.Children.Add(BuildSection(
            "theme-colors", "Theme", hasDropdown: false,
            "Mailbox theme",
            "The four the reference ships — Colorful, White, Dark Gray and Black — and any theme "
            + "file of your own. Chosen on the Options page's General tab, where the reference "
            + "puts it too.",
            action: "options.general"));

        stack.Children.Add(BuildSection(
            "info", "About", hasDropdown: false,
            $"Mailbox {Program.ThisAssembly.Stamp}",
            "A mail, calendar and contacts client for Linux, under the GNU General Public "
            + "License version 3. Your mail is one SQLite file per account under this machine's "
            + "own data directory; nothing is sent anywhere but to the servers you set up.",
            action: "about"));

        return stack;
    }

    /// <summary>A page's title, in the size every Backstage page uses.</summary>
    private Control Heading(string text)
    {
        var heading = new TextBlock
        {
            Text = text,
            FontSize = 21,
            Margin = new Thickness(0, 0, 0, 14),
        };

        Bind(heading, TextBlock.ForegroundProperty, "text.primary.brush");
        return heading;
    }

    // ------------------------------------------------------------------------------------
    // Save As — what leaves the store leaves it verbatim
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
    // Open & Export — the importers, arriving one format at a time
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

        // Where the reference puts it, second on the page — and offered for every account rather
        // than greyed for all but one kind, because the server-side half of it is a standard the
        // account's own server either speaks or does not, and the window says which.
        stack.Children.Add(BuildSection(
            "reminder", "Automatic\nReplies", false,
            "Automatic Replies (Out of Office)",
            "Have your mail server answer for you while you are away — with dates, if it can hold "
            + "them — so the replies keep going while this computer is off.",
            action: "away"));

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

        // The account that is actually open, and what it actually is. These two lines were a
        // pair of literals, so this page drew the same address and the same protocol whatever
        // was in the store — including an address that was in no store at all.
        var account = App.Accounts.Default?.Account;

        var lines = new StackPanel { Margin = new Thickness(0, 6), VerticalAlignment = VerticalAlignment.Center };
        var address = new TextBlock { Text = account?.Address ?? "No account yet" };
        Bind(address, TextBlock.ForegroundProperty, "text.primary.brush");
        var kind = new TextBlock
        {
            Text = account?.TypeLabel ?? "Add Account sets one up",
            FontSize = 11,
        };
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
    /// <summary>
    /// Names what one of the Info page's menus holds, and presses one of its entries.
    /// </summary>
    /// <remarks>
    /// A flyout never appears in a capture, so a menu transcribed from a reference shot is a
    /// claim with nothing behind it until something reads it back. This walks the real
    /// <see cref="MenuFlyout"/> the page built — not a second list written for the harness,
    /// which would agree with the page right up until somebody edited one of them.
    /// </remarks>
    internal void PoseMenu(string which, string? press)
    {
        var menu = which.StartsWith("settings", StringComparison.OrdinalIgnoreCase)
            ? AccountSettingsMenu()
            : ToolsMenu();

        foreach (var item in menu.ItemsSource?.OfType<MenuItem>() ?? [])
        {
            var lines = (item.Header as StackPanel)?.Children.OfType<StackPanel>().FirstOrDefault();
            var title = lines?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "(no title)";
            var detail = lines?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text ?? string.Empty;
            Log.Info($"Harness: {which} menu — “{title}” · {detail}{(item.IsEnabled ? string.Empty : "  [greyed]")}");
        }

        if (press is { Length: > 0 }) ActionRequested?.Invoke(this, press);
    }

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
            // The capture's own four, in its own order and wording (rules and alerts/tools.png);
            // only Clean Up Old Items is reworded, its description there naming a file format
            // this application does not write.
            MenuEntry("cleanup", "Mailbox Cleanup…",
                "Manage mailbox size with advanced tools.", "tools.cleanup"),
            MenuEntry("delete", "Empty Deleted Items Folder",
                "Permanently delete all items in the Deleted Items folder.", "tools.emptydeleted"),
            MenuEntry("archive", "Clean Up Old Items…",
                "Move old items to the Archive folder.", "tools.archive"),
            MenuEntry("folder-open", "Set Archive Folder…",
                "Set the destination folder for quick archiving.", "tools.archivefolder"),

            // An addition, and last so the reference's four read as its four. Nothing else here
            // brings back what was permanently deleted, and the holding area exists.
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
