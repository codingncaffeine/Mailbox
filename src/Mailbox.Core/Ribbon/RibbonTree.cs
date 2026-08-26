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

    public List<RibbonTreeGroup> Groups { get; init; } = [];
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
/// The tree edits the <em>Simplified</em> bar, which is the ribbon a first run shows and the one
/// the reference's own editor targets — its header reads "Customize the Single Line Ribbon".
/// Classic groups are left as shipped; a tab's order, name and tick apply to both.
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
            var entry = new RibbonTreeTab { Id = tab.Id, Label = tab.Label };

            if (layout.Simplified.TryGetValue(tab.Id, out var bar))
            {
                foreach (var group in bar.Groups)
                {
                    entry.Groups.Add(new RibbonTreeGroup
                    {
                        Id = group.Id,
                        Label = group.Label,
                        Commands = [.. group.Items.Where(i => !i.IsSentinel).Select(i => i.Command)],
                    });
                }
            }

            tree.Tabs.Add(entry);
        }

        return tree;
    }

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

        foreach (var tab in From(shipped).Tabs.Where(t => !known.Contains(t.Id)))
        {
            Tabs.Add(tab);
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
        var used = Tabs.SelectMany(t => t.Groups)
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
            if (mine.Groups.Count != theirs.Groups.Count) return true;

            for (var g = 0; g < mine.Groups.Count; g++)
            {
                if (!string.Equals(mine.Groups[g].Id, theirs.Groups[g].Id, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!string.Equals(
                        mine.Groups[g].Label, theirs.Groups[g].Label, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!mine.Groups[g].Commands.SequenceEqual(theirs.Groups[g].Commands)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The layout this tree describes, built over the shipped one.
    /// </summary>
    /// <remarks>
    /// A command keeps the shape it was authored with wherever it already appears — New Email
    /// stays a split button and Search People stays an input — so moving one between groups does
    /// not quietly turn it into a plain button. A command placed for the first time becomes a
    /// small labelled button, which is what the Simplified bar is made of.
    /// </remarks>
    public RibbonLayout ApplyTo(RibbonLayout shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        var shapes = shipped.Simplified.Values
            .SelectMany(bar => bar.Groups)
            .SelectMany(group => group.Items)
            .Where(item => !item.IsSentinel)
            .GroupBy(item => item.Command)
            .ToDictionary(g => g.Key, g => g.First());

        var tabs = new List<RibbonTab>(shipped.Tabs.Where(t => t.IsBackstage));
        var bars = new Dictionary<string, SimplifiedBar>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Tabs.Where(t => t.IsVisible))
        {
            var original = shipped.FindTab(entry.Id);

            tabs.Add(original is null
                ? new RibbonTab { Id = entry.Id, Label = entry.Label, Groups = [] }
                : original with { Label = entry.Label });

            // The rule that closes a row is the layout's furniture rather than the reader's —
            // not a command, and not something Customize Ribbon moves — so it is carried over
            // rather than rebuilt from the tree, which had been giving every customized row the
            // default one whether the shipped row had it or not.
            var shippedBar = shipped.Simplified.GetValueOrDefault(entry.Id);

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
                                shapes.TryGetValue(id, out var shape)
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
}
