using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mailbox.App.Theming;
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
        Settings = new SettingsStore();
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

        // Signature checking happens as mail is collected, never as it is drawn. The receiver
        // gets the verifier; nothing else does.
        Resolver = new DnsResolver();
        var signatures = Resolver.CanResolve ? new DkimVerification(Resolver) : null;

        Transfer = new SendReceiveService(
            mail => new Pop3Receiver(mail) { Authentication = signatures });

        Commands = new CommandCatalog();
        Commands.RegisterRange(MailCommands.All);
        Commands.RegisterRange(ViewCommands.All);
        Commands.RegisterRange(ComposeCommands.All);

        RibbonEdits = new RibbonCustomization();
        QuickAccess = new QuickAccessLayout(Settings, DefaultRibbonLayouts.Mail.QuickAccess);
        Groups = new SendReceiveGroups(Settings);
        Signatures = new Signatures(Settings);

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
            desktop.MainWindow = window;

            // Fidelity harness: pose the window at an exact width, then render once and exit
            // when MAILBOX_CAPTURE is set.
            WindowCapture.ApplyRequestedSize(window);
            WindowCapture.AttachTo(window, () => desktop.Shutdown());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
