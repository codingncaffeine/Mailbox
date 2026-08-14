namespace Mailbox.Core.Ribbon;

/// <summary>
/// How much of a group is showing once the window is too narrow to show all of them.
/// </summary>
/// <remarks>
/// The published ribbon framework spec models this as a set of named <c>SizeDefinition</c>s per
/// group, with the degrade order given by <c>Scale</c> entries listed largest-first. Ours is the
/// same idea reduced to what a single <see cref="RibbonGroup.CollapsePriority"/> can express,
/// which is enough because our items only come in two sizes.
/// </remarks>
public enum RibbonGroupVariant
{
    /// <summary>Every item at the size the layout document asked for.</summary>
    Normal,

    /// <summary>Large items demoted to small, so the group packs into columns of three.</summary>
    Compact,

    /// <summary>The whole group as one button, its contents behind a flyout.</summary>
    Popup,
}

/// <summary>What one group measures at each variant, and where it sits in the degrade order.</summary>
public readonly record struct RibbonGroupWidth(
    string GroupId,
    int CollapsePriority,
    double Normal,
    double Compact,
    double Popup)
{
    public double At(RibbonGroupVariant variant) => variant switch
    {
        RibbonGroupVariant.Normal => Normal,
        RibbonGroupVariant.Compact => Compact,
        _ => Popup,
    };
}

/// <summary>
/// Decides which groups give way, and in what order, when a tab's groups do not fit.
/// </summary>
/// <remarks>
/// The reference application never scrolls its ribbon. It degrades the least important groups
/// and keeps the rest legible, which is why <see cref="RibbonGroup.CollapsePriority"/> is part of
/// the layout document rather than a rendering detail — a user who rearranges the ribbon is
/// rearranging the degrade order with it.
/// <para>
/// The rule: the highest priority number gives way first, and it gives way <em>completely</em> —
/// Normal to Compact to Popup — before the next group is touched. Collapsing every group by one
/// step at a time would technically fit, but it makes the whole ribbon worse at once instead of
/// spending the least important group first, which is what the reference does and what a user
/// who set the priorities is asking for.
/// </para>
/// <para>
/// Kept free of any UI type so the order can be tested against widths rather than against a
/// window that has to be sized and photographed.
/// </para>
/// </remarks>
public static class RibbonCollapsePolicy
{
    /// <summary>
    /// The variant for each group, positionally matching <paramref name="groups"/>.
    /// </summary>
    /// <param name="groups">The tab's groups, in the order they are drawn.</param>
    /// <param name="available">Width the groups have to share.</param>
    /// <param name="furniture">
    /// Width taken by everything that is not a group and cannot collapse — the separators
    /// between them, and any padding the host adds.
    /// </param>
    public static RibbonGroupVariant[] Choose(
        IReadOnlyList<RibbonGroupWidth> groups, double available, double furniture = 0)
    {
        ArgumentNullException.ThrowIfNull(groups);

        // Normal is the zero value, so a fresh array is already the uncollapsed answer.
        var chosen = new RibbonGroupVariant[groups.Count];
        if (groups.Count == 0) return chosen;

        // An unconstrained or not-yet-known width is not a reason to collapse anything. The
        // first measure pass routinely arrives with infinity, and collapsing there would settle
        // the ribbon into a shape it then has to climb back out of.
        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
        {
            return chosen;
        }

        var total = furniture;
        foreach (var group in groups) total += group.Normal;

        while (total > available)
        {
            var next = -1;
            for (var i = 0; i < groups.Count; i++)
            {
                if (chosen[i] == RibbonGroupVariant.Popup) continue;
                if (next < 0 || groups[i].CollapsePriority > groups[next].CollapsePriority)
                {
                    next = i;
                }
            }

            // Everything is already a popup and it still does not fit. There is nothing further
            // to give; the host clips, exactly as the reference does at absurd widths.
            if (next < 0) break;

            var before = groups[next].At(chosen[next]);
            chosen[next] = chosen[next] == RibbonGroupVariant.Normal
                ? RibbonGroupVariant.Compact
                : RibbonGroupVariant.Popup;

            // Each pass moves one group one step, and a group has two steps, so this terminates
            // in at most 2n passes even if a variant somehow measures no narrower than the one
            // it replaced.
            total += groups[next].At(chosen[next]) - before;
        }

        return chosen;
    }
}
