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
    /// Where feeds are filed: a store of their own, not one of the reader's mail accounts.
    /// </summary>
    /// <remarks>
    /// Not in <see cref="Accounts"/>, deliberately, so nothing that walks the reader's accounts
    /// finds it — not Send/Receive, not the unified inbox, not Account Settings, not the compose
    /// From line. The folder pane can show it as a root of its own, and does not unless asked.
    /// </remarks>
    public static FeedStores FeedStore { get; private set; } = null!;

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

    /// <summary>The Trust Center's crypto switches. Both ship off, by decision.</summary>
    public static SecurityOptions Security { get; private set; } = null!;

    /// <summary>The RSS subscriptions, and the reader that delivers them into mail folders.</summary>
    public static Mailbox.Core.Feeds.FeedSubscriptions Feeds { get; private set; } = null!;

    /// <summary>The words whose articles are never delivered.</summary>
    public static Mailbox.Core.Feeds.MuteFilters Mutes { get; private set; } = null!;

    /// <summary>Files the newsletters the reader reads as articles into the feeds tree.</summary>
    public static Mailbox.Protocols.NewsletterRouter Newsletters { get; private set; } = null!;

    /// <summary>The calendars this reader publishes, and where each one goes.</summary>
    public static Mailbox.Core.Calendars.PublishedCollections Published { get; private set; } = null!;

    public static Mailbox.Protocols.FeedReceiver FeedReader { get; private set; } = null!;

    /// <summary>Personal Stationery: the fonts new mail, replies and plain text are written in.</summary>
    public static StationeryFonts Stationery { get; private set; } = null!;

    /// <summary>Which accounts are checked together, and when.</summary>
    public static SendReceiveGroups Groups { get; private set; } = null!;

    /// <summary>The signatures, and which account uses which.</summary>
    public static Signatures Signatures { get; private set; } = null!;

    /// <summary>The addresses each account may send as, beyond its own.</summary>
    public static Identities Identities { get; private set; } = null!;

    /// <summary>How long a sent message waits before it can actually go.</summary>
    public static UndoSend UndoSend { get; private set; } = null!;

    /// <summary>The Mail page's settings, typed, for the code that acts on them.</summary>
    public static MailOptions MailOptions { get; private set; } = null!;

    /// <summary>The Calendar page's settings, as the calendar views read them.</summary>
    public static CalendarOptions CalendarOptions { get; private set; } = null!;

    /// <summary>The People page's settings, as the People module reads them.</summary>
    public static PeopleOptions PeopleOptions { get; private set; } = null!;

    /// <summary>The address books, and what reads and writes a contact in them.</summary>
    public static Mailbox.Contacts.ContactBook Contacts { get; private set; } = null!;

    /// <summary>
    /// The LDAP directories people are looked up in — a company or a university's own book,
    /// which is read and never written.
    /// </summary>
    public static Mailbox.Contacts.Directory.Directories Directories { get; private set; } = null!;

    /// <summary>
    /// The bind passwords, read from the keyring once each and then remembered.
    /// </summary>
    /// <remarks>
    /// Cached because this is asked on the way into a search that runs while somebody is typing:
    /// the keyring is a D-Bus round trip and, on a locked keyring, a prompt — neither of which
    /// belongs between two keystrokes. Cleared when a directory is saved, which is the only
    /// moment a password can have changed.
    /// </remarks>
    private static readonly Dictionary<string, string?> DirectoryPasswords = [];

    /// <summary>Forgets the remembered bind passwords, for a directory whose settings changed.</summary>
    public static void ForgetDirectoryPasswords()
    {
        lock (DirectoryPasswords) DirectoryPasswords.Clear();
        DirectorySuggestions.Forget();
    }

    /// <summary>
    /// The directories' contribution to the Auto-Complete List, fetched beside the typing rather
    /// than in it.
    /// </summary>
    public static Mailbox.Contacts.Directory.DirectorySuggestions DirectorySuggestions { get; } =
        new(
            typed => SearchDirectoriesAsync(typed, onlyAddressable: true),
            work => Avalonia.Threading.Dispatcher.UIThread.Post(work));

    /// <summary>
    /// Everyone in every directory matching what was typed.
    /// </summary>
    /// <remarks>
    /// The passwords are gathered first and awaited, and only then is the search handed to a
    /// worker: the keyring is asynchronous and the LDAP client is not, and waiting on the first
    /// from wherever this was called would be the block this arrangement exists to avoid.
    /// </remarks>
    public static async Task<Mailbox.Contacts.Directory.DirectoryResult> SearchDirectoriesAsync(
        string? typed, bool onlyAddressable = false)
    {
        var directories = Directories.Searchable();
        var passwords = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            if (directory.BindDn.Length == 0 || passwords.ContainsKey(directory.PasswordKey)) continue;
            passwords[directory.PasswordKey] = await DirectoryPasswordAsync(directory);
        }

        return await Task.Run(() => Mailbox.Contacts.Directory.DirectoryLookup.Search(
            directories,
            directory => passwords.GetValueOrDefault(directory.PasswordKey),
            typed,
            onlyAddressable));
    }

    private static async Task<string?> DirectoryPasswordAsync(Mailbox.Contacts.Directory.LdapDirectory directory)
    {
        lock (DirectoryPasswords)
        {
            if (DirectoryPasswords.TryGetValue(directory.PasswordKey, out var held)) return held;
        }

        var password = await Secrets.LoadAsync(
            directory.PasswordKey, Mailbox.Contacts.Directory.Directories.PasswordPurpose);

        lock (DirectoryPasswords) DirectoryPasswords[directory.PasswordKey] = password;
        return password;
    }

    /// <summary>Options › Advanced › AutoArchive Settings…, as the archiver reads them.</summary>
    public static Mailbox.Core.Archive.AutoArchiveOptions AutoArchive { get; private set; } = null!;

    /// <summary>What a single click in the list's Categories and Flag columns does.</summary>
    public static QuickClickSettings QuickClick { get; private set; } = null!;

    /// <summary>Which key runs which command — every command's default, with the reader's changes over it.</summary>
    public static Mailbox.Core.Keyboard.KeyMap Keys { get; private set; } = null!;

    /// <summary>The junk filter, reading its level live from the Options page.</summary>
    public static JunkService Junk { get; private set; } = null!;

    /// <summary>
    /// The one PIM store: every calendar, task list, note list and address book.
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

    /// <summary>The DAV engine over those collections, run with Send/Receive.</summary>
    public static PimSyncService PimSync { get; private set; } = null!;

    /// <summary>The one set of colour categories, and what keeps every store in step with it.</summary>
    public static CategoryBook Categories { get; private set; } = null!;

    /// <summary>
    /// Every open account's store with the address it belongs to, for the things that read across
    /// all of them — the to-do list's flagged mail, and the categories' mirrors.
    /// </summary>
    public static IReadOnlyList<(string Address, MailRepository Mail)> Mailboxes()
        => [.. Accounts.All.Select(a => (a.Account.Address, a.Mail))];

    /// <summary>
    /// What acts on a message as it arrives: the junk filter, the rules, the plugins. Held so a
    /// feed poll can run the same pipeline when the Options tick asks it to — a feed item that
    /// rules apply to has to meet the same handlers in the same order as any other message.
    /// </summary>
    public static ArrivalPipeline Arrival { get; private set; } = new();

    /// <summary>The Rules and Alerts wizard's rules, run on arrival and by Run Rules Now.</summary>
    public static RulesHandler Rules { get; private set; } = null!;

    /// <summary>The single-instance guard, or null during a capture run. Set by <c>Program.Main</c>.</summary>
    public static Mailbox.Core.SingleInstance? Instance { get; set; }

    /// <summary>The user's ribbon edits, and the layout that comes of applying them.</summary>
    public static RibbonCustomization RibbonEdits { get; private set; } = null!;

    /// <summary>
    /// The plugin host: what is installed, what is running, and every contribution a
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
    /// nothing else — in particular not to anything the reading pane can reach, because the design's
    /// "no key discovery to display a message" is a property of the code rather than a rule
    /// somebody remembers.
    /// </remarks>
    public static DnsResolver Resolver { get; private set; } = null!;

    /// <summary>Where passwords are kept. Never a file of our own.</summary>
    public static ICredentialStore Secrets { get; private set; } = null!;

    /// <summary>
    /// True when a capture run has asked for the desktop keyring rather than the in-memory store.
    /// </summary>
    /// <remarks>
    /// The one claim a posed run cannot otherwise make: that a password typed into a form reaches
    /// the desktop keyring. Read back inside this process the two stores are indistinguishable, so
    /// the verdict has to come from outside it, and that means the write has to go there.
    /// </remarks>
    public static bool RealKeyringRequested => string.Equals(
        Environment.GetEnvironmentVariable("MAILBOX_KEYRING"), "real", StringComparison.OrdinalIgnoreCase);

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
    /// The notification-area icon: a menu to open the window, write a message, check mail
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

            // Two drawings, held for the life of the icon: the empty mailbox and the one with
            // post in it. Which is shown is the unread count's answer — see TrayArtwork.
            var empty = TrayIconArt(Mailbox.Core.Notifications.TrayArtwork.Empty);
            var full = TrayIconArt(Mailbox.Core.Notifications.TrayArtwork.Full);

            _tray = new TrayIcon
            {
                Icon = new WindowIcon(empty),
                ToolTipText = "Mailbox",
                IsVisible = true,
                Menu = menu,
            };

            // Left-click brings the window forward, as a tray icon is expected to.
            _tray.Clicked += (_, _) => window.BringForward();

            Log.Info("The notification-area icon is up.");

            // The count is drawn onto the icon as it changes, and the tooltip says it in words.
            if (window.DataContext is ShellViewModel shell)
            {
                void Refresh()
                {
                    var unread = shell.TotalUnread;
                    _tray.ToolTipText = unread > 0 ? $"Mailbox — {unread} unread" : "Mailbox";

                    // The drawing first, the count on top of it: the picture says whether there
                    // is anything waiting and the badge says how much. Reading the last of it —
                    // or marking it read — empties the box again, which is the whole rule.
                    var art = unread > 0 ? full : empty;

                    try
                    {
                        _tray.Icon = Notifications.TrayBadge.For(art, unread);

                        // The one part of this nobody can photograph from a harness run: what
                        // the panel was handed, and why.
                        Log.Info($"Tray icon: {Mailbox.Core.Notifications.TrayArtwork.For(unread)} "
                                 + $"({unread} unread).");
                    }
                    catch (Exception ex)
                    {
                        // The drawing is still right even where the badge will not draw, so the
                        // state the reader actually looks for survives a failure here.
                        Log.Warn("The tray badge could not be drawn.", ex);
                        _tray.Icon = new WindowIcon(art);
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

    /// <summary>
    /// One of the tray's two drawings, at the size the notification area is handed.
    /// </summary>
    /// <remarks>
    /// 32 pixels, which is what the tray has always been given here; the ladder beside it goes
    /// to 256 for a panel that asks for more. Every size is cropped from the same frame, so the
    /// mailbox does not move or change size when the icon swaps.
    /// </remarks>
    private static Avalonia.Media.Imaging.Bitmap TrayIconArt(string art)
        => new(Avalonia.Platform.AssetLoader.Open(
            new Uri($"avares://mailbox/Assets/Icons/{art}-32.png")));

    /// <summary>Harness only: the badged tray icon as a PNG, at four times its size so it can be looked at.</summary>
    private static void WriteBadgeSample(string request, ShellViewModel? shell = null)
    {
        var colon = request.IndexOf(':');
        if (colon <= 0) return;

        var wanted = request[..colon];
        var path = request[(colon + 1)..];

        // "auto" is the running store's own answer, which is what makes this a read-back rather
        // than a drawing exercise.
        var count = string.Equals(wanted, "auto", StringComparison.OrdinalIgnoreCase)
            ? shell?.TotalUnread ?? 0
            : int.TryParse(wanted, out var asked) ? asked : -1;

        if (count < 0) return;

        try
        {
            // The drawing that count would put up, not a fixed one: the point of the sample is
            // what the tray shows, and half of that is which mailbox it is.
            var icon = TrayIconArt(Mailbox.Core.Notifications.TrayArtwork.For(count));

            // The same drawing the tray icon is built from, saved where it can be looked at.
            var sample = Notifications.TrayBadge.Render(icon, count);
            sample.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Log.Info($"Harness: tray shows the {Mailbox.Core.Notifications.TrayArtwork.For(count)} "
                     + $"mailbox for {count} unread; written to {path}.");
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
            && Settings.GetString(ThemeSetting) is { Length: > 0 } theme)
        {
            // "Use the desktop's setting" stores a sentinel, not a theme id: the id it means
            // is asked of the desktop now, and re-asked whenever the desktop changes its mind.
            var chosen = theme == DesktopTheme.Sentinel ? DesktopTheme.Resolve() : theme;
            if (theme == DesktopTheme.Sentinel) DesktopTheme.Watch();

            if (Themes.Library.Canonical(chosen) is { } known)
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
        }

        // The Mailbox Background rides the appearance slot, whatever the theme decision was.
        Theming.BackdropChoice.Restore(Settings, Themes);

        if (Environment.GetEnvironmentVariable(ThemeService.DensityVariable) is not null) return;

        if (Enum.TryParse<Density>(Settings.GetString(DensitySetting), out var density))
        {
            Themes.SetDensity(density);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            StartUp();
        }
        catch (Exception failure) when (ShowStartupFailure(failure))
        {
            // Swallowed on purpose: the failure window is the application now. It says what
            // happened, and closing it ends a process whose exit code still says failure.
        }
    }

    /// <summary>
    /// A startup that cannot proceed says so on screen. Seven launches died invisibly in one
    /// evening to a store written by a newer build — the log had the exact sentence and the
    /// reader saw nothing. False hands the exception on to the crash log unchanged: a posed
    /// run reads logs and must exit rather than wait on a window, and a failure before the
    /// lifetime exists has nowhere to show one.
    /// </summary>
    private bool ShowStartupFailure(Exception failure)
    {
        if (WindowCapture.IsRequested)
        {
            Log.Info($"Harness: the startup failure window would say — {failure.Message}");
            return false;
        }

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return false;

        try
        {
            Log.Error("Startup failed; showing the failure window.", failure);
            Environment.ExitCode = 1;

            var window = new Views.StartupFailureWindow(failure);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            window.Activate();
            return true;
        }
        catch
        {
            // The window itself failing to build must not eat the original failure.
            return false;
        }
    }

    private void StartUp()
    {
        // Composition root. Bundled typefaces register before the resolver is built, and font
        // resolution happens before the theme is composed, because typography tokens are
        // rewritten to families this machine can actually draw.
        BundledFonts.Register();

        // The code pages mail still arrives in. Registered at the root as well as in the mapper,
        // because the reading pane decodes a body straight off the stored MIME and never goes
        // through the mapper to do it — which is how a Shift_JIS message came to have a correct
        // subject over a body of accented Latin.
        Mailbox.Protocols.LegacyCodePages.Register();

        // A capture run works on a throwaway copy of the settings. The harness poses states —
        // hide the reading pane, collapse the nav, zoom — and several of those persist, which is
        // right for a person and wrong for a photograph: a smoke test once turned the reading
        // pane off in the owner's real settings and every capture for the next hour had no
        // pane. The copy carries the theme and the account order in, and nothing back out.
        // MAILBOX_SETTINGS=<path> points the scratch copy at a file the caller keeps, which is the
        // only way a harness run can say anything about a setting *surviving* — the unnamed copy
        // is per-process, so two runs of the same pose are two first runs. Capture runs only: a
        // real run's settings are the person's, wherever this variable happens to be set.
        Settings = WindowCapture.IsRequested
            ? SettingsStore.ScratchCopy(Environment.GetEnvironmentVariable("MAILBOX_SETTINGS"))
            : new SettingsStore();

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
        // edit to the theme in use shows without a restart (the hot reload).
        var themesDirectory = Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory();
        // The import and palette doors run before the library loads, so a pose can import — or
        // write a palette's theme — and apply it in one run.
        Theming.ThemeImportDoor.RunIfAsked(themesDirectory);
        Theming.PaletteDoor.RunIfAsked(themesDirectory);
        Themes = new ThemeService(Fonts, Mailbox.Theming.Files.ThemeLibrary.Load(themesDirectory));
        RestoreAppearance();
        WatchThemeFiles(themesDirectory);

        // A capture run that names no store gets an empty scratch one, never the real thing —
        // the settings above are a throwaway copy for exactly this reason, and the store is more
        // so: opening the real one under a newer tree migrates it forward in place, and the
        // installed build then refuses to start until it is rebuilt. pim.db, feeds.db and the
        // keyring all sit beside the accounts directory, so one path guards the lot. A pose that
        // wants mail in the picture says MAILBOX_STORE, which every batch runner already does.
        AccountOrder = new SettingsAccountOrder(Settings);
        var accountsDirectory = Environment.GetEnvironmentVariable("MAILBOX_STORE");
        if (string.IsNullOrEmpty(accountsDirectory))
        {
            accountsDirectory = WindowCapture.IsRequested
                ? System.IO.Directory.CreateDirectory(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"mailbox-store-{Environment.ProcessId}", "accounts")).FullName
                : AccountStores.DefaultDirectory();
        }
        Accounts = new AccountStores(accountsDirectory, AccountOrder);

        // feeds.db sits beside the accounts directory, for the same reason pim.db does — and
        // outside it, so nothing that opens every file in there as a mail account opens this one.
        FeedStore = new FeedStores(FeedStores.PathBeside(accountsDirectory));

        // The one-off that puts existing subscriptions where they now belong. Feeds used to be
        // filed into whichever mail account sorted first, which was both wrong in principle and
        // unstable in practice. Copies everything, counts it, and only then takes the old away.
        var moved = FeedStoreMove.MoveAll(
            FeedStore.Account, Accounts.All, Mailbox.Protocols.FeedReceiver.RootFolder);

        if (moved.DidAnything)
        {
            Log.Info($"Feeds: moved into a store of their own — {moved.Articles} article(s), "
                     + $"{moved.Folders} folder(s), {moved.Boards} board(s).");
        }

        // pim.db sits beside the accounts directory, so a posed store brings its own calendar.
        var pimPath = PimPathBeside(accountsDirectory);
        StoreDirectory = System.IO.Path.GetDirectoryName(pimPath)!;
        PimFile = new PimStore(pimPath);
        Pim = new PimRepository(PimFile);

        // A capture run keeps its passwords in memory: it poses accounts that do not exist, and
        // the keyring may be locked on a headless desktop, where asking it would wait forever.
        //
        // MAILBOX_KEYRING=real opts back in, because the in-memory store makes one claim
        // unprovable: that what a form writes actually reaches the desktop keyring. Read back
        // from the process that wrote it, an in-memory store and a real one are the same
        // evidence — the only verdict worth having is `secret-tool` at a command line, outside
        // this process, and that needs the write to have gone there. A run that asks for it is
        // asking to leave an entry behind, so it names its own account and clears up after
        // itself.
        Secrets = WindowCapture.IsRequested && !RealKeyringRequested
            ? new InMemoryCredentialStore()
            : Credentials.Best();
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
        Directories = new Mailbox.Contacts.Directory.Directories(Settings);

        // One set of categories over every module. The mail accounts keep a mirror of it so
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
        // change applies to the next message; the design's corpus is per account, so the classifier is
        // handed the arriving message's own store.
        Junk = new JunkService(MailOptions, Contacts);

        // The catalogue before the pipeline, because the plugin host joins both: its commands
        // enter the catalogue and its arrival stage ends the pipeline.
        Commands = BuiltInCommands();
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
        // the newsletters the reader reads as articles, then the plugins — last, so a hook sees
        // where the application's own handlers left it. Both protocols run the same pipeline, so
        // all of it means the same thing on POP3 and IMAP.
        //
        // Newsletters go after the rules on purpose: a reader who has written a rule about a
        // publication meant that rule, and it should win over the general arrangement.
        // The application's clock rather than the machine's: a rule whose action is "flag for
        // follow-up today" writes a date, and a date written from a second clock disagrees with
        // the one the list is grouping by the moment MAILBOX_TODAY pins anything. Live and
        // identical to the machine's in an ordinary run — see PosedClock.
        //
        // The feed lists stand before the pipeline that carries their router: the pipeline takes
        // its handlers by value, so a field assigned below this line rides along as null — and a
        // null handler throws on every arriving message while the ones behind it still run,
        // which reads as a working sync with a warning in the log and no newsletters in Feeds.
        Feeds = new Mailbox.Core.Feeds.FeedSubscriptions(Settings);
        Mutes = new Mailbox.Core.Feeds.MuteFilters(Settings);
        Newsletters = new Mailbox.Protocols.NewsletterRouter(Feeds);
        Rules = new RulesHandler(() => Mailbox.Core.PosedClock.Now);
        Arrival = new ArrivalPipeline(
            Junk, new IgnoreHandler(), new FocusedInboxHandler(), Rules, Newsletters, Plugins.Arrivals);
        var arrival = Arrival;

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
            mail => new SmtpSender(mail) { FileSentCopies = MailOptions.SaveCopiesInSent, OnSent = Rules },
            mail => new ImapSynchronizer(mail)
            {
                Authentication = signatures,
                OnArrival = arrival,
            });

        // The retention window on Recover Deleted Items: what was deleted longer ago than
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

        // Built here with the other settings-backed lists, and handed to the sync service, which
        // was made further up before this existed.
        Published = new Mailbox.Core.Calendars.PublishedCollections(Settings);
        PimSync.Published = Published;
        FeedReader = new Mailbox.Protocols.FeedReceiver(Feeds)
        {
            Mutes = Mutes,

            // Set here rather than only when the Feeds module is first opened: the scheduled poll
            // runs whether or not the reader has been in there this session, and an interval that
            // only took effect after a visit would be one that did nothing on most launches.
            DefaultRefresh = Settings.GetNumber(Views.FeedReadingDialog.IntervalKey, 0) is > 0 and var everyMinutes
                ? TimeSpan.FromMinutes(everyMinutes)
                : null,
        };
        Stationery = new StationeryFonts(Settings);
        Groups = new SendReceiveGroups(Settings);
        Signatures = new Signatures(Settings);
        Identities = new Identities(Settings);
        UndoSend = new UndoSend(Settings);

        // The list's field vocabulary learns the plugins' columns: the hooks are how a Core
        // file that cannot know the host still labels and sizes an id a plugin owns.
        Mailbox.Core.Views.ViewFields.ExtraLabel = id => Plugins.ColumnLabel(id);
        Mailbox.Core.Views.ViewFields.ExtraWidth = id =>
            Plugins.Columns().FirstOrDefault(c => c.Id == id) is { Id.Length: > 0 } column ? column.Width : null;

        // Last in the composition, so everything a plugin's Initialize reaches for is already
        // standing. The window subscribes to Changed for its ribbon; nothing here needs to.
        Plugins.Start();

        // The startup update check, only when the Options page's own switch says so, and never
        // in a capture run — the design's "nothing phones home" is the default, and this is the one
        // standing consent that overrides it. The answer goes to the log; the Backstage's
        // button is the interactive ask.
        if (!WindowCapture.IsRequested && Settings.GetBool(UpdateCheck.AutomaticKey))
        {
            _ = Task.Run(async () => Log.Info($"Update: {await UpdateCheck.CheckAsync()}"));
        }

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

            // A tray icon with a menu, part of the desktop-integration contract. Not during a capture run — the harness
            // starts many instances, and a tray icon per capture would clutter the session's
            // notification area and outlive the process that made it.
            var trayUp = !WindowCapture.IsRequested && InstallTrayIcon(window, desktop);

            // The badge cannot be photographed on a tray, so the harness writes it to a file:
            // MAILBOX_TRAY_BADGE=<count>:<path.png> renders the icon wearing that count, and
            // =auto:<path.png> renders what this store actually asks for — which is the only
            // way to check the count itself, the tray being off in a capture run.
            if (Environment.GetEnvironmentVariable("MAILBOX_TRAY_BADGE") is { Length: > 0 } badge)
            {
                window.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => WriteBadgeSample(badge, window.DataContext as ShellViewModel),
                    Avalonia.Threading.DispatcherPriority.Background);
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

                // Said explicitly rather than left at the lifetime's default, OnLastWindowClose.
                // The two were the same until the shell grew hidden machinery windows: the warm
                // message window is a real window, so under the default the shell's close no
                // longer closed the *last* window, nothing shut down, and the process outlived
                // its interface — a tray icon over no window, every activation of which failed
                // re-showing a closed shell. The application's life is the shell window's,
                // whatever machinery is pooled behind it.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }

            // The dispatcher stall watchdog, when a run asks for it or is already in debug: a
            // background thread that logs the UI thread the moment it blocks for longer than a
            // frame allows. Off in an ordinary run, so it carries no cost there; kept alive for
            // the process's lifetime and stopped when the window closes.
            if (!WindowCapture.IsRequested && DispatcherStallWatchdog.Requested)
            {
                var watchdog = new DispatcherStallWatchdog(
                    action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
                    DispatcherStallWatchdog.RequestedThreshold);
                watchdog.Start();
                window.Closed += (_, _) => watchdog.Dispose();
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

    /// <summary>
    /// Every built-in command, in one catalogue.
    /// </summary>
    /// <remarks>
    /// A method rather than eleven lines inside start-up because <c>--export-shortcuts</c> needs
    /// the same catalogue without starting an application, and a shortcut page generated from a
    /// second list of registrations would drift from the one the keys resolve through — which is
    /// the whole reason the page is generated rather than written.
    /// </remarks>
    internal static CommandCatalog BuiltInCommands()
    {
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        catalog.RegisterRange(ViewCommands.All);
        catalog.RegisterRange(ComposeCommands.All);
        catalog.RegisterRange(CalendarCommands.All);
        catalog.RegisterRange(AppointmentCommands.All);
        catalog.RegisterRange(ContactCommands.All);
        catalog.RegisterRange(PeopleCommands.All);
        catalog.RegisterRange(TaskCommands.All);
        catalog.RegisterRange(NoteCommands.All);
        catalog.RegisterRange(JournalCommands.All);
        catalog.RegisterRange(FeedCommands.All);
        return catalog;
    }
}
