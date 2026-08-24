using Mailbox.Core.Settings;

namespace Mailbox.Core.Ribbon;

/// <summary>Which window's ribbon a display setting belongs to.</summary>
/// <remarks>
/// The reference remembers the ribbon layout per kind of window: switching the main window to
/// the classic ribbon leaves a message window on the simplified one until it is switched there
/// too. Two hosts, two settings.
/// </remarks>
public enum RibbonWindow
{
    /// <summary>The main window — mail, and the inline reply's compose tabs inside it.</summary>
    Shell,

    /// <summary>A message window of its own.</summary>
    Compose,

    /// <summary>A received message opened in its own window — the read ribbon's host.</summary>
    Message,
}

/// <summary>
/// The two choices the Ribbon Display Options menu makes, as the menu makes them: which layout
/// (Simplified or Classic), and whether the ribbon stays up or shows its tabs only.
/// </summary>
/// <param name="Layout">Simplified or Classic — never Collapsed; that is the other axis.</param>
/// <param name="TabsOnly">"Show tabs only": collapsed to the strip until a tab is clicked.</param>
public sealed record RibbonDisplayState(RibbonDisplayMode Layout, bool TabsOnly)
{
    /// <summary>What the ribbon control shows for this state.</summary>
    public RibbonDisplayMode Mode => TabsOnly ? RibbonDisplayMode.Collapsed : Layout;

    public static RibbonDisplayState Default { get; } = new(RibbonDisplayMode.Simplified, TabsOnly: false);
}

/// <summary>
/// The ribbon display choices, remembered across launches the way the reference remembers
/// them.
/// </summary>
/// <remarks>
/// Two keys per host rather than one, because the menu offers two independent choices and a
/// collapsed ribbon has to come back in the layout it was collapsed from: <c>ribbon.layout</c>
/// is <c>simplified</c> or <c>classic</c>, <c>ribbon.show</c> is <c>always</c> or <c>tabs</c>;
/// the compose window's pair is <c>ribbon.compose.layout</c> / <c>ribbon.compose.show</c>.
/// Written as words so the settings file stays something a person can read and edit; a value
/// that is neither word reads as the default rather than as an error.
/// </remarks>
public sealed class RibbonDisplaySettings
{
    public const string ShellLayoutKey = "ribbon.layout";
    public const string ShellShowKey = "ribbon.show";
    public const string ComposeLayoutKey = "ribbon.compose.layout";
    public const string ComposeShowKey = "ribbon.compose.show";
    public const string MessageLayoutKey = "ribbon.message.layout";
    public const string MessageShowKey = "ribbon.message.show";

    private readonly SettingsStore _settings;

    public RibbonDisplaySettings(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>What <paramref name="host"/>'s ribbon should open as.</summary>
    public RibbonDisplayState Get(RibbonWindow host)
    {
        var (layoutKey, showKey) = Keys(host);

        var layout = string.Equals(_settings.GetString(layoutKey), "classic", StringComparison.OrdinalIgnoreCase)
            ? RibbonDisplayMode.Classic
            : RibbonDisplayMode.Simplified;

        var tabsOnly = string.Equals(_settings.GetString(showKey), "tabs", StringComparison.OrdinalIgnoreCase);

        return new RibbonDisplayState(layout, tabsOnly);
    }

    /// <summary>
    /// Remembers a change made from the menu. <paramref name="layout"/> is the layout the ribbon
    /// shows, or would show if it were not collapsed — never Collapsed itself.
    /// </summary>
    public void Set(RibbonWindow host, RibbonDisplayMode layout, bool tabsOnly)
    {
        if (layout == RibbonDisplayMode.Collapsed)
        {
            throw new ArgumentException(
                "Collapsed is not a layout; pass the layout the ribbon collapsed from and tabsOnly: true.",
                nameof(layout));
        }

        var (layoutKey, showKey) = Keys(host);
        _settings.Set(layoutKey, layout == RibbonDisplayMode.Classic ? "classic" : "simplified");
        _settings.Set(showKey, tabsOnly ? "tabs" : "always");
    }

    private static (string Layout, string Show) Keys(RibbonWindow host) => host switch
    {
        RibbonWindow.Compose => (ComposeLayoutKey, ComposeShowKey),
        RibbonWindow.Message => (MessageLayoutKey, MessageShowKey),
        _ => (ShellLayoutKey, ShellShowKey),
    };
}
