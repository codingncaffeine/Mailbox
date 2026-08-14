using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Diagnostics;
using Mailbox.Core;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

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

        var shell = new ShellViewModel(App.Themes, App.Commands, layout, layoutMode);

        WireRail(shell);
        DataContext = shell;

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
        backstage.CloseRequested += (_, _) =>
        {
            host.IsVisible = false;
            host.Content = null;
        };

        host.Content = backstage;
        host.IsVisible = true;
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
    private void OnRibbonCommand(object? sender, RibbonCommandEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (!App.Commands.TryGet(e.Command, out var command)) return;

        shell.StatusRight = $"{command.Label} — not wired yet ({command.Id})";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
