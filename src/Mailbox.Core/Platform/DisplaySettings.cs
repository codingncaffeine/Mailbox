using System.Globalization;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Platform;

/// <summary>Which windowing backend to start on.</summary>
public enum DisplayBackend
{
    /// <summary>
    /// What Mailbox defaults to: the native Wayland backend on a Wayland session, X11 anywhere
    /// else, and X11 underneath if that backend cannot open a window.
    /// </summary>
    Auto,

    /// <summary>X11, always.</summary>
    X11,

    /// <summary>The native Wayland backend. Strict: no compositor, no start.</summary>
    Wayland,
}

/// <summary>
/// The two display choices a Linux desktop makes for an application and the reference never
/// had to: which windowing backend to open on, and what scale to lay the window out at.
/// </summary>
/// <remarks>
/// Both are read before the platform initialises — from the settings file, by <c>Program.Main</c>
/// — because neither can change once a window is up. On X11 the backend takes its scale from
/// the screen's <c>Xft.dpi</c>, which on one desktop was 100 and made the whole window 4% larger
/// than a 100% reference; a reader who wants to judge the application against another at the
/// same size can pin 100% here. <c>display.backend</c> is <c>auto</c>, <c>x11</c> or
/// <c>wayland</c>; <c>display.scale</c> is <c>auto</c> or a number such as <c>1</c> or
/// <c>1.25</c>. The environment variables the harness uses stay the override: a capture run
/// pins its own scale, and <c>MAILBOX_WAYLAND=1</c> picks the backend regardless.
/// </remarks>
public sealed class DisplaySettings
{
    public const string BackendKey = "display.backend";
    public const string ScaleKey = "display.scale";

    /// <summary>The scales the Options row offers, beyond automatic.</summary>
    public static readonly IReadOnlyList<double> Scales = [1.0, 1.25, 1.5, 1.75, 2.0];

    private readonly SettingsStore _settings;

    public DisplaySettings(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public DisplayBackend Backend
    {
        get => _settings.GetString(BackendKey).Trim().ToLowerInvariant() switch
        {
            "x11" => DisplayBackend.X11,
            "wayland" => DisplayBackend.Wayland,
            _ => DisplayBackend.Auto,
        };
        set => _settings.Set(BackendKey, value switch
        {
            DisplayBackend.X11 => "x11",
            DisplayBackend.Wayland => "wayland",
            _ => "auto",
        });
    }

    /// <summary>The pinned scale, or null for automatic — the desktop's own.</summary>
    public double? Scale
    {
        get
        {
            var text = _settings.GetString(ScaleKey).Trim();
            if (text.Length == 0 || string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase)) return null;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale is >= 0.5 and <= 4
                ? scale
                : null;
        }
        set => _settings.Set(ScaleKey, value is { } scale
            ? scale.ToString(CultureInfo.InvariantCulture)
            : "auto");
    }

    /// <summary>
    /// What the choices come to in the environment the platform reads, applied only where the
    /// environment has not already spoken: a pinned scale becomes the two Avalonia scale
    /// variables, and the Wayland backend becomes <c>MAILBOX_WAYLAND=1</c>.
    /// </summary>
    /// <returns>A line for the log naming what was applied, or null when nothing was.</returns>
    public string? ApplyToEnvironment(Func<string, string?> read, Action<string, string> write)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        var applied = new List<string>();

        if (Scale is { } scale
            && string.IsNullOrEmpty(read("AVALONIA_SCREEN_SCALE_FACTORS"))
            && string.IsNullOrEmpty(read("AVALONIA_GLOBAL_SCALE_FACTOR")))
        {
            write("AVALONIA_SCREEN_SCALE_FACTORS", "mailbox-setting=1");
            write("AVALONIA_GLOBAL_SCALE_FACTOR", scale.ToString(CultureInfo.InvariantCulture));
            applied.Add($"scale {scale:0.##}");
        }

        if (Backend == DisplayBackend.Wayland && string.IsNullOrEmpty(read("MAILBOX_WAYLAND")))
        {
            write("MAILBOX_WAYLAND", "1");
            applied.Add("the Wayland backend");
        }

        return applied.Count == 0 ? null : "Display settings: " + string.Join(", ", applied) + ".";
    }
}
