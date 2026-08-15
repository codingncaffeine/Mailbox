using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.App.Views;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Security;
using Mailbox.Security.Dns;
using Mailbox.Store;
using Mailbox.Theming.Themes;
using Mailbox.Core.Diagnostics;
using Mailbox.Theming;
using Mailbox.Theming.Fonts;

namespace Mailbox.App;

public partial class App : Application
{
    public static ThemeService Themes { get; private set; } = null!;
    public static FontResolver Fonts { get; private set; } = null!;
    public static CommandCatalog Commands { get; private set; } = null!;

    /// <summary>
    /// Preferences. A JSON file rather than a database: this is a hundred-odd small values read
    /// once and written on change, and keeping it a file means it can be read, diffed and backed
    /// up by hand. The mail store is the thing that needs SQLite, and that is a separate store.
    /// </summary>
    public static SettingsStore Settings { get; private set; } = null!;

    /// <summary>Every account, each with its own store file.</summary>
    public static AccountStores Accounts { get; private set; } = null!;

    /// <summary>
    /// The Quick Access Toolbar's commands, placement and visibility.
    /// </summary>
    /// <remarks>
    /// One instance, because two surfaces edit it — the chevron flyout on the toolbar and the
    /// Options page — and a second copy would let them disagree about what is on the bar.
    /// </remarks>
    public static QuickAccessLayout QuickAccess { get; private set; } = null!;

    /// <summary>Which accounts are checked together, and when.</summary>
    public static SendReceiveGroups Groups { get; private set; } = null!;

    /// <summary>The signatures, and which account uses which.</summary>
    public static Signatures Signatures { get; private set; } = null!;

    /// <summary>How long a sent message waits before it can actually go (§12).</summary>
    public static UndoSend UndoSend { get; private set; } = null!;

    /// <summary>The Mail page's settings, typed, for the code that acts on them.</summary>
    public static MailOptions MailOptions { get; private set; } = null!;

    /// <summary>The junk filter (§7.8), reading its level live from the Options page.</summary>
    public static JunkService Junk { get; private set; } = null!;

    /// <summary>The single-instance guard, or null during a capture run. Set by <c>Program.Main</c>.</summary>
    public static Mailbox.Core.SingleInstance? Instance { get; set; }

    /// <summary>The user's ribbon edits, and the layout that comes of applying them.</summary>
    public static RibbonCustomization RibbonEdits { get; private set; } = null!;

    /// <summary>The ribbon the shell renders: the shipped layout with any edits over it.</summary>
    public static RibbonLayout MailRibbon() => RibbonEdits.Apply(DefaultRibbonLayouts.Mail);

    /// <summary>Account order and which one is the default.</summary>
    public static IAccountOrder AccountOrder { get; private set; } = null!;

    /// <summary>Polling, sending, and the orchestration over both, across every account.</summary>
    public static SendReceiveService Transfer { get; private set; } = null!;

    /// <summary>
    /// The resolver signature checking uses, and the only thing in the application that asks
    /// DNS anything.
    /// </summary>
    /// <remarks>
    /// Held here so it is plainly one object with one owner. It is handed to the receiver and to
    /// nothing else — in particular not to anything the reading pane can reach, because §19's
    /// "no key discovery to display a message" is a property of the code rather than a rule
    /// somebody remembers.
    /// </remarks>
    public static DnsResolver Resolver { get; private set; } = null!;

    /// <summary>Where passwords are kept. Never a file of our own.</summary>
    public static ICredentialStore Secrets { get; private set; } = null!;

    /// <summary>Keys under which the appearance choices persist.</summary>
    public const string ThemeSetting = "appearance.theme";
    public const string DensitySetting = "appearance.density";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private TrayIcon? _tray;

    /// <summary>
    /// The notification-area icon (§10): a menu to open the window, write a message, check mail
    /// or quit, the unread count drawn on the icon and carried in the tooltip. Left-click brings
    /// the window forward. Held in a field so it outlives this method and is not collected.
    /// </summary>
    /// <returns>True when the icon is up — the precondition for starting minimised to it.</returns>
    private bool InstallTrayIcon(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var open = new NativeMenuItem("Open Mailbox");
            open.Click += (_, _) => window.BringForward();

            var compose = new NativeMenuItem("New Email");
            compose.Click += (_, _) => { window.BringForward(); window.ComposeFromCommandLine(["--compose"]); };

            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => desktop.Shutdown();

            var menu = new NativeMenu();
            menu.Add(open);
            menu.Add(compose);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(quit);

            var icon = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://mailbox/Assets/Icons/mailbox-32.png")));

            _tray = new TrayIcon
            {
                Icon = new WindowIcon(icon),
                ToolTipText = "Mailbox",
                IsVisible = true,
                Menu = menu,
            };

            // Left-click brings the window forward, as a tray icon is expected to.
            _tray.Clicked += (_, _) => window.BringForward();

            // The count is drawn onto the icon as it changes, and the tooltip says it in words.
            if (window.DataContext is ShellViewModel shell)
            {
                void Refresh()
                {
                    var unread = shell.TotalUnread;
                    _tray.ToolTipText = unread > 0 ? $"Mailbox — {unread} unread" : "Mailbox";

                    try
                    {
                        _tray.Icon = Notifications.TrayBadge.For(icon, unread);
                    }
                    catch (Exception ex)
                    {
                        // The plain icon is still up; a badge that will not draw is not worth more.
                        Log.Warn("The tray badge could not be drawn.", ex);
                    }
                }

                shell.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ShellViewModel.TotalUnread)) Refresh();
                };
                Refresh();
            }

            desktop.ShutdownRequested += (_, _) => _tray.IsVisible = false;
            return true;
        }
        catch (Exception ex)
        {
            // A session with no notification-area host: the window still runs, it just has no
            // tray icon. Not worth failing the launch over.
            Log.Warn($"The tray icon could not be created ({ex.Message}).");
            return false;
        }
    }

    /// <summary>Harness only: the badged tray icon as a PNG, at four times its size so it can be looked at.</summary>
    private static void WriteBadgeSample(string request)
    {
        var colon = request.IndexOf(':');
        if (colon <= 0 || !int.TryParse(request[..colon], out var count)) return;
        var path = request[(colon + 1)..];

        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://mailbox/Assets/Icons/mailbox-32.png"));
            var icon = new Avalonia.Media.Imaging.Bitmap(stream);

            // The same drawing the tray icon is built from, saved where it can be looked at.
            var sample = Notifications.TrayBadge.Render(icon, count);
            sample.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Log.Info($"Harness: tray badge for {count} written to {path}.");
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the tray badge sample could not be written.", ex);
        }
    }

    /// <summary>
    /// Applies the stored theme and density. The environment variables still win, because the
    /// fidelity harness sets them to photograph a theme that is not the one chosen here.
    /// </summary>
    private static void RestoreAppearance()
    {
        if (Environment.GetEnvironmentVariable(ThemeService.ThemeVariable) is null
            && Settings.GetString(ThemeSetting) is { Length: > 0 } theme
            && OfficeThemes.All.Contains(theme))
        {
            Themes.Apply(theme);
        }

        if (Environment.GetEnvironmentVariable(ThemeService.DensityVariable) is not null) return;

        if (Enum.TryParse<Density>(Settings.GetString(DensitySetting), out var density))
        {
            Themes.SetDensity(density);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Composition root. Bundled typefaces register before the resolver is built, and font
        // resolution happens before the theme is composed, because typography tokens are
        // rewritten to families this machine can actually draw.
        BundledFonts.Register();

        // A capture run works on a throwaway copy of the settings. The harness poses states —
        // hide the reading pane, collapse the nav, zoom — and several of those persist, which is
        // right for a person and wrong for a photograph: a smoke test once turned the reading
        // pane off in the owner's real settings and every capture for the next hour had no
        // pane. The copy carries the theme and the account order in, and nothing back out.
        Settings = WindowCapture.IsRequested ? SettingsStore.ScratchCopy() : new SettingsStore();
        Fonts = FontResolver.FromSystem();
        Themes = new ThemeService(Fonts);
        RestoreAppearance();

        // A directory under the harness's own path when capturing, so a screenshot run never
        // touches real mail.
        AccountOrder = new SettingsAccountOrder(Settings);
        Accounts = new AccountStores(
            Environment.GetEnvironmentVariable("MAILBOX_STORE") ?? AccountStores.DefaultDirectory(),
            AccountOrder);

        Secrets = Credentials.Best();
        MailOptions = new MailOptions(Settings);

        // Signature checking happens as mail is collected, never as it is drawn. The receiver
        // gets the verifier; nothing else does.
        Resolver = new DnsResolver();
        var signatures = Resolver.CanResolve ? new DkimVerification(Resolver) : null;

        // The junk filter, at the level the Junk Options dialog currently holds. Read live so a
        // change applies to the next message; §7.8's corpus is per account, so the classifier is
        // handed the arriving message's own store.
        Junk = new JunkService(MailOptions);

        // What acts on a message as it arrives, in order: the junk filter, then the rules. Both
        // protocols run the same pipeline, so a rule means the same thing on POP3 and IMAP.
        var arrival = new ArrivalPipeline(Junk);

        // Read at the moment a collector is made, which is per run — so the Options page's
        // choice applies to the next send/receive rather than the next launch. IMAP and POP3
        // check the same signatures the same way, on arrival; the service picks the collector
        // from the account's protocol.
        Transfer = new SendReceiveService(
            mail => new Pop3Receiver(mail)
            {
                Authentication = signatures,
                OnArrival = arrival,
            },
            mail => new SmtpSender(mail) { FileSentCopies = MailOptions.SaveCopiesInSent },
            mail => new ImapSynchronizer(mail)
            {
                Authentication = signatures,
                OnArrival = arrival,
            });

        Commands = new CommandCatalog();
        Commands.RegisterRange(MailCommands.All);
        Commands.RegisterRange(ViewCommands.All);
        Commands.RegisterRange(ComposeCommands.All);

        RibbonEdits = new RibbonCustomization();
        QuickAccess = new QuickAccessLayout(Settings, DefaultRibbonLayouts.Mail.QuickAccess);
        Groups = new SendReceiveGroups(Settings);
        Signatures = new Signatures(Settings);
        UndoSend = new UndoSend(Settings);

        _ = new ThemeResourceBridge(Resources, Themes);

        Log.Info($"Theme {Themes.ThemeId}, density {Themes.Density}");
        Log.Info($"UI font {Fonts.Resolve("Segoe UI").Rendered}, " +
                 $"content font {Fonts.Resolve("Calibri").Rendered}");
        Log.Info($"{Commands.Count} commands registered");
        Log.Info(Resolver.CanResolve
            ? $"Signatures are checked as mail arrives, using {Resolver.Servers.Count} nameserver(s)."
            : "No nameserver is configured, so signatures cannot be checked here.");

        if (Fonts.MissingExpectedSubstitutes() is { Count: > 0 } missing)
        {
            Log.Warn($"Metric-compatible substitutes not installed: {string.Join(", ", missing)}");
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();

            // A tray icon with a menu, as §10 asks for. Not during a capture run — the harness
            // starts many instances, and a tray icon per capture would clutter the session's
            // notification area and outlive the process that made it.
            var trayUp = !WindowCapture.IsRequested && InstallTrayIcon(window, desktop);

            // The badge cannot be photographed on a tray, so the harness writes it to a file:
            // MAILBOX_TRAY_BADGE=<count>:<path.png> renders the icon wearing that count.
            if (Environment.GetEnvironmentVariable("MAILBOX_TRAY_BADGE") is { Length: > 0 } badge)
            {
                WriteBadgeSample(badge);
            }

            // `--minimized` — the autostart entry's switch — starts into the tray with no window,
            // when there is a tray to start into. The window is created and wired exactly as
            // usual and simply not shown; the lifetime adopts it as the main window the first
            // time it is, so closing it then quits as it always has. Until then only Quit ends
            // the process, since there is no window whose closing could.
            var startHidden = trayUp
                && desktop.Args is { } startArgs
                && startArgs.Contains(Mailbox.Core.Platform.Autostart.MinimizedSwitch, StringComparer.Ordinal);

            if (startHidden)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window.Opened += (_, _) =>
                {
                    if (desktop.MainWindow is not null) return;
                    desktop.MainWindow = window;
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                };
                Log.Info("Started minimised to the notification area.");
            }
            else
            {
                desktop.MainWindow = window;
            }

            // Read off the window rather than the request, so a run that asked for one backend
            // and got another is recorded as what it is.
            Log.Info(WindowingBackend.Describe(window));

            // A mailto: link or --compose on the command line opens a compose window once the
            // shell is up — Mailbox acting as the system mail client on a cold start. The harness
            // sets its own environment and passes no such args, so a capture run is unaffected.
            if (desktop.Args is { Length: > 0 } args && !WindowCapture.IsRequested && !startHidden)
            {
                window.Opened += (_, _) => window.ComposeFromCommandLine(args);
            }

            // Become the primary instance and act on a later launch's command line — a mailto:
            // click while Mailbox is open opens onto the running application, and brings it
            // forward. Wired after the window exists so a handoff never reaches a half-built one.
            // A second launch that only asked to start minimised — the autostart entry firing
            // while Mailbox already runs — asks for nothing visible, and gets nothing.
            Instance?.Listen(commandLine => Dispatcher.UIThread.Post(() =>
            {
                if (commandLine.All(a => a == Mailbox.Core.Platform.Autostart.MinimizedSwitch)) return;

                window.BringForward();
                window.ComposeFromCommandLine(commandLine);
            }));

            desktop.ShutdownRequested += (_, _) => Instance?.Dispose();

            // The Options page's "Empty Deleted Items folders when exiting". Off by default, as
            // the reference has it, because with POP3 this store may hold the only copy.
            desktop.ShutdownRequested += (_, _) =>
            {
                if (!MailOptions.EmptyDeletedItemsOnExit) return;

                try
                {
                    var deleted = BackstageActions.EmptyDeletedItems();
                    if (deleted > 0) Log.Info($"Emptied Deleted Items on exit: {deleted} message(s).");
                }
                catch (Exception ex)
                {
                    // Exit goes ahead regardless. A failure to tidy up is not a reason to
                    // refuse to close.
                    Log.Warn("Could not empty Deleted Items on exit.", ex);
                }
            };

            // Fidelity harness: pose the window at an exact width, then render once and exit
            // when MAILBOX_CAPTURE is set.
            WindowCapture.ApplyRequestedSize(window);
            WindowCapture.AttachTo(window, () => desktop.Shutdown());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
