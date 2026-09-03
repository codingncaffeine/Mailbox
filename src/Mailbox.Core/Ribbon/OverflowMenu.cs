using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>One row of the Simplified bar's "…": a command, a rule, or a submenu of commands.</summary>
/// <param name="Label">What the row reads, for a submenu; empty for a command or a rule.</param>
/// <param name="Command">The command this row runs, or null for a rule or a submenu.</param>
/// <param name="Children">A submenu's commands, in order; empty otherwise.</param>
public sealed record OverflowEntry(string Label, CommandId? Command, IReadOnlyList<CommandId> Children)
{
    public static OverflowEntry Rule { get; } = new(string.Empty, null, []);

    public static OverflowEntry For(CommandId command) => new(string.Empty, command, []);

    public static OverflowEntry Submenu(string label, IReadOnlyList<CommandId> children)
        => new(label, null, children);

    /// <summary>A group's name, with that group's commands listed under it.</summary>
    public static OverflowEntry Header(string label) => new(label, null, []);

    public bool IsRule => Command is null && Children.Count == 0 && Label.Length == 0;

    /// <summary>A heading rather than a row: not pickable, and names what follows it.</summary>
    public bool IsHeader => Command is null && Children.Count == 0 && Label.Length > 0;

    public bool IsSubmenu => Children.Count > 0;
}

/// <summary>
/// What the Simplified bar's "…" holds: the controls the bar pushed off at this width, and then
/// the rest of the tab.
/// </summary>
/// <remarks>
/// Grouped the way the reference groups it: each of the tab's groups as a heading, with that
/// group's commands listed under it, in ribbon order — Move &amp; Delete, Respond, Quick Steps,
/// Tags, Find, and so on. What the bar is still showing is not repeated here; everything else the
/// tab has is, whether it was pushed off a moment ago or was never on the bar.
/// <para>
/// Grouped rather than one flat list, and this is the load-bearing part: flat, this menu ran to
/// fifty-odd alphabetical entries — taller than the screen, with no scrollbar, so it opened with
/// its head cut off and the pushed-off commands were the part that got cut. The buttons the bar
/// had taken away were in the menu and could not be found in it, which is the same as not being
/// there. Headings with their commands under them is what the reference does with the same
/// problem, and its own menu is long: the flyout scrolls rather than the list being shortened.
/// </para>
/// <para>
/// The pushed-off commands used to come first and flat, on the reasoning that they are what
/// somebody who has just watched a button leave the bar is looking for. The reference does not do
/// that — its squeezed capture starts straight at the first group heading — and a command that
/// moves between two places in the menu depending on the window's width is harder to find again
/// than one that is always under its own group.
/// </para>
/// <para>
/// A plan rather than a menu so it can be checked without a window: what belongs in this list is
/// arithmetic over the layout, and the control's job is only to draw it.
/// </para>
/// </remarks>
public static class OverflowMenu
{
    /// <summary>Where a command with no tab of its own is offered.</summary>
    public const string BeyondLabel = "More Commands";

    /// <summary>Shown when the tab has nothing the bar is not already carrying.</summary>
    public const string EmptyLabel = "Nothing further on this tab";

    public static IReadOnlyList<OverflowEntry> Plan(
        RibbonTab tab,
        IReadOnlyList<RibbonItem> barItems,
        IReadOnlyList<CommandId> pushedOff,
        IReadOnlyList<MailboxCommand> beyondDefaultLayout,
        Func<CommandId, bool> known)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(barItems);
        ArgumentNullException.ThrowIfNull(pushedOff);
        ArgumentNullException.ThrowIfNull(beyondDefaultLayout);
        ArgumentNullException.ThrowIfNull(known);

        var plan = new List<OverflowEntry>();
        var listed = new HashSet<CommandId>();

        // What the bar is actually showing at this width. Everything else the tab has belongs in
        // the menu, whether it was pushed off a moment ago or was never on the bar at all —
        // the reference draws no line between the two, and its own squeezed capture starts
        // straight at the first group heading rather than at a flat block of what just left.
        var pushed = pushedOff.ToHashSet();
        var showing = barItems
            .Where(i => i.Kind != RibbonItemKind.Separator)
            .Select(i => i.Command)
            .Where(id => !pushed.Contains(id))
            .ToHashSet();

        foreach (var group in tab.Groups)
        {
            var commands = group.Items
                .Where(i => i.Kind != RibbonItemKind.Separator)
                .Select(i => i.Command)
                .Distinct()
                .Where(id => !showing.Contains(id) && !listed.Contains(id) && known(id))
                .ToList();

            if (commands.Count == 0) continue;

            plan.Add(OverflowEntry.Header(group.Label));

            foreach (var id in commands)
            {
                listed.Add(id);
                plan.Add(OverflowEntry.For(id));
            }
        }

        var beyond = beyondDefaultLayout
            .Where(c => !listed.Contains(c.Id) && known(c.Id))
            .OrderBy(c => c.Label, StringComparer.CurrentCulture)
            .Select(c => c.Id)
            .ToList();

        // The commands that are in the catalog and on no tab. A submenu rather than a heading
        // with sixty-odd rows under it: the reference's menu has no such section at all, and
        // inlining this one buried its groups — the part that is the reference's shape — under a
        // list of everything the application can do.
        if (beyond.Count > 0) plan.Add(OverflowEntry.Submenu(BeyondLabel, beyond));

        return plan;
    }
}
