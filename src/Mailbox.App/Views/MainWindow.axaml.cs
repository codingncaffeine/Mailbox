using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Diagnostics;
using Mailbox.Core;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

public partial class MainWindow : Window
{
    private readonly RibbonView _ribbon;

    public MainWindow()
    {
        InitializeComponent();

        // Set in code rather than XAML: Avalonia 12.1 exposes TextOptions through Get/Set
        // methods, not public attached-property fields. See Theming/TextRendering.cs.
        TextRendering.Apply(this);

        SetUpTitleBar();

        var layout = DefaultRibbonLayouts.Mail;
        var layoutMode = ShellLayoutModes.Resolve();

        _ribbon = new RibbonView(App.Commands, layout)
        {
            DisplayMode = Environment.GetEnvironmentVariable("MAILBOX_RIBBON")?.ToLowerInvariant()
                switch
                {
                    "classic" => RibbonDisplayMode.Classic,
                    "collapsed" => RibbonDisplayMode.Collapsed,
                    _ => RibbonDisplayMode.Simplified,
                },
        };
        _ribbon.CommandInvoked += OnRibbonCommand;
        _ribbon.BackstageRequested += (_, _) => ShowBackstage();
        this.FindControl<ContentControl>("RibbonHost")!.Content = _ribbon;

        // The rendering diagnostics the text investigation needs go to the log, not the status
        // bar. In the reference the bar carries the item counts and nothing else.
        Log.Info($"Text rendering: {TextRendering.Describe()}");
        Log.Info($"UI font: {App.Fonts.Resolve("Segoe UI").Rendered}");
        Log.Info($"Body font: {App.Fonts.Resolve("Calibri").Rendered}");

        var shell = new ShellViewModel(App.Themes, App.Commands, layout, layoutMode, App.Accounts);

        WireRail(shell);
        WireWindowMenu();
        WireToolbarCommands(shell);
        WireAccountButton(shell);
        DataContext = shell;

        ApplyHarnessState(shell);

        // Lets the fidelity harness capture the peek states, which a screenshot otherwise
        // cannot reach because they need a click.
        switch (Environment.GetEnvironmentVariable("MAILBOX_PEEK")?.ToLowerInvariant())
        {
            case "calendar": Opened += (_, _) => TogglePeek(); break;
            case "docked": Opened += (_, _) => DockPeek(); break;
            case "backstage": Opened += (_, _) => ShowBackstage(); break;

            // Opens the ribbon's display-options menu, so a capture can check a popup's colours.
            case "menu":
                Opened += async (_, _) =>
                {
                    _ribbon?.OpenDisplayOptions();
                    await Task.Yield();
                };
                break;

            // Options is its own window, so the harness captures that rather than the shell.
            case "options":
                Opened += async (_, _) =>
                {
                    var dialog = new OptionsWindow(App.Themes);
                    dialog.Opened += async (_, _) =>
                    {
                        await Task.Delay(700);
                        if (WindowCapture.RequestedPath is { } path)
                        {
                            WindowCapture.Capture(dialog, path, WindowCapture.Scale);
                            Console.WriteLine($"Captured {path}");
                        }
                        Environment.Exit(0);
                    };
                    await dialog.ShowDialog(this);
                };
                break;
        }
    }

    /// <summary>Opens the File view over everything, with its back arrow to return.</summary>
    private void ShowBackstage()
    {
        var host = this.FindControl<ContentControl>("BackstageHost")!;
        var backstage = new BackstageView();
        backstage.OptionsRequested += async (_, _) => await ShowOptions();
        backstage.AddAccountRequested += async (_, _) => await AddAccountAsync();
        backstage.ActionRequested += async (_, action) => await BackstageActionAsync(action);
        backstage.CloseRequested += (_, _) => CloseBackstage();

        host.Content = backstage;
        host.IsVisible = true;
    }

    private void CloseBackstage()
    {
        var host = this.FindControl<ContentControl>("BackstageHost")!;
        host.IsVisible = false;
        host.Content = null;
    }

    /// <summary>Opens the Options dialog modally over the shell.</summary>
    private async Task ShowOptions()
        => await new OptionsWindow(App.Themes).ShowDialog<bool>(this);

    // ------------------------------------------------------------------------------------
    // Calendar peek and dock
    // ------------------------------------------------------------------------------------

    private CalendarPeek? _floatingPeek;

    /// <summary>
    /// Gives each rail module a command. Calendar toggles the peek; the rest are inert until
    /// their modules exist.
    /// </summary>
    private void WireRail(ShellViewModel shell)
    {
        foreach (var tab in shell.Modules)
        {
            var module = tab.Module;
            tab.Activate = new RelayCommand(() =>
            {
                if (module == MailboxModule.Calendar) TogglePeek();
            });
        }
    }

    private void TogglePeek()
    {
        if (_floatingPeek is not null)
        {
            ClosePeek();
            return;
        }

        var peek = new CalendarPeek(DateTime.Now, docked: false);
        peek.DockRequested += (_, _) => DockPeek();

        // Anchored just right of the rail, near the icon that opened it — the position
        // the reference application uses so the peek reads as belonging to that module.
        Canvas.SetLeft(peek, 52);
        Canvas.SetTop(peek, 8);

        this.FindControl<Canvas>("PeekLayer")!.Children.Add(peek);
        _floatingPeek = peek;
    }

    private void ClosePeek()
    {
        if (_floatingPeek is null) return;
        this.FindControl<Canvas>("PeekLayer")!.Children.Remove(_floatingPeek);
        _floatingPeek = null;
    }

    /// <summary>
    /// The little corner button: the floating peek becomes a docked panel down the right edge,
    /// where it takes the reading pane's place until closed.
    /// </summary>
    private void DockPeek()
    {
        ClosePeek();
        if (DataContext is not ShellViewModel shell) return;

        var docked = new CalendarPeek(DateTime.Now, docked: true);
        docked.CloseRequested += (_, _) => UndockPeek();

        this.FindControl<ContentControl>("DockHost")!.Content = docked;
        shell.IsCalendarDocked = true;
    }

    private void UndockPeek()
    {
        if (DataContext is not ShellViewModel shell) return;
        this.FindControl<ContentControl>("DockHost")!.Content = null;
        shell.IsCalendarDocked = false;
    }

    /// <summary>
    /// Wires the client-side-decorated caption: buttons on the right, drag anywhere on the
    /// bar, double-click to maximize. Avalonia gives us the extended client area but none of
    /// the behaviour, so all of it is ours.
    /// </summary>
    /// <summary>
    /// Width of the invisible band along each window edge that starts a resize.
    /// </summary>
    /// <remarks>
    /// The window carries no system decoration — that frame is drawn square, and against a
    /// window with rounded corners it left a hard right angle around the curve with a
    /// transparent wedge between the two. Dropping it takes the compositor's resize borders
    /// with it, so the window grows its own.
    /// </remarks>
    private const double ResizeMargin = 6;

    private static readonly (WindowEdge Edge, StandardCursorType Cursor)[] EdgeCursors =
    [
        (WindowEdge.NorthWest, StandardCursorType.TopLeftCorner),
        (WindowEdge.NorthEast, StandardCursorType.TopRightCorner),
        (WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner),
        (WindowEdge.SouthEast, StandardCursorType.BottomRightCorner),
        (WindowEdge.North, StandardCursorType.TopSide),
        (WindowEdge.South, StandardCursorType.BottomSide),
        (WindowEdge.West, StandardCursorType.LeftSide),
        (WindowEdge.East, StandardCursorType.RightSide),
    ];

    /// <summary>Which edge the pointer is over, or null when it is not near one.</summary>
    private WindowEdge? EdgeAt(Point p)
    {
        if (WindowState != WindowState.Normal) return null;

        var west = p.X <= ResizeMargin;
        var east = p.X >= Bounds.Width - ResizeMargin;
        var north = p.Y <= ResizeMargin;
        var south = p.Y >= Bounds.Height - ResizeMargin;

        return (north, south, west, east) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (true, _, _, true) => WindowEdge.NorthEast,
            (_, true, true, _) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.North,
            (_, true, _, _) => WindowEdge.South,
            (_, _, true, _) => WindowEdge.West,
            (_, _, _, true) => WindowEdge.East,
            _ => null,
        };
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        Cursor = EdgeAt(e.GetPosition(this)) is { } edge
            ? new Cursor(EdgeCursors.First(c => c.Edge == edge).Cursor)
            : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (EdgeAt(e.GetPosition(this)) is not { } edge) return;

        e.Handled = true;
        BeginResizeDrag(edge, e);
    }

    /// <summary>
    /// Puts the shell into a state a screenshot can be taken of. Every one of these is
    /// reachable by clicking, and a capture cannot click — so a control wired to state nobody
    /// photographs is a control nobody has actually checked.
    /// </summary>
    private static void ApplyHarnessState(ShellViewModel shell)
    {
        var wanted = Environment.GetEnvironmentVariable("MAILBOX_STATE");
        if (string.IsNullOrWhiteSpace(wanted)) return;

        foreach (var state in wanted.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (state.Trim())
            {
                case "unread": shell.ShowUnread.Execute(null); break;
                case "sort-from": shell.SortBy("From"); break;
                case "sort-subject": shell.SortBy("Subject"); break;
                case "sort-asc": shell.ToggleSortDirection.Execute(null); break;
                case "group-collapsed": shell.ToggleGroup.Execute(null); break;
                case "nav-collapsed": shell.ToggleNav.Execute(null); break;
                case "no-reading": shell.HideReadingPane.Execute(null); break;
                case "zoom-in": shell.ZoomIn.Execute(null); break;
                case "zoom-out": shell.ZoomOut.Execute(null); break;
                default: Log.Warn($"Unknown MAILBOX_STATE: {state}"); break;
            }
        }
    }

    /// <summary>
    /// The window menu behind the app icon. With no system frame the window owns this too.
    /// </summary>
    private void WireWindowMenu()
    {
        if (this.FindControl<Button>("WindowMenuButton") is not { } button) return;

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        MenuItem Item(string header, Action run, Func<bool>? enabled = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled?.Invoke() ?? true };
            item.Click += (_, _) => run();
            return item;
        }

        void Rebuild()
        {
            var maximized = WindowState == WindowState.Maximized;
            menu.ItemsSource = new[]
            {
                Item("Restore", () => WindowState = WindowState.Normal, () => maximized),
                Item("Minimize", () => WindowState = WindowState.Minimized),
                Item("Maximize", () => WindowState = WindowState.Maximized, () => !maximized),
                Item("Close", Close),
            };
        }

        Rebuild();
        button.Flyout = menu;
        button.Click += (_, _) => Rebuild();
    }

    /// <summary>
    /// Routes the toolbar buttons through the same handler the ribbon uses.
    /// </summary>
    /// <remarks>
    /// The Quick Access Toolbar, the reading pane's actions and the Modern command bar are all
    /// built from the same view model, and none of them was bound to anything — the identical
    /// command was live on the ribbon and dead everywhere else. Anything that stands for a
    /// command routes to one place, so wiring a command wires every way of reaching it.
    /// </remarks>
    private void WireToolbarCommands(ShellViewModel shell)
    {
        foreach (var button in shell.QuickAccess
                     .Concat(shell.ReadingPaneActions)
                     .Concat(shell.CommandBar))
        {
            var id = button.Command;
            button.Invoke = new RelayCommand(() => RunCommand(id));
        }
    }

    /// <summary>
    /// Hangs the account panel off the avatar. Done here rather than in
    /// <see cref="SetUpTitleBar"/> because that runs before the view model exists.
    /// </summary>
    private void WireAccountButton(ShellViewModel shell)
    {
        if (this.FindControl<Button>("AccountButton") is not { } account) return;

        account.Flyout = AccountFlyout.Build(
            shell.AccountAddress,
            shell.AccountInitial,
            onViewAccount: () => shell.StatusRight = "View account — not wired yet",
            onAddAccount: () => shell.StatusRight = "Add an account — not wired yet");
    }

    private void SetUpTitleBar()
    {
        var caption = new CaptionButtons(this);
        this.FindControl<ContentControl>("CaptionHost")!.Content = caption;

        if (Environment.GetEnvironmentVariable("MAILBOX_HOVER") is { Length: > 0 } hovered)
        {
            Opened += (_, _) => caption.ForceHover(hovered.ToLowerInvariant());
        }

        if (this.FindControl<Control>("TitleBar") is not { } bar) return;

        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            // A double-click on the bar toggles maximize, as every desktop expects.
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return;
            }

            BeginMoveDrag(e);
        };
    }

    /// <summary>
    /// Phase 0 has no behaviour behind the commands yet; this proves the catalogue round-trip
    /// from ribbon click to a resolved command. Phases 2 onward attach real handlers.
    /// </summary>
    private void OnRibbonCommand(object? sender, RibbonCommandEventArgs e) => RunCommand(e.Command);

    /// <summary>
    /// The single place a command arrives, whichever control raised it. Phases 2 onward replace
    /// the placeholder with real handlers; until then every route reports the same thing, which
    /// is at least honest about what is and is not built.
    /// </summary>
    private void RunCommand(CommandId id)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (!App.Commands.TryGet(id, out var command)) return;

        Log.Debug($"Command invoked: {command.Id}");

        if (id == MailCommands.SendReceiveAll.Id) { _ = SendReceiveAsync(shell); return; }
        if (id == MailCommands.WorkOffline.Id) { ToggleWorkOffline(shell); return; }

        shell.StatusRight = $"{command.Label} — not wired yet ({command.Id})";
    }

    /// <summary>
    /// Send/Receive All Folders. Runs off the UI thread and reports through the status bar;
    /// the button is not disabled because a second press should be able to cancel, which is
    /// what Phase 8's progress dialog will add.
    /// </summary>
    private async Task SendReceiveAsync(ShellViewModel shell)
    {
        if (_transferring) return;

        var accounts = AccountConnections();
        if (accounts.Count == 0)
        {
            shell.StatusRight = "No account is set up yet. File, Add Account.";
            return;
        }

        _transferring = true;
        void OnProgress(object? _, PollProgress p) =>
            Dispatcher.UIThread.Post(() => shell.StatusRight = $"{p.Stage} {p.Account}…");

        App.Transfer.Progress += OnProgress;

        try
        {
            var result = await Task.Run(() =>
                App.Transfer.RunAsync(accounts, DateTimeOffset.UtcNow));

            shell.StatusRight = result.Summary();
            shell.Refresh();
        }
        catch (Exception ex)
        {
            Log.Crash("send/receive", ex);
            shell.StatusRight = "Send/receive could not finish. See the log.";
        }
        finally
        {
            App.Transfer.Progress -= OnProgress;
            _transferring = false;
        }
    }

    private bool _transferring;

    /// <summary>
    /// Everything the Backstage's Account Information page can ask for. One place, so a new
    /// entry in either menu is a case here rather than another handler wired somewhere else.
    /// </summary>
    private async Task BackstageActionAsync(string action)
    {
        if (DataContext is not ShellViewModel shell) return;

        switch (action)
        {
            case "account.settings":
            {
                var dialog = new AccountSettingsDialog();
                await dialog.ShowDialog(this);
                if (dialog.Changed) { CloseBackstage(); shell.Refresh(); }
                break;
            }

            case "account.password":
                if (RequireAccount(shell) is { } forPassword)
                {
                    await new UpdatePasswordDialog(forPassword).ShowDialog(this);
                }

                break;

            case "account.server":
                if (RequireAccount(shell) is { } forServer)
                {
                    var dialog = new ServerSettingsDialog(forServer);
                    await dialog.ShowDialog(this);
                    if (dialog.Saved) { CloseBackstage(); shell.Refresh(); }
                }

                break;

            case "tools.emptydeleted":
                await EmptyDeletedItemsAsync(shell);
                break;

            case "tools.cleanup":
                await new MailboxCleanupDialog().ShowDialog(this);
                shell.Refresh();
                break;

            case "rules":
                await new RulesAndAlertsDialog().ShowDialog(this);
                break;
        }
    }

    /// <summary>
    /// The account these dialogs act on. The default one, or nothing when none exists — in
    /// which case saying so beats opening a dialog with no account behind it.
    /// </summary>
    private Account? RequireAccount(ShellViewModel shell)
    {
        var account = App.Accounts.Default?.Account;
        if (account is null) shell.StatusRight = "No account is set up yet. File, Add Account.";
        return account;
    }

    /// <summary>
    /// Empties Deleted Items across every account. Confirmed, and the wording says how many
    /// go, because with POP3 this store may hold the only copy.
    /// </summary>
    private async Task EmptyDeletedItemsAsync(ShellViewModel shell)
    {
        var folders = App.Accounts.All
            .Select(a => (Open: a, Folder: a.Mail.FolderWithRole(a.Account.Id, FolderRole.Deleted)))
            .Where(x => x.Folder is not null)
            .ToList();

        var total = folders.Sum(x => x.Folder!.Total);
        if (total == 0)
        {
            shell.StatusRight = "Deleted Items is already empty.";
            return;
        }

        var confirmed = await Confirm.AskAsync(
            this,
            "Empty Deleted Items",
            $"Permanently delete {total:N0} item{(total == 1 ? "" : "s")} from Deleted Items?\n\n"
            + "This cannot be undone, and where mail was removed from the server this is the "
            + "only copy.",
            "Delete");

        if (!confirmed) return;

        foreach (var (open, folder) in folders)
        {
            foreach (var message in open.Mail.Messages(folder!.Id, int.MaxValue))
            {
                open.Mail.DeleteMessage(message.Id);
            }
        }

        shell.Refresh();
        shell.StatusRight = $"{total:N0} item{(total == 1 ? "" : "s")} deleted.";
    }

    /// <summary>
    /// Opens the account wizard, and reloads once it closes so the new account's folders
    /// appear without a restart.
    /// </summary>
    private async Task AddAccountAsync()
    {
        var wizard = new AccountWizard();
        await wizard.ShowDialog(this);

        if (wizard.Created is null) return;
        if (DataContext is not ShellViewModel shell) return;

        CloseBackstage();
        shell.Refresh();
        shell.StatusRight = $"{wizard.Created.Address} added. Press F9 to check for mail.";
    }

    /// <summary>
    /// F9 runs a send/receive, as every mail client since the nineties has. Handled here
    /// rather than as a command gesture because the ribbon's Alt traversal, which will own the
    /// gesture table, is still to come.
    /// </summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not ShellViewModel shell) return;

        switch (e.Key)
        {
            case Avalonia.Input.Key.F9:
                e.Handled = true;
                _ = SendReceiveAsync(shell);
                break;

            case Avalonia.Input.Key.F5:
                e.Handled = true;
                shell.Refresh();
                break;
        }
    }

    private void ToggleWorkOffline(ShellViewModel shell)
    {
        App.Transfer.SetWorkOffline(!App.Transfer.WorkOffline, AccountConnections());
        shell.StatusRight = App.Transfer.WorkOffline ? "Working offline." : "Working online.";
    }

    /// <summary>
    /// Turns the open accounts into something the transfer service can use, pulling each
    /// password out of the keyring as late as possible. An account whose servers were never
    /// filled in is skipped rather than attempted against an empty hostname.
    /// </summary>
    private static List<TransferTarget> AccountConnections()
    {
        var targets = new List<TransferTarget>();

        foreach (var open in App.Accounts.All)
        {
            var settings = AccountSettings.Load(App.Settings, open.Account.Address);
            if (settings is null) continue;

            targets.Add(new TransferTarget(
                settings.ToConnection(open.Account, App.Secrets), open.Mail));
        }

        return targets;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
