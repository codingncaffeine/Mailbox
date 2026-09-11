using Avalonia.Controls.ApplicationLifetimes;
using Mailbox.Core.Settings;
using Mailbox.Theming;
using Mailbox.Theming.Fonts;

namespace Mailbox.App;

public partial class App
{
    /// <summary>
    /// The progress toaster's process: the reader's theme and one dialog, and none of the rest.
    /// </summary>
    /// <remarks>
    /// The same steps <see cref="StartUp"/> takes to put the theme in place, in the same order —
    /// typefaces, fonts, language, theme, appearance — so the toaster cannot drift from the look
    /// of the window it reports for. The settings are read into memory and never written: this
    /// process sees the reader's theme and has no business changing anything.
    /// </remarks>
    private void StartToaster()
    {
        BundledFonts.Register();
        Settings = SettingsStore.InMemoryCopy();
        Fonts = FontResolver.FromSystem();
        LoadLanguage();

        Themes = new ThemeService(
            Fonts, Mailbox.Theming.Files.ThemeLibrary.Load(Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory()));
        RestoreAppearance();

        // What turns the theme's tokens into the resources every brush in the dialog is bound to.
        // Without it the dialog is drawn in nothing at all — measured: a toaster the right size and
        // nine-tenths transparent, beside the in-process dialog's solid one.
        _ = new Theming.ThemeResourceBridge(Resources, Themes);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ProgressToasterCompanion.Start(desktop);
        }
    }
}
