using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// A group in the customization tree: a name and the commands under it, in order.
/// </summary>
public sealed class RibbonTreeGroup
{
    public required string Id { get; init; }
    public required string Label { get; set; }

    /// <summary>True for a group the user made, which is the only kind that can be deleted.</summary>
    public bool IsCustom { get; init; }

    public List<CommandId> Commands { get; init; } = [];
}

/// <summary>A tab in the customization tree, with its tick and its groups.</summary>
public sealed class RibbonTreeTab
{
    public required string Id { get; init; }
    public required string Label { get; set; }

    /// <summary>The checkbox beside the tab. An unticked tab is not on the ribbon at all.</summary>
    public bool IsVisible { get; set; } = true;

    public bool IsCustom { get; init; }

    /// <summary>The Simplified bar's clusters for this tab.</summary>
    public List<RibbonTreeGroup> Groups { get; init; } = [];

    /// <summary>
    /// The classic ribbon's groups for this tab. Null in a tree read from a document written
    /// before they were recorded — those edits were made to the Simplified bar alone, so null
    /// means the shipped classic groups, and <see cref="RibbonTree.Reconcile"/> fills it in
    /// before anything edits or applies the tree.
    /// </summary>
    public List<RibbonTreeGroup>? ClassicGroups { get; set; }
}

/// <summary>Which of the two ribbon renderings Customize Ribbon is editing.</summary>
public enum RibbonEditTarget
{
    Simplified,
    Classic,
}

/// <summary>
/// The ribbon as Customize Ribbon shows it: tabs, the groups under them, and the commands under
/// those.
/// </summary>
/// <remarks>
/// This is the editor's working model and the persisted document at once, which is why it is
/// mutable where the layout document is not. Applying it produces a <see cref="RibbonLayout"/>,
/// and that is the only thing the ribbon ever renders — an edit cannot reach the screen except
/// by going through the same document a shipped layout does.
/// <para>
/// It carries every tab, including the ones the user has unticked. Describing only what is on
/// screen would make hiding a tab irreversible: the editor would have nothing left to re-tick.
/// </para>
/// <para>
/// The tree describes <em>both</em> renderings: each tab carries the Simplified bar's clusters
/// and the classic ribbon's groups, and applying writes both back. Customize Ribbon edits the
/// one the reader is running — the reference's own editor does the same, heading itself
/// "Customize the Single Line Ribbon" or "Customize the Classic Ribbon" to match, so an added
/// command reaches the ribbon the reader is actually looking at. A tab's order, name and tick
/// stay shared, because one tab strip serves both renderings here.
/// </para>
/// </remarks>
public sealed class RibbonTree
{
    public List<RibbonTreeTab> Tabs { get; init; } = [];

    /// <summary>
    /// Reads a layout into a tree. The Backstage tab is left out: File opens a full-window
    /// takeover rather than a ribbon, so there is nothing under it to arrange, and the
    /// reference's own editor omits it too.
    /// </summary>
    public static RibbonTree From(RibbonLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var tree = new RibbonTree();

        foreach (var tab in layout.Tabs.Where(t => !t.IsBackstage))
        {
            var entry = new RibbonTreeTab { Id = tab.Id, Label = tab.Label, ClassicGroups = [] };

            if (layout.Simplified.TryGetValue(tab.Id, out var bar))
            {
                entry.Groups.AddRange(bar.Groups.Select(AsTreeGroup));
            }

            entry.ClassicGroups.AddRange(tab.Groups.Select(AsTreeGroup));

            tree.Tabs.Add(entry);
        }

        return tree;
    }

    private static RibbonTreeGroup AsTreeGroup(RibbonGroup group) => new()
    {
        Id = group.Id,
        Label = group.Label,
        Commands = [.. group.Items.Where(i => !i.IsSentinel).Select(i => i.Command)],
    };

    /// <summary>
    /// Folds in tabs the document does not mention, so a tab added by a later build appears
    /// rather than vanishing for anyone who has ever customized their ribbon.
    /// </summary>
    /// <remarks>
    /// The same reasoning as the settings file keeping keys it does not understand. A saved
    /// document is a statement about the tabs that existed when it was written, not a claim
    /// that no others may exist.
    /// </remarks>
    public void Reconcile(RibbonLayout shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        var known = Tabs.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var original = From(shipped);

        foreach (var tab in original.Tabs.Where(t => !known.Contains(t.Id)))
        {
            Tabs.Add(tab);
        }

        // A document from before the classic groups were recorded said nothing about them, and
        // nothing is what it meant: those edits were made to the Simplified bar alone, so the
        // classic ribbon it describes is the shipped one.
        foreach (var tab in Tabs.Where(t => t.ClassicGroups is null))
        {
            tab.ClassicGroups = original.Tabs
                .FirstOrDefault(t => string.Equals(t.Id, tab.Id, StringComparison.OrdinalIgnoreCase))
                ?.ClassicGroups ?? [];
        }
    }

    /// <summary>Puts a tab back the way it shipped, leaving the rest of the tree alone.</summary>
    public bool ResetTab(RibbonLayout shipped, string tabId)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        var index = Tabs.FindIndex(t => string.Equals(t.Id, tabId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        var original = From(shipped).Tabs
            .FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.OrdinalIgnoreCase));

        // A custom tab has no shipped state to go back to, so resetting it removes it.
        if (original is null)
        {
            Tabs.RemoveAt(index);
            return true;
        }

        Tabs[index] = original;
        return true;
    }

    /// <summary>An id no tab is using, for a tab the user has just made.</summary>
    public string NextTabId()
    {
        var used = Tabs.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NextId(used, "custom.tab");
    }

    /// <summary>An id no group in the tree is using.</summary>
    /// <remarks>
    /// Unique across the whole tree rather than within its tab, because a group can be dragged
    /// to another tab and two groups sharing an id would then be indistinguishable.
    /// </remarks>
    public string NextGroupId()
    {
        var used = Tabs.SelectMany(t => t.Groups.Concat(t.ClassicGroups ?? []))
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return NextId(used, "custom.group");
    }

    private static string NextId(HashSet<string> used, string prefix)
    {
        for (var n = 1; ; n++)
        {
            var candidate = $"{prefix}.{n}";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>True when the tree says anything the shipped layout does not.</summary>
    public bool DiffersFrom(RibbonLayout shipped)
    {
        var original = From(shipped);

        if (original.Tabs.Count != Tabs.Count) return true;

        for (var i = 0; i < Tabs.Count; i++)
        {
            var mine = Tabs[i];
            var theirs = original.Tabs[i];

            if (!string.Equals(mine.Id, theirs.Id, StringComparison.Ordinal)) return true;
            if (!string.Equals(mine.Label, theirs.Label, StringComparison.Ordinal)) return true;
            if (!mine.IsVisible) return true;
            if (GroupsDiffer(mine.Groups, theirs.Groups)) return true;

            // A null list means the shipped classic groups, which is exactly no difference.
            if (mine.ClassicGroups is { } classic
                && GroupsDiffer(classic, theirs.ClassicGroups ?? []))
            {
                return true;
            }
        }

        return false;
    }

    private static bool GroupsDiffer(List<RibbonTreeGroup> mine, List<RibbonTreeGroup> theirs)
    {
        if (mine.Count != theirs.Count) return true;

        for (var g = 0; g < mine.Count; g++)
        {
            if (!string.Equals(mine[g].Id, theirs[g].Id, StringComparison.Ordinal)) return true;
            if (!string.Equals(mine[g].Label, theirs[g].Label, StringComparison.Ordinal)) return true;
            if (!mine[g].Commands.SequenceEqual(theirs[g].Commands)) return true;
        }

        return false;
    }

    /// <summary>
    /// The layout this tree describes, built over the shipped one — both renderings, since the
    /// tree carries both.
    /// </summary>
    /// <remarks>
    /// A command keeps the shape it was authored with wherever it already appears — New Email
    /// stays a split button and Search People stays an input — so moving one between groups does
    /// not quietly turn it into a plain button. A command placed for the first time becomes what
    /// its destination is made of: a small labelled button on the Simplified bar, a large one in
    /// a classic group.
    /// </remarks>
    public RibbonLayout ApplyTo(RibbonLayout shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        var barShapes = shipped.Simplified.Values
            .SelectMany(bar => bar.Groups)
            .SelectMany(group => group.Items)
            .Where(item => !item.IsSentinel)
            .GroupBy(item => item.Command)
            .ToDictionary(g => g.Key, g => g.First());

        var classicShapes = shipped.Tabs
            .SelectMany(tab => tab.Groups)
            .SelectMany(group => group.Items)
            .Where(item => !item.IsSentinel)
            .GroupBy(item => item.Command)
            .ToDictionary(g => g.Key, g => g.First());

        var classicGroups = shipped.Tabs
            .SelectMany(tab => tab.Groups)
            .GroupBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var tabs = new List<RibbonTab>(shipped.Tabs.Where(t => t.IsBackstage));
        var bars = new Dictionary<string, SimplifiedBar>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Tabs.Where(t => t.IsVisible))
        {
            var original = shipped.FindTab(entry.Id);
            var groups = BuildClassicGroups(entry, original, classicGroups, classicShapes, barShapes);

            // A tab with nothing on the Simplified bar gets no bar at all rather than an empty
            // row — which is what keeps a classic-only tab off the Simplified strip, where the
            // reference does not draw it.
            var shippedBar = shipped.Simplified.GetValueOrDefault(entry.Id);
            var hasBar = shippedBar is not null || entry.Groups.Count > 0;

            tabs.Add(original is null
                ? new RibbonTab
                {
                    Id = entry.Id,
                    Label = entry.Label,
                    Groups = groups,
                    ClassicOnly = !hasBar,
                }
                : original with { Label = entry.Label, Groups = groups });

            if (!hasBar) continue;

            // The rule that closes a row is the layout's furniture rather than the reader's —
            // not a command, and not something Customize Ribbon moves — so it is carried over
            // rather than rebuilt from the tree, which had been giving every customized row the
            // default one whether the shipped row had it or not.
            bars[entry.Id] = new SimplifiedBar
            {
                TrailingRule = shippedBar?.TrailingRule ?? true,
                Groups =
                [
                    .. entry.Groups.Select(group => new RibbonGroup
                    {
                        Id = group.Id,
                        Label = group.Label,
                        Items =
                        [
                            .. group.Commands.Select(id =>
                                barShapes.TryGetValue(id, out var shape)
                                    ? shape
                                    : RibbonItem.Small(id)),
                        ],
                    }),
                ],
            };
        }

        return shipped with
        {
            Tabs = tabs,
            Simplified = bars,
            IsUserModified = true,
        };
    }

    /// <summary>The classic groups a tab should render, built from the tree's description.</summary>
    private static IReadOnlyList<RibbonGroup> BuildClassicGroups(
        RibbonTreeTab entry,
        RibbonTab? original,
        Dictionary<string, RibbonGroup> shippedGroups,
        Dictionary<CommandId, RibbonItem> classicShapes,
        Dictionary<CommandId, RibbonItem> barShapes)
    {
        // A tree that says nothing about the classic groups means the shipped ones.
        if (entry.ClassicGroups is null) return original?.Groups ?? [];

        var groups = new List<RibbonGroup>();

        foreach (var group in entry.ClassicGroups)
        {
            var shipped = shippedGroups.GetValueOrDefault(group.Id);

            // An unedited group is carried whole, because rebuilding it from the command list
            // would strip what the list does not record: the corner launcher, a gallery's box,
            // the KeyTip and the collapse order.
            if (shipped is not null
                && group.Commands.SequenceEqual(
                    shipped.Items.Where(i => !i.IsSentinel).Select(i => i.Command)))
            {
                groups.Add(shipped with { Label = group.Label });
                continue;
            }

            // A command only the Simplified bar places arrives with its kind — a split button
            // stays one — but at the classic size, which is what a classic group is made of.
            var items = group.Commands.Select(id =>
                    classicShapes.TryGetValue(id, out var shape) ? shape
                    : barShapes.TryGetValue(id, out var fromBar)
                        ? fromBar with { Size = RibbonItemSize.Large }
                        : RibbonItem.Large(id))
                .ToList();

            groups.Add(shipped is not null
                ? shipped with { Label = group.Label, Items = items }
                : new RibbonGroup { Id = group.Id, Label = group.Label, Items = items });
        }

        return groups;
    }
}
