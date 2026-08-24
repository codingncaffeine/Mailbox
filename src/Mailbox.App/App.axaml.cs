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
using Mailbox.Protocols.OAuth;
using Mailbox.Security;
using Mailbox.Security.Dns;
using Mailbox.Security.Tls;
using Mailbox.Store;
using Mailbox.Store.Pim;
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

    /// <summary>
    /// Which ribbon each window opens with — Simplified or Classic, always shown or tabs only —
    /// remembered per window kind, as the reference remembers it.
    /// </summary>
    public static RibbonDisplaySettings RibbonDisplay { get; private set; } = null!;

    /// <summary>The Favourites section at the top of the folder pane: which folders, in what order.</summary>
    public static Mailbox.Core.Folders.Favourites Favourites { get; private set; } = null!;

    /// <summary>The favourite contacts, which is what the To-Do Bar's People section holds.</summary>
    public static Mailbox.Core.People.ContactFavourites ContactFavourites { get; private set; } = null!;

    /// <summary>The Trust Center's crypto switches. Both start off, per §14.</summary>
    public static SecurityOptions Security { get; private set; } = null!;

    /// <summary>The RSS subscriptions, and the reader that delivers them into mail folders.</summary>
    public static Mailbox.Core.Feeds.FeedSubscriptions Feeds { get; private set; } = null!;

    public static Mailbox.Protocols.FeedReceiver FeedReader { get; private set; } = null!;

    /// <summary>Personal Stationery: the fonts new mail, replies and plain text are written in.</summary>
    public static StationeryFonts Stationery { get; private set; } = null!;

    /// <summary>Which accounts are checked together, and when.</summary>
    public static SendReceiveGroups Groups { get; private set; } = null!;

    /// <summary>The signatures, and which account uses which.</summary>
    public static Signatures Signatures { get; private set; } = null!;

    /// <summary>How long a sent message waits before it can actually go (§12).</summary>
    public static UndoSend UndoSend { get; private set; } = null!;

    /// <summary>The Mail page's settings, typed, for the code that acts on them.</summary>
    public static MailOptions MailOptions { get; private set; } = null!;

    /// <summary>The Calendar page's settings, as the calendar views read them.</summary>
    public static CalendarOptions CalendarOptions { get; private set; } = null!;

    /// <summary>The People page's settings, as the People module reads them.</summary>
    public static PeopleOptions PeopleOptions { get; private set; } = null!;

    /// <summary>The address books, and what reads and writes a contact in them.</summary>
    public static Mailbox.Contacts.ContactBook Contacts { get; private set; } = null!;

    /// <summary>Options › Advanced › AutoArchive Settings…, as the archiver reads them.</summary>
    public static Mailbox.Core.Archive.AutoArchiveOptions AutoArchive { get; private set; } = null!;

    /// <summary>What a single click in the list's Categories and Flag columns does.</summary>
    public static QuickClickSettings QuickClick { get; private set; } = null!;

    /// <summary>Which key runs which command — every command's default, with the reader's changes over it.</summary>
    public static Mailbox.Core.Keyboard.KeyMap Keys { get; private set; } = null!;

    /// <summary>The junk filter (§7.8), reading its level live from the Options page.</summary>
    public static JunkService Junk { get; private set; } = null!;

    /// <summary>
    /// The one PIM store: every calendar, task list, note list and address book (§4).
    /// </summary>
    /// <remarks>
    /// Beside the accounts directory rather than under it, so a harness run that poses a mail
    /// seed gets that seed's calendar too and never touches the real one.
    /// </remarks>
    /// <summary>
    /// Where everything this application keeps lives — the directory holding <c>pim.db</c> and the
    /// accounts, which a posed store moves along with everything else in it.
    /// </summary>
    /// <remarks>
    /// Resolved once rather than worked out again by each caller: the key material sits here too,
    /// and a store that follows <c>MAILBOX_STORE</c> while the keys do not is a run that reads the
    /// reader's own keys to draw a seeded message.
    /// </remarks>
    public static string StoreDirectory { get; private set; } = string.Empty;

    public static PimStore PimFile { get; private set; } = null!;

    /// <summary>What the modules ask of the PIM store.</summary>
    public static PimRepository Pim { get; private set; } = null!;

    /// <summary>The DAV engine over those collections, run with Send/Receive (§7.5).</summary>
    public static PimSyncService PimSync { get; private set; } = null!;

    /// <summary>The one set of colour categories, and what keeps every store in step with it.</summary>
    public static CategoryBook Categories { get; private set; } = null!;

    /// <summary>
    /// Every open account's store with the address it belongs to, for the things that read across
    /// all of them — the to-do list's flagged mail, and the categories' mirrors.
    /// </summary>
    public static IReadOnlyList<(string Address, MailRepository Mail)> Mailboxes()
        => [.. Accounts.All.Select(a => (a.Account.Address, a.Mail))];

    /// <summary>The Rules and Alerts wizard's rules, run on arrival and by Run Rules Now.</summary>
    public static RulesHandler Rules { get; private set; } = null!;

    /// <summary>The single-instance guard, or null during a capture run. Set by <c>Program.Main</c>.</summary>
    public static Mailbox.Core.SingleInstance? Instance { get; set; }

    /// <summary>The user's ribbon edits, and the layout that comes of applying them.</summary>
    public static RibbonCustomization RibbonEdits { get; private set; } = null!;

    /// <summary>
    /// The plugin host (§13): what is installed, what is running, and every contribution a
    /// plugin has made — commands, tabs, hooks, bars — tracked so disabling reverses it.
    /// </summary>
    public static Mailbox.Plugins.PluginHost Plugins { get; private set; } = null!;

    /// <summary>The Quick Steps: the gallery's entries, and what each does.</summary>
    public static QuickSteps QuickSteps { get; private set; } = null!;

    /// <summary>
    /// The ribbon the shell renders: the shipped layout with any edits over it, and the Quick
    /// Steps gallery listing the steps as they stand.
    /// </summary>
    /// <summary>
    /// Where <c>pim.db</c> goes for a given accounts directory: its parent, which is the data
    /// directory in a real run and the seed's own directory under the harness.
    /// </summary>
    internal static string PimPathBeside(string accountsDirectory)
    {
        var parent = System.IO.Path.GetDirectoryName(
            System.IO.Path.TrimEndingDirectorySeparator(accountsDirectory));
        return string.IsNullOrEmpty(parent)
            ? PimStore.DefaultPath()
            : System.IO.Path.Combine(parent, "pim.db");
    }

    public static RibbonLayout MailRibbon()
        => QuickStepsRibbon.Inject(
            RibbonEdits.Apply(Plugins.InjectRibbon(DefaultRibbonLayouts.Mail)), QuickSteps.All);

    /// <summary>
    /// The ribbon while a reply grows inline: the shell's own tabs with the compose window's
    /// Message tab appended — the reference keeps File through Help on the strip and adds
    /// Message, selected, rather than swapping the whole strip for the compose window's.
    /// </summary>
    /// <remarks>
    /// The tab is cloned onto M so its KeyTip cannot collide with Home's H on a strip the two
    /// never otherwise share.
    /// </remarks>
    public static RibbonLayout InlineReplyRibbon()
    {
        var shell = MailRibbon();
        var compose = DefaultRibbonLayouts.Compose;

        if (compose.FindTab("message") is not { } message) return compose;

        // The compose window authors its Simplified rows directly rather than through named
        // clusters (see RibbonLayout.SimplifiedRows), so the row travels whole; the shell's own
        // clusters re-derive their rows over this on the way out.
        var rows = shell.SimplifiedRows.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (compose.SimplifiedRows.TryGetValue("message", out var row)) rows["message"] = row;

        return shell with
        {
            Tabs = [.. shell.Tabs, message with { KeyTip = "M" }],
            SimplifiedRows = rows,
        };
    }

    /// <summary>
    /// Puts every Quick Step in the catalogue as a command — the shipped three already are, by
    /// the ids the layout places; the reader's own join them — so the gallery, the QAT and the
    /// shortcut editor see them like any command. Called at start and whenever the list changes.
    /// </summary>
    private static void RegisterQuickSteps()
    {
        foreach (var step in QuickSteps.All)
        {
            var command = step.ToCommand();
            if (Commands.TryGet(command.Id, out var existing))
            {
                // A step already in the catalogue takes its current name, icon and shortcut —
                // "Move to: ?" reads "Move to: Projects" once it is set up, as the reference's
                // does — and keeps whatever the shipped command carried that a step does not
                // know about, its KeyTip above all.
                Commands.Replace(existing with
                {
                    Label = command.Label,
                    Description = command.Description,
                    Icon = command.Icon,
                    DefaultGesture = command.DefaultGesture ?? existing.DefaultGesture,
                });
                continue;
            }

            Commands.Register(command);
        }
    }

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

    /// <summary>
    /// Which server certificates the reader has agreed to.
    /// </summary>
    /// <remarks>
    /// One for the application, over the settings file, because a decision about a server is
    /// about the server rather than about the account that happened to reach it first — two
    /// accounts on one host ask once between them.
    /// </remarks>
    public static CertificateTrust Trust { get; private set; } = null!;

    /// <summary>
    /// The accounts that sign in rather than hold a password, and their tokens.
    /// </summary>
    /// <remarks>
    /// One per application rather than one per send/receive: an access token is good for about an
    /// hour, and the point of keeping the source is not having to buy another one every poll.
    /// </remarks>
    public static OAuthAccounts OAuth { get; private set; } = null!;

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

    private static FileSystemWatcher? _themeWatcher;

    /// <summary>
    /// Watches the themes directory and reloads the library when a theme file changes; the
    /// service re-applies the current theme when it is one of the files. Debounced through the
    /// dispatcher, because an editor saving a file raises several events for one save. A
    /// directory that does not exist yet is not watched — there is nothing in it to change —
    /// and a capture run has no need of it.
    /// </summary>
    private static void WatchThemeFiles(string directory)
    {
        if (WindowCapture.IsRequested || !Directory.Exists(directory)) return;

        try
        {
            _themeWatcher = new FileSystemWatcher(directory, "*" + Mailbox.Theming.Files.ThemeFileFormat.Extension)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            var pending = false;
            void Reload(object? _, FileSystemEventArgs e)
            {
                if (pending) return;
                pending = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(250);
                    pending = false;
                    Themes.ReplaceLibrary(Mailbox.Theming.Files.ThemeLibrary.Load(directory));
                    Log.Info($"Theme files reloaded after {e.Name} changed.");
                });
            }

            _themeWatcher.Changed += Reload;
            _themeWatcher.Created += Reload;
            _themeWatcher.Deleted += Reload;
            _themeWatcher.Renamed += (o, e) => Reload(o, e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("The themes directory could not be watched; edits to theme files show at the next start.", ex);
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
            && Themes.Library.Canonical(theme) is { } known)
        {
            try
            {
                Themes.Apply(known);
            }
            catch (Mailbox.Theming.Tokens.ThemeResolutionException ex)
            {
                // A theme file that no longer resolves — a base gone, a token missing — is not
                // a reason the application will not start; it says so and stays on Colorful.
                Log.Warn($"The saved theme \"{theme}\" could not be applied: {ex.Message}");
            }
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

        // The harness poses settings on the scratch copy: MAILBOX_SETTING="key=value|key=value",
        // with true/false and numbers typed as what they look like. Capture runs only — a real
        // run's settings are the person's.
        if (WindowCapture.IsRequested && Environment.GetEnvironmentVariable("MAILBOX_SETTING") is { Length: > 0 } posed)
        {
            foreach (var pair in posed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();
                if (bool.TryParse(value, out var b)) Settings.Set(key, b);
                else if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) Settings.Set(key, d);
                else Settings.Set(key, value);
                Log.Info($"Harness: setting {key} = {value}.");
            }
        }
        Fonts = FontResolver.FromSystem();
        // The reader's theme files beside the built-ins, and a watch on their directory so an
        // edit to the theme in use shows without a restart (§8's hot reload).
        var themesDirectory = Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory();
        Themes = new ThemeService(Fonts, Mailbox.Theming.Files.ThemeLibrary.Load(themesDirectory));
        RestoreAppearance();
        WatchThemeFiles(themesDirectory);

        // A directory under the harness's own path when capturing, so a screenshot run never
        // touches real mail.
        AccountOrder = new SettingsAccountOrder(Settings);
        var accountsDirectory = Environment.GetEnvironmentVariable("MAILBOX_STORE") ?? AccountStores.DefaultDirectory();
        Accounts = new AccountStores(accountsDirectory, AccountOrder);

        // pim.db sits beside the accounts directory, so a posed store brings its own calendar.
        var pimPath = PimPathBeside(accountsDirectory);
        StoreDirectory = System.IO.Path.GetDirectoryName(pimPath)!;
        PimFile = new PimStore(pimPath);
        Pim = new PimRepository(PimFile);

        // A capture run keeps its passwords in memory: it poses accounts that do not exist, and
        // the keyring may be locked on a headless desktop, where asking it would wait forever.
        Secrets = WindowCapture.IsRequested ? new InMemoryCredentialStore() : Credentials.Best();
        OAuth = new OAuthAccounts(Secrets);
        Trust = new CertificateTrust(Settings);

        // The wire logs go beside the application log, under state rather than in a temporary
        // directory: a protocol log is the reader's own mail, and /tmp is world-readable on a
        // shared machine. Off unless MAILBOX_PROTOCOL_LOG says otherwise.
        ProtocolDiagnostics.Directory = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Log.LogDirectory()) ?? Log.LogDirectory(), "protocol");
        PimSync = new PimSyncService(Pim, Secrets, OAuth, Settings);
        MailOptions = new MailOptions(Settings);
        CalendarOptions = new CalendarOptions(Settings);
        PeopleOptions = new PeopleOptions(Settings);
        Contacts = new Mailbox.Contacts.ContactBook(Pim);

        // One set of categories over every module (§9). The mail accounts keep a mirror of it so
        // their own join tables have rows to point at; adopting on first run is what keeps mail
        // that was already coloured coloured.
        Categories = new CategoryBook(Pim, () => [.. Accounts.All.Select(a => a.Mail)]);
        Categories.EnsureDefaults();

        AutoArchive = new Mailbox.Core.Archive.AutoArchiveOptions(Settings);
        QuickClick = new QuickClickSettings(Settings);

        // Signature checking happens as mail is collected, never as it is drawn. The receiver
        // gets the verifier; nothing else does.
        Resolver = new DnsResolver();
        var signatures = Resolver.CanResolve ? new DkimVerification(Resolver) : null;

        // The junk filter, at the level the Junk Options dialog currently holds. Read live so a
        // change applies to the next message; §7.8's corpus is per account, so the classifier is
        // handed the arriving message's own store.
        Junk = new JunkService(MailOptions, Contacts);

        // The catalogue before the pipeline, because the plugin host joins both: its commands
        // enter the catalogue and its arrival stage ends the pipeline.
        Commands = new CommandCatalog();
        Commands.RegisterRange(MailCommands.All);
        Commands.RegisterRange(ViewCommands.All);
        Commands.RegisterRange(ComposeCommands.All);
        Commands.RegisterRange(CalendarCommands.All);
        Commands.RegisterRange(AppointmentCommands.All);
        Commands.RegisterRange(ContactCommands.All);
        Commands.RegisterRange(PeopleCommands.All);
        Commands.RegisterRange(TaskCommands.All);
        Commands.RegisterRange(NoteCommands.All);
        Commands.RegisterRange(JournalCommands.All);
        Keys = new Mailbox.Core.Keyboard.KeyMap(Settings, Commands);
        Mailbox.Controls.Ribbon.RibbonView.GestureLookup = command => Keys.GestureFor(command.Id)?.Display;

        // A capture run must not load the machine's plugins any more than its settings or its
        // keyring: MAILBOX_PLUGINS poses a directory, and without one a capture gets an empty
        // scratch root — the same reasoning as MAILBOX_STORE.
        var pluginsRoot = Environment.GetEnvironmentVariable("MAILBOX_PLUGINS");
        if (string.IsNullOrEmpty(pluginsRoot))
        {
            pluginsRoot = WindowCapture.IsRequested
                ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mailbox-plugins-{Environment.ProcessId}")
                : Mailbox.Plugins.PluginHost.DefaultRoot();
        }

        Plugins = new Mailbox.Plugins.PluginHost(pluginsRoot, new Mailbox.Plugins.PluginHostServices
        {
            Commands = Commands,
            Settings = Settings,
            Mailboxes = Mailboxes,
            Pim = Pim,
            QueuePut = item => PimSync.QueuePut(item),
            RunOnUiThread = action => Dispatcher.UIThread.Post(action),
        });

        // What acts on a message as it arrives, in order: the junk filter, then the rules, then
        // the plugins — last, so a hook sees where the application's own handlers left it. Both
        // protocols run the same pipeline, so all of it means the same thing on POP3 and IMAP.
        Rules = new RulesHandler();
        var arrival = new ArrivalPipeline(
            Junk, new IgnoreHandler(), new FocusedInboxHandler(), Rules, Plugins.Arrivals);

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

        // The retention window on Recover Deleted Items (§11): what was deleted longer ago than
        // the Options page keeps goes for good, once per launch, before anything shows.
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-MailOptions.RecoverDays);
            var purged = Accounts.All.Sum(a => a.Mail.PurgeRecoverableOlderThan(cutoff));
            if (purged > 0) Log.Info($"Purged {purged} recoverable message(s) older than {MailOptions.RecoverDays} days.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not purge the recoverable holding area.", ex);
        }

        RibbonEdits = new RibbonCustomization();
        QuickSteps = new QuickSteps(Settings);
        RegisterQuickSteps();
        QuickSteps.Changed += (_, _) => RegisterQuickSteps();
        QuickAccess = new QuickAccessLayout(Settings, DefaultRibbonLayouts.Mail.QuickAccess);
        RibbonDisplay = new RibbonDisplaySettings(Settings);
        Favourites = new Mailbox.Core.Folders.Favourites(Settings);
        ContactFavourites = new Mailbox.Core.People.ContactFavourites(Settings);
        Security = new SecurityOptions(Settings);
        Feeds = new Mailbox.Core.Feeds.FeedSubscriptions(Settings);
        FeedReader = new Mailbox.Protocols.FeedReceiver(Feeds);
        Stationery = new StationeryFonts(Settings);
        Groups = new SendReceiveGroups(Settings);
        Signatures = new Signatures(Settings);
        UndoSend = new UndoSend(Settings);

        // The list's field vocabulary learns the plugins' columns: the hooks are how a Core
        // file that cannot know the host still labels and sizes an id a plugin owns.
        Mailbox.Core.Views.ViewFields.ExtraLabel = id => Plugins.ColumnLabel(id);
        Mailbox.Core.Views.ViewFields.ExtraWidth = id =>
            Plugins.Columns().FirstOrDefault(c => c.Id == id) is { Id.Length: > 0 } column ? column.Width : null;

        // Last in the composition, so everything a plugin's Initialize reaches for is already
        // standing. The window subscribes to Changed for its ribbon; nothing here needs to.
        Plugins.Start();

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

            // The scale a window opens at is not always the one it settles at: on X11 the
            // screen's Xft.dpi is applied once the window is mapped, and 100 there means 1.0417 —
            // everything four percent larger than a 100% reference, and worth knowing before
            // judging fidelity by eye. Logged at open and again whenever it changes.
            Log.Info($"Scaling: {window.RenderScaling:0.####}");
            window.ScalingChanged += (_, _) => Log.Info($"Scaling: {window.RenderScaling:0.####}");

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
