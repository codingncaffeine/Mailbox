using Mailbox.Core.Commands;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// Puts the Quick Steps into the ribbon: the gallery on the classic Home tab lists them all, and
/// the Simplified bar's boxed entry is the first of them, as the reference draws both.
/// </summary>
/// <remarks>
/// The shipped layout names the three shipped steps by their own command ids, so with the
/// defaults in place this rewrites the group to what it already was — the fidelity captures are
/// unchanged. Steps the reader adds, renames or reorders show up the same way, because the
/// gallery is a rendering of the list rather than a copy of it. Applied after the reader's
/// ribbon edits, so a Customize Ribbon change to any other group survives, and a Quick Steps
/// group the reader removed stays removed.
/// </remarks>
public static class QuickStepsRibbon
{
    /// <summary>The id of the classic group and the Simplified cluster the steps live in.</summary>
    public const string GroupId = "quicksteps";

    public static RibbonLayout Inject(RibbonLayout layout, IReadOnlyList<QuickStep> steps)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count == 0) return layout;

        var tabs = layout.Tabs.Select(tab => tab with
        {
            Groups = [.. tab.Groups.Select(group => group.Id == GroupId && group.IsGallery
                ? group with { Items = [.. steps.Select(s => RibbonItem.Small(s.CommandId))] }
                : group)],
        }).ToList();

        var simplified = new Dictionary<string, SimplifiedBar>();
        foreach (var (tabId, bar) in layout.Simplified)
        {
            simplified[tabId] = bar with
            {
                Groups = [.. bar.Groups.Select(cluster => cluster.Id == GroupId
                    ? cluster with
                    {
                        Items = [.. cluster.Items.Select((item, index) => index == 0 && item.Kind == RibbonItemKind.BoxedButton
                            ? item with { Command = steps[0].CommandId }
                            : item)],
                    }
                    : cluster)],
            };
        }

        return layout with { Tabs = tabs, Simplified = simplified };
    }
}
