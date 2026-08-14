namespace Mailbox.Core.Ribbon;

/// <summary>
/// Which tabs a strip shows, given which contextual sets are currently active.
/// </summary>
/// <remarks>
/// Office reveals contextual tabs in named sets rather than one at a time — "Table Tools"
/// carrying Design and Layout — so the set is the unit a host switches on and this is the rule
/// that turns a set of active names into a tab list.
/// <para>
/// Kept out of the control for the same reason the collapse ladder is: a rule about which tabs
/// exist can be tested against a layout document, where testing it against a rendered strip
/// needs a window, a toolkit and a screenshot.
/// </para>
/// </remarks>
public static class ContextualTabs
{
    /// <summary>Every ordinary tab, plus the contextual ones whose set is active.</summary>
    public static IEnumerable<RibbonTab> Visible(
        IEnumerable<RibbonTab> tabs, IReadOnlySet<string> activeGroups)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(activeGroups);

        return tabs.Where(tab =>
            tab.ContextualGroup is not { } group || activeGroups.Contains(group));
    }

    /// <summary>
    /// The tab that should be selected once <paramref name="current"/> may have been taken
    /// away, or null when the current one is still on screen.
    /// </summary>
    /// <remarks>
    /// A contextual set disappearing while one of its tabs is selected leaves the ribbon
    /// pointing at a tab that is no longer in the strip — which renders as an empty body under
    /// a strip with nothing highlighted, rather than as an error.
    /// </remarks>
    public static RibbonTab? FallbackFor(
        IEnumerable<RibbonTab> tabs, IReadOnlySet<string> activeGroups, string current)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        var visible = Visible(tabs, activeGroups).ToList();

        if (visible.Any(t => string.Equals(t.Id, current, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return visible.FirstOrDefault(t => !t.IsBackstage && !t.IsContextual);
    }
}
