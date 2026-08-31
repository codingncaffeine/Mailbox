using Avalonia;
using Avalonia.Platform;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App;

/// <summary>
/// The fifth theme choice: "Use the desktop's setting". Resolves the desktop's light-or-dark
/// preference to a built-in — Colorful for light, Dark Gray for dark — and re-resolves live
/// when the desktop switches, so the mail client follows the sunset like everything else.
/// </summary>
/// <remarks>
/// The preference comes through Avalonia's platform settings, which on Linux is the
/// <c>org.freedesktop.portal.Settings</c> colour-scheme key and its change signal. When the
/// platform offers no settings at all — a bare session with no portal — <c>gsettings</c> is
/// asked once as the fallback; a desktop with neither gets light, said in the log rather than
/// guessed silently.
/// </remarks>
public static class DesktopTheme
{
    /// <summary>What the theme setting stores for this choice — never a theme id.</summary>
    public const string Sentinel = "system";

    /// <summary>What the choice maps to; the light half is the application's own default.</summary>
    public const string LightTheme = "colorful";

    public const string DarkTheme = "darkgray";

    private static bool _watching;

    /// <summary>The theme id the desktop's current preference means.</summary>
    /// <remarks>
    /// The portal is asked directly and synchronously first. Avalonia's own platform settings
    /// read the same key, but over a connection that comes up in the background — at startup
    /// it still answers light while the desktop is dark, and the window would flash the wrong
    /// theme before the change signal corrected it. One blocking read settles it before
    /// anything is drawn; the other two sources are the fallbacks, in order of how much they
    /// know.
    /// </remarks>
    public static string Resolve()
    {
        if (PortalColorScheme() is { } portal)
        {
            return portal == 1 ? DarkTheme : LightTheme;
        }

        if (Application.Current?.PlatformSettings is { } platform)
        {
            return platform.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark
                ? DarkTheme
                : LightTheme;
        }

        return GSettingsSaysDark() ? DarkTheme : LightTheme;
    }

    /// <summary>
    /// The portal's <c>color-scheme</c> value — 0 no preference, 1 prefer dark, 2 prefer
    /// light — or null when the portal cannot be asked. One <c>dbus-send</c>, blocking, with
    /// a short leash: the answer decides the first frame's colours.
    /// </summary>
    private static uint? PortalColorScheme()
    {
        try
        {
            var run = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dbus-send",
                    ArgumentList =
                    {
                        "--session", "--print-reply=literal", "--reply-timeout=1500",
                        "--dest=org.freedesktop.portal.Desktop",
                        "/org/freedesktop/portal/desktop",
                        "org.freedesktop.portal.Settings.ReadOne",
                        "string:org.freedesktop.appearance", "string:color-scheme",
                    },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            run.Start();
            var answer = run.StandardOutput.ReadToEnd();
            run.WaitForExit(3000);
            if (run.ExitCode != 0) return null;

            // "   variant       uint32 1" — the number at the end is the value.
            var last = answer.TrimEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return uint.TryParse(last, out var value) ? value : null;
        }
        catch (Exception ex)
        {
            Log.Info($"The settings portal could not be asked for the colour scheme ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Arms the live half: whenever the desktop's colours change and the stored choice is
    /// still <see cref="Sentinel"/>, the resolved theme is applied on the spot. Idempotent —
    /// the subscription is taken once for the application's life.
    /// </summary>
    public static void Watch()
    {
        if (_watching || Application.Current?.PlatformSettings is not { } platform) return;
        _watching = true;

        platform.ColorValuesChanged += (_, values) =>
        {
            if (App.Settings.GetString(App.ThemeSetting) != Sentinel) return;

            // The signal repeats — once per changed colour value — and re-applying an applied
            // theme would repaint the window for nothing.
            var resolved = values.ThemeVariant == PlatformThemeVariant.Dark ? DarkTheme : LightTheme;
            if (string.Equals(App.Themes.ThemeId, resolved, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                App.Themes.ApplyFresh(resolved);
                Log.Info($"Desktop theme changed; following it to {resolved}.");
            }
            catch (Mailbox.Theming.Tokens.ThemeResolutionException ex)
            {
                Log.Warn($"The desktop's theme change could not be followed: {ex.Message}");
            }
        };
    }

    /// <summary>
    /// The one question, asked of gsettings: is the desktop's colour scheme dark? Only for a
    /// platform with no settings source of its own; failures of any kind mean "not dark".
    /// </summary>
    private static bool GSettingsSaysDark()
    {
        try
        {
            var run = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "gsettings",
                    ArgumentList = { "get", "org.gnome.desktop.interface", "color-scheme" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            run.Start();
            var answer = run.StandardOutput.ReadToEnd();
            run.WaitForExit(2000);
            return answer.Contains("prefer-dark", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Info($"gsettings could not be asked for the colour scheme ({ex.Message}); using light.");
            return false;
        }
    }
}
