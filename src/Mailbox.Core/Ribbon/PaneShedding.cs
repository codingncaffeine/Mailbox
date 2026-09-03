namespace Mailbox.Core.Ribbon;

/// <summary>
/// What the shell's panes do as the window narrows past the width they were all built for.
/// </summary>
/// <remarks>
/// The bar sheds its controls into the "…" and the tab strip squeezes and then scrolls; the panes
/// are the third thing that has to give, and until this they did not — the folder pane kept its
/// full 236 whatever the window did, so at 350 across it left the message list about sixty pixels
/// and the shell was unusable rather than narrow.
/// <para>
/// <b>Both thresholds sit at or below the width the window used to stop at</b>, which is the
/// point: 760 was the floor, so nothing at 760 or wider changes for anybody. A reader who has
/// never dragged the window narrow than it would go sees exactly what they saw before, and what
/// these rules govern is only the range that used to be unreachable.
/// </para>
/// <para>
/// Reversible, and deliberately not a setting. Neither of these writes anything down: they are
/// what the window is doing at this width, so widening it again gives back the pane the reader
/// asked for rather than one they have to ask for again. The reader's own choices —
/// <c>NavCollapsed</c>, <c>ReadingPaneVisible</c> — are untouched underneath.
/// </para>
/// </remarks>
public static class PaneShedding
{
    /// <summary>
    /// The width the shell used to refuse to go below, and the width at which the reading pane
    /// stops being drawn. It is the pane that wants the most room, so it is the first to go.
    /// </summary>
    public const double ReadingPaneFloor = 760;

    /// <summary>
    /// Below this the folder pane minimises to its strip. Chosen so that when it happens there is
    /// still a list worth reading beside it: the strip is 48 and the rail 56, which leaves a
    /// message list of about 500 at this width and about 250 at the window's own floor.
    /// </summary>
    public const double FolderPaneFloor = 600;

    /// <summary>
    /// The narrowest the shell goes, matching the reference: rail, minimised folder pane, list.
    /// </summary>
    public const double ShellFloor = 350;

    /// <summary>Whether the reading pane has to give up its room at this width.</summary>
    public static bool HidesReadingPane(double width) => width < ReadingPaneFloor;

    /// <summary>Whether the folder pane is down to its strip at this width.</summary>
    public static bool MinimisesFolderPane(double width) => width < FolderPaneFloor;
}
