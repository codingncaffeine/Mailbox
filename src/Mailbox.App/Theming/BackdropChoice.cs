using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;
using Mailbox.Theming;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Theming;

/// <summary>
/// The reader's Mailbox Background: what the "Mailbox Background:" row chose, kept in the
/// settings and composed into the theme through the appearance slot. Empty means "from the
/// theme" — the slot stays clear and whatever the theme itself says stands, which for every
/// built-in is nothing. An explicit choice beats the theme's, and clearing it is a complete
/// return to stock: the slot holds these keys and no others, so there is nowhere for a theme
/// to leave residue.
/// </summary>
internal static class BackdropChoice
{
    /// <summary>"" (from the theme), "none", "pattern:&lt;name&gt;", or an image path.</summary>
    internal const string Setting = "appearance.backdrop";

    /// <summary>The alignment a drag wrote, as "x% y%"; empty leaves the theme's.</summary>
    internal const string AlignmentSetting = "appearance.backdrop.alignment";

    /// <summary>The reach override: "" (from the theme), "caption", or "tabs".</summary>
    internal const string ExtentSetting = "appearance.backdrop.extent";

    /// <summary>Overrides the stored choice at startup: a pattern name, "none", "theme", or a path.</summary>
    internal const string Variable = "MAILBOX_BACKDROP";

    /// <summary>Overrides the stored alignment at startup, as "x% y%" or CSS keywords.</summary>
    internal const string AlignVariable = "MAILBOX_BACKDROP_ALIGN";

    /// <summary>Reads the stored (or harness-given) choice and composes it into the theme.</summary>
    internal static void Restore(SettingsStore settings, ThemeService themes)
    {
        var choice = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } forced
            ? Normalise(forced)
            : settings.GetString(Setting);
        var alignment = Environment.GetEnvironmentVariable(AlignVariable) is { Length: > 0 } forcedAlign
            ? forcedAlign
            : settings.GetString(AlignmentSetting);

        Apply(themes, choice, alignment, settings.GetString(ExtentSetting));

        if (Environment.GetEnvironmentVariable(Variable) is not null)
        {
            themes.Tokens.TryGetString(TokenKeys.TitleBar.Backdrop, out var resolved);
            themes.Tokens.TryGetString(TokenKeys.TitleBar.BackdropAlignment, out var resolvedAlign);
            Log.Info($"Harness: backdrop choice \"{choice}\" applied; {TokenKeys.TitleBar.Backdrop} resolves to "
                     + $"\"{resolved}\", alignment \"{resolvedAlign}\".");
        }
    }

    /// <summary>Stores a new choice and applies it. Empty returns the decision to the theme.</summary>
    internal static void Choose(SettingsStore settings, ThemeService themes, string choice)
    {
        settings.Set(Setting, choice);
        if (choice.Length == 0 || choice == "none") settings.Set(AlignmentSetting, string.Empty);
        Apply(themes, choice, settings.GetString(AlignmentSetting), settings.GetString(ExtentSetting));
    }

    /// <summary>Stores the alignment a drag committed and applies it.</summary>
    internal static void Align(SettingsStore settings, ThemeService themes, string alignment)
    {
        settings.Set(AlignmentSetting, alignment);
        Apply(themes, settings.GetString(Setting), alignment, settings.GetString(ExtentSetting));
    }

    /// <summary>Stores the reach override — "" hands it back to the theme — and applies it.</summary>
    internal static void Reach(SettingsStore settings, ThemeService themes, string extent)
    {
        settings.Set(ExtentSetting, extent);
        Apply(themes, settings.GetString(Setting), settings.GetString(AlignmentSetting), extent);
    }

    /// <summary>The current stored choice.</summary>
    internal static string Current(SettingsStore settings) => settings.GetString(Setting);

    /// <summary>The current stored reach override.</summary>
    internal static string Reach(SettingsStore settings) => settings.GetString(ExtentSetting);

    /// <summary>
    /// Installing a theme that brings its own background is a newer wish than whatever the
    /// Mailbox Background row held: a "(None)" chosen months ago must not silently blank the
    /// artwork somebody installed a theme for. The choice returns to "(From the theme)";
    /// picking it again afterwards works exactly as before.
    /// </summary>
    /// <returns>True when the standing choice was cleared for the theme's own background.</returns>
    internal static bool YieldToTheme(SettingsStore settings, ThemeService themes, Mailbox.Theming.Files.ThemeFile file)
    {
        if (Current(settings).Length == 0) return false;
        if (!file.Tokens.TryGetRaw(TokenKeys.TitleBar.Backdrop, out var backdrop) || backdrop.Length == 0) return false;

        Log.Info($"\"{file.Id}\" brings its own background; the Mailbox Background choice returns to (From the theme).");
        Choose(settings, themes, string.Empty);
        return true;
    }

    private static void Apply(ThemeService themes, string choice, string alignment, string extent)
    {
        if (choice.Length == 0 && extent.Length == 0)
        {
            themes.SetAppearance(null);
            return;
        }

        var appearance = new TokenSet();
        if (choice.Length > 0)
        {
            appearance.Set(TokenKeys.TitleBar.Backdrop, choice == "none" ? string.Empty : choice);

            // A chosen image is drawn whole and opaque; a pattern keeps the theme's
            // calibrated subtlety. Both are references, not values, wherever the theme
            // already decides.
            if (choice != "none" && !choice.StartsWith("pattern:", StringComparison.OrdinalIgnoreCase))
            {
                appearance.Set(TokenKeys.TitleBar.BackdropOpacity, "1");
                appearance.Set(TokenKeys.TitleBar.BackdropSize, "cover");
            }

            if (alignment.Length > 0) appearance.Set(TokenKeys.TitleBar.BackdropAlignment, alignment);
        }

        // The reach is its own choice: it may rein a theme's image back to the caption, or
        // send a chosen image down through the tab strip — with or without a choice above.
        if (extent.Length > 0) appearance.Set(TokenKeys.TitleBar.BackdropExtent, extent);

        themes.SetAppearance(appearance);
    }

    /// <summary>
    /// Brings the reader's chosen image under the themes directory, re-encoded to PNG through
    /// the decoder — nothing lands on disk with its original bytes — and returns the relative
    /// path the backdrop layer resolves, so the config directory moves whole. Null when the
    /// file does not decode.
    /// </summary>
    internal static string? ImportImage(string sourcePath)
    {
        try
        {
            using var stream = File.OpenRead(sourcePath);
            using var image = new Avalonia.Media.Imaging.Bitmap(stream);

            var directory = Path.Combine(Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory(), "images", "own");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, "background.png");
            image.Save(destination, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return Path.Combine("images", "own", "background.png");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"The chosen background \"{sourcePath}\" could not be used: {ex.Message}");
            return null;
        }
    }

    /// <summary>The harness variable's short forms: a bare pattern name, or "theme" for "from the theme".</summary>
    private static string Normalise(string value)
    {
        var text = value.Trim();
        if (string.Equals(text, "theme", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase)) return "none";
        return Views.CaptionPatterns.IsKnown(text) ? $"pattern:{text.ToLowerInvariant()}" : text;
    }
}
