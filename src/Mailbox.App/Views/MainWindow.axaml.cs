using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private readonly KeyTipSession _keyTips = new();

    /// <summary>Alt has gone down with nothing else held, so releasing it opens the KeyTips.</summary>
    private bool _altAlone;

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

        var quickAccess = new QuickAccessLayout(App.Settings, layout.QuickAccess);

        // The toolbar's two placements and its hidden state need a click to reach, so the
        // harness poses them instead — without persisting, or every capture would leave the
        // next run arranged however the last photograph wanted it.
        switch (Environment.GetEnvironmentVariable("MAILBOX_QAT")?.Trim().ToLowerInvariant())
        {
            case "below": quickAccess.Pose(QuickAccessPlacement.BelowRibbon); break;
            case "above": quickAccess.Pose(QuickAccessPlacement.AboveRibbon); break;
            case "hidden": quickAccess.Pose(visible: false); break;
        }

        var shell = new ShellViewModel(
            App.Themes, App.Commands, layout, layoutMode, App.Accounts, quickAccess);

        _ribbon.IsQuickAccessVisible = quickAccess.IsVisible;
        _ribbon.QuickAccessVisibilityToggled += (_, _) =>
        {
            quickAccess.IsVisible = !quickAccess.IsVisible;
            _ribbon.IsQuickAccessVisible = quickAccess.IsVisible;
            shell.RaiseQuickAccessPlacement();
        };

        WireQuickAccess(shell);
        WireRail(shell);
        WireWindowMenu();
        WireToolbarCommands(shell);
        WireAccountButton(shell);
        WireArrangeMenu(shell);
        WireListInteraction(shell);
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

        // KeyTips exist only while Alt is held, which a capture cannot do. `tabs` poses the
        // first level; a tab id poses that tab's own. Driven through the real key handler
        // rather than around it, so what gets photographed is the traversal itself.
        if (Environment.GetEnvironmentVariable("MAILBOX_KEYTIPS")?.Trim().ToLowerInvariant()
            is { Length: > 0 } keyTips)
        {
            Opened += (_, _) =>
            {
                _keyTips.Begin(FirstLevelKeyTips());

                if (keyTips is "tabs" or "1") return;
                if (_ribbon.Layout.FindTab(keyTips)?.KeyTip is not { } tip) return;

                foreach (var character in tip) _keyTips.HandleKey(KeyFor(character));
            };
        }
    }

    /// <summary>Types a KeyTip character into the traversal. Harness only.</summary>
    private static Avalonia.Input.Key KeyFor(char character)
        => char.IsAsciiDigit(character)
            ? Avalonia.Input.Key.D0 + (character - '0')
            : Avalonia.Input.Key.A + (char.ToUpperInvariant(character) - 'A');

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

    /// <summary>Opens the Options dialog modally over the shell, optionally on a given page.</summary>
    private async Task ShowOptions(string? page = null)
        => await new OptionsWindow(App.Themes, page).ShowDialog<bool>(this);

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
                case "sort-asc": shell.ToggleSort.Execute(null); break;
                case "group-collapsed": shell.ToggleGroupCollapsed("Today"); break;
                case "nav-collapsed": shell.ToggleNav.Execute(null); break;
                case "no-reading": shell.HideReadingPane.Execute(null); break;
                case "zoom-in": shell.ZoomIn.Execute(null); break;
                case "zoom-out": shell.ZoomOut.Execute(null); break;
                default: Log.Warn($"Unknown MAILBOX_STATE: {state}"); break;
            }
        }
    }

    /// <summary>
    /// The arrangement menu behind the "By Date" label. Built from the arrangement list rather
    /// than written out, so adding one is a single entry in the engine.
    /// </summary>
    private void WireArrangeMenu(ShellViewModel shell)
    {
        if (this.FindControl<Button>("ArrangeButton") is not { } button) return;

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        void Build()
        {
            var items = new List<MenuItem>();

            foreach (var arrangement in shell.Arrangements_)
            {
                var chosen = arrangement;
                var item = new MenuItem
                {
                    Header = Store.Lists.Arrangements.Label(chosen),
                    Icon = chosen == shell.Arrangement
                        ? new TextBlock { Text = "\u2713" }
                        : null,
                };
                item.Click += (_, _) => { shell.Arrangement = chosen; Build(); };
                items.Add(item);
            }

            var conversations = new MenuItem
            {
                Header = shell.ShowAsConversations
                    ? "Show as Conversations ✓"
                    : "Show as Conversations",
            };
            conversations.Click += (_, _) =>
            {
                shell.ShowAsConversations = !shell.ShowAsConversations;
                Build();
            };

            var newest = new MenuItem { Header = shell.SortDescending ? "Newest on top ✓" : "Newest on top" };
            newest.Click += (_, _) => { shell.SortDescending = true; Build(); };

            var oldest = new MenuItem { Header = shell.SortDescending ? "Oldest on top" : "Oldest on top ✓" };
            oldest.Click += (_, _) => { shell.SortDescending = false; Build(); };

            flyout.ItemsSource = items.Concat([conversations, newest, oldest]).ToList();
        }

        Build();
        button.Flyout = flyout;
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
    /// Hangs the customize menu off the chevron at the end of the Quick Access Toolbar.
    /// </summary>
    private void WireQuickAccess(ShellViewModel shell)
    {
        if (shell.QuickAccessCustomization is not { } customization) return;

        // One chevron per placement, each with its own flyout — a single flyout cannot be
        // attached to two controls.
        foreach (var name in new[] { "QuickAccessCustomize", "QuickAccessCustomizeBelow" })
        {
            if (this.FindControl<Button>(name) is not { } chevron) continue;

            chevron.Flyout = QuickAccessFlyout.Build(
                App.Commands,
                customization,
                changed: () =>
                {
                    shell.RebuildQuickAccess();

                    // The buttons are new objects, so whatever bound their commands has to run
                    // again or the rebuilt toolbar is a row of controls that do nothing.
                    WireToolbarCommands(shell);
                    _ribbon.IsQuickAccessVisible = customization.IsVisible;
                },
                moreCommands: () => _ = ShowOptions("qat"));
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
    /// Alt on its own opens the KeyTip traversal; Alt as a modifier does not.
    /// </summary>
    /// <remarks>
    /// Which of the two it is only becomes clear on release, so the decision waits for
    /// <see cref="OnKeyUp"/> — deciding on the way down would put badges over the ribbon every
    /// time someone reached for Alt+Tab.
    /// </remarks>
    private static bool IsAltKey(Avalonia.Input.Key key)
        => key is Avalonia.Input.Key.LeftAlt
            or Avalonia.Input.Key.RightAlt
            or Avalonia.Input.Key.System;

    protected override void OnKeyUp(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Handled || !IsAltKey(e.Key) || !_altAlone) return;

        _altAlone = false;
        e.Handled = true;

        if (_keyTips.IsActive) _keyTips.End();
        else _keyTips.Begin(FirstLevelKeyTips());
    }

    /// <summary>
    /// What Alt reveals first: the tabs, and the Quick Access Toolbar numbered left to right.
    /// </summary>
    private IReadOnlyList<KeyTipTarget> FirstLevelKeyTips()
    {
        var targets = new List<KeyTipTarget>(_ribbon.TabKeyTips());

        // The toolbar has two homes and may be hidden altogether, so this asks which one is
        // actually on screen rather than assuming the title bar.
        var qat = new[] { "QuickAccessBar", "QuickAccessBarBelow" }
            .Select(this.FindControl<ItemsControl>)
            .FirstOrDefault(bar => bar is { IsEffectivelyVisible: true });

        if (qat is null) return targets;

        var position = 0;
        foreach (var button in qat.GetVisualDescendants().OfType<Button>())
        {
            // The reference numbers the QAT rather than lettering it, and stops at nine.
            if (++position > 9) break;

            var invoke = button.Command;
            var parameter = button.CommandParameter;

            targets.Add(new KeyTipTarget
            {
                Tip = position.ToString(CultureInfo.InvariantCulture),
                Target = button,
                Activate = () =>
                {
                    if (invoke?.CanExecute(parameter) == true) invoke.Execute(parameter);
                },
            });
        }

        return targets;
    }

    /// <summary>
    /// F9 runs a send/receive, as every mail client since the nineties has.
    /// </summary>
    /// <remarks>
    /// These belong in the command catalogue so the shortcut editor in Phase 8 can rebind them.
    /// They are here for now because nothing yet reads a gesture table.
    /// </remarks>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (IsAltKey(e.Key))
        {
            _altAlone = e.KeyModifiers is Avalonia.Input.KeyModifiers.Alt
                or Avalonia.Input.KeyModifiers.None;
            return;
        }

        _altAlone = false;

        // While badges are up the keyboard belongs to the traversal. A key it does not
        // recognise dismisses it and is then handled normally, rather than vanishing.
        if (_keyTips.IsActive)
        {
            if (_keyTips.HandleKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            _keyTips.End();
        }

        base.OnKeyDown(e);
        if (e.Handled || DataContext is not ShellViewModel shell) return;

        var control = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        var rows = SelectedRows();

        switch (e.Key)
        {
            case Avalonia.Input.Key.F9:
                _ = SendReceiveAsync(shell);
                break;

            case Avalonia.Input.Key.F5:
                shell.Refresh();
                break;

            // Delete goes to Deleted Items; Shift+Delete asks first, because with POP3 the
            // store may be the only copy left.
            case Avalonia.Input.Key.Delete when shift:
                _ = ConfirmPermanentDeleteAsync(shell, rows);
                break;

            case Avalonia.Input.Key.Delete:
                shell.Delete(rows, permanently: false);
                break;

            case Avalonia.Input.Key.Q when control:
                shell.SetRead(rows, read: true);
                break;

            case Avalonia.Input.Key.U when control:
                shell.SetRead(rows, read: false);
                break;

            case Avalonia.Input.Key.G when control && shift:
                shell.SetFlagged(rows, flagged: rows.Any(r => !r.IsFlagged));
                break;

            case Avalonia.Input.Key.I when control && shift:
                shell.GoTo(FolderRole.Inbox);
                break;

            case Avalonia.Input.Key.O when control && shift:
                shell.GoTo(FolderRole.Outbox);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The row actions and dragging to a folder.
    /// </summary>
    /// <remarks>
    /// Both are handled on the list rather than per row: rows are virtualized and recycled, so
    /// anything attached to one is attached to whatever scrolls into its place next.
    /// </remarks>
    private void WireListInteraction(ShellViewModel shell)
    {
        if (this.FindControl<ListBox>("MessageList") is not { } list) return;

        // The action buttons. Found by walking up from what was clicked, because the button
        // itself lives inside a template the list owns.
        list.AddHandler(Button.ClickEvent, (object? sender, RoutedEventArgs e) =>
        {
            if (e.Source is not Button { Tag: string action } button) return;
            if (button.DataContext is not ViewModels.MessageRow row) return;

            e.Handled = true;
            switch (action)
            {
                case "archive": shell.MoveTo([row], FolderRole.Archive); break;
                case "delete": shell.Delete([row], permanently: false); break;
                case "flag": shell.SetFlagged([row], !row.IsFlagged); break;
                case "read": shell.SetRead([row], row.IsUnread); break;
            }
        }, RoutingStrategies.Tunnel);

        // Dragging out of the list. Begun from the press, which is what the platform needs;
        // Avalonia holds it until the pointer actually moves, so a plain click still selects.
        list.AddHandler(PointerPressedEvent, async (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            if ((e.Source as Control)?.DataContext is not ViewModels.MessageRow pressed) return;
            if (_dragging) return;

            // Whatever is selected, or the row under the pointer when it is not one of them.
            var rows = SelectedRows();
            if (!rows.Contains(pressed)) rows = [pressed];

            _dragging = true;
            try
            {
                using var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.Create(MessageDragFormat, Pack(rows)));

                await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
            }
            finally
            {
                _dragging = false;
            }
        }, RoutingStrategies.Bubble);

        WireFolderDropTarget(shell);
    }

    /// <summary>
    /// Our own drag format, in-process only. Message ids mean nothing outside this application
    /// and offering them to other windows would be inviting a paste of numbers.
    /// </summary>
    private static readonly DataFormat<byte[]> MessageDragFormat =
        DataFormat.CreateBytesApplicationFormat("mailbox-message-ids");

    private bool _dragging;

    /// <summary>
    /// Message ids as bytes. The platform's drag payload is bytes, and eight per id is both
    /// exact and cheap — no text encoding to get wrong, and no ambiguity about the separator.
    /// </summary>
    private static byte[] Pack(IReadOnlyList<ViewModels.MessageRow> rows)
    {
        var bytes = new byte[rows.Count * sizeof(long)];
        for (var i = 0; i < rows.Count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(long)), rows[i].Id);
        }

        return bytes;
    }

    private static IReadOnlyList<long> Unpack(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length % sizeof(long) != 0) return [];

        var ids = new long[bytes.Length / sizeof(long)];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = BitConverter.ToInt64(bytes, i * sizeof(long));
        }

        return ids;
    }

    /// <summary>
    /// The folder pane accepts messages dropped on it. The drop target is the list rather than
    /// each row, so the folder under the pointer is worked out at drop time from what is
    /// actually there.
    /// </summary>
    private void WireFolderDropTarget(ShellViewModel shell)
    {
        if (this.FindControl<ListBox>("FolderList") is not { } folders) return;

        DragDrop.SetAllowDrop(folders, true);

        folders.AddHandler(DragDrop.DragOverEvent, (object? _, DragEventArgs e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(MessageDragFormat) && FolderUnder(e) is not null
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        });

        folders.AddHandler(DragDrop.DropEvent, (object? _, DragEventArgs e) =>
        {
            e.Handled = true;
            if (Unpack(e.DataTransfer.TryGetValue(MessageDragFormat)) is not { Count: > 0 } ids) return;
            if (FolderUnder(e) is not { } target) return;

            shell.MoveToFolder(ids, target);
        });
    }

    /// <summary>Which folder row the pointer is over, or null when it is over nothing.</summary>
    private static ViewModels.FolderNode? FolderUnder(DragEventArgs e)
        => (e.Source as Control)?.DataContext as ViewModels.FolderNode;

    /// <summary>What the list has highlighted, headers excluded.</summary>
    private IReadOnlyList<ViewModels.MessageRow> SelectedRows()
    {
        var list = this.FindControl<ListBox>("MessageList");
        if (list?.SelectedItems is not { } selected) return [];

        return [.. selected.OfType<ViewModels.MessageRow>()];
    }

    private async Task ConfirmPermanentDeleteAsync(ShellViewModel shell,
        IReadOnlyList<ViewModels.MessageRow> rows)
    {
        if (rows.Count == 0) return;

        var confirmed = await Confirm.AskAsync(
            this,
            "Delete permanently",
            rows.Count == 1
                ? $"Permanently delete \"{rows[0].Subject}\"?\n\nThis cannot be undone."
                : $"Permanently delete {rows.Count:N0} messages?\n\nThis cannot be undone.",
            "Delete");

        if (confirmed) shell.Delete(rows, permanently: true);
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
