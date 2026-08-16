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

    public bool IsRule => Command is null && Children.Count == 0;

    public bool IsSubmenu => Children.Count > 0;
}

/// <summary>
/// What the Simplified bar's "…" holds: the controls the bar pushed off at this width, and then
/// the rest of the tab.
/// </summary>
/// <remarks>
/// The pushed-off controls come first and flat, because they are what somebody who has just
/// watched a button leave the bar is looking for. Everything else the tab has goes under its own
/// group, one submenu each, and the commands that are in the catalogue but on no tab go under a
/// last one.
/// <para>
/// Grouped rather than one flat list, and this is the load-bearing part: flat, this menu ran to
/// fifty-odd alphabetical entries — taller than the screen, with no scrollbar, so it opened with
/// its head cut off and the pushed-off commands were the part that got cut. The buttons the bar
/// had taken away were in the menu and could not be found in it, which is the same as not being
/// there.
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

        foreach (var id in pushedOff)
        {
            if (!known(id) || !listed.Add(id)) continue;
            plan.Add(OverflowEntry.For(id));
        }

        if (plan.Count > 0) plan.Add(OverflowEntry.Rule);

        // What the bar carries at all, pushed off or not: those are on the bar, and do not
        // belong in a list of what is not.
        var onTheBar = barItems
            .Where(i => i.Kind != RibbonItemKind.Separator)
            .Select(i => i.Command)
            .ToHashSet();

        foreach (var group in tab.Groups)
        {
            var commands = group.Items
                .Where(i => i.Kind != RibbonItemKind.Separator)
                .Select(i => i.Command)
                .Distinct()
                .Where(id => !onTheBar.Contains(id) && !listed.Contains(id) && known(id))
                .ToList();

            if (commands.Count == 0) continue;

            foreach (var id in commands) listed.Add(id);
            plan.Add(OverflowEntry.Submenu(group.Label, commands));
        }

        var beyond = beyondDefaultLayout
            .Where(c => !listed.Contains(c.Id) && known(c.Id))
            .OrderBy(c => c.Label, StringComparer.CurrentCulture)
            .Select(c => c.Id)
            .ToList();

        if (beyond.Count > 0) plan.Add(OverflowEntry.Submenu(BeyondLabel, beyond));

        return plan;
    }
}
