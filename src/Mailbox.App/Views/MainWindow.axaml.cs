using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Ribbon;
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

        var shell = new ShellViewModel(App.Themes, App.Commands, layout, layoutMode)
        {
            // Phase 0 surfaces rendering diagnostics where the connection state will later go.
            StatusRight = $"{TextRendering.Describe()}   |   "
                        + $"UI {App.Fonts.Resolve("Segoe UI").Rendered}   |   "
                        + $"Body {App.Fonts.Resolve("Calibri").Rendered}",
        };

        WireRail(shell);
        DataContext = shell;

        // Lets the fidelity harness capture the peek states, which a screenshot otherwise
        // cannot reach because they need a click.
        switch (Environment.GetEnvironmentVariable("MAILBOX_PEEK")?.ToLowerInvariant())
        {
            case "calendar": Opened += (_, _) => TogglePeek(); break;
            case "docked": Opened += (_, _) => DockPeek(); break;
            case "backstage": Opened += (_, _) => ShowBackstage(); break;

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
        // Outlook uses so the peek reads as belonging to that module.
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
    private void SetUpTitleBar()
    {
        this.FindControl<ContentControl>("CaptionHost")!.Content = new CaptionButtons(this);

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
