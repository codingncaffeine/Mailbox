namespace Mailbox.Core;

/// <summary>
/// Which generation of the reference application the shell imitates. Orthogonal to the colour
/// theme: any of the four palettes works with either layout.
/// </summary>
/// <remarks>
/// The two are genuinely different products rather than a restyle, so this is a structural
/// switch rather than a token set. Colour lives in <c>Mailbox.Theming</c>; this decides what
/// chrome exists at all.
/// </remarks>
public enum ShellLayoutMode
{
    /// <summary>
    /// The reference application. Full ribbon with a tab strip, Quick Access Toolbar in the title bar,
    /// module switcher along the bottom of the folder pane, search above the message list.
    /// </summary>
    Classic,

    /// <summary>
    /// The reference's newer Windows client. A thin unified header carrying search, a single-row
    /// command bar in place of the ribbon, and a vertical app rail down the left edge holding
    /// Mail, Calendar, People, Tasks and Apps — the arrangement it shares with the same vendor's
    /// web client.
    /// </summary>
    Modern,
}

public static class ShellLayoutModes
{
    public const string Variable = "MAILBOX_LAYOUT";

    public static ShellLayoutMode Resolve()
        => Environment.GetEnvironmentVariable(Variable)?.ToLowerInvariant() switch
        {
            "modern" or "new" => ShellLayoutMode.Modern,
            _ => ShellLayoutMode.Classic,
        };

    public static string DisplayName(ShellLayoutMode mode) => mode switch
    {
        ShellLayoutMode.Modern => "Modern",
        _ => "Classic",
    };
}
