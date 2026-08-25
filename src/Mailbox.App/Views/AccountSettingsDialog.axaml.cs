using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mailbox.Core.Calendars;
using Mailbox.Protocols;
using Mailbox.Store;
using Mailbox.Store.Pim;
using static Mailbox.App.Views.SystemDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// The account list: add, remove, reorder, and choose which one sends by default — with the
/// data files, and the tabs the reference gives the feeds, calendars and address books.
/// </summary>
/// <remarks>
/// A system dialog: the reference draws this one with the desktop's own controls, so it is
/// the desktop's light grey in every theme, with the older coloured toolbar icons and a tab
/// strip of faint hairlines. Every measurement is off the Account Settings captures — the
/// 613×501 window, the 62px white banner naming the page, the tabs 11px under its rule, the
/// list 583 wide and 175 tall with 17px rows, the Close button 73×21 in the corner.
/// <para>
/// The reference gives this seven tabs. Six are here. SharePoint Lists is the one left out: it
/// is a SharePoint feature this application does not have and will not get, and showing it empty
/// would be a promise rather than a gap. Published Calendars is kept — the reference publishes
/// to its own service, but publishing a calendar is CalDAV here, which the calendar module
/// brings.
/// </para>
/// <para>
/// Data Files lists a file per account, as the reference does: each row is a file that can be
/// backed up, copied to another machine or opened somewhere else on its own. Add opens such a
/// file; Remove closes one and leaves it on disk, as the reference's does.
/// </para>
/// <para>
/// RSS Feeds, Internet Calendars and Address Books all act: each lists what the store holds,
/// adds one, renames one and removes one. A button that still cannot do its job — Published
/// Calendars, which wants CalDAV publishing — is live and says what it is waiting for, rather
/// than being greyed with no explanation or left off the toolbar the reference shows.
/// </para>
/// </remarks>
public sealed class AccountSettingsDialog : Window
{
    /// <summary>The window, measured: the reference's client area is 613×501.</summary>
    private const double DialogWidth = 613;
    private const double DialogHeight = 501;

    private readonly ClassicTabControl _tabs = new();
    private readonly TextBlock _bannerHeading = Label(string.Empty, bold: true);
    private readonly TextBlock _bannerText = Label(string.Empty);

    // Email
    private readonly ClassicListView _accounts = new();
    private readonly Button _new;
    private readonly Button _repair;
    private readonly Button _change;
    private readonly Button _remove;
    private readonly Button _setDefault;
    private readonly Button _up;
    private readonly Button _down;
    private readonly Button _changeFolder;
    private readonly TextBlock _deliveryPath = Label(string.Empty, bold: true);
    private readonly TextBlock _deliveryFile = Label(string.Empty);

    // Internet Calendars
    private readonly ClassicListView _calendars = new();
    private readonly Button _calendarNew;
    private readonly Button _calendarChange;
    private readonly Button _calendarRemove;

    // Data Files
    private readonly ClassicListView _files = new();
    private readonly Button _fileAdd;
    private readonly Button _fileOpen;
    private readonly Button _fileSettings;
    private readonly Button _fileDefault;
    private readonly Button _fileRemove;

    /// <summary>What each tab's banner says: the page name in bold, and the sentence under it.</summary>
    private static readonly (string Heading, string Text)[] Banners =
    [
        ("Email Accounts", "You can add or remove an account. You can select an account and change its settings."),
        ("Data Files", "Mailbox Data Files"),
        ("RSS Feeds", "You can add or remove an RSS Feed. You can select an RSS Feed and change its settings."),
        ("Internet Calendars", "You can add or remove an Internet Calendar. You can select a calendar and change its settings."),
        ("Published Calendars", "You can change or remove a calendar you have published. You can select a calendar and change its settings."),
        ("Directories and Address Books", "You can choose a directory or address book below to change or remove it."),
    ];

    /// <summary>The tab names, in the reference's order, for the harness and the tests.</summary>
    public static readonly IReadOnlyList<string> TabNames =
        ["Email", "Data Files", "RSS Feeds", "Internet Calendars", "Published Calendars", "Address Books"];

    /// <summary>True when something changed, so the shell knows to reload.</summary>
    public bool Changed { get; private set; }

    /// <param name="startTab">Which tab to open on: an index into <see cref="TabNames"/>, or a name.</param>
    public AccountSettingsDialog(string? startTab = null)
    {
        Title = "Account Settings";
        Width = DialogWidth;
        Height = DialogHeight;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _new = ToolButton("new", "New...", AddAsync);
        _repair = ToolButton("repair", "Repair...", RepairSelectedAsync);
        _change = ToolButton("change", "Change...", ChangeSelectedAsync);
        _setDefault = ToolButton("default", "Set as Default", SetDefault);
        _remove = ToolButton("remove", "Remove", RemoveSelectedAsync);
        _up = ToolButton("up", string.Empty, () => Move(-1));
        _down = ToolButton("down", string.Empty, () => Move(1));
        _changeFolder = PushButton("Change Folder", ChangeFolderAsync, width: 92);

        _fileAdd = ToolButton("add-file", "Add...", AttachAsync);
        _fileOpen = ToolButton("folder", "Open File Location...", OpenStoreFolder);
        _fileSettings = ToolButton("change", "Settings...", FileSettingsAsync);
        _fileDefault = ToolButton("default", "Set as Default", SetDefaultFile);
        _fileRemove = ToolButton("remove", "Remove", DetachSelectedAsync);

        _calendarNew = ToolButton("new", "New...", NewSubscriptionAsync);
        _calendarChange = ToolButton("change", "Change...", ChangeSubscriptionAsync);
        _calendarRemove = ToolButton("remove", "Remove", RemoveSubscriptionAsync);

        _tabs.AddTab(TabNames[0], EmailTab());
        _tabs.AddTab(TabNames[1], DataFilesTab());
        _tabs.AddTab(TabNames[2], RssTab());
        _tabs.AddTab(TabNames[3], InternetCalendarsTab());
        _tabs.AddTab(TabNames[4], PublishedCalendarsTab());
        _tabs.AddTab(TabNames[5], AddressBooksTab());
        _tabs.SelectionChanged += (_, _) => ShowBanner();

        SystemDialogChrome.Apply(this, Layout());
        Reload();
        ShowBanner();

        if (startTab is { Length: > 0 })
        {
            var index = int.TryParse(startTab, out var n)
                ? n
                : TabNames.ToList().FindIndex(t => t.Equals(startTab, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < TabNames.Count) _tabs.SelectedIndex = index;
        }
    }

    /// <summary>Which tab is open, by name.</summary>
    public string CurrentTab => TabNames[Math.Max(0, _tabs.SelectedIndex)];

    private void ShowBanner()
    {
        var (heading, text) = Banners[Math.Max(0, _tabs.SelectedIndex)];
        _bannerHeading.Text = heading;
        _bannerText.Text = text;
    }

    // ---- The frame ------------------------------------------------------------------------

    private Control Layout()
    {
        var close = PushButton("Close", Close);
        close.IsCancel = true;
        close.IsDefault = true;

        // The bottom band: 38px, the button 9 down and 7 in from the right, measured.
        var bottom = new Panel { Height = 38 };
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, 9, 7, 0);
        bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom);

        var banner = Banner(_bannerHeading, _bannerText);
        DockPanel.SetDock(banner, Dock.Top);
        var rule = BannerRule();
        DockPanel.SetDock(rule, Dock.Top);

        // The tabs stand 11px under the rule and 6px in from either edge.
        _tabs.Margin = new Thickness(6, 11, 6, 0);

        return new DockPanel { Children = { banner, rule, bottom, _tabs } };
    }

    /// <summary>
    /// A page: the toolbar across the top, the list under it, and a block of a fixed height
    /// below the list — 122px, measured — that each tab fills its own way. The list takes the
    /// height that is left, which at the reference's size is its 175px.
    /// </summary>
    private static Grid Page(StackPanel toolbar, ClassicListView list, Panel? below)
    {
        var page = new Grid { RowDefinitions = new RowDefinitions("39,*,122") };

        Grid.SetRow(toolbar, 0);
        page.Children.Add(toolbar);

        list.Margin = new Thickness(8, 0, 6, 0);
        Grid.SetRow(list, 1);
        page.Children.Add(list);

        if (below is not null)
        {
            Grid.SetRow(below, 2);
            page.Children.Add(below);
        }

        return page;
    }

    /// <summary>Places a control in a page's lower block at the offsets measured off the capture.</summary>
    private static T At<T>(T control, double left, double top, double? width = null) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Top;
        control.Margin = new Thickness(left, top, 0, 0);
        if (width is { } w) control.Width = w;
        return control;
    }

    // ---- Email ----------------------------------------------------------------------------

    private Control EmailTab()
    {
        _accounts.Columns = [new ClassicColumn("Name", 282), new ClassicColumn("Type", 281)];
        _accounts.SelectionChanged += (_, _) => UpdateButtons();
        _accounts.ItemActivated += async (_, _) => await ChangeSelectedAsync();

        var toolbar = Toolbar(_new, _repair, _change, _setDefault, _remove, _up, _down);

        var below = new Panel
        {
            Children =
            {
                At(Label("Selected account delivers new messages to the following location:"), 7, 16),
                At(_changeFolder, 9, 36),
                At(_deliveryPath, 112, 41),
                At(_deliveryFile, 112, 60),
            },
        };

        return Page(toolbar, _accounts, below);
    }

    /// <summary>One line in the list: the two columns the reference shows.</summary>
    private static ClassicRow AccountRow(OpenAccount open) => new(
        [
            open.Account.DisplayName.Length > 0 ? open.Account.DisplayName : open.Account.Address,
            open.IsDefault ? $"{open.Account.TypeLabel} (send from this account by default)" : open.Account.TypeLabel,
        ],
        Marked: open.IsDefault,
        Tag: open.Account.Address);

    private OpenAccount? Selected => _accounts.SelectedRow?.Tag is string address ? App.Accounts.Find(address) : null;

    private void Reload()
    {
        _accounts.SetRows(App.Accounts.All.Select(AccountRow).ToList());
        _files.SetRows(App.Accounts.All.Select(FileRow).ToList());
        UpdateButtons();
        UpdateFileButtons();
    }

    private void UpdateButtons()
    {
        var row = Selected;
        _change.IsEnabled = row is not null;
        _repair.IsEnabled = row is not null;
        _remove.IsEnabled = row is not null;
        _setDefault.IsEnabled = row is not null && !row.IsDefault;
        _up.IsEnabled = row is not null && _accounts.SelectedIndex > 0;
        _down.IsEnabled = row is not null && _accounts.SelectedIndex < _accounts.Rows.Count - 1;
        _changeFolder.IsEnabled = row is not null && row.Account.Protocol == MailProtocol.Pop3;

        if (row is null)
        {
            _deliveryPath.Text = string.Empty;
            _deliveryFile.Text = string.Empty;
            return;
        }

        // "work@example.net\Inbox", the account and the folder new mail lands in — the
        // reference's notation, which is the data file's name and the folder's — and the file
        // under it, its middle elided to fit as the reference elides its path.
        _deliveryPath.Text = $"{row.Account.Address}\\{DeliveryFolderName(row)}";
        _deliveryFile.Text = "in data file " + CompactPath(row.Path, 470);
    }

    private static string DeliveryFolderName(OpenAccount account)
    {
        var settings = AccountSettings.Load(App.Settings, account.Account.Address);
        if (settings?.DeliveryFolderId is { } id && account.Mail.GetFolder(id) is { } folder)
        {
            return FolderPath(account, folder);
        }
        return "Inbox";
    }

    /// <summary>The folder's name under its parents', "Inbox\Receipts".</summary>
    private static string FolderPath(OpenAccount account, Folder folder)
    {
        var all = account.Mail.Folders(account.Account.Id).ToDictionary(f => f.Id);
        var parts = new List<string> { folder.Name };
        var parent = folder.ParentId;
        while (parent is { } id && all.TryGetValue(id, out var up) && parts.Count < 16)
        {
            parts.Insert(0, up.Name);
            parent = up.ParentId;
        }
        return string.Join("\\", parts);
    }

    /// <summary>
    /// A path with its middle segments replaced by "..." until it fits, keeping the first ones
    /// and the last two — the reference elides its own file's path the same way.
    /// </summary>
    private static string CompactPath(string path, double maxWidth)
    {
        var typeface = new Typeface(
            Application.Current?.FindResource("ui.fontfamily") as FontFamily ?? FontFamily.Default);
        double Width(string s) => new FormattedText(
            s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, Brushes.Black).Width;

        if (Width(path) <= maxWidth) return path;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var keep = Math.Max(0, parts.Count - 3); keep >= 0; keep--)
        {
            if (parts.Count <= keep + 2) break;
            var head = string.Join("/", parts.Take(keep));
            var tail = string.Join("/", parts.TakeLast(2));
            var candidate = $"/{head}{(keep > 0 ? "/" : string.Empty)}.../{tail}";
            if (Width(candidate) <= maxWidth || keep == 0) return candidate;
        }
        return path;
    }

    private async Task AddAsync()
    {
        var wizard = new AccountWizard();
        await wizard.ShowDialog(this);

        if (wizard.Created is null) return;
        Changed = true;
        Reload();
    }

    private async Task ChangeSelectedAsync()
    {
        if (Selected is not { } row) return;

        var dialog = new ServerSettingsDialog(row.Account);
        await dialog.ShowDialog(this);

        if (!dialog.Saved) return;
        Changed = true;
        Reload();
    }

    /// <summary>
    /// Re-runs autoconfig and offers what it finds. The reference calls this Repair and has it
    /// re-run autodiscover; the equivalent here is to work the servers out again from the
    /// address, which is what fixes an account that was set up wrong or whose provider moved.
    /// </summary>
    private async Task RepairSelectedAsync()
    {
        if (Selected is not { } row) return;

        var current = AccountSettings.Load(App.Settings, row.Account.Address);
        var found = Autoconfig.ForAddress(
            row.Account.Address,
            row.Account.Protocol == MailProtocol.Imap
                ? MailProtocolKind.Imap
                : MailProtocolKind.Pop3);

        var proposed = AccountSettings.From(found);
        var unchanged = current is not null
                        && current.IncomingHost == proposed.IncomingHost
                        && current.IncomingPort == proposed.IncomingPort
                        && current.OutgoingHost == proposed.OutgoingHost
                        && current.OutgoingPort == proposed.OutgoingPort;

        if (unchanged)
        {
            await Confirm.TellAsync(this, "Repair account",
                $"The settings for {row.Account.Address} already match what Mailbox would work "
                + "out from the address. Nothing to change.");
            return;
        }

        var apply = await Confirm.AskAsync(
            this,
            "Repair account",
            $"Replace the server settings for {row.Account.Address} with these?\n\n"
            + $"Incoming    {proposed.IncomingHost}:{proposed.IncomingPort}\n"
            + $"Outgoing    {proposed.OutgoingHost}:{proposed.OutgoingPort}\n\n"
            + (found.IsKnownProvider
                ? "These are the published settings for this provider."
                : "These are a guess from the domain, not published settings."),
            "Replace",
            destructive: false);

        if (!apply) return;

        (current is null ? proposed : proposed with
        {
            LeaveOnServer = current.LeaveOnServer,
            DeleteAfterDays = current.DeleteAfterDays,
            DeliveryFolderId = current.DeliveryFolderId,
        }).Save(App.Settings, row.Account.Address);

        Changed = true;
        Reload();
    }

    private void SetDefault()
    {
        if (Selected is not { } row) return;

        App.AccountOrder.DefaultAddress = row.Account.Address;
        Changed = true;
        Reload();
    }

    private void Move(int direction)
    {
        if (Selected is not { } row) return;

        App.AccountOrder.Move(row.Account.Address, direction);
        Changed = true;
        Reload();
    }

    /// <summary>
    /// Removes an account and everything filed under it. Confirmed first, and the wording says
    /// what actually goes: with POP3 the store may be the only copy left.
    /// </summary>
    private async Task RemoveSelectedAsync()
    {
        if (Selected is not { } row) return;

        var messages = row.Mail.Folders(row.Account.Id).Sum(f => f.Total);

        var confirmed = await Confirm.AskAsync(
            this,
            "Remove account",
            $"Remove {row.Account.Address}?\n\n" +
            (messages > 0
                ? $"{messages:N0} message{(messages == 1 ? "" : "s")} will be deleted with it. " +
                  "Where mail was downloaded and removed from the server, this is the only " +
                  $"copy.\n\nThe file {Path.GetFileName(row.Path)} will be deleted."
                : $"No mail is filed under this account. The file " +
                  $"{Path.GetFileName(row.Path)} will be deleted."),
            "Remove");

        if (!confirmed) return;

        App.Accounts.Remove(row.Account.Address);
        Changed = true;
        Reload();
    }

    /// <summary>
    /// Where a POP3 account's new mail lands: any folder of the account, its Inbox by default.
    /// The reference's dialog offers every data file's folders, because its POP accounts can
    /// deliver into any file; here a file is an account, so the choice is among its own.
    /// </summary>
    private async Task ChangeFolderAsync()
    {
        if (Selected is not { } row) return;

        var settings = AccountSettings.Load(App.Settings, row.Account.Address);
        var current = settings?.DeliveryFolderId ?? row.Mail.FolderWithRole(row.Account.Id, FolderRole.Inbox)?.Id;

        var picker = new FolderPickerDialog(
            "New Email Delivery Location",
            "Choose a folder for new email:",
            [row],
            (row, current),
            allowRoot: false);
        await picker.ShowDialog(this);

        if (picker.Result is not { Folder: { } folder }) return;
        if (settings is null)
        {
            await Confirm.TellAsync(this, "Change Folder",
                $"{row.Account.Address} has no server settings yet, so there is nothing to deliver. "
                + "Set its servers with Change... first.");
            return;
        }

        SetDeliveryFolder(row, settings, folder);
    }

    /// <summary>Records the folder; the Inbox is recorded as no choice at all, which is what it means.</summary>
    private void SetDeliveryFolder(OpenAccount row, AccountSettings settings, Folder folder)
    {
        var inbox = row.Mail.FolderWithRole(row.Account.Id, FolderRole.Inbox);
        (settings with { DeliveryFolderId = folder.Id == inbox?.Id ? null : folder.Id })
            .Save(App.Settings, row.Account.Address);
        Changed = true;
        UpdateButtons();
    }

    // ---- Data Files -----------------------------------------------------------------------

    private Control DataFilesTab()
    {
        _files.Columns = [new ClassicColumn("Name", 141), new ClassicColumn("Location", 442)];
        _files.SelectionChanged += (_, _) => UpdateFileButtons();
        _files.ItemActivated += async (_, _) => await FileSettingsAsync();

        var toolbar = Toolbar(_fileAdd, _fileSettings, _fileDefault, _fileRemove, _fileOpen);

        var paragraph = Paragraph(
            "Select a data file in the list, then click Settings for more details or click Open "
            + "File Location to display the folder that contains the data file. To move or copy "
            + "these files, you must first quit Mailbox.");
        paragraph.Width = 476;

        // The button stands 12px under the list at the page's right; a wider window keeps it there.
        var more = PushButton("Tell Me More...", TellMeMoreAsync, width: 90);
        more.HorizontalAlignment = HorizontalAlignment.Right;
        more.VerticalAlignment = VerticalAlignment.Top;
        more.Margin = new Thickness(0, 12, 7, 0);

        return Page(toolbar, _files, new Panel { Children = { At(paragraph, 7, 11), more } });
    }

    private static ClassicRow FileRow(OpenAccount open) => new(
        [Path.GetFileName(open.Path), open.Path],
        Marked: open.IsDefault,
        Tag: open.Account.Address);

    private OpenAccount? SelectedFile => _files.SelectedRow?.Tag is string address ? App.Accounts.Find(address) : null;

    private void UpdateFileButtons()
    {
        var row = SelectedFile;
        _fileSettings.IsEnabled = row is not null;
        _fileDefault.IsEnabled = row is not null && !row.IsDefault;
        _fileRemove.IsEnabled = row is not null;
    }

    /// <summary>Opens an account file from elsewhere: a backup, or one detached earlier.</summary>
    private async Task AttachAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Mailbox Data File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Mailbox data files") { Patterns = ["*.db"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        var (account, error) = App.Accounts.Attach(path);
        if (account is null)
        {
            await Confirm.TellAsync(this, "Open Mailbox Data File", error ?? "The file could not be opened.");
            return;
        }

        Changed = true;
        Reload();
        _files.SelectedIndex = _files.Rows.ToList().FindIndex(r => Equals(r.Tag, account.Account.Address));
    }

    private async Task FileSettingsAsync()
    {
        if (SelectedFile is not { } row) return;

        var dialog = new DataFileSettingsDialog(row);
        await dialog.ShowDialog(this);

        if (!dialog.Changed) return;
        Changed = true;
        Reload();
    }

    private void SetDefaultFile()
    {
        if (SelectedFile is not { } row) return;

        App.AccountOrder.DefaultAddress = row.Account.Address;
        Changed = true;
        Reload();
    }

    /// <summary>
    /// Closes a data file and leaves it on disk, as the reference's Remove does here — the
    /// account disappears from the list, and its mail is in a file that Add can open again.
    /// </summary>
    private async Task DetachSelectedAsync()
    {
        if (SelectedFile is not { } row) return;

        var confirmed = await Confirm.AskAsync(
            this,
            "Remove data file",
            $"Close {Path.GetFileName(row.Path)}?\n\n"
            + $"{row.Account.Address} will no longer appear in Mailbox. Its mail is not deleted: "
            + "the file is moved to the detached folder beside the accounts, and Add... can open "
            + "it again.",
            "Remove");

        if (!confirmed) return;

        var moved = Detach(row);
        if (moved is not null)
        {
            await Confirm.TellAsync(this, "Remove data file", $"The file was moved to\n{moved}");
        }
    }

    private string? Detach(OpenAccount row)
    {
        var moved = App.Accounts.Detach(row.Account.Address);
        Changed = true;
        Reload();
        return moved;
    }

    private void OpenStoreFolder()
    {
        try
        {
            var folder = App.Accounts.Directory_;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Mailbox.Core.Diagnostics.Log.Warn("Could not open the store's folder.", ex);
        }
    }

    private Task TellMeMoreAsync() => Confirm.TellAsync(this, "Mailbox Data Files",
        "Each account keeps its mail in a file of its own, named after the address, in the "
        + "accounts folder. Copy a file somewhere safe and that account is backed up; open it "
        + "again with Add... on this tab, here or on another machine. Remove closes a file "
        + "without deleting it. Settings... shows a file's size and can compact it after a lot "
        + "of mail has been deleted.");

    // ---- RSS Feeds ------------------------------------------------------------------------

    private Control RssTab()
    {
        var list = new ClassicListView
        {
            Columns = [new ClassicColumn("Feed Name", 338), new ClassicColumn("Last Updated On", 245)],
        };

        void Fill()
        {
            list.SetRows(
            [
                .. App.Feeds.All.Select(f => new ClassicRow(
                    [f.Name, f.LastChecked is { } when ? when.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) : "(never)"],
                    Tag: f.Url)),
            ]);
        }

        Fill();

        var change = ToolButton("change", "Change...", async () =>
        {
            if (list.SelectedRow?.Tag is not string url) return;
            var named = await Prompt.AskAsync(this, "RSS Feed Options", "Feed Name:", list.SelectedRow.Cells[0]);
            if (named is null) return;

            App.Feeds.Rename(url, named);
            Fill();
        });

        var remove = ToolButton("remove", "Remove", () =>
        {
            if (list.SelectedRow?.Tag is not string url) return;
            App.Feeds.Remove(url);
            Fill();
        });

        void Enable()
        {
            change.IsEnabled = list.SelectedRow is not null;
            remove.IsEnabled = list.SelectedRow is not null;
        }

        list.SelectionChanged += (_, _) => Enable();
        Enable();

        var toolbar = Toolbar(
            ToolButton("new", "New...", async () =>
            {
                var dialog = new SubscriptionDialog(
                    "New RSS Feed",
                    "Enter the location of the RSS Feed you want to add to Mailbox:",
                    "Example: http://www.example.com/feed/main.xml");
                await dialog.ShowDialog(this);
                if (dialog.Location is not { Length: > 0 } location) return;

                // Named after its address until the first download, which is when the feed says
                // what it is called — a subscription that could not be read still shows here.
                var name = Uri.TryCreate(location, UriKind.Absolute, out var uri) ? uri.Host : location;
                App.Feeds.Add(location, name);
                Fill();
            }),
            change, remove);

        var paragraph = Paragraph(
            "Subscribed RSS Feeds are checked once during each download interval. This prevents "
            + "your RSS Feed from possibly being suspended by an RSS publisher.");
        paragraph.Width = 560;

        var below = new Panel
        {
            Children =
            {
                At(Label("New items are delivered to a folder of their own under RSS Feeds."), 7, 9),
                At(paragraph, 8, 73),
            },
        };

        return Page(toolbar, list, below);
    }

    // ---- Internet Calendars ---------------------------------------------------------------

    private Control InternetCalendarsTab()
    {
        _calendars.Columns =
        [
            new ClassicColumn("Internet Calendar", 282),
            new ClassicColumn("Size", 84),
            new ClassicColumn("Last Updated on", 217),
        ];

        _calendars.SelectionChanged += (_, _) => EnableSubscriptionButtons();
        FillSubscriptions();

        var paragraph = Paragraph(
            "Subscribed Internet Calendars are checked once during each download interval. This "
            + "prevents your list from possibly being suspended by the publisher of an Internet "
            + "Calendar.");
        paragraph.Width = 560;

        return Page(
            Toolbar(_calendarNew, _calendarChange, _calendarRemove),
            _calendars,
            new Panel { Children = { At(paragraph, 8, 9) } });
    }

    /// <summary>
    /// The subscribed calendars: a read-only collection with an address of its own. Nothing else
    /// in the store is both — a calendar made here has no address, and one belonging to an
    /// account is not read-only — so the pair is what tells a subscription apart.
    /// </summary>
    private static IReadOnlyList<Collection> Subscriptions()
        => [.. App.Pim.Collections(CollectionKind.Events).Where(c => c.IsReadOnly && c.DavUrl is { Length: > 0 })];

    private void FillSubscriptions()
    {
        _calendars.SetRows(
        [
            .. Subscriptions().Select(c => new ClassicRow(
                [
                    c.DisplayName,
                    MailboxCleanupDialog.Size(
                        App.Pim.Items(c.Id).Sum(i => (long)System.Text.Encoding.UTF8.GetByteCount(i.RawPayload))),

                    // Never checked is not the same as checked and found empty, and the column
                    // is the only place a reader can tell a dead subscription from a quiet one.
                    c.LastCheckedUtc is { } when
                        ? when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        : "(never)",
                ],
                Tag: c.Id)),
        ]);

        EnableSubscriptionButtons();
    }

    private void EnableSubscriptionButtons()
    {
        var chosen = _calendars.SelectedRow is not null;
        _calendarChange.IsEnabled = chosen;
        _calendarRemove.IsEnabled = chosen;
    }

    private async Task NewSubscriptionAsync()
    {
        var dialog = new SubscriptionDialog(
            "New Internet Calendar Subscription",
            "Enter the location of the Internet Calendar you want to add to Mailbox:",
            "Example: webcal://www.example.com/calendars/Calendar.ics");
        await dialog.ShowDialog(this);
        if (dialog.Location is not { Length: > 0 } location) return;

        if (Subscribe(location) is null)
        {
            await Confirm.TellAsync(
                this, "New Internet Calendar Subscription", "That is not a calendar address.");
        }
    }

    /// <summary>
    /// Subscribes and refreshes the list, or null when what was typed is not an address. Apart
    /// from the button because a modal question is what the harness cannot press.
    /// </summary>
    private Collection? Subscribe(string location)
    {
        if (!CalendarSubscription.TryAddress(location, out var address)) return null;

        // Read-only, as the calendar module's own subscribe makes it: what a publisher sends is
        // theirs, and an edit here would queue a PUT to a server that never offered one.
        var calendar = App.Pim.AddCollection(
            CollectionKind.Events,
            CalendarSubscription.SuggestedName(address),
            App.CalendarOptions.DefaultColour,
            account: string.Empty,
            davUrl: address.ToString(),
            readOnly: true);

        Changed = true;
        FillSubscriptions();
        Mailbox.Core.Diagnostics.Log.Info($"Account Settings: subscribed to {address} as collection {calendar.Id}.");
        return calendar;
    }

    private async Task ChangeSubscriptionAsync()
    {
        if (_calendars.SelectedRow?.Tag is not long id || App.Pim.Collection(id) is not { } calendar) return;

        // The reference opens a Subscription Options dialog here. No capture of it exists, and
        // of what it carries only the name has anywhere to go — the download limit and the
        // attachment switch describe a service this does not talk to — so this asks for the one
        // field, the way the RSS tab beside it asks for a feed's.
        var named = await Prompt.AskAsync(this, "Internet Calendar Options", "Folder Name:", calendar.DisplayName);
        if (string.IsNullOrWhiteSpace(named)) return;

        RenameSubscription(id, named);
    }

    private void RenameSubscription(long id, string name)
    {
        App.Pim.RenameCollection(id, name.Trim());
        Changed = true;
        FillSubscriptions();
    }

    private async Task RemoveSubscriptionAsync()
    {
        if (_calendars.SelectedRow?.Tag is not long id || App.Pim.Collection(id) is not { } calendar) return;

        if (!await Confirm.AskAsync(
                this,
                "Account Settings",
                $"Remove the \u201c{calendar.DisplayName}\u201d Internet Calendar? What was downloaded from it "
                + "goes too. The calendar itself belongs to whoever publishes it and is left alone.",
                "Remove"))
        {
            return;
        }

        RemoveSubscription(id);
    }

    private void RemoveSubscription(long id)
    {
        App.Pim.RemoveCollection(id);
        Changed = true;
        FillSubscriptions();
        Mailbox.Core.Diagnostics.Log.Info($"Account Settings: subscription {id} removed.");
    }

    // ---- Published Calendars --------------------------------------------------------------

    private Control PublishedCalendarsTab()
    {
        var list = new ClassicListView
        {
            Columns = [new ClassicColumn("Calendar", 282), new ClassicColumn("Location", 301)],
        };

        var change = ToolButton("change", "Change...", () => Task.CompletedTask);
        var remove = ToolButton("remove", "Remove", () => { });
        change.IsEnabled = false;
        remove.IsEnabled = false;

        var paragraph = Paragraph(
            "Publishing a calendar arrives with the calendar module, over CalDAV. A calendar you "
            + "publish will be listed here.");
        paragraph.Width = 560;

        return Page(Toolbar(change, remove), list, new Panel { Children = { At(paragraph, 8, 9) } });
    }

    // ---- Address Books --------------------------------------------------------------------

    private Control AddressBooksTab()
    {
        var list = new ClassicListView
        {
            Columns = [new ClassicColumn("Name", 282), new ClassicColumn("Type", 281)],
        };

        // What the People module has: the local address books, and the ones a CardDAV account
        // brought. The type column says which, as the reference's does for its own kinds.
        void Fill()
        {
            list.SetRows(
            [
                .. App.Contacts.AddressBooks().Select(book => new ClassicRow(
                    [book.DisplayName, book.DavUrl is { Length: > 0 } ? "CardDAV" : "Mailbox Address Book"],
                    Tag: book.Id)),
            ]);
        }

        Fill();

        var change = ToolButton("change", "Change...", () => Task.CompletedTask);
        var remove = ToolButton("remove", "Remove", () =>
        {
            if (list.SelectedRow?.Tag is not long id || App.Contacts.AddressBooks().Count <= 1) return;
            App.Contacts.Repository.RemoveCollection(id);
            Changed = true;
            Fill();
        });

        change.IsEnabled = false;
        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedRow is not null && App.Contacts.AddressBooks().Count > 1;
        remove.IsEnabled = false;

        var toolbar = Toolbar(
            ToolButton("book", "New...", async () =>
            {
                var name = await Prompt.AskAsync(this, "New Address Book", "Name:", "Contacts");
                if (string.IsNullOrWhiteSpace(name)) return;

                App.Contacts.Repository.AddCollection(Mailbox.Store.Pim.CollectionKind.Contacts, name.Trim());
                Changed = true;
                Fill();
            }),
            change, remove);

        return Page(toolbar, list, null);
    }

    /// <summary>
    /// What a button says when its feature belongs to a later phase: which part of the
    /// application brings it, rather than a silent nothing.
    /// </summary>
    private Task Later(string title, string message) => Confirm.TellAsync(this, title, message);

    // ---- The harness ----------------------------------------------------------------------

    /// <summary>
    /// Presses a button for the fidelity harness, which cannot click, and says what the store
    /// holds afterwards. <c>MAILBOX_ACCOUNTS_ACTION</c>: <c>select:&lt;n&gt;</c> then one of
    /// <c>setdefault</c>, <c>up</c>, <c>down</c>, <c>changefolder:&lt;name&gt;</c>,
    /// <c>filedefault</c>, <c>detach</c>, <c>attach:&lt;path&gt;</c>, <c>compact</c>; and for the
    /// Internet Calendars tab <c>subscribe:&lt;url&gt;</c>, <c>calendar:&lt;n&gt;</c>,
    /// <c>renamecalendar:&lt;name&gt;</c>, <c>removecalendar</c>. The ones that ask a question
    /// first are answered here rather than through their dialog, because a modal question is
    /// what a harness cannot press.
    /// </summary>
    internal void Harness(string actions)
    {
        foreach (var raw in actions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (action, argument) = raw.Split(':', 2) is [var a, var b] ? (a, b) : (raw, string.Empty);
            switch (action.ToLowerInvariant())
            {
                case "select":
                    _accounts.SelectedIndex = int.Parse(argument, CultureInfo.InvariantCulture);
                    _files.SelectedIndex = _accounts.SelectedIndex;
                    break;
                case "tab":
                    _tabs.SelectedIndex = int.Parse(argument, CultureInfo.InvariantCulture);
                    break;
                case "setdefault": Press(_setDefault); break;
                case "filedefault": Press(_fileDefault); break;
                case "up": Press(_up); break;
                case "down": Press(_down); break;
                case "changefolder":
                    if (Selected is { } row && AccountSettings.Load(App.Settings, row.Account.Address) is { } settings
                        && row.Mail.Folders(row.Account.Id).FirstOrDefault(f => f.Name.Contains(argument, StringComparison.OrdinalIgnoreCase)) is { } folder)
                    {
                        SetDeliveryFolder(row, settings, folder);
                    }
                    break;
                case "detach":
                    if (SelectedFile is { } file) Mailbox.Core.Diagnostics.Log.Info($"Harness: detached to {Detach(file)}");
                    break;
                case "attach":
                    var (attached, error) = App.Accounts.Attach(argument);
                    Mailbox.Core.Diagnostics.Log.Info($"Harness: attach {(attached is null ? "refused: " + error : "opened " + attached.Path)}");
                    Reload();
                    break;
                case "compact":
                    if (SelectedFile is { } target) Mailbox.Core.Diagnostics.Log.Info($"Harness: compacted to {target.Store.Compact():N0} bytes");
                    break;
                case "calendar":
                    _calendars.SelectedIndex = int.Parse(argument, CultureInfo.InvariantCulture);
                    break;
                case "subscribe":
                    Mailbox.Core.Diagnostics.Log.Info(
                        $"Harness: subscribe {(Subscribe(argument) is { } added ? $"added collection {added.Id}" : "refused")}.");
                    break;
                case "renamecalendar":
                    if (_calendars.SelectedRow?.Tag is long toRename) RenameSubscription(toRename, argument);
                    break;
                case "removecalendar":
                    if (_calendars.SelectedRow?.Tag is long toRemove) RemoveSubscription(toRemove);
                    break;
            }
        }

        // What the store says now, for the log to be read back.
        Mailbox.Core.Diagnostics.Log.Info($"Harness: accounts are {string.Join(", ", App.Accounts.All.Select(a => a.Account.Address + (a.IsDefault ? " (default)" : string.Empty)))}.");
        foreach (var account in App.Accounts.All)
        {
            var settings = AccountSettings.Load(App.Settings, account.Account.Address);
            Mailbox.Core.Diagnostics.Log.Info($"Harness: {account.Account.Address} delivers to {DeliveryFolderName(account)}"
                + (settings?.DeliveryFolderId is { } id ? $" (folder {id})" : " (the Inbox, no choice recorded)") + ".");
        }
        Mailbox.Core.Diagnostics.Log.Info($"Harness: buttons — change {(_change.IsEnabled ? "on" : "off")}, "
            + $"set default {(_setDefault.IsEnabled ? "on" : "off")}, up {(_up.IsEnabled ? "on" : "off")}, "
            + $"down {(_down.IsEnabled ? "on" : "off")}, change folder {(_changeFolder.IsEnabled ? "on" : "off")}, "
            + $"calendar change {(_calendarChange.IsEnabled ? "on" : "off")}, "
            + $"calendar remove {(_calendarRemove.IsEnabled ? "on" : "off")}.");

        foreach (var subscription in Subscriptions())
        {
            Mailbox.Core.Diagnostics.Log.Info(
                $"Harness: subscription \u201c{subscription.DisplayName}\u201d \u2192 {subscription.DavUrl}, last checked "
                + (subscription.LastCheckedUtc is { } at ? at.ToString("u", CultureInfo.InvariantCulture) : "never") + ".");
        }
    }

    private static void Press(Button button)
    {
        if (!button.IsEnabled) return;
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }
}
