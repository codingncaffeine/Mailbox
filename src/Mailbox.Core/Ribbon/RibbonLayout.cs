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
    /// An editable field sitting in the ribbon, with no chevron. The reference's Find group puts
    /// the Search People box directly on the bar rather than behind a button.
    /// </summary>
    TextBox,

    /// <summary>
    /// A bordered box showing a value and a chevron — the Font and Font Size boxes on the
    /// compose ribbon. Distinct from <see cref="TextBox"/> because it picks rather than accepts.
    /// </summary>
    ComboBox,

    /// <summary>
    /// A command drawn inside a bordered box rather than as a bare button: the Quick Steps entry
    /// on the Home row, which the reference boxes to mark it as one of a gallery.
    /// </summary>
    BoxedButton,

    /// <summary>
    /// The "…" that ends a cluster on the Simplified bar, opening the commands that cluster has
    /// no room for. Distinct from the bar's own overflow at the far right: this one belongs to
    /// one cluster, and the reference draws both on the same row.
    /// </summary>
    Overflow,

    /// <summary>
    /// The small corner arrow that opens a cluster's full dialog. The classic ribbon hangs one
    /// off <see cref="RibbonGroup.DialogLauncher"/>; on the Simplified bar it is an item in the
    /// row, because the row has no groups to hang it from.
    /// </summary>
    DialogLauncher,
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

    /// <summary>
    /// Fixed width, for the fields the reference sizes rather than letting them fit their
    /// contents — the Font and Font Size boxes on the compose ribbon are 107px and 51px
    /// whatever is in them.
    /// </summary>
    public double? Width { get; init; }

    /// <summary>
    /// What a field shows. Static until the editor in Phase 5 gives it a selection to report —
    /// the reference's boxes read the caret, and an empty box beside a body full of text reads
    /// as broken rather than as unfinished.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// True for the items that stand for furniture rather than a command — the rule between
    /// clusters and the cluster's own "…". Both carry a placeholder id so that the record's
    /// required <see cref="Command"/> is satisfied, and neither is a command anyone placed.
    /// </summary>
    public bool IsSentinel =>
        Kind is RibbonItemKind.Separator or RibbonItemKind.Overflow;

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

    /// <summary>
    /// The id every rule carries, on the ribbon and on the Quick Access Toolbar alike. It
    /// stands for furniture rather than a command, so nothing registers it in the catalogue.
    /// </summary>
    public static CommandId SeparatorId { get; } = new("app.separator");

    /// <summary>The vertical rule between two clusters on the Simplified bar.</summary>
    public static RibbonItem Rule()
        => new()
        {
            Command = SeparatorId,
            Kind = RibbonItemKind.Separator,
            Size = RibbonItemSize.Small,
            ShowLabel = false,
        };

    /// <summary>The "…" ending a cluster, opening what that cluster has no room for.</summary>
    public static RibbonItem Overflow()
        => new()
        {
            Command = new CommandId("app.overflow"),
            Kind = RibbonItemKind.Overflow,
            Size = RibbonItemSize.Small,
            ShowLabel = false,
        };

    /// <summary>The corner arrow opening a cluster's dialog.</summary>
    public static RibbonItem Launcher(CommandId opens)
        => new()
        {
            Command = opens,
            Kind = RibbonItemKind.DialogLauncher,
            Size = RibbonItemSize.Small,
            ShowLabel = false,
        };

    /// <summary>A fixed-width picker on the bar, like the Font and Font Size boxes.</summary>
    public static RibbonItem Combo(CommandId command, double width, string text = "")
        => new()
        {
            Command = command,
            Size = RibbonItemSize.Small,
            Kind = RibbonItemKind.ComboBox,
            ShowLabel = false,
            Width = width,
            Text = text,
        };

    /// <summary>A fixed-width input on the bar, like Search People.</summary>
    public static RibbonItem Field(CommandId command, double width, string placeholder = "")
        => new()
        {
            Command = command,
            Size = RibbonItemSize.Small,
            Kind = RibbonItemKind.TextBox,
            ShowLabel = false,
            Width = width,
            Text = placeholder,
        };

    /// <summary>A command boxed on the bar, like the Quick Steps entry.</summary>
    public static RibbonItem Boxed(CommandId command, double width)
        => new()
        {
            Command = command,
            Size = RibbonItemSize.Small,
            Kind = RibbonItemKind.BoxedButton,
            Width = width,
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

/// <summary>
/// The Simplified bar for one tab: the clusters the rules divide it into, named.
/// </summary>
/// <remarks>
/// The bar renders as a flat run of items, so it was first transcribed as one — but the rules in
/// it are group boundaries rather than decoration, and Customize Ribbon shows them as a tree of
/// named groups. Naming them here makes the editor's tree fall out of the layout document, and
/// makes the row something a user can rearrange a group at a time.
/// <para>
/// The Simplified groups are not the classic ones renamed: the reference merges Delete and Move
/// into "Move &amp; Delete" here, and drops several groups entirely.
/// </para>
/// </remarks>
public sealed record SimplifiedBar
{
    public required IReadOnlyList<RibbonGroup> Groups { get; init; }

    /// <summary>
    /// A rule closing the row, before the overflow "…".
    /// </summary>
    /// <remarks>
    /// Measured on all three captured tabs: Home's rules fall at x = 191, 341, 457, 596, 831,
    /// 1049, 1093 and 1289 — eight rules for eight clusters, so the last one closes the run
    /// rather than dividing it. Send/Receive and View do the same at 1019 and 977.
    /// </remarks>
    public bool TrailingRule { get; init; } = true;

    /// <summary>The row as the renderer wants it: items, with a rule between clusters.</summary>
    public IReadOnlyList<RibbonItem> Flatten()
    {
        var row = new List<RibbonItem>();

        foreach (var group in Groups)
        {
            if (row.Count > 0) row.Add(RibbonItem.Rule());
            row.AddRange(group.Items);
        }

        if (TrailingRule && row.Count > 0) row.Add(RibbonItem.Rule());
        return row;
    }
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
    /// The Simplified bar per tab, keyed by tab id: named clusters rather than a flat run.
    /// </summary>
    /// <remarks>
    /// Held separately from <see cref="RibbonTab.Groups"/> because Simplified is not a
    /// rendering of the classic groups — the reference application curates a different, shorter command set for
    /// it, groups it differently, and reorders to put the common actions first.
    /// </remarks>
    public IReadOnlyDictionary<string, SimplifiedBar> Simplified { get; init; }
        = new Dictionary<string, SimplifiedBar>();

    /// <summary>
    /// The single-row Simplified command set per tab, keyed by tab id. Separators are
    /// <see cref="RibbonItemKind.Separator"/> entries, and anything past what fits moves into
    /// the overflow menu rather than wrapping.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Simplified"/> where a tab declares clusters, so the names and the
    /// row cannot disagree; a host that has no use for the tree — the compose window — authors
    /// its rows here directly instead. Computed rather than cached: a customized layout is a
    /// <c>with</c> copy of this one, and a cached row would survive the copy as the old ribbon.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<RibbonItem>> SimplifiedRows
    {
        get
        {
            if (Simplified.Count == 0) return field;

            var rows = new Dictionary<string, IReadOnlyList<RibbonItem>>(field);
            foreach (var (tab, bar) in Simplified) rows[tab] = bar.Flatten();
            return rows;
        }

        init;
    } = new Dictionary<string, IReadOnlyList<RibbonItem>>();

    /// <summary>
    /// The prompt sitting after the last tab — "Tell me what you want to do" on the compose
    /// window. Null where the host has none, which is what the shell's captures show.
    /// </summary>
    public string? TellMe { get; init; }

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
                .Where(i => !i.IsSentinel)
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
    /// <para>
    /// So does the Simplified bar, which is the ribbon a first run actually shows: the View tab
    /// has no classic groups at all, and reading only those reported every command on it as
    /// unplaced.
    /// </para>
    /// </remarks>
    public IEnumerable<CommandId> PlacedCommands =>
        Tabs.SelectMany(t => t.Groups)
            .SelectMany(g => g.Items)
            .Where(i => !i.IsSentinel)
            .Select(i => i.Command)
            .Concat(Tabs.SelectMany(t => t.Groups)
                .Select(g => g.DialogLauncher)
                .Where(id => id is not null)
                .Select(id => id!.Value))
            .Concat(SimplifiedRows.Values
                .SelectMany(row => row)
                .Where(i => !i.IsSentinel)
                .Select(i => i.Command))
            .Concat(QuickAccess)
            .Distinct();
}
