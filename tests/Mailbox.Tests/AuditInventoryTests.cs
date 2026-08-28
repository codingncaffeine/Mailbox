using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// The audit's Phase 0 inventories, generated from the code rather than transcribed.
/// </summary>
/// <remarks>
/// A hand-kept inventory cannot notice what was added to the tree and not to it, and the
/// surface nobody wrote down is exactly the one that goes unaudited. So these are dumped from
/// the objects the application itself builds, on request:
/// <code>
/// MAILBOX_INVENTORY_DUMP=artifacts/audit/phase0 dotnet test tests/Mailbox.Tests \
///     --filter AuditInventoryTests
/// </code>
/// The command catalogue has its own dump next door in <see cref="AuditWiringSweepTests"/>
/// (<c>MAILBOX_CATALOGUE_DUMP</c>); this covers the ribbon, which is the other half — a command
/// exists, and separately a layout does or does not place it anywhere a reader can press.
/// </remarks>
public class AuditInventoryTests
{
    private static readonly (string Name, RibbonLayout Layout)[] Layouts =
    [
        ("mail", DefaultRibbonLayouts.Mail),
        ("calendar", DefaultRibbonLayouts.Calendar),
        ("people", DefaultRibbonLayouts.People),
        ("compose", DefaultRibbonLayouts.Compose),
    ];

    /// <summary>
    /// Every layout names tabs, every tab names groups, and every group holds something. An
    /// empty group draws a gap with a label under it, which reads as a bug rather than as a
    /// group nobody filled.
    /// </summary>
    [Fact]
    public void EveryRibbonGroupInEveryDefaultLayoutHoldsSomething()
    {
        var empty = new List<string>();
        var groups = 0;

        foreach (var (name, layout) in Layouts)
        {
            Assert.NotEmpty(layout.Tabs);

            foreach (var tab in layout.Tabs)
            {
                if (tab.IsBackstage) continue;

                foreach (var group in tab.Groups)
                {
                    groups++;
                    if (group.Items.Count == 0) empty.Add($"{name}/{tab.Id}/{group.Label}");
                }
            }
        }

        Assert.True(groups > 60, $"only {groups} groups found — the sweep is not seeing the layouts");
        Assert.Empty(empty);
    }

    /// <summary>
    /// Simplified is a different arrangement of the same tabs, not a different set of them:
    /// every row it defines belongs to a tab the layout actually has.
    /// </summary>
    [Fact]
    public void EverySimplifiedRowBelongsToATabThatExists()
    {
        var orphans = new List<string>();

        foreach (var (name, layout) in Layouts)
        {
            foreach (var (tabId, _) in layout.SimplifiedRows)
            {
                if (layout.FindTab(tabId) is null) orphans.Add($"{name}/{tabId}");
            }
        }

        Assert.Empty(orphans);
    }

    /// <summary>
    /// Writes the ribbon inventory when asked. Gated, like the catalogue dump, because a test
    /// run should not write into the tree unless the audit asked it to.
    /// </summary>
    [Fact]
    public void DumpTheRibbonInventoryOnRequest()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_INVENTORY_DUMP") is not { Length: > 0 } asked)
        {
            return;
        }

        // Resolved against the repository, not against the test host's working directory —
        // which is the binary's own folder, so a caller who asks for artifacts/audit/phase0
        // gets it buried under tests/Mailbox.Tests/bin and wonders where the dump went.
        var dir = Path.IsPathRooted(asked) ? asked : Path.Combine(RepoRoot(), asked);
        Directory.CreateDirectory(dir);

        var rows = new List<string> { "layout\tmode\ttab\ttab_label\tgroup\titem_count\tcommands" };
        var summary = new List<string>();

        foreach (var (name, layout) in Layouts)
        {
            var tabs = 0;
            var groups = 0;
            var items = 0;

            foreach (var tab in layout.Tabs)
            {
                tabs++;
                foreach (var group in tab.Groups)
                {
                    groups++;
                    var placed = group.Items.Where(i => !i.IsSentinel).Select(i => i.Command.Value).ToArray();
                    items += placed.Length;
                    rows.Add($"{name}\tclassic\t{tab.Id}\t{tab.Label}\t{group.Label}\t{placed.Length}\t{string.Join(" ", placed)}");
                }
            }

            foreach (var (tabId, row) in layout.SimplifiedRows)
            {
                var placed = row.Where(i => !i.IsSentinel).Select(i => i.Command.Value).ToArray();
                rows.Add($"{name}\tsimplified\t{tabId}\t{layout.FindTab(tabId)?.Label ?? "?"}\t(row)\t{placed.Length}\t{string.Join(" ", placed)}");
            }

            summary.Add($"{name,-10} {tabs,3} tabs  {groups,4} groups  {items,5} placed items  "
                        + $"{layout.SimplifiedRows.Count,3} simplified rows");
        }

        File.WriteAllLines(Path.Combine(dir, "ribbon-inventory.tsv"), rows);
        File.WriteAllLines(Path.Combine(dir, "ribbon-summary.txt"), summary);

        Assert.True(rows.Count > 60);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
