using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// How large a ribbon item renders. Mirrors the published ribbon framework spec's size definitions:
/// a group declares its preference at each size, and the scaling policy degrades groups in a
/// declared order as the window narrows.
/// </summary>
public enum RibbonItemSize
{
    /// <summary>32px icon above a label that may wrap to two lines. The headline commands.</summary>
    Large,

    /// <summary>16px icon with the label beside it. Three stack vertically in a group.</summary>
    Small,
}

/// <summary>
/// the reference's own ribbon display modes, toggled from the chevron at the right end of the bar.
/// </summary>
public enum RibbonDisplayMode
{
    /// <summary>
    /// The the reference application default: one row of icon-and-label commands with separators, an
    /// overflow menu, and no group labels. Compact enough that the message list keeps the
    /// screen.
    /// </summary>
    Simplified,

    /// <summary>
    /// The tall ribbon: multi-row groups with labels beneath, large and small buttons, and
    /// dialog launchers. What people mean by "the classic ribbon".
    /// </summary>
    Classic,

    /// <summary>Collapsed to the tab strip; clicking a tab floats the body over the content.</summary>
    Collapsed,
}

public enum RibbonItemKind
{
    Button,

    /// <summary>Button with a dropdown arrow; clicking the arrow opens a menu.</summary>
    SplitButton,

    /// <summary>Opens a menu only — no default action.</summary>
    DropDown,

    /// <summary>Vertical rule between logical clusters inside one group.</summary>
    Separator,

    /// <summary>
    /// An editable field sitting in the ribbon. the reference's Find group puts the Search People box
    /// directly on the bar rather than behind a button.
    /// </summary>
    TextBox,
}

/// <summary>One control placed in a ribbon group.</summary>
public sealed record RibbonItem
{
    public required CommandId Command { get; init; }
    public RibbonItemSize Size { get; init; } = RibbonItemSize.Large;
    public RibbonItemKind Kind { get; init; } = RibbonItemKind.Button;

    /// <summary>
    /// False for the icon-only small buttons the reference application uses where the icon alone is unambiguous —
    /// the Ignore / Clean Up / Junk stack in the Delete group, for instance.
    /// </summary>
    public bool ShowLabel { get; init; } = true;

    /// <summary>Rendered greyed and unclickable, as Read Aloud is with nothing selected.</summary>
    public bool IsDisabled { get; init; }

    public static RibbonItem Large(CommandId command, RibbonItemKind kind = RibbonItemKind.Button)
        => new() { Command = command, Size = RibbonItemSize.Large, Kind = kind };

    public static RibbonItem Small(CommandId command, RibbonItemKind kind = RibbonItemKind.Button)
        => new() { Command = command, Size = RibbonItemSize.Small, Kind = kind };

    /// <summary>A small button showing its icon only.</summary>
    public static RibbonItem Glyph(CommandId command, RibbonItemKind kind = RibbonItemKind.Button)
        => new()
        {
            Command = command,
            Size = RibbonItemSize.Small,
            Kind = kind,
            ShowLabel = false,
        };
}

/// <summary>
/// A labelled cluster of items, separated from its neighbours by a vertical rule, with the
/// label centred beneath.
/// </summary>
public sealed record RibbonGroup
{
    public required string Id { get; init; }

    /// <summary>Shown centred at the bottom of the group. "Respond", "Tags", "Find".</summary>
    public required string Label { get; init; }

    public required IReadOnlyList<RibbonItem> Items { get; init; }

    /// <summary>
    /// Opens the group's full options dialog. the reference application shows this as a small arrow in the
    /// bottom-right corner of the group.
    /// </summary>
    public CommandId? DialogLauncher { get; init; }

    /// <summary>
    /// Renders the group's items inside a bordered, differently-shaded container with its own
    /// scroll chevron — the Quick Steps box on the reference's Home tab. A gallery is one control
    /// showing several entries, not several controls side by side.
    /// </summary>
    public bool IsGallery { get; init; }

    /// <summary>
    /// Position in the collapse order. Groups with a higher number degrade to a popup button
    /// first as the window narrows. Mirrors <c>ReduceOrder</c> in Fluent.Ribbon and the
    /// ordering of <c>Scale</c> declarations in the published ribbon framework spec.
    /// </summary>
    public int CollapsePriority { get; init; }
}

/// <summary>A ribbon tab. Every module supplies its own set.</summary>
public sealed record RibbonTab
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required IReadOnlyList<RibbonGroup> Groups { get; init; }

    /// <summary>1–3 uppercase characters revealed by Alt.</summary>
    public string? KeyTip { get; init; }

    /// <summary>Contextual tabs appear only while something is selected, and are tinted.</summary>
    public bool IsContextual => ContextualGroup is not null;

    /// <summary>
    /// The named set this tab belongs to, or null for an ordinary tab.
    /// </summary>
    /// <remarks>
    /// Office contextual tabs arrive in labelled sets rather than one at a time — "Table Tools"
    /// carrying Design and Layout — with the set's name on a header spanning them and a tint
    /// marking the whole run. The set is the unit that appears and disappears, so it is what a
    /// host switches on.
    /// </remarks>
    public string? ContextualGroup { get; init; }

    /// <summary>
    /// True for File. It sits leftmost in the strip like any other tab, but selecting it opens
    /// the Backstage — a full-window takeover — instead of changing the ribbon beneath.
    /// </summary>
    public bool IsBackstage { get; init; }
}

/// <summary>
/// The ribbon as data.
/// </summary>
/// <remarks>
/// The ribbon renders a layout document rather than hand-authored XAML. The shipped default is
/// authored to the reference application parity; a user layout overrides it; both are the same shape, and either
/// can be exported, shared or reset. Customize Ribbon then falls out almost free — and plugin
/// commands are placed through the identical path as built-ins, with no second code path.
/// <para>
/// This constrains the design from day one and is much harder to retrofit, which is why it
/// exists in Phase 0/1 rather than later.
/// </para>
/// </remarks>
public sealed record RibbonLayout
{
    public required MailboxModule Module { get; init; }
    public required IReadOnlyList<RibbonTab> Tabs { get; init; }

    /// <summary>Commands on the Quick Access Toolbar, in order.</summary>
    public IReadOnlyList<CommandId> QuickAccess { get; init; } = [];

    /// <summary>
    /// The single-row Simplified command set per tab, keyed by tab id. Separators are
    /// <see cref="RibbonItemKind.Separator"/> entries, and anything past what fits moves into
    /// the overflow menu rather than wrapping.
    /// </summary>
    /// <remarks>
    /// Held separately from <see cref="RibbonTab.Groups"/> because Simplified is not a
    /// rendering of the classic groups — the reference application curates a different, shorter command set for
    /// it, drops the group labels entirely, and reorders to put the common actions first.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<RibbonItem>> SimplifiedRows { get; init; }
        = new Dictionary<string, IReadOnlyList<RibbonItem>>();

    /// <summary>False when this is the shipped default rather than a user's edit.</summary>
    public bool IsUserModified { get; init; }

    public RibbonTab? FindTab(string id)
        => Tabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reports every place two things claim the same KeyTip: two tabs in the strip, or two
    /// commands within one tab.
    /// </summary>
    /// <remarks>
    /// A tab is the right unit, not a module. Alt traversal shows one tab's commands at a time,
    /// so two tabs may reuse a letter freely — the reference does — and only a clash inside a
    /// single tab makes a letter ambiguous. Scoping this per module instead would forbid the
    /// reuse the reference relies on, and would also make a compose window's commands collide
    /// with the main window's for no reason: they are never on screen together.
    /// <para>
    /// Called by a test, because a KeyTip collision is invisible until someone presses Alt.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> FindKeyTipConflicts(CommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var conflicts = new List<string>();

        var duplicateTabs = Tabs
            .Where(t => t.KeyTip is not null)
            .GroupBy(t => t.KeyTip!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        conflicts.AddRange(duplicateTabs.Select(g =>
            $"{Module}: tab KeyTip '{g.Key}' claimed by {string.Join(", ", g.Select(t => t.Id))}"));

        foreach (var tab in Tabs)
        {
            // Both renderings of a tab are traversed the same way, so a letter has to be
            // unambiguous across whichever of them is on screen.
            var placed = tab.Groups
                .SelectMany(g => g.Items)
                .Concat(SimplifiedRows.TryGetValue(tab.Id, out var row) ? row : [])
                .Where(i => i.Kind != RibbonItemKind.Separator)
                .Select(i => i.Command)
                .Distinct();

            var duplicates = placed
                .Select(id => catalog.TryGet(id, out var c) ? c : null)
                .Where(c => c?.KeyTip is not null)
                .GroupBy(c => c!.KeyTip!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            conflicts.AddRange(duplicates.Select(g =>
                $"{Module}/{tab.Id}: KeyTip '{g.Key}' claimed by " +
                string.Join(", ", g.Select(c => c!.Id))));
        }

        return conflicts;
    }

    /// <summary>Every command the layout places, for the "already on the ribbon" check.</summary>
    /// <remarks>
    /// A group's dialog launcher counts. It is a real control with a real command behind it —
    /// the little arrow in the group's corner — so leaving it out reported placed commands as
    /// unplaced, which would have offered them again in Customize Ribbon.
    /// </remarks>
    public IEnumerable<CommandId> PlacedCommands =>
        Tabs.SelectMany(t => t.Groups)
            .SelectMany(g => g.Items)
            .Where(i => i.Kind != RibbonItemKind.Separator)
            .Select(i => i.Command)
            .Concat(Tabs.SelectMany(t => t.Groups)
                .Select(g => g.DialogLauncher)
                .Where(id => id is not null)
                .Select(id => id!.Value))
            .Concat(QuickAccess)
            .Distinct();
}
