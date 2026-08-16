using Mailbox.Controls.Ribbon;
using Mailbox.Core.Ribbon;

namespace Mailbox.App.Views;

/// <summary>
/// Opens a window's ribbon in the layout it was last left in, and remembers each change made
/// from the Ribbon Display Options menu — per window kind, as the reference does.
/// </summary>
/// <remarks>
/// The ribbon control knows nothing about settings; this is the piece between it and
/// <see cref="RibbonDisplaySettings"/>. The fidelity harness poses a mode with
/// <c>MAILBOX_RIBBON</c>, and a pose wins over what is remembered — it is set before the change
/// handler is attached, so posing writes nothing, and a capture run is on a scratch copy of the
/// settings file besides.
/// </remarks>
internal static class RibbonDisplayMemory
{
    /// <param name="ribbon">The window's ribbon, freshly constructed.</param>
    /// <param name="host">Whose memory to use.</param>
    /// <param name="pose">
    /// <c>classic</c>, <c>simplified</c>, <c>collapsed</c> or <c>revealed</c> from the harness,
    /// or null to open as remembered.
    /// </param>
    public static void Wire(RibbonView ribbon, RibbonWindow host, string? pose)
    {
        ArgumentNullException.ThrowIfNull(ribbon);

        switch (pose?.Trim().ToLowerInvariant())
        {
            case "classic":
                ribbon.DisplayMode = RibbonDisplayMode.Classic;
                break;
            case "simplified":
                ribbon.DisplayMode = RibbonDisplayMode.Simplified;
                break;
            case "collapsed" or "revealed":
                ribbon.DisplayMode = RibbonDisplayMode.Collapsed;
                break;
            default:
                var remembered = App.RibbonDisplay.Get(host);
                // The layout first, so a collapsed ribbon knows what to come back as.
                ribbon.DisplayMode = remembered.Layout;
                if (remembered.TabsOnly) ribbon.DisplayMode = RibbonDisplayMode.Collapsed;
                break;
        }

        ribbon.DisplayModeChanged += (_, _) => App.RibbonDisplay.Set(
            host, ribbon.ExpandedMode, tabsOnly: ribbon.DisplayMode == RibbonDisplayMode.Collapsed);
    }
}
